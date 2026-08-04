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
        _layoutPreset = _settings.LayoutPreset;
        ApplyLayoutPreset(_layoutPreset, saveSettings: false);

        // The XAML no longer hard-codes FrameImage's scaling filter - apply the saved one now.
        ApplyScalingMode();

        Topmost = _settings.AlwaysOnTop;

        // Restore saved window bounds after layout settles
        Dispatcher.InvokeAsync(() =>
        {
            // Pin the minimum width to the footer's natural width first, so a restored
            // (or later dragged) window can never be narrower than the status-bar icons.
            UpdateMinimumWindowWidth();

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

        // Screen gestures must use the preview route so a handled Image event cannot drop them.
        // Keep the window's normal mouse route for all other controls, especially menus.
        FrameImage.PreviewMouseLeftButtonDown += OnFramePreviewMouseDown;
        FrameImage.PreviewMouseLeftButtonUp += OnFramePreviewMouseUp;

        ApplyMidiMonitorMenuState();
        UpdateMidiLinkBadge();   // reflect the transport chosen during ctor startup
        Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Background);

        // Don't drag a USB-MIDI user through the daemon connect + FTP login on launch.
        // When USB is the chosen/active MIDI path, the SysEx features already work over
        // USB (started in the ctor, independent of the daemon); the screen stays opt-in
        // via an explicit Connect. Only auto-connect when TCP is the MIDI path.
        bool usbPath = _settings.MidiTransport == MidiTransportMode.Usb || _midiCoord.UsingUsb;
        if (usbPath)
        {
            AppLog.Info("[conn] USB MIDI active - skipping daemon auto-connect (screen available via Connect)");
            SetConnectionStatus(ConnState.Disconnected);   // render the USB-aware status line
        }
        else
        {
            BeginConnect();
        }
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
            string msg = IsConnected
                ? AppMessages.Quit.DisconnectAndQuit
                : AppMessages.Quit.QuitApp;
            if (MessageBox.Show(msg, AppMessages.Quit.Title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        AppLog.Info("[shutdown] main window closing");
        if (_cal.Dirty)
        {
            var result = MessageBox.Show(
                AppMessages.Calibration.UnsavedChanges,
                AppMessages.Calibration.UnsavedTitle,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (result == MessageBoxResult.Yes)
            {
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                _cal.Dirty = false;
            }
        }

        if (!_fs.Active)
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
        _screenSession?.Dispose();
        if (_ctrl != null && _ctrlErrorHandler != null) _ctrl.CtrlError -= _ctrlErrorHandler;
        _ctrlErrorHandler = null;
        (_ctrl as IDisposable)?.Dispose();
        _midiCoord?.Dispose();
        _sysExService?.Reset();
        CleanupAudio();
        _pingCts?.Cancel();
        _pingCts?.Dispose();
        if (_llKbHook != IntPtr.Zero) { UnhookWindowsHookEx(_llKbHook); _llKbHook = IntPtr.Zero; }
    }

    void OnKeyDown(object s, KeyEventArgs e)
    {
        bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && e.Key == Key.Z)
        {
            if (_cal.Mode) { if (_shiftHeld) CalHistRedo(); else CalHistUndo(); }
            OverlayLayer.InvalidateVisual(); return;
        }
        if (ctrl && e.Key == Key.Y)
        {
            if (_cal.Mode) CalHistRedo();
            OverlayLayer.InvalidateVisual(); return;
        }
        if (ctrl && e.Key == Key.K) { OpenCommandPalette(); e.Handled = true; return; }

        if (ctrl && e.Key == Key.V && _kbdCapture && _kbdSendEnabled)
        {
            PasteClipboardToKronos();
            e.Handled = true; return;
        }

        if (ctrl && e.Key == Key.D1) { SetWindowSize(0.75); return; }
        if (ctrl && e.Key == Key.D2) { SetWindowSize(1.0);  return; }
        if (ctrl && e.Key == Key.D3) { SetWindowSize(1.25); return; }
        if (ctrl && e.Key == Key.D4) { SetWindowSize(1.50); return; }
        if (ctrl && e.Key == Key.D5) { SetWindowSize(2.00); return; }
        if (ctrl && e.Key == Key.S && !_cal.Mode) { SaveScreenshot(); e.Handled = true; return; }

        // ── Fullscreen shortcuts (intercept before capture so they work even when forwarding) ──
        if (_fs.Active && !ctrl && !e.IsRepeat)
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
        if (_kbdCapture && _kbdSendEnabled && !ctrl && !e.IsRepeat && !_cal.Mode && e.Key == Key.Return && _extendedKey)
        {
            _instantKeys.Add(Key.Return);   // suppress KEY-up so KEY 28 0 is never sent to vkbd
            BTN_Enter.FlashDepress();
            Ctrl(DaemonCommand.Button(PanelButton.Enter));
            e.Handled = true; return;
        }

        // ── Macros (user-defined first, then built-ins) - requires modifier key ─
        if (!e.IsRepeat && _kbdCapture && _kbdSendEnabled && !_cal.Mode
            && Keyboard.Modifiers != ModifierKeys.None)
        {
            var baseKey = e.Key == Key.System ? e.SystemKey : e.Key;
            var trigger = new Keybind(baseKey, Keyboard.Modifiers);
            if (TryFireUserMacro(trigger)) { e.Handled = true; return; }
            if (ctrl && e.Key == Key.A)    { MacroSelectAll();  e.Handled = true; return; }
        }

        // ── Numpad 0–9 / − / · : always forward when capture active ──────────
        if (_kbdCapture && _kbdSendEnabled && !ctrl && !e.IsRepeat && !_cal.Mode)
        {
            int? numBtn = e.Key switch
            {
                Key.NumPad0 => 0, Key.NumPad1 => 1, Key.NumPad2 => 2, Key.NumPad3 => 3,
                Key.NumPad4 => 4, Key.NumPad5 => 5, Key.NumPad6 => 6, Key.NumPad7 => 7,
                Key.NumPad8 => 8, Key.NumPad9 => 9, _ => (int?)null
            };
            if (numBtn.HasValue) { NumButton(numBtn.Value)?.FlashDepress(); Ctrl(DaemonCommand.NumberButton(numBtn.Value)); e.Handled = true; return; }
            if (e.Key == Key.Subtract) { BTN_data_dash.FlashDepress();   Ctrl(DaemonCommand.Button(PanelButton.NumDash)); e.Handled = true; return; }
            if (e.Key == Key.Decimal)  { BTN_data_period.FlashDepress(); Ctrl(DaemonCommand.Button(PanelButton.NumDot));  e.Handled = true; return; }
        }

        // ── Keyboard capture: forward before any local shortcut ──────────────
        // F1–F12 fall through to the IsAction checks below (mode select, help, etc.).
        if (_kbdCapture && _kbdSendEnabled && !ctrl && !e.IsRepeat && !_cal.Mode
            && (e.Key < Key.F1 || e.Key > Key.F12))
        {
            // Shifted override: Kronos needs a different keycode or Shift handling
            var shifted = _shiftHeld ? KeyMap.ToLinuxShifted(e.Key) : null;
            if (shifted.HasValue)
            {
                AppLog.Debug($"[kbd] shifted key {e.Key} → linux {shifted.Value.Code} keepShift={shifted.Value.KeepShift}");
                if (!shifted.Value.KeepShift) Ctrl(DaemonCommand.Shift(false));   // drop Shift if not needed
                Ctrl(DaemonCommand.Key(shifted.Value.Code, true));
                Ctrl(DaemonCommand.Key(shifted.Value.Code, false));
                if (!shifted.Value.KeepShift) Ctrl(DaemonCommand.Shift(true));   // restore Shift
                _instantKeys.Add(e.Key);
                e.Handled = true; return;
            }

            var rawMap = RawKeyMap.Get(e.Key, _shiftHeld);
            if (rawMap != null)
            {
                AppLog.Debug($"[kbd] raw-map {rawMap.HostKeyDisplay} → KEY {rawMap.RawCode}");
                if (rawMap.RawShift) Ctrl(DaemonCommand.Shift(true));
                Ctrl(DaemonCommand.Key(rawMap.RawCode, true));
                _activeRawKeys[e.Key] = rawMap;   // release exactly this code on key-up
                StartRepeat(rawMap.RawCode);
                e.Handled = true; return;
            }

            int? lkc = KeyMap.ToLinux(e.Key);
            if (lkc.HasValue)
            {
                AppLog.Debug($"[kbd] {e.Key} → linux {lkc.Value}");
                // Eva text fields: unshifted letter → uppercase, Left-Shift+letter → lowercase.
                // (Eva only recognises KEY_LEFTSHIFT/42 as a case modifier; KEY_RIGHTSHIFT/54 is
                //  ignored by Eva, so Right Shift naturally gives uppercase - useful distinction.)
                // CapsLock: inject Left Shift so letters are lowercase without holding Shift.
                if (e.Key is >= Key.A and <= Key.Z
                    && Keyboard.GetKeyStates(Key.CapsLock).HasFlag(KeyStates.Toggled)
                    && !_shiftHeld)
                {
                    if (_capsShiftedKeys.Count == 0) Ctrl(DaemonCommand.Shift(true));
                    _capsShiftedKeys.Add(e.Key);
                }
                Ctrl(DaemonCommand.Key(lkc.Value, true));
                if (RepeatableKeys.Contains(e.Key))
                    StartRepeat(lkc.Value);
                e.Handled = true; return;
            }
        }

        if (IsAction("Quit", e))  { TryQuit(); return; }

        if (e.Key == Key.Escape)
        {
            if (_fs.Active)               { ToggleFullscreen(); return; }
            if (_drag.Pending || _drag.Active)
            {
                var cancelPos = _drag.Active ? _drag.Last : _drag.PendingPos;
                _drag.Pending = false; _drag.Active = false;
                FrameImage.ReleaseMouseCapture();
                Ctrl(DaemonCommand.TouchUp(cancelPos.x, cancelPos.y));
                OverlayLayer.InvalidateVisual(); return;
            }
            if (_zoomOn)                     { _zoomOn = false; OverlayLayer.InvalidateVisual(); return; }
            Ctrl(DaemonCommand.Button(PanelButton.Exit)); return;
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
            Ctrl(DaemonCommand.VgaMirror(_mirrorState)); return;
        }

        if (IsAction("Fullscreen", e))   { ToggleFullscreen(); return; }

        if (IsAction("HideDataInput",  e)) { ToggleHideDataInput();  return; }
        if (IsAction("HideValueInput", e)) { ToggleHideValueInput(); return; }

        if (e.Key == Key.Return)
        {
            Ctrl(DaemonCommand.Button(PanelButton.Enter)); return;
        }

        // Mode select (rebindable; default F2–F8; F1 reserved for Help above). Action defined
        // once in the command registry (BuildCommandRegistry); the keybind just routes to it by Id.
        foreach (var modeId in ModeCommandIds)
            if (IsAction(modeId, e)) { RunCommand(modeId); return; }

        // Bank select (unassigned by default; rebindable in Settings) - same "Bank ..." registry Ids.
        foreach (char b in new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'G' })
        {
            if (IsAction($"Bank I-{b}",    e)) { RunCommand($"Bank I-{b}");    return; }
            if (IsAction($"Bank U-{b}",    e)) { RunCommand($"Bank U-{b}");    return; }
            if (IsAction($"Bank U-{b}{b}", e)) { RunCommand($"Bank U-{b}{b}"); return; }
        }

        // Sequencer transport (unassigned by default; rebindable in Settings) - gated the
        // same way as the footer buttons' IsEnabled, so a shortcut can't do anything the
        // greyed-out button itself couldn't. Falls through (not "handled") when the current
        // mode doesn't support it, same as any other unmatched key. The gate stays HERE; the
        // registry only owns the action, not when the keybind is allowed to fire it.
        if (_seqTransport.IsTransportEnabled)
        {
            if (IsAction("Seq Locate",  e)) { RunCommand("Seq Locate");  return; }
            if (IsAction("Seq Rewind",  e)) { RunCommand("Seq Rewind");  return; }
            if (IsAction("Seq Forward", e)) { RunCommand("Seq Forward"); return; }
            if (IsAction("Seq Pause",   e)) { RunCommand("Seq Pause");   return; }
            if (IsAction("Seq Record",  e)) { RunCommand("Seq Record");  return; }
            if (IsAction("Seq Start",   e)) { RunCommand("Seq Start");   return; }
        }
        if (_seqTransport.IsSaveEnabled && IsAction("Seq Save", e)) { RunCommand("Seq Save"); return; }

        // Tap tempo (global - not seq-mode gated). Ignore auto-repeat so a HELD key can't
        // spam phantom taps; real tap tempo needs discrete presses. Connection-gated to
        // match the footer button (front-panel injection needs the daemon ctrl channel).
        if (IsConnected && !e.IsRepeat && IsAction("Tap Tempo", e)) { RunCommand("Tap Tempo"); return; }

        if (IsAction("Calibrate", e))
        {
            _cal.Mode = !_cal.Mode;
            if (_cal.Mode) EnterCalMode(); else ExitCalMode();
            Console.WriteLine($"[cal] calibrate mode {(_cal.Mode ? "ON" : "OFF")}");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_cal.Mode && e.Key == Key.R)
        {
            _cal.Mesh.Reset();
            _cal.Dirty = true;
            _cal.History.Clear(); _cal.HistPos = -1;
            Console.WriteLine("[cal] mesh reset to identity (unsaved)");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_cal.Mode && e.Key == Key.X)
        {
            _cal.BiasDots.Clear();
            _cal.History.Clear(); _cal.HistPos = -1;
            Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
            Console.WriteLine("[cal] bias dots cleared");
            OverlayLayer.InvalidateVisual(); return;
        }

        if (_cal.Mode && e.Key == Key.S)
        {
            Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
            _cal.Dirty = false;
            Console.WriteLine("[cal] mesh saved");
            OverlayLayer.InvalidateVisual(); return;
        }

    }

    void OnKeyUp(object s, KeyEventArgs e)
    {
        _shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_kbdCapture && _kbdSendEnabled)
        {
            // Releasing Shift while a key-repeat is active would leave the repeat running
            // without Shift on the Kronos - e.g. '>' (Shift+OemPeriod) degrades to '.'
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
            // Release the exact code recorded at key-down - NOT a re-resolution by the current
            // Shift state, which diverges if Shift was toggled while the key was held.
            if (_activeRawKeys.Remove(e.Key, out var sentRaw))
            {
                if (_repeatCode == sentRaw.RawCode) StopRepeat();
                Ctrl(DaemonCommand.Key(sentRaw.RawCode, false));
                if (sentRaw.RawShift) Ctrl(DaemonCommand.Shift(false));
                e.Handled = true; return;
            }
            int? lkc = KeyMap.ToLinux(e.Key);
            if (lkc.HasValue)
            {
                if (_repeatCode == lkc.Value) StopRepeat();
                Ctrl(DaemonCommand.Key(lkc.Value, false));
                if (_capsShiftedKeys.Remove(e.Key) && _capsShiftedKeys.Count == 0)
                    Ctrl(DaemonCommand.Shift(false));
                e.Handled = true;
            }
        }
    }

    // Release any keys sent via the raw keymap that are still held down.  Key-up normally
    // drains _activeRawKeys one key at a time, but on capture loss (focus lost, click-out,
    // keyboard-send disabled) the key-up never reaches us, so a held key would stick on the
    // Kronos.  Mirror the key-up release path (KEY <code> 0, plus KEY 42 0 if it used Shift).
    void ReleaseActiveRawKeys()
    {
        if (_activeRawKeys.Count == 0) return;
        bool releasedShift = false;
        foreach (var raw in _activeRawKeys.Values)
        {
            Ctrl(DaemonCommand.Key(raw.RawCode, false));
            if (raw.RawShift) releasedShift = true;
        }
        _activeRawKeys.Clear();
        if (releasedShift) Ctrl(DaemonCommand.Shift(false));
    }

    void OnMouseWheel(object s, MouseWheelEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Ctrl+scroll adjusts zoom level instead of sending wheel to Kronos
        if (ctrl)
        {
            if (e.Delta > 0) DoZoomIn(); else DoZoomOut();
            return;
        }

        bool cw = e.Delta > 0;
        Ctrl(DaemonCommand.Wheel(cw));
        TriggerWheelAnim(cw ? 1 : -1);
    }

    void OnMouseMove(object s, MouseEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);

        if (_cal.Mode)
        {
            _cal.HoverNode = FindNearestCalNode(pos);

            if (_cal.DraggingNode.HasValue)
            {
                var (col, row) = _cal.DraggingNode.Value;
                var clamped = new Point(
                    Math.Clamp(pos.X, 0, RootGrid.ActualWidth),
                    Math.Clamp(pos.Y, 0, RootGrid.ActualHeight));
                var (nx, ny)   = ScreenToKronosNode(clamped);
                _cal.Mesh.SetOffset(col, row,
                    nx - _cal.Mesh.NatX(col, _frameW),
                    ny - _cal.Mesh.NatY(row, _frameH));
                _cal.Dirty = true;
                OverlayLayer.InvalidateVisual();
                return;
            }
            // no active node drag - fall through to touch move logic
        }

        OverlayLayer.InvalidateVisual();

        if (_drag.Pending || _drag.Active)
        {
            var (nx, ny) = ScreenToKronos(pos);
            var (cnx, cny) = ApplyCal(nx, ny);

            if (_drag.Pending)
            {
                int dist = Math.Abs(cnx - _drag.PendingPos.x) + Math.Abs(cny - _drag.PendingPos.y);
                if (dist >= DragState.StartThresh)
                {
                    _drag.Pending = false;
                    _drag.Active  = true;
                    _drag.Last    = (cnx, cny);
                }
            }
            if (_drag.Active)
            {
                int dist = Math.Abs(cnx - _drag.Last.x) + Math.Abs(cny - _drag.Last.y);
                if (dist >= DragState.MoveThresh)
                {
                    _drag.Last = (cnx, cny);
                    Ctrl(DaemonCommand.TouchMove(cnx, cny));
                    _drag.Marker = (pos, DateTime.Now);
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
            ReleaseActiveRawKeys();
            if (_capsShiftedKeys.Count > 0) { Ctrl(DaemonCommand.Shift(false)); _capsShiftedKeys.Clear(); }
        }
        if (_kbdCapture != prevCapture) UpdateKbdStatus();

        OverlayLayer.InvalidateVisual();

        // Calibration mode: right-click → dot add/remove; left-click near node → drag it;
        // left-click with no nearby node → fall through to TOUCH_DOWN below
        if (_cal.Mode && CalHitRect.Contains(pos))
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                int? dotIdx = FindNearestBiasDot(pos);
                if (dotIdx.HasValue)
                {
                    CalHistPush(new CalHistEntry(CalHistKind.DotRemoved,
                        DotIdx: dotIdx.Value, Dot: _cal.BiasDots[dotIdx.Value]));
                    _cal.BiasDots.RemoveAt(dotIdx.Value);
                    Console.WriteLine($"[cal] bias dot {dotIdx.Value} removed");
                }
                else
                {
                    // Store InverseApply(click) so Apply(stored) == click position now,
                    // and the dot moves naturally with any subsequent mesh changes.
                    var (nx, ny) = ScreenToKronos(pos);
                    var (sx, sy) = _cal.Mesh.InverseApply(nx, ny, _frameW, _frameH);
                    var dot = new CalBiasDot(sx, sy);
                    _cal.BiasDots.Add(dot);
                    CalHistPush(new CalHistEntry(CalHistKind.DotAdded,
                        DotIdx: _cal.BiasDots.Count - 1, Dot: dot));
                    Console.WriteLine($"[cal] bias dot → ({nx}, {ny}) stored as ({sx}, {sy})");
                }
                Storage.SaveCal(_cal.Mesh, _cal.BiasDots);
                OverlayLayer.InvalidateVisual();
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                var node = FindNearestCalNode(pos);
                if (node.HasValue)
                {
                    var (col, row) = node.Value;
                    _cal.DragStartOffset = _cal.Mesh.GetOffset(col, row);
                    _cal.DraggingNode = node;
                    return;
                }
                // no nearby node - fall through to TOUCH_DOWN below
            }
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if (_frameRect.Contains(pos))
            {
                var (nx, ny) = ScreenToKronos(pos);
                var (cnx, cny) = ApplyCal(nx, ny);
                _drag.PendingPos = (cnx, cny);
                _drag.Last       = (cnx, cny);  // valid fallback for leave/capture-loss
                _drag.Pending    = true;
                Ctrl(DaemonCommand.TouchDown(cnx, cny));  // send immediately, not deferred to first move
                _drag.Marker = (pos, DateTime.Now);
                FrameImage.CaptureMouse();
                OverlayLayer.InvalidateVisual();
            }
        }
    }

    void OnFramePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Suppress the second touch in a fullscreen double-click before starting a gesture.
        if (e.ClickCount == 2 && _fs.Active)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        OnMouseDown(sender, e);
        e.Handled = true;
    }

    void OnFramePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        OnMouseUp(sender, e);
        e.Handled = true;
    }

    void OnMouseUp(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pos = e.GetPosition(RootGrid);

        if (_cal.DraggingNode.HasValue)
        {
            var (col, row) = _cal.DraggingNode.Value;
            var (newOffX, newOffY) = _cal.Mesh.GetOffset(col, row);
            var (oldOffX, oldOffY) = _cal.DragStartOffset;
            if (oldOffX != newOffX || oldOffY != newOffY)
                CalHistPush(new CalHistEntry(CalHistKind.NodeMove,
                    col, row, oldOffX, oldOffY, newOffX, newOffY));
            _cal.DraggingNode = null;
            OverlayLayer.InvalidateVisual();
            return;
        }

        if (_drag.Pending)
        {
            _drag.Pending = false;
            FrameImage.ReleaseMouseCapture();
            Ctrl(DaemonCommand.TouchUp(_drag.PendingPos.x, _drag.PendingPos.y));
            _drag.Marker = (pos, DateTime.Now);
            OverlayLayer.InvalidateVisual();
        }
        else if (_drag.Active)
        {
            var (nx, ny) = ScreenToKronos(pos);
            var (cnx, cny) = ApplyCal(nx, ny);
            _drag.Active = false;
            FrameImage.ReleaseMouseCapture();
            Ctrl(DaemonCommand.TouchUp(cnx, cny));
            _drag.Marker = (pos, DateTime.Now);
            OverlayLayer.InvalidateVisual();
        }
    }

    void OnMouseLeave(object s, MouseEventArgs e)
    {
        // Release any in-progress touch drag when the cursor exits the window so the
        // Kronos doesn't get stuck in a "touch held" state when there's no mouse-up.
        if (_cal.DraggingNode.HasValue)
        {
            _cal.DraggingNode = null;
            OverlayLayer.InvalidateVisual();
        }

        if (_drag.Active)
            Console.WriteLine($"[touch] drag ended by mouse-leave at ({_drag.Last.x}, {_drag.Last.y})");
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
        Ctrl(DaemonCommand.Key(_repeatCode, true));
        Ctrl(DaemonCommand.Key(_repeatCode, false));
    }

    void OnFrameLostMouseCapture(object s, MouseEventArgs e)
    {
        // Fires when capture is released explicitly (no-op - state already cleared)
        // or implicitly (e.g., alt+tab, window deactivation) - clean up any stuck drag.
        CancelDrag();
    }

    void CancelDrag()
    {
        if (_drag.Active)
        {
            _drag.Active  = false;
            Ctrl(DaemonCommand.TouchUp(_drag.Last.x, _drag.Last.y));
            _drag.Marker = null;
            OverlayLayer.InvalidateVisual();
        }
        else if (_drag.Pending)
        {
            _drag.Pending = false;
            Ctrl(DaemonCommand.TouchUp(_drag.PendingPos.x, _drag.PendingPos.y));
            _drag.Marker = null;
            OverlayLayer.InvalidateVisual();
        }
        // Mirror the mouse-up paths, which always release. Reaching here from
        // OnFrameLostMouseCapture is a no-op (capture is already gone); reaching here from
        // OnMouseLeave is not, and would otherwise leave FrameImage holding capture.
        if (FrameImage.IsMouseCaptured) FrameImage.ReleaseMouseCapture();
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
        Ctrl(DaemonCommand.Key(endCode, true));
        Ctrl(DaemonCommand.Key(endCode, false));
        Ctrl(DaemonCommand.Shift(true));
        Ctrl(DaemonCommand.Key(homeCode, true));
        Ctrl(DaemonCommand.Key(homeCode, false));
        Ctrl(DaemonCommand.Shift(false));
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
            Ctrl(DaemonCommand.Key(step.Code, step.Down));
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
