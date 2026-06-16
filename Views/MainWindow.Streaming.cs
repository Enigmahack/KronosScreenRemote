using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    bool   _ftpAuthenticated     = false;
    string _ftpAuthenticatedHost = "";

    void TriggerReconnect()
    {
        ResetBootState();
        _receiver?.Dispose();
        _receiver = null;
        _ctrl.Reset();  // drop persistent ctrl socket; new one will be created on next Send
        SetConnectionStatus(ConnState.Connecting);  // immediate UI-thread feedback
        _ = Task.Run(ConnectAsync);
    }

    async Task ConnectAsync()
    {
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            SetConnectionStatus(ConnState.Connecting);  // already running — reconfirm visually
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(_host))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateTitle("Not Connected");
                    MessageBox.Show(
                        "No Kronos IP address is configured.\n\nGo to Settings and enter the Kronos IP address.",
                        "Connection", MessageBoxButton.OK, MessageBoxImage.Information);
                    OpenSettingsDialog();
                });
                return;
            }

            if (!await EnsureFtpLoginAsync())
            {
                SetConnectionStatus(ConnState.Disconnected);
                await Dispatcher.InvokeAsync(() => UpdateTitle("Not Connected")).Task.ConfigureAwait(false);
                return;
            }

            AppLog.Info($"[conn] connecting to {_host}:{_port} mode={(_pullMode ? "pull" : "change")} fps={_fps}");
            SetConnectionStatus(ConnState.Connecting);
            // ConfigureAwait(false) keeps subsequent code on the thread-pool thread.
            // Without it, DispatcherOperation.GetAwaiter() resumes on the UI thread,
            // capturing DispatcherSynchronizationContext for all downstream awaits —
            // including the Task.WhenAny watchdog in StreamReceiver — which then can't
            // fire until the Dispatcher is idle, breaking the 10-second timeout.
            await Dispatcher.InvokeAsync(() =>
                UpdateTitle($"Connecting to {_host}…")).Task.ConfigureAwait(false);
            try
            {
                _receiver = new StreamReceiver(_host, _port, _pullMode, _fps,
                                              _settings.FtpUsername, _settings.FtpPassword);
                _receiver.FrameReceived  += OnFrameReceived;
                _receiver.Disconnected   += OnDisconnected;
                await _receiver.ConnectAsync();

                _frameW  = _receiver.Width;
                _frameH  = _receiver.Height;
                _basePal = _receiver.Palette;
                RebuildLut();

                AppLog.Info($"[conn] connected — {_frameW}×{_frameH} {(_pullMode ? "pull" : "change-driven")} cap={_fps}fps");
                SetConnectionStatus(ConnState.Connected);
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateTitle(_host);
                    AddRecentHost(_host);
                    _wb   = new WriteableBitmap(_frameW, _frameH, 96, 96,
                                                PixelFormats.Bgr32, null);
                    FrameImage.Source = _wb;
                });
                await Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Background);

                // Push saved VGA mirror + screensaver settings to the daemon on every connect
                _mirrorState = _settings.VgaMirrorEnabled;
                Ctrl(_mirrorState ? "MIRROR_ON" : "MIRROR_OFF");
                Ctrl($"SS_TIMEOUT {_settings.ScreensaverTimeout}");

                _modePollCts?.Cancel();
                _modePollCts = new CancellationTokenSource();
                TopLeftOcr.Reset();   // ensure first frame fires an immediate STATE query
                _ = ModePollLoop(_modePollCts.Token);
            }
            catch (UnauthorizedAccessException ex)
            {
                _ftpAuthenticated = false;
                await ShowConnectError(
                    $"[conn] auth rejected: {ex.Message}",
                    "Authentication Failed",
                    "Authentication Failed",
                    "The Kronos daemon rejected the FTP credentials.\n\nClick Reconnect to try again.");
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                await ShowConnectError(
                    $"[conn] timeout: {ex.Message}",
                    "Connection Timed Out",
                    "Connection Timed Out",
                    ex.Message);
            }
            catch (Exception ex)
            {
                await ShowConnectError(
                    $"[conn] failed: {ex.GetType().Name}: {ex.Message}",
                    "Connection Failed",
                    "Kronos ScreenRemote",
                    $"Connection failed:\n{ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    async Task<bool> EnsureFtpLoginAsync()
    {
        // Already authenticated for this host — skip on auto-reconnects and stream drops.
        if (_ftpAuthenticated && _ftpAuthenticatedHost == _host)
            return true;

        _ftpAuthenticated = false;

        // Silent verify with cached credentials — if they work, skip the dialog entirely.
        if (!string.IsNullOrEmpty(_settings.FtpUsername))
        {
            var (silentOk, _) = await KronosFtpSession.VerifyAsync(
                _host, _settings.FtpPort, _settings.FtpUsername, _settings.FtpPassword)
                .ConfigureAwait(false);
            if (silentOk)
            {
                _ftpAuthenticated     = true;
                _ftpAuthenticatedHost = _host;
                return true;
            }
        }

        // Prompt — up to 3 interactive attempts regardless of silent verify outcome.
        bool dialogOk  = false;
        bool exhausted = false;
        await Dispatcher.InvokeAsync(() =>
        {
            var dlg = new LoginDialog(_host, _settings.FtpPort,
                                      _settings.FtpUsername, _settings.FtpPassword,
                                      attemptsAllowed: 3)
                      { Owner = this };
            dialogOk  = dlg.ShowDialog() == true;
            exhausted = dlg.ExhaustedAttempts;
            if (dialogOk)
            {
                _settings.FtpUsername = dlg.Username;
                _settings.FtpPassword = dlg.Password;
                if (dlg.SavePassword) Storage.SaveSettings(_settings);
            }
        }).Task.ConfigureAwait(false);

        if (dialogOk)
        {
            _ftpAuthenticated     = true;
            _ftpAuthenticatedHost = _host;
            return true;
        }

        if (exhausted)
        {
            await Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    "FTP authentication failed after 3 attempts.\nClick Reconnect to try again.",
                    "Authentication Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error))
                .Task.ConfigureAwait(false);
        }

        return false;
    }

    async Task ShowConnectError(string logMsg, string titleSuffix, string dialogTitle, string dialogMsg)
    {
        AppLog.Error(logMsg);
        SetConnectionStatus(ConnState.Disconnected);
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateTitle(titleSuffix);
            MessageBox.Show(dialogMsg, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    void OnFrameReceived(byte[] data) => _frameQ.Enqueue(data);

    void OnDisconnected()
    {
        AppLog.Info("[conn] disconnected");
        _modePollCts?.Cancel();
        TopLeftOcr.Reset();
        _ctrl.Reset();
        SetConnectionStatus(ConnState.Disconnected);
        Dispatcher.InvokeAsync(() =>
        {
            ResetBootState();
            _helpActive       = false;
            BTN_Help.IsActive = false;
            ModeText.Text     = "";
            UpdateTitle("Connection Lost");
        });
    }

    async Task ModePollLoop(CancellationToken ct)
    {
        // STATE polling is only useful when no reference PNGs are loaded.
        // When ModeDetector.HasAny() is true, framebuffer detection handles all
        // mode updates; letting the STATE response override them causes reversion
        // to whatever mode the server last saw from a client BUTTON command.
        while (!ct.IsCancellationRequested)
        {
            if (!ModeDetector.HasAny())
            {
                var resp = await _ctrl.QueryAsync("STATE");
                if (resp != null && resp.StartsWith("MODE=", StringComparison.Ordinal) &&
                    int.TryParse(resp[5..], out int mode) && mode > 0 &&
                    (DateTime.Now - _lastUserModeChange).TotalSeconds > 1.5)
                    await Dispatcher.InvokeAsync(() => SetModeButton(mode));
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task QueryModeAsync()
    {
        var resp = await _ctrl.QueryAsync("STATE");
        if (resp != null && resp.StartsWith("MODE=", StringComparison.Ordinal) &&
            int.TryParse(resp[5..], out int mode))
            await Dispatcher.InvokeAsync(() => SetModeButton(mode));
    }

    void SetModeButton(int mode)
    {
        if (mode > 0)
        {
            if (mode != _currentMode) _prevMode = _currentMode;
            _currentMode = mode;
            _pendingMode = 0;  // detection is authoritative — clear pending
            if (!_detectedModeEver)
            {
                _detectedModeEver = true;
                _bootPhase = false;   // dismiss overlay immediately — no fade
            }
        }

        var btn = mode switch
        {
            1 => BTN_Setlist,
            2 => BTN_Combi,
            3 => BTN_Program,
            4 => BTN_Sequence,
            5 => BTN_Sampling,
            6 => BTN_Global,
            7 => BTN_Disk,
            _ => (KronosButton?)null
        };

        if (btn != null && !_combiProgramEditActive)
            btn.Activate();
        // mode=0 (server doesn't know yet) — leave current state rather than blanking

        var modeName = mode switch
        {
            1 => "Setlist", 2 => "Combi",    3 => "Program",
            4 => "Sequence",5 => "Sampling", 6 => "Global",
            7 => "Disk",    _ => ""
        };
        if (modeName.Length > 0)
        {
            AppLog.Debug($"[mode] {modeName}");
            ModeText.Text = $"Mode: {modeName}";
            _controlPaletteWin?.SetMode(mode);
        }
    }

    void ClearModeButtons()
    {
        foreach (var btn in new[] { BTN_Setlist, BTN_Combi, BTN_Program,
                                     BTN_Sequence, BTN_Sampling, BTN_Global, BTN_Disk })
            btn.IsActive = false;
        ModeText.Text = "";
    }

    void EnterCombiProgramEdit()
    {
        _combiProgramEditActive = true;
        _combiProgramFlashState = false;
        BTN_Combi.IsActive    = true;
        BTN_Program.IsActive  = false;
        _combiProgramFlashTimer.Start();
        AppLog.Debug("[mode] program-edit-from-combi: entered");
        ModeText.Text = "Mode: Program (from Combi)";
        _controlPaletteWin?.SetMode(3);
    }

    void ExitCombiProgramEdit()
    {
        _combiProgramEditActive = false;
        _combiProgramFlashTimer.Stop();
        // Re-apply current mode so button state is consistent
        var btn = _currentMode switch
        {
            1 => BTN_Setlist,  2 => BTN_Combi,    3 => BTN_Program,
            4 => BTN_Sequence, 5 => BTN_Sampling, 6 => BTN_Global,
            7 => BTN_Disk,     _ => (KronosButton?)null
        };
        btn?.Activate();
        AppLog.Debug("[mode] program-edit-from-combi: exited");
    }

    void RenderTick(object? s, EventArgs e)
    {
        double dt = 0.016;
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderTime) return;
            if (_lastRenderTime != TimeSpan.MinValue)
                dt = Math.Min(0.1, (re.RenderingTime - _lastRenderTime).TotalSeconds);
            _lastRenderTime = re.RenderingTime;
        }

        bool newFrame = false;
        byte[]? raw   = null;
        while (_frameQ.TryDequeue(out var f)) { raw = f; newFrame = true; }

        if (newFrame && raw != null)
        {
            _fpsFrameCount++;
            var now = DateTime.Now;
            if (_fpsLastCheck == DateTime.MinValue) _fpsLastCheck = now;
            else if ((now - _fpsLastCheck).TotalSeconds >= 1.0)
            {
                _measuredFps   = _fpsFrameCount / (now - _fpsLastCheck).TotalSeconds;
                _fpsFrameCount = 0;
                _fpsLastCheck  = now;
                FpsText.Text   = $"{_measuredFps:F1} fps";
            }

            _rawFrame = raw;
            ApplyLut();
            _frameIsMostlyBlack      = IsFrameMostlyBlack(raw, _lut);        // 90% — suppresses mode detection
            _frameIsLikelyBootScreen = IsFrameMostlyBlack(raw, _lut, 0.60);  // 60% — gates splash display

            // Top-left 140×55 changed — update mode and help state independently.
            // Rows 0–26 = mode banner; rows 27–55 = help banner; never overlap.
            // Guard on !_frameIsMostlyBlack: near-black reference pixels (dark mode banner text)
            // score as false positives against the black boot framebuffer, so skip detection
            // until Eva's UI is visible (at least 10% non-black pixels across the frame).
            if (TopLeftOcr.HasChanged(raw, _frameW) && !_frameIsMostlyBlack)
            {
                _helpActive      = ModeDetector.IsHelpActive(raw, _frameW, _lut);
                BTN_Help.IsActive = _helpActive;

                int detected = ModeDetector.Identify(raw, _frameW, _lut);
                if (detected > 0)
                    SetModeButton(detected);
                else if (!ModeDetector.HasAny())
                    _ = QueryModeAsync();
                // refs loaded but no match = transitional frame; leave mode unchanged
            }

            // Combi-program-edit detection — runs every frame, not gated by HasChanged,
            // because the indicator at (696,39) is outside the top-left OCR region.
            // Exit via mode change (_currentMode != 3) is immediate — that's a positive detection.
            // Exit via indicator absence uses a holdoff: the indicator must be continuously absent
            // for CombiEditExitDelaySec before ExitCombiProgramEdit fires, so a menu/overlay
            // briefly covering the indicator region doesn't kill the flash animation.
            if (!_frameIsMostlyBlack)
            {
                bool indicatorActive = CombiProgramEditDetector.IsActive(raw, _frameW, _lut);
                if (!_combiProgramEditActive && _currentMode == 3 && (_prevMode == 2 || _prevMode == 0) && indicatorActive)
                {
                    _combiEditIndicatorGoneAt = DateTime.MinValue;
                    EnterCombiProgramEdit();
                }
                else if (_combiProgramEditActive)
                {
                    if (_currentMode != 3)
                    {
                        _combiEditIndicatorGoneAt = DateTime.MinValue;
                        ExitCombiProgramEdit();
                    }
                    else if (indicatorActive)
                    {
                        _combiEditIndicatorGoneAt = DateTime.MinValue; // indicator back — reset holdoff
                    }
                    else
                    {
                        // indicator absent but mode still 3 — may be a menu covering (696,39)
                        if (_combiEditIndicatorGoneAt == DateTime.MinValue)
                            _combiEditIndicatorGoneAt = DateTime.Now;
                        else if ((DateTime.Now - _combiEditIndicatorGoneAt).TotalSeconds >= CombiEditExitDelaySec)
                        {
                            _combiEditIndicatorGoneAt = DateTime.MinValue;
                            ExitCombiProgramEdit();
                        }
                    }
                }
            }
        }

        // Boot phase entry: enters immediately once connected (BootEntryDelaySec=0) if no mode detected.
        // Cleared instantly by SetModeButton the first time a valid mode is confirmed.
        if (_rawFrame != null && _bootFirstFrame == DateTime.MinValue)
            _bootFirstFrame = DateTime.Now;

        if (!_detectedModeEver && !_bootPhase && _bootFirstFrame != DateTime.MinValue &&
            (DateTime.Now - _bootFirstFrame).TotalSeconds >= BootEntryDelaySec)
        {
            _bootPhase         = true;
            _bootPhaseStart    = DateTime.Now;
            _preloadTimerStart = DateTime.Now;
            BuildPreloadSchedule();
            ClearModeButtons();
        }

        // Boot load-phase detection — run on every new frame while the overlay is active.
        // Phases advance strictly forward; each detection latches its own timestamp.
        if (_bootPhase && newFrame && _rawFrame != null)
        {
            var detected = BootPhaseDetector.Identify(_rawFrame, _frameW, _lut);
            if (detected == BootPhaseDetector.Phase.Finishing &&
                _bootLoadPhase < BootPhaseDetector.Phase.Finishing)
            {
                _finishingFillFrac = ComputeBootFillFraction(); // freeze bar at current position
                _bootLoadPhase  = BootPhaseDetector.Phase.Finishing;
            }
            else if (detected == BootPhaseDetector.Phase.BankData &&
                     _bootLoadPhase < BootPhaseDetector.Phase.BankData)
            {
                _bootLoadPhase      = BootPhaseDetector.Phase.BankData;
                _bankDataDetectedAt = DateTime.Now;
            }
            else if (detected == BootPhaseDetector.Phase.PreloadKSC &&
                     _bootLoadPhase < BootPhaseDetector.Phase.PreloadKSC)
            {
                _bootLoadPhase = BootPhaseDetector.Phase.PreloadKSC;
                // Do NOT reset _preloadTimerStart — it was latched at boot entry to avoid
                // the bar jumping backward if detection fires a few seconds late.
            }
        }

        if (_touchMarker.HasValue && !_dragActive && !_dragPending)
        {
            if ((DateTime.Now - _touchMarker.Value.t).TotalSeconds >= 0.6)
            {
                _touchMarker = null;
                OverlayLayer.InvalidateVisual();
            }
        }

        // Pending mode fallback — if detection never confirmed within the timeout,
        // apply the user-selected mode so the button eventually lights up.
        if (_pendingMode > 0 && DateTime.Now >= _pendingModeDeadline)
        {
            int fallback = _pendingMode;
            _pendingMode = 0;
            AppLog.Debug($"[mode] pending mode {fallback} confirmed via timeout fallback");
            SetModeButton(fallback);
        }

        if (newFrame || _touchMarker.HasValue || _bootPhase)
            OverlayLayer.InvalidateVisual();

        var (rawL, rawR) = _audioEngine?.GetLevels() ?? (-80.0, -80.0);
        VuMeter.Update(rawL, rawR, dt);
    }

    void RebuildLut()
    {
        for (int i = 0; i < 256; i++)
        {
            var e = _overrides.TryGetValue(i, out var ov) ? ov : _basePal[i];
            // Bgr32: uint32 = 0x00RRGGBB in little-endian → bytes B G R X ✓
            _lut[i] = (e.R << 16) | (e.G << 8) | e.B;
        }
    }

    unsafe void ApplyLut()
    {
        if (_wb == null || _rawFrame == null) return;
        try
        {
            _wb.Lock();
            try
            {
                int* ptr = (int*)_wb.BackBuffer;
                fixed (byte* frame = _rawFrame)
                fixed (int*  lut   = _lut)
                {
                    int n = _frameW * _frameH;
                    for (int i = 0; i < n; i++)
                        ptr[i] = lut[frame[i]];
                }
                _wb.AddDirtyRect(new Int32Rect(0, 0, _frameW, _frameH));
            }
            finally
            {
                _wb.Unlock();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // WPF hardware surface lost (common after modal dialog on some GPU drivers).
            // Recreate the bitmap; next tick will fill it in.
            _wb = new WriteableBitmap(_frameW, _frameH, 96, 96, PixelFormats.Bgr32, null);
            FrameImage.Source = _wb;
        }
    }

    void RefreshFrameRect()
    {
        // Derive from FrameImage's actual rendered position so column 2 is
        // automatically excluded — clicks there never fall inside _frameRect.
        var origin = FrameImage.TranslatePoint(new Point(0, 0), RootGrid);
        double imgW = FrameImage.ActualWidth, imgH = FrameImage.ActualHeight;

        if (_aspectLock)
        {
            FrameImage.Stretch = Stretch.Uniform;
            double scale = Math.Min(imgW / _frameW, imgH / _frameH);
            double cw = _frameW * scale, ch = _frameH * scale;
            _frameRect = new Rect(
                origin.X + (imgW - cw) / 2,
                origin.Y + (imgH - ch) / 2,
                cw, ch);
        }
        else
        {
            FrameImage.Stretch = Stretch.Fill;
            _frameRect = new Rect(origin, new Size(imgW, imgH));
        }
        OverlayLayer.InvalidateVisual();
    }
}
