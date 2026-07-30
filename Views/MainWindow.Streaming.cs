using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    void TriggerReconnect()
    {
        BeginConnect();
    }

    void BeginConnect()
    {
        if (string.IsNullOrWhiteSpace(_host))
        {
            _screenSession.Disconnect();
            SetConnectionStatus(ConnState.Connecting);
            UpdateTitle("Not Connected");
            MessageBox.Show(
                AppMessages.Connection.NoIpConfigured,
                AppMessages.Connection.NoIpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            OpenSettingsDialog(SettingsTab.Connection);
            SetConnectionStatus(ConnState.Disconnected);
            return;
        }

        AppLog.Info($"[conn] connecting to {_host}:{_port} mode={(_pullMode ? "pull" : "change")} fps={_fps}");
        SetConnectionStatus(ConnState.Connecting);
        UpdateTitle($"Connecting to {_host}…");
        _screenSession.Start(
            new ScreenConnection(_host, _port, _pullMode, _fps,
                                 _settings.FtpUsername, _settings.FtpPassword),
            _ => EnsureFtpLoginAsync());
    }

    void Disconnect(string title = "Not Connected")
    {
        _screenSession.Disconnect();
        SetConnectionStatus(ConnState.Disconnected);
        UpdateTitle(title);
    }

    Task<bool> EnsureFtpLoginAsync() => KronosFtpSession.EnsureLoginAsync(this, _settings, _host);

    void OnSessionConnected(ScreenSessionInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_screenSession.IsCurrent(info.Id)) return;
            _frameW = info.Width;
            _frameH = info.Height;
            _basePal = info.Palette;
            RebuildLut();
            AppLog.Info($"[conn] connected — {_frameW}×{_frameH} {(_pullMode ? "pull" : "change-driven")} cap={_fps}fps");
            SetConnectionStatus(ConnState.Connected);
            UpdateTitle(info.Connection.Host);
            AddRecentHost(info.Connection.Host);
            _wb = new WriteableBitmap(_frameW, _frameH, 96, 96, PixelFormats.Bgr32, null);
            FrameImage.Source = _wb;
            RefreshFrameRect();
            _mirrorState = _settings.VgaMirrorEnabled;
            Ctrl(DaemonCommand.VgaMirror(_mirrorState));
            Ctrl(DaemonCommand.ScreensaverTimeout(_settings.ScreensaverTimeout));
            _topLeftOcr.Reset();
            _sysExService.ApplyMidiSettings(
                _settings.MidiMonitorEnabled, _settings.ProactiveSysExPolling,
                _settings.SysExPollIntervalSec, _settings.SysExPollOnChanges);
            _midiCoord.SetScreenConnection(true, _host, _ctrlPort);
        });
    }

    void OnSessionConnectionFailed(ScreenSessionFailure failure)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_screenSession.IsLatest(failure.Id)) return;
            if (failure.Error == null)
            {
                SetConnectionStatus(ConnState.Disconnected);
                UpdateTitle("Not Connected");
                return;
            }

            string titleSuffix, dialogTitle, dialogMessage, logMessage;
            if (failure.Error is UnauthorizedAccessException)
            {
                KronosFtpSession.ResetAuthentication();
                titleSuffix = dialogTitle = AppMessages.Titles.AuthenticationFailed;
                dialogMessage = AppMessages.Connection.DaemonRejectedCredentials;
                logMessage = $"[conn] auth rejected: {failure.Error.Message}";
            }
            else if (failure.Error is TimeoutException or OperationCanceledException)
            {
                titleSuffix = dialogTitle = AppMessages.Connection.TimedOutTitle;
                dialogMessage = failure.Error.Message;
                logMessage = $"[conn] timeout: {failure.Error.Message}";
            }
            else
            {
                titleSuffix = "Connection Failed";
                dialogTitle = AppMessages.Connection.FailedTitle;
                dialogMessage = AppMessages.Connection.Failed(failure.Error.Message);
                logMessage = $"[conn] failed: {failure.Error.GetType().Name}: {failure.Error.Message}";
            }
            AppLog.Error(logMessage);
            SetConnectionStatus(ConnState.Disconnected);
            UpdateTitle(titleSuffix);
            MessageBox.Show(dialogMessage, dialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    void OnSessionDisconnected(long id)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_screenSession.IsLatest(id)) return;
            AppLog.Info("[conn] disconnected");
            _topLeftOcr.Reset();
            SetConnectionStatus(ConnState.Disconnected);
            _helpActive = false;
            BTN_Help.IsActive = false;
            ModeText.Text = "";
            UpdateTitle("Connection Lost");
        });
    }

    void OnSessionStateReceived(ScreenDaemonState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_screenSession.IsCurrent(state.Id)) return;
            _daemonBooting = state.Booting;
            ApplyDaemonState(state.Mode, state.EditContext);
        }, DispatcherPriority.Background);
    }

    // Apply one STATE poll result. EDITCTX (only ever non-zero while MODE=Program)
    // takes priority: it drives the flashing-Program/lit-origin-button state directly,
    // with no debounce needed since the daemon's eva_mode.ko source is exact per call.
    void ApplyDaemonState(Mode mode, EditContext ctx)
    {
        if (ctx == EditContext.None)
        {
            if (_editCtx.Active) ExitProgramEditContext();

            // STATE is authoritative and exact (eva_mode.ko or, at worst, the daemon's own
            // pixel fallback) — unlike the old client-side pixel/SysEx heuristics this
            // replaced, there's no stale/false reading to hold off for. Apply it as soon as
            // it disagrees with what's currently shown, so a button press lights up as fast
            // as the daemon itself confirms the change (one poll interval, not an added grace).
            if (mode != Mode.Unknown && mode != _currentMode)
                SetModeButton(mode);
            return;
        }

        if (mode != _currentMode) SetModeButton(mode);   // keep _currentMode bookkeeping correct
        if (!_editCtx.Active || _editCtx.Origin != ctx)
            EnterProgramEditContext(ctx);
    }

    void SetModeButton(Mode mode)
    {
        if (mode != Mode.Unknown)
        {
            if (mode != _currentMode)
            {
                _prevMode = _currentMode;
                _seqTransport.Reset();   // see SeqTransportViewModel.Reset for why
                _seqTransport.CurrentMode = mode;
            }
            _currentMode = mode;
            _pendingMode = Mode.Unknown;  // detection is authoritative — clear pending
            _detectedModeEver = true;
        }

        if (ButtonForMode(mode) is { } btn && !_editCtx.Active)
            btn.Activate();

        // mode=Unknown (server doesn't know yet) — leave current state rather than blanking
        var modeName = mode.DisplayName();
        if (modeName.Length > 0)
        {
            AppLog.Debug($"[mode] {modeName}");
            ModeText.Text = $"Mode: {modeName}";

            if (mode != _prevMode)
                _sysExService.RefreshNow();
        }
    }

    void ClearModeButtons()
    {
        foreach (var btn in new[] { BTN_Setlist, BTN_Combi, BTN_Program,
                                     BTN_Sequence, BTN_Sampling, BTN_Global, BTN_Disk })
            btn.IsActive = false;
        ModeText.Text = "";
    }

    void EnterProgramEditContext(EditContext ctx)
    {
        _editCtx.Active     = true;
        _editCtx.Origin     = ctx;
        _editCtx.FlashState = false;
        BTN_Combi.IsActive    = ctx == EditContext.ProgramFromCombi;
        BTN_Sequence.IsActive = ctx == EditContext.ProgramFromSequence;
        BTN_Program.IsActive  = false;
        _editCtx.FlashTimer.Start();
        AppLog.Debug($"[mode] program-edit-from-{ctx.DisplayName()}: entered");
        ModeText.Text = $"Mode: Program (from {ctx.DisplayName()})";
    }

    void ExitProgramEditContext()
    {
        AppLog.Debug($"[mode] program-edit-from-{_editCtx.Origin.DisplayName()}: exited");
        _editCtx.Active = false;
        _editCtx.Origin = EditContext.None;
        _editCtx.FlashTimer.Stop();
        // Re-apply current mode so button state is consistent
        ButtonForMode(_currentMode)?.Activate();
    }

    // The mode-key control that lights for a given operating mode (null for Unknown).
    KronosButton? ButtonForMode(Mode mode) => mode switch
    {
        Mode.Setlist  => BTN_Setlist,
        Mode.Combi    => BTN_Combi,
        Mode.Program  => BTN_Program,
        Mode.Sequence => BTN_Sequence,
        Mode.Sampling => BTN_Sampling,
        Mode.Global   => BTN_Global,
        Mode.Disk     => BTN_Disk,
        _             => null,
    };

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

        // Pull the latest frame into a UI-owned buffer.  The buffer is allocated (and re-sized on a
        // resolution change) here on the UI thread, so its reference is never mutated off-thread;
        // TryCopyLatestFrame copies under the receiver's lock, so the receive thread can never
        // overwrite bytes the UI is about to read.
        bool newFrame = false;
        byte[]? raw   = null;
        int frameSize = _frameW * _frameH;
        if (frameSize > 0)
        {
            var buf = _frameBuf;
            if (buf == null || buf.Length != frameSize) { buf = new byte[frameSize]; _frameBuf = buf; }
            if (_screenSession.TryCopyLatestFrame(buf)) { raw = buf; newFrame = true; }
        }

        if (newFrame && raw != null)
        {
            var fps = _fpsCounter.Tick(DateTime.Now);
            if (fps.HasValue) FpsText.Text = $"{fps.Value:F1} fps";

            _rawFrame = raw;
            ApplyLut();

            // Top-left 140×55 changed — re-check help-overlay state (rows 27–55 of the ROI).
            // Mode/edit-context no longer come from pixels at all — ScreenSession polls the
            // daemon's STATE command directly. Guard on !_daemonBooting (the
            // daemon's own authoritative BOOT= field, also read via that same STATE poll):
            // dark help-banner reference pixels score as false positives against the
            // still-mostly-black boot frame, so skip detection until the daemon itself
            // says the board is up.
            if (_topLeftOcr.HasChanged(raw, _frameW) && !_daemonBooting)
            {
                _helpActive       = _helpDetector.IsHelpActive(raw, _frameW, _lut);
                BTN_Help.IsActive = _helpActive;
            }
        }

        if (_drag.Marker.HasValue && !_drag.Active && !_drag.Pending)
        {
            if ((DateTime.Now - _drag.Marker.Value.t).TotalSeconds >= 0.6)
            {
                _drag.Marker = null;
                OverlayLayer.InvalidateVisual();
            }
        }

        // Pending mode fallback — if detection never confirmed within the timeout,
        // apply the user-selected mode so the button eventually lights up.
        if (_pendingMode != Mode.Unknown && DateTime.Now >= _pendingModeDeadline)
        {
            Mode fallback = _pendingMode;
            _pendingMode = Mode.Unknown;
            AppLog.Debug($"[mode] pending mode {fallback} confirmed via timeout fallback");
            SetModeButton(fallback);
        }

        if (newFrame || _drag.Marker.HasValue)
            OverlayLayer.InvalidateVisual();

        var (rawL, rawR) = _audioEngine?.GetLevels() ?? (-80.0, -80.0);
        VuMeter.Update(rawL, rawR, dt);
    }

    void RebuildLut()
    {
        // Brightness/contrast/gamma/saturation are baked into the 256-entry LUT here, so the
        // per-pixel blit in ApplyLut stays a single lookup — the whole frame is tone-adjusted for
        // free.  Sharpen is the exception (it needs neighbouring pixels) and runs in ApplyLut.
        var curve  = ImageAdjust.BuildToneCurve(
            _settings.ImageBrightness, _settings.ImageContrast, _settings.ImageGamma);
        double sat = ImageAdjust.SaturationFactor(_settings.ImageSaturation);
        for (int i = 0; i < 256; i++)
        {
            var e = _overrides.TryGetValue(i, out var ov) ? ov : _basePal[i];
            // Bgr32: uint32 = 0x00RRGGBB in little-endian → bytes B G R X ✓
            _lut[i] = ImageAdjust.ApplyToChannel(e.R, e.G, e.B, curve, sat);
        }
    }

    // Scratch RGB buffer for the sharpen pass — LUT output goes here so the unsharp mask can read
    // undisturbed source pixels while writing the sharpened result into the back buffer.  Only
    // allocated when sharpen is enabled; reused across frames and resized on a resolution change.
    int[]? _sharpBuf;

    unsafe void ApplyLut()
    {
        if (_wb == null || _rawFrame == null) return;
        // Guard the unsafe LUT loop: never read more bytes than the frame buffer holds.
        // A short/mismatched frame (e.g. a resolution change still queued after reconnect)
        // would otherwise cause an out-of-bounds read / access violation.
        if (_rawFrame.Length < _frameW * _frameH) return;
        int sharpen = _settings.ImageSharpen;
        try
        {
            _wb.Lock();
            try
            {
                int  n    = _frameW * _frameH;
                int* back = (int*)_wb.BackBuffer;

                if (sharpen <= 0)
                {
                    // Fast path (default): LUT straight into the back buffer.
                    fixed (byte* frame = _rawFrame)
                    fixed (int*  lut   = _lut)
                        for (int i = 0; i < n; i++)
                            back[i] = lut[frame[i]];
                }
                else
                {
                    // Sharpen path: LUT into the scratch buffer, then unsharp-mask into the back buffer.
                    var src = _sharpBuf;
                    if (src == null || src.Length != n) { src = new int[n]; _sharpBuf = src; }
                    fixed (byte* frame = _rawFrame)
                    fixed (int*  lut   = _lut)
                    fixed (int*  s     = src)
                    {
                        for (int i = 0; i < n; i++)
                            s[i] = lut[frame[i]];
                        ImageAdjust.UnsharpMask(s, back, _frameW, _frameH,
                                                sharpen / 100.0 * ImageAdjust.MaxSharpen);
                    }
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

    // Push the configured upscale filter onto FrameImage.  Called at startup (the XAML no longer
    // hard-codes a mode) and whenever the setting changes via the menus or Settings dialog.
    void ApplyScalingMode()
    {
        var mode = _settings.ImageScalingMode switch
        {
            ScalingQuality.Sharp  => BitmapScalingMode.NearestNeighbor,
            ScalingQuality.Smooth => BitmapScalingMode.LowQuality,
            _                     => BitmapScalingMode.HighQuality,
        };
        RenderOptions.SetBitmapScalingMode(FrameImage, mode);
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
    