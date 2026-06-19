using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    delegate IntPtr LowLevelKbProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKbProc cb, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool   UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    void OnLoaded(object s, RoutedEventArgs e)
    {
        _pixPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        UpdateKbdStatus();

        // WH_KEYBOARD_LL fires before WPF dispatches the key event, so _extendedKey is always
        // up-to-date when OnKeyDown runs.  Distinguishes numpad Enter (extended) from main Enter.
        _llKbProc = LLKeyboardProc;
        _llKbHook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _llKbProc, IntPtr.Zero, 0);

        _repeatTimer.Tick += OnRepeatTick;

        CompositionTarget.Rendering += RenderTick;

        WireMenu();
        InitAudio();
        InitTrayIcon();
        BtnRailExpand.Click      += (_, _) => ToggleFocusedDataExpand();
        BtnValueRailExpand.Click += (_, _) => ToggleFocusedValueExpand();
        _layoutPreset = _settings.LayoutPreset == LayoutPreset.Detached
            ? LayoutPreset.Full
            : _settings.LayoutPreset;
        ApplyLayoutPreset(_layoutPreset, saveSettings: false);

        Topmost = _settings.AlwaysOnTop;

        // Restore saved window bounds after layout settles
        Dispatcher.InvokeAsync(() =>
        {
            if (_settings.WindowLeft >= 0 && _settings.WindowTop >= 0)
            {
                Left = Math.Max(_settings.WindowLeft, SystemParameters.VirtualScreenLeft);
                Top  = Math.Max(_settings.WindowTop,  SystemParameters.VirtualScreenTop);
                if (_settings.WindowWidth > 200 && _settings.WindowHeight > 100)
                {
                    Width  = _settings.WindowWidth;
                    Height = _settings.WindowHeight;
                }
            }
            if (_settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }, DispatcherPriority.Loaded);

        // Double-click on frame exits fullscreen; handled in preview to suppress the
        // second TOUCH_DOWN that would otherwise be sent to Kronos on click 2.
        FrameImage.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && _isFullscreen) { ToggleFullscreen(); e.Handled = true; }
        };

        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Background);
        Task.Run(ConnectAsync);
    }

    IntPtr LLKeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        // KBDLLHOOKSTRUCT layout: vkCode(0) scanCode(4) flags(8) time(12) dwExtraInfo(16)
        // flags bit 0 = LLKHF_EXTENDED (set for numpad Enter, right-side Ctrl/Alt, etc.)
        if (code >= 0 && (int)wParam == 0x0100 /*WM_KEYDOWN*/)
        {
            int vk    = Marshal.ReadInt32(lParam, 0);
            int flags = Marshal.ReadInt32(lParam, 8);
            if (vk == 13 /*VK_RETURN*/)
                _extendedKey = (flags & 1) != 0;
        }
        return CallNextHookEx(_llKbHook, code, wParam, lParam);
    }

    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (_settings.PromptBeforeQuitting)
        {
            string msg = _connState == ConnState.Connected
                ? "Disconnect from Kronos and quit?"
                : "Quit Kronos ScreenRemote?";
            if (MessageBox.Show(msg, "Quit", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        AppLog.Info("[shutdown] main window closing");
        if (_calDirty)
        {
            var result = MessageBox.Show(
                "You have unsaved calibration changes.\nSave before exiting?",
                "Unsaved Calibration",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (result == MessageBoxResult.Yes)
            {
                Storage.SaveCal(_calMesh, _calBiasDots);
                _calDirty = false;
            }
        }

        if (!_isFullscreen)
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft   = Left;
                _settings.WindowTop    = Top;
                _settings.WindowWidth  = Width;
                _settings.WindowHeight = Height;
            }
            Storage.SaveSettings(_settings);
        }

        _trayIcon?.Dispose();
        CompositionTarget.Rendering -= RenderTick;
        CleanupAudio();
        _pingCts?.Cancel();
        _modePollCts?.Cancel();
        _receiver?.Dispose();
        if (_llKbHook != IntPtr.Zero) { UnhookWindowsHookEx(_llKbHook); _llKbHook = IntPtr.Zero; }
    }

    void OnKeyDown(object s, KeyEventArgs e)
    {
        bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && e.Key == Key.Z)
        {
            if (_calMode) { if (_shiftHeld) CalHistRedo(); else CalHistUndo(); }
            else          { if (_shiftHeld) HistRedo();    else HistUndo();    }
            OverlayLayer.InvalidateVisual(); return;
        }
        if (ctrl && e.Key == Key.Y)
        {
            if (_calMode) CalHistRedo(); else HistRedo();
            OverlayLayer.InvalidateVisual(); return;
        }
        if (ctrl && e.Key == Key.K) { OpenCommandPalette(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C && _edOpen)
        {
            _clipboard = EffRgb(_edSel);
            Console.WriteLine($"[clipboard] copied entry {_edSel}: {_clipboard}");
            return;
        }
        if (ctrl && e.Key == Key.V && _edOpen && _clipboard.HasValue)
        {
            if (!_locked.Contains(_edSel))
            {
                var old = _overrides.TryGetValue(_edSel, out var ov) ? ov : (PaletteEntry?)null;
                _overrides[_edSel] = _clipboard.Value;
                HistPush(_edSel, old, _clipboard.Value);
                RebuildLut(); ApplyLut();
            }
            _edTyped = null;
            OverlayLayer.InvalidateVisual(); return;
        }

        if (ctrl && e.Key == Key.V && !_edOpen && _kbdCapture && _kbdSendEnabled)
        {
            PasteClipboardToKronos();
            e.Handled = true; return;
        }

        if (ctrl && e.Key == Key.D1) { SetWindowSize(0.75); return; }
        if (ctrl && e.Key == Key.D2) { SetWindowSize(1.0);  return; }
        if (ctrl && e.Key == Key.D3) { SetWindowSize(1.25); return; }
        if (ctrl && e.Key == Key.D4) { SetWindowSize(1.50); return; }
        if (ctrl && e.Key == Key.D5) { SetWindowSize(2.00); return; }
        if (ctrl && e.Key == Key.S && !_edOpen && !_calMode) { SaveScreenshot(); e.Handled = true; return; }

        // ── Fullscreen shortcuts (intercept before capture so they work even when forwarding) ──
        if (_isFullscreen && !ctrl && !e.IsRepeat)
        {
            if (e.Key == Key.OemTilde)
            {
                MainMenu.Visibility = MainMenu.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Loaded);
                return;
            }
        }

        // ── Numpad Enter → Kronos when capture is active ─────────────────────
        if (_kbdCapture && _kbdSendEnabled && !ctrl && !e.IsRepeat && !_calMode && e.Key == Key.Return && _extendedKey)
        {
            _instantKeys.Add(Key.Return);   // suppress KEY-up so KEY 28 0 is never sent to vkbd
            BTN_Enter.FlashDepress();
            Ctrl("BUTTON ENTER");
            e.Handled = true; return;
        }

        // ── Macros (user-defined first, then built-ins) — requires modifier key ─
        if (!e.IsRepeat && _kbdCapture && _kbdSendEnabled && !_edOpen && !_calMode
            && Keyboard.Modifiers != ModifierKeys.None)
        {
            var baseKey = e.Key == Key.System ? e.SystemKey : e.Key;
            var trigger = new Keybind(baseKey, Keyboard.Modifiers);
            if (TryFireUserMacro(trigger)) { e.Handled = true; return; }
            if (ctrl && e.Key == Key.A)    { MacroSelectAll();  e.Handled = true; return; }
        }

        // ── Numpad 0–9 / − / · : always forward when capture active ──────────
        if (_kbdCapture && _kbdSendEnabled && !_edOpen && !ctrl && !e.IsRepeat && !_calMode)
        {
            int? numBtn = e.Key switch
            {
                Key.NumPad0 => 0, Key.NumPad1 => 1, Key.NumPad2 => 2, Key.NumPad3 => 3,
                Key.NumPad4 => 4, Key.NumPad5 => 5, Key.NumPad6 => 6, Key.NumPad7 => 7,
                Key.NumPad8 => 8, Key.NumPad9 => 9, _ => (int?)null
            };
            if (numBtn.HasValue) { NumButton(numBtn.Value)?.FlashDepress(); Ctrl($"BUTTON NUM{numBtn.Value}"); e.Handled = true; return; }
            if (e.Key == Key.Subtract) { BTN_data_dash.FlashDepress();   Ctrl("BUTTON NUM_DASH"); e.Handled = true; return; }
            if (e.Key == Key.Decimal)  { BTN_data_period.FlashDepress(); Ctrl("BUTTON NUM_DOT");  e.Handled = true; return; }
        }

        // ── Keyboard capture: forward before any local shortcut ──────────────
        // F1–F12 fall through to the IsAction checks below (mode select, help, etc.).
        if (_kbdCapture && _kbdSendEnabled && !_edOpen && !ctrl && !e.IsRepeat && !_calMode
            && (e.Key < Key.F1 || e.Key > Key.F12))
        {
            // Shifted override: Kronos needs a different keycode or Shift handling
            var shifted = _shiftHeld ? KeyMap.ToLinuxShifted(e.Key) : null;
            if (shifted.HasValue)
            {
                AppLog.Debug($"[kbd] shifted key {e.Key} → linux {shifted.Value.Code} keepShift={shifted.Value.KeepShift}");
                if (!shifted.Value.KeepShift) Ctrl("KEY 42 0");   // drop Shift if not needed
                Ctrl($"KEY {shifted.Value.Code} 1");
                Ctrl($"KEY {shifted.Value.Code} 0");
                if (!shifted.Value.KeepShift) Ctrl("KEY 42 1");   // restore Shift
                _instantKeys.Add(e.Key);
                e.Handled = true; return;
            }

            var rawMap = RawKeyMap.Get(e.Key, _shiftHeld);
            if (rawMap != null)
            {
                AppLog.Debug($"[kbd] raw-map {rawMap.HostKeyDisplay} → KEY {rawMap.RawCode}");
                if (rawMap.RawShift) Ctrl("KEY 42 1");
                Ctrl($"KEY {rawMap.RawCode} 1");
                StartRepeat(rawMap.RawCode);
                e.Handled = true; return;
            }

            int? lkc = KeyMap.ToLinux(e.Key);
            if (lkc.HasValue)
            {
                AppLog.Debug($"[kbd] {e.Key} → linux {lkc.Value}");
                // Eva text fields: unshifted letter → uppercase, Left-Shift+letter → lowercase.
                // (Eva only recognises KEY_LEFTSHIFT/42 as a case modifier; KEY_RIGHTSHIFT/54 is
                //  ignored by Eva, so Right Shift naturally gives uppercase — useful distinction.)
                // CapsLock: inject Left Shift so letters are lowercase without holding Shift.
                if (e.Key is >= Key.A and <= Key.Z
                    && Keyboard.GetKeyStates(Key.CapsLock).HasFlag(KeyStates.Toggled)
                    && !_shiftHeld)
                {
                    if (_capsShiftedKeys.Count == 0) Ctrl("KEY 42 1");
                    _capsShiftedKeys.Add(e.Key);
                }
                Ctrl($"KEY {lkc.Value} 1");
                if (RepeatableKeys.Contains(e.Key))
                    StartRepeat(lkc.Value);
                e.Handled = true; return;
            }
        }

        if (IsAction("Quit", e))  { TryQuit(); return; }

        if (e.Key == Key.Escape)
        {
            if (_isFullscreen)               { ToggleFullscreen(); return; }
            if (_helpOpen)                   { _helpOpen = false; OverlayLayer.InvalidateVisual(); return; }
            if (_edOpen && _edTyped != null) { _edTyped  = null;  OverlayLayer.InvalidateVisual(); return; }
            if (_edOpen)                     { _edOpen   = false; OverlayLayer.InvalidateVisual(); return; }
            if (_dragPending || _dragActive)
            {
                var cancelPos = _dragActive ? _dragLast : _dragPendingPos;
                _dragPending = false; _dragActive = false;
                FrameImage.ReleaseMouseCapture();
                Ctrl($"TOUCH_UP {cancelPos.x} {cancelPos.y}");
                OverlayLayer.InvalidateVisual(); return;
            }
            if (_zoomOn)                     { _zoomOn = false; OverlayLayer.InvalidateVisual(); return; }
            Ctrl("BUTTON EXIT"); return;
        }

        if (IsAction("Help", e))         { OpenHelpWindow(); return; }

        if (IsAction("Zoom Window", e))
        {
            _zoomOn = !_zoomOn; OverlayLayer.InvalidateVisual(); return;
        }
        if (e.Key is Key.OemPlus or Key.Add || IsAction("Zoom In", e))
        {
            DoZoomIn(); return;
        }
        if (e.Key is Key.OemMinus || IsAction("Zoom Out", e))
        {
            DoZoomOut(); return;
        }

        if (IsAction("AspectLock", e))   { _aspectLock = !_aspectLock; RefreshFrameRect(); return; }

        if (IsAction("Mirror", e))
        {
            _mirrorState = !_mirrorState;
            Ctrl(_mirrorState ? "MIRROR_ON" : "MIRROR_OFF"); return;
        }

        if (IsAction("Fullscreen", e))   { ToggleFullscreen(); return; }

        if (IsAction("HideDataInput",  e)) { ToggleHideDataInput();  return; }
        if (IsAction("HideValueInput", e)) { ToggleHideValueInput(); return; }

        if (e.Key == Key.Return && !_edOpen)
        {
            Ctrl("BUTTON ENTER"); return;
        }

        // Mode select (rebindable; default F2–F8; F1 reserved for Help above)
        if (IsAction("Mode Setlist",  e)) { SendMode(1); return; }
        if (IsAction("Mode Combi",    e)) { SendMode(2); return; }
        if (IsAction("Mode Program",  e)) { SendMode(3); return; }
        if (IsAction("Mode Sequence", e)) { SendMode(4); return; }
        if (IsAction("Mode Sampling", e)) { SendMode(5); return; }
        if (IsAction("Mode Global",   e)) { SendMode(6); return; }
        if (IsAction("Mode Disk",     e)) { SendMode(7); return; }

        // Bank select (unassigned by default; rebindable in Settings)
        foreach (char b in new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'G' })
        {
            if (IsAction($"Bank I-{b}",    e)) { Ctrl($"BUTTON BANK_I{b}");           return; }
            if (IsAction($"Bank U-{b}",    e)) { Ctrl($"BUTTON BANK_U{b}");           return; }
            if (IsAction($"Bank U-{b}{b}", e)) { Ctrl($"CHORD BANK_U{b} BANK_I{b}"); return; }
        }

        if (IsAction("Calibrate", e))
        {
            _calMode = !_calMode;
            if (_calMode) EnterCalMode(); else ExitCalMode();
            Console.WriteLine($"[cal] calibrate mode {(_calMode ? "ON" : "OFF")}");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_calMode && e.Key == Key.R)
        {
            _calMesh.Reset();
            _calDirty = true;
            _calHistory.Clear(); _calHistPos = -1;
            Console.WriteLine("[cal] mesh reset to identity (unsaved)");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_calMode && e.Key == Key.X)
        {
            _calBiasDots.Clear();
            _calHistory.Clear(); _calHistPos = -1;
            Storage.SaveCal(_calMesh, _calBiasDots);
            Console.WriteLine("[cal] bias dots cleared");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_calMode && e.Key == Key.S)
        {
            Storage.SaveCal(_calMesh, _calBiasDots);
            _calDirty = false;
            Console.WriteLine("[cal] mesh saved");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (!_edOpen) return;

        if (e.Key == Key.R) { _edCh = 0; _edTyped = null; OverlayLayer.InvalidateVisual(); return; }
        if (e.Key == Key.G) { _edCh = 1; _edTyped = null; OverlayLayer.InvalidateVisual(); return; }
        if (e.Key == Key.B) { _edCh = 2; _edTyped = null; OverlayLayer.InvalidateVisual(); return; }

        if (e.Key == Key.L)
        {
            bool wasLocked = _locked.Contains(_edSel);
            HistPushLock(_edSel, wasLocked, !wasLocked);
            if (wasLocked) _locked.Remove(_edSel);
            else _locked.Add(_edSel);
            Storage.SaveLocks(_locked);
            OverlayLayer.InvalidateVisual(); return;
        }

        if (e.Key == Key.S)
        {
            Storage.SaveOverrides(_overrides);
            Storage.SaveLocks(_locked);
            RebuildLut(); ApplyLut(); return;
        }

        if (e.Key == Key.Delete)
        {
            if (!_locked.Contains(_edSel) && _overrides.TryGetValue(_edSel, out var old))
            {
                _overrides.Remove(_edSel);
                HistPush(_edSel, old, null);
                RebuildLut(); ApplyLut();
            }
            _edTyped = null; OverlayLayer.InvalidateVisual(); return;
        }

        if (e.Key == Key.Back)
        {
            if (_edTyped?.Length > 0)
                _edTyped = _edTyped.Length == 1 ? null : _edTyped[..^1];
            OverlayLayer.InvalidateVisual(); return;
        }

        if (e.Key == Key.Return)
        {
            if (_edTyped != null && !_locked.Contains(_edSel) &&
                int.TryParse(_edTyped, out int v))
                SetChannel(_edCh, v);
            _edTyped = null; OverlayLayer.InvalidateVisual(); return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            int delta = e.Key == Key.Up ? 1 : -1;
            if (_shiftHeld) delta *= 10;
            DeltaChannel(delta);
            _edTyped = null; OverlayLayer.InvalidateVisual(); return;
        }

        char c2 = e.Key switch
        {
            >= Key.D0 and <= Key.D9   => (char)('0' + (e.Key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
            _ => '\0'
        };
        if (c2 != '\0')
        {
            var typed = (_edTyped ?? "") + c2;
            _edTyped  = typed.Length > 3 ? typed[^3..] : typed;
            OverlayLayer.InvalidateVisual();
        }
    }

    void OnKeyUp(object s, KeyEventArgs e)
    {
        _shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!_edOpen && _kbdCapture && _kbdSendEnabled)
        {
            // Releasing Shift while a key-repeat is active would leave the repeat running
            // without Shift on the Kronos — e.g. '>' (Shift+OemPeriod) degrades to '.'
            // each tick once Shift is released.  Stop immediately to keep states in sync.
            if (e.Key is Key.LeftShift or Key.RightShift && _repeatCode != 0)
                StopRepeat();

            // Numpad keys that route to physical BUTTON commands on key-down never send a
            // KEY command, so there is no KEY-up to emit.  Suppress here to prevent an
            // orphaned KEY-up if someone has added a raw mapping for these keys.
            if (e.Key is Key.NumPad0 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4 or
                         Key.NumPad5 or Key.NumPad6 or Key.NumPad7 or Key.NumPad8 or Key.NumPad9 or
                         Key.Subtract or Key.Decimal)
                return;

            if (_instantKeys.Remove(e.Key)) { e.Handled = true; return; }
            var rawMapUp = RawKeyMap.Get(e.Key, _shiftHeld) ?? RawKeyMap.Get(e.Key, !_shiftHeld);
            if (rawMapUp != null)
            {
                if (_repeatCode == rawMapUp.RawCode) StopRepeat();
                if (rawMapUp.RawShift) Ctrl("KEY 42 0");
                Ctrl($"KEY {rawMapUp.RawCode} 0");
                e.Handled = true; return;
            }
            int? lkc = KeyMap.ToLinux(e.Key);
            if (lkc.HasValue)
            {
                if (_repeatCode == lkc.Value) StopRepeat();
                Ctrl($"KEY {lkc.Value} 0");
                if (_capsShiftedKeys.Remove(e.Key) && _capsShiftedKeys.Count == 0)
                    Ctrl("KEY 42 0");
                e.Handled = true;
            }
        }
    }

    void OnMouseWheel(object s, MouseWheelEventArgs e)
    {
        var pos  = Mouse.GetPosition(RootGrid);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Ctrl+scroll adjusts zoom level instead of sending wheel to Kronos
        if (ctrl && !_edOpen)
        {
            if (e.Delta > 0) DoZoomIn(); else DoZoomOut();
            return;
        }

        if (_edOpen && _panelRect.Contains(pos))
        {
            if (_locked.Contains(_edSel)) return;
            int delta = (e.Delta > 0 ? 1 : -1) * (_shiftHeld ? 10 : 1);
            DeltaChannel(delta);
            _edTyped = null;
            OverlayLayer.InvalidateVisual();
            return;
        }

        bool cw = e.Delta > 0;
        Ctrl(cw ? "WHEEL CW" : "WHEEL CCW");
        TriggerWheelAnim(cw ? 1 : -1);
    }

    void OnMouseMove(object s, MouseEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);

        if (_calMode)
        {
            _calHoverNode = FindNearestCalNode(pos);

            if (_calDraggingNode.HasValue)
            {
                var (col, row) = _calDraggingNode.Value;
                var clamped = new Point(
                    Math.Clamp(pos.X, 0, RootGrid.ActualWidth),
                    Math.Clamp(pos.Y, 0, RootGrid.ActualHeight));
                var (nx, ny)   = ScreenToKronosNode(clamped);
                _calMesh.SetOffset(col, row,
                    nx - _calMesh.NatX(col, _frameW),
                    ny - _calMesh.NatY(row, _frameH));
                _calDirty = true;
                OverlayLayer.InvalidateVisual();
                return;
            }
            // no active node drag — fall through to touch move logic
        }

        OverlayLayer.InvalidateVisual();

        if (_dragPending || _dragActive)
        {
            var (nx, ny) = ScreenToKronos(pos);
            var (cnx, cny) = ApplyCal(nx, ny);

            if (_dragPending)
            {
                int dist = Math.Abs(cnx - _dragPendingPos.x) + Math.Abs(cny - _dragPendingPos.y);
                if (dist >= DragStartThresh)
                {
                    _dragPending = false;
                    _dragActive  = true;
                    _dragLast    = (cnx, cny);
                }
            }
            if (_dragActive)
            {
                int dist = Math.Abs(cnx - _dragLast.x) + Math.Abs(cny - _dragLast.y);
                if (dist >= DragMoveThresh)
                {
                    _dragLast = (cnx, cny);
                    Ctrl($"TOUCH_MOVE {cnx} {cny}");
                    _touchMarker = (pos, DateTime.Now);
                }
            }
        }
    }

    void OnMouseDown(object s, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);

        // Clicking inside the frame display enables keyboard capture (keys → Kronos).
        // Clicking anywhere else (control panel, buttons, wheel) releases it so local
        // shortcuts like H (help) work without going through the menu.
        bool prevCapture = _kbdCapture;
        _kbdCapture = _frameRect.Contains(pos);
        if (!_kbdCapture && prevCapture)
        {
            _instantKeys.Clear();
            StopRepeat();
            if (_capsShiftedKeys.Count > 0) { Ctrl("KEY 42 0"); _capsShiftedKeys.Clear(); }
        }
        if (_kbdCapture != prevCapture) UpdateKbdStatus();

        OverlayLayer.InvalidateVisual();

        // Calibration mode: right-click → dot add/remove; left-click near node → drag it;
        // left-click with no nearby node → fall through to TOUCH_DOWN below
        if (_calMode && CalHitRect.Contains(pos))
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                int? dotIdx = FindNearestBiasDot(pos);
                if (dotIdx.HasValue)
                {
                    CalHistPush(new CalHistEntry(CalHistKind.DotRemoved,
                        DotIdx: dotIdx.Value, Dot: _calBiasDots[dotIdx.Value]));
                    _calBiasDots.RemoveAt(dotIdx.Value);
                    Console.WriteLine($"[cal] bias dot {dotIdx.Value} removed");
                }
                else
                {
                    // Store InverseApply(click) so Apply(stored) == click position now,
                    // and the dot moves naturally with any subsequent mesh changes.
                    var (nx, ny) = ScreenToKronos(pos);
                    var (sx, sy) = _calMesh.InverseApply(nx, ny, _frameW, _frameH);
                    var dot = new CalBiasDot(sx, sy);
                    _calBiasDots.Add(dot);
                    CalHistPush(new CalHistEntry(CalHistKind.DotAdded,
                        DotIdx: _calBiasDots.Count - 1, Dot: dot));
                    Console.WriteLine($"[cal] bias dot → ({nx}, {ny}) stored as ({sx}, {sy})");
                }
                Storage.SaveCal(_calMesh, _calBiasDots);
                OverlayLayer.InvalidateVisual();
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                var node = FindNearestCalNode(pos);
                if (node.HasValue)
                {
                    var (col, row) = node.Value;
                    _calDragStartOffset = _calMesh.GetOffset(col, row);
                    _calDraggingNode = node;
                    return;
                }
                // no nearby node — fall through to TOUCH_DOWN below
            }
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (_edOpen && _panelRect.Contains(pos))
            {
                var sw = OverlayRenderer.SwatchAt(pos, _gridOrigin);
                if (sw.HasValue)
                {
                    _edSel = sw.Value; _edTyped = null;
                }
                else
                {
                    var hit = OverlayRenderer.SliderHit(pos, _panelRect.X, _sliderTop);
                    if (hit.HasValue)
                    {
                        _edCh = hit.Value.ch;
                        SetChannel(hit.Value.ch, hit.Value.val);
                        _edTyped = null;
                    }
                }
                OverlayLayer.InvalidateVisual(); return;
            }

            if (_edOpen)
            {
                var idx = RawIdxAt(pos);
                if (idx.HasValue) { _edSel = idx.Value; _edTyped = null; }
                OverlayLayer.InvalidateVisual(); return;
            }

            if (_frameRect.Contains(pos))
            {
                var (nx, ny) = ScreenToKronos(pos);
                var (cnx, cny) = ApplyCal(nx, ny);
                _dragPendingPos = (cnx, cny);
                _dragLast       = (cnx, cny);  // valid fallback for leave/capture-loss
                _dragPending    = true;
                Ctrl($"TOUCH_DOWN {cnx} {cny}");  // send immediately, not deferred to first move
                _touchMarker = (pos, DateTime.Now);
                FrameImage.CaptureMouse();
                OverlayLayer.InvalidateVisual();
            }
        }
    }

    void OnMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pos = e.GetPosition(RootGrid);

        if (_calDraggingNode.HasValue)
        {
            var (col, row) = _calDraggingNode.Value;
            var (newOffX, newOffY) = _calMesh.GetOffset(col, row);
            var (oldOffX, oldOffY) = _calDragStartOffset;
            if (oldOffX != newOffX || oldOffY != newOffY)
                CalHistPush(new CalHistEntry(CalHistKind.NodeMove,
                    col, row, oldOffX, oldOffY, newOffX, newOffY));
            _calDraggingNode = null;
            OverlayLayer.InvalidateVisual();
            return;
        }

        if (_dragPending)
        {
            _dragPending = false;
            FrameImage.ReleaseMouseCapture();
            Ctrl($"TOUCH_UP {_dragPendingPos.x} {_dragPendingPos.y}");
            _touchMarker = (pos, DateTime.Now);
            OverlayLayer.InvalidateVisual();
        }
        else if (_dragActive)
        {
            var (nx, ny) = ScreenToKronos(pos);
            var (cnx, cny) = ApplyCal(nx, ny);
            _dragActive = false;
            FrameImage.ReleaseMouseCapture();
            Ctrl($"TOUCH_UP {cnx} {cny}");
            _touchMarker = (pos, DateTime.Now);
            OverlayLayer.InvalidateVisual();
        }
    }

    void OnMouseLeave(object s, MouseEventArgs e)
    {
        // Release any in-progress touch drag when the cursor exits the window so the
        // Kronos doesn't get stuck in a "touch held" state when there's no mouse-up.
        if (_calDraggingNode.HasValue)
        {
            _calDraggingNode = null;
            OverlayLayer.InvalidateVisual();
        }

        if (_dragActive)
            Console.WriteLine($"[touch] drag ended by mouse-leave at ({_dragLast.x}, {_dragLast.y})");
        CancelDrag();
    }

    // ── Key repeat ────────────────────────────────────────────────────────────

    static readonly HashSet<Key> RepeatableKeys = new()
    {
        Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H, Key.I, Key.J,
        Key.K, Key.L, Key.M, Key.N, Key.O, Key.P, Key.Q, Key.R, Key.S, Key.T,
        Key.U, Key.V, Key.W, Key.X, Key.Y, Key.Z,
        Key.D0, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9,
        Key.Back, Key.Delete, Key.Space, Key.Tab, Key.Return,
        Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.Prior, Key.Next,
        Key.OemMinus, Key.OemPlus, Key.OemComma, Key.OemPeriod, Key.OemQuestion,
        Key.OemOpenBrackets, Key.Oem6, Key.Oem5, Key.OemSemicolon, Key.OemQuotes, Key.OemTilde,
    };

    void StartRepeat(int linuxCode)
    {
        _repeatCode  = linuxCode;
        _repeatPhase = false;
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(400);
        _repeatTimer.Start();
    }

    void StopRepeat()
    {
        _repeatTimer.Stop();
        _repeatCode = 0;
    }

    void OnRepeatTick(object? s, EventArgs e)
    {
        if (!_repeatPhase)
        {
            _repeatPhase = true;
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(40);
        }
        if (_repeatCode == 0 || !_kbdCapture || !_kbdSendEnabled) { StopRepeat(); return; }
        Ctrl($"KEY {_repeatCode} 1");
        Ctrl($"KEY {_repeatCode} 0");
    }

    void OnFrameLostMouseCapture(object s, MouseEventArgs e)
    {
        // Fires when capture is released explicitly (no-op — state already cleared)
        // or implicitly (e.g., alt+tab, window deactivation) — clean up any stuck drag.
        CancelDrag();
    }

    void CancelDrag()
    {
        if (_dragActive)
        {
            _dragActive  = false;
            Ctrl($"TOUCH_UP {_dragLast.x} {_dragLast.y}");
            _touchMarker = null;
            OverlayLayer.InvalidateVisual();
        }
        else if (_dragPending)
        {
            _dragPending = false;
            Ctrl($"TOUCH_UP {_dragPendingPos.x} {_dragPendingPos.y}");
            _touchMarker = null;
            OverlayLayer.InvalidateVisual();
        }
    }

    // ── Built-in macros ──────────────────────────────────────────────────────
    // Resolve a key's Linux code the same way live dispatch does:
    // raw map first, then KeyMap, then a hardcoded fallback.
    static int ResolveCode(Key k, int fallback)
        => RawKeyMap.Get(k, false)?.RawCode ?? KeyMap.ToLinux(k) ?? fallback;

    // Ctrl+A → End, Shift+Home  (selects all text in the focused Kronos text field)
    void MacroSelectAll()
    {
        int endCode  = ResolveCode(Key.End,  107);
        int homeCode = ResolveCode(Key.Home, 102);
        AppLog.Debug($"[macro] SelectAll → KEY {endCode} (End), Shift+KEY {homeCode} (Home)");
        Ctrl($"KEY {endCode} 1");
        Ctrl($"KEY {endCode} 0");
        Ctrl("KEY 42 1");
        Ctrl($"KEY {homeCode} 1");
        Ctrl($"KEY {homeCode} 0");
        Ctrl("KEY 42 0");
    }

    bool TryFireUserMacro(Keybind trigger)
    {
        var macro = _settings.Macros.FirstOrDefault(m =>
            m.Trigger.Key       == trigger.Key       &&
            m.Trigger.Modifiers == trigger.Modifiers &&
            m.Steps.Count       > 0);
        if (macro == null) return false;
        AppLog.Debug($"[macro] firing '{macro.Description}'");
        _ = RunUserMacroAsync(macro);
        return true;
    }

    async Task RunUserMacroAsync(MacroDefinition macro)
    {
        foreach (var step in macro.Steps)
        {
            Ctrl($"KEY {step.Code} {(step.Down ? 1 : 0)}");
            await Task.Delay(macro.StepDelayMs);
        }
        AppLog.Info($"[macro] '{macro.Description}' done ({macro.Steps.Count} steps, {macro.StepDelayMs}ms/step)");
    }

    // ── Clipboard paste to Kronos ─────────────────────────────────────────────

    void PasteClipboardToKronos()
    {
        if (!Clipboard.ContainsText()) return;
        var raw = Clipboard.GetText();

        var chars = new System.Collections.Generic.List<char>();
        int skipped = 0;
        foreach (char c in raw)
        {
            if (c == '\r' || c == '\n' || (c < 0x20 && c != '\t') || c >= 0x80)
                { skipped++; continue; }
            if (CharMap.GetCommands(c) == null) { skipped++; continue; }
            chars.Add(c);
        }

        if (chars.Count == 0)
        {
            AppLog.Info("[paste] nothing sendable after filtering" +
                        (skipped > 0 ? $" ({skipped} chars stripped)" : ""));
            return;
        }

        int charCount = chars.Count;
        int skipCount = skipped;
        AppLog.Info($"[paste] typing {charCount} chars via KEY" +
                    (skipCount > 0 ? $", {skipCount} stripped" : ""));

        _ = Task.Run(async () =>
        {
            foreach (char c in chars)
            {
                var cmds = CharMap.GetCommands(c);
                if (cmds == null) continue;
                foreach (var cmd in cmds)
                    Ctrl(cmd);
                await Task.Delay(50);
            }
            AppLog.Info($"[paste] {charCount} chars typed");
        });
    }

    void DoZoomIn()
    {
        _zoomLevel = Math.Min(10.0, Math.Round(_zoomLevel + 0.5, 1));
        _zoomOn = true;
        OverlayLayer.InvalidateVisual();
    }

    void DoZoomOut()
    {
        _zoomLevel = Math.Max(_settings.ZoomDefaultLevel, Math.Round(_zoomLevel - 0.5, 1));
        OverlayLayer.InvalidateVisual();
    }
}
