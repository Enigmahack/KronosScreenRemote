using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KronosScreenRemote;

public partial class MainWindow
{
    bool   _ftpAuthenticated     = false;
    string _ftpAuthenticatedHost = "";

    // Tear down the current connection and start a fresh one (user reconnect, host change,
    // or streaming-parameter change).
    void TriggerReconnect()
    {
        _modePollCts?.Cancel();  // stop the old connection's mode-poll loop; ConnectAsync recreates it
        TearDownReceiver();
        BeginConnect();
    }

    // Dispose the live receiver and drop the persistent ctrl socket.  Shared by every teardown
    // path (reconnect + explicit disconnect) so the two can't drift.
    void TearDownReceiver()
    {
        var rcv = _receiver;
        _receiver = null;
        rcv?.Dispose();
        _ctrl.Reset();  // drop persistent ctrl socket; new one will be created on next Send
    }

    // Single entry point for starting a connection attempt.  Cancels any in-flight attempt so a
    // newer request always supersedes a stuck one (FTP verify / 10 s TCP watchdog) instead of
    // no-op'ing behind a "connecting" guard and silently dropping the reconnect.
    void BeginConnect()
    {
        ResetBootState();

        var prev = _connectCts;
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        prev?.Cancel();
        prev?.Dispose();

        SetConnectionStatus(ConnState.Connecting);  // immediate UI-thread feedback
        _ = Task.Run(() => ConnectAsync(ct));
    }

    async Task ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_host))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateTitle("Not Connected");
                MessageBox.Show(
                    "No Kronos IP address is configured.\n\nGo to Settings and enter the Kronos IP address.",
                    "Connection", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenSettingsDialog(SettingsTab.Connection);
            });
            SetConnectionStatus(ConnState.Disconnected);  // BeginConnect set Connecting; undo it
            return;
        }

        if (!await EnsureFtpLoginAsync())
        {
            if (ct.IsCancellationRequested) return;
            SetConnectionStatus(ConnState.Disconnected);
            await Dispatcher.InvokeAsync(() => UpdateTitle("Not Connected")).Task.ConfigureAwait(false);
            return;
        }
        if (ct.IsCancellationRequested) return;  // superseded during login

        AppLog.Info($"[conn] connecting to {_host}:{_port} mode={(_pullMode ? "pull" : "change")} fps={_fps}");
        SetConnectionStatus(ConnState.Connecting);
        // ConfigureAwait(false) keeps subsequent code on the thread-pool thread.
        // Without it, DispatcherOperation.GetAwaiter() resumes on the UI thread,
        // capturing DispatcherSynchronizationContext for all downstream awaits —
        // including the Task.WhenAny watchdog in StreamReceiver — which then can't
        // fire until the Dispatcher is idle, breaking the 10-second timeout.
        await Dispatcher.InvokeAsync(() =>
            UpdateTitle($"Connecting to {_host}…")).Task.ConfigureAwait(false);

        // Build the receiver locally and publish it to _receiver only after the handshake
        // succeeds, so a superseding disconnect/reconnect can't resurrect a half-connected
        // socket.  Disconnected is identity-guarded on ReferenceEquals(_receiver, receiver), and
        // RenderTick only ever polls the current _receiver, so a stale receiver winding down can
        // neither fire the disconnect handler nor deliver frames to the UI.
        var receiver = new StreamReceiver(_host, _port, _pullMode, _fps,
                                          _settings.FtpUsername, _settings.FtpPassword);
        receiver.Disconnected  += ()   => { if (ReferenceEquals(_receiver, receiver)) OnDisconnected(); };

        try
        {
            await receiver.ConnectAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            receiver.Dispose();
            return;  // superseded by a newer connect or an explicit disconnect — stay quiet
        }
        catch (UnauthorizedAccessException ex)
        {
            receiver.Dispose();
            _ftpAuthenticated = false;
            await ShowConnectError(
                $"[conn] auth rejected: {ex.Message}",
                "Authentication Failed",
                "Authentication Failed",
                "The Kronos daemon rejected the FTP credentials.\n\nClick Reconnect to try again.");
            return;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            receiver.Dispose();
            await ShowConnectError(
                $"[conn] timeout: {ex.Message}",
                "Connection Timed Out",
                "Connection Timed Out",
                ex.Message);
            return;
        }
        catch (Exception ex)
        {
            receiver.Dispose();
            await ShowConnectError(
                $"[conn] failed: {ex.GetType().Name}: {ex.Message}",
                "Connection Failed",
                "Kronos ScreenRemote",
                $"Connection failed:\n{ex.Message}");
            return;
        }

        if (ct.IsCancellationRequested) { receiver.Dispose(); return; }  // superseded during handshake

        // Publish the live receiver.
        _receiver = receiver;

        // A Disconnect() or superseding connect can land in the window between the check above
        // and this publish: it cancels ct, sets _receiver = null, and disposes whatever it sees
        // (nothing yet).  Re-check so the just-published receiver can't outlive that teardown as
        // an orphaned, still-streaming socket.  Only roll back our own receiver — a newer attempt
        // may already have replaced it.  (Dispose is idempotent, so a double-dispose here is safe.)
        if (ct.IsCancellationRequested)
        {
            if (ReferenceEquals(_receiver, receiver)) _receiver = null;
            receiver.Dispose();
            return;
        }

        _frameW  = receiver.Width;
        _frameH  = receiver.Height;
        _basePal = receiver.Palette;
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
        await Dispatcher.InvokeAsync(RefreshFrameRect, DispatcherPriority.Background)
            .Task.ConfigureAwait(false);

        // Push saved VGA mirror + screensaver settings to the daemon on every connect
        _mirrorState = _settings.VgaMirrorEnabled;
        Ctrl(DaemonCommand.VgaMirror(_mirrorState));
        Ctrl(DaemonCommand.ScreensaverTimeout(_settings.ScreensaverTimeout));

        _modePollCts?.Cancel();
        _modePollCts?.Dispose();
        _modePollCts = new CancellationTokenSource();
        TopLeftOcr.Reset();   // ensure the first frame re-evaluates help-overlay state
        _ = ModePollLoop(_modePollCts.Token);

        _sysExService.ApplyMidiSettings(
            _settings.MidiMonitorEnabled, _settings.ProactiveSysExPolling,
            _settings.SysExPollIntervalSec, _settings.SysExPollOnChanges);
        // Hand the TCP endpoint to the coordinator; it picks TCP or (preferring)
        // USB per the transport-mode setting and (re)starts the SysEx service.
        _midiCoord.SetScreenConnection(true, _host, _ctrlPort);
    }

    // Single teardown path for every explicit disconnect (menu / context / tray / command
    // palette).  Cancels any in-flight connect and background loops, then resets to disconnected.
    void Disconnect(string title = "Not Connected")
    {
        _connectCts?.Cancel();
        _modePollCts?.Cancel();
        ResetBootState();

        TearDownReceiver();

        SetConnectionStatus(ConnState.Disconnected);  // also resets sysex, stops ping/audio, clears frame
        UpdateTitle(title);
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

    void OnDisconnected()
    {
        AppLog.Info("[conn] disconnected");
        _modePollCts?.Cancel();
        TopLeftOcr.Reset();
        _ctrl.Reset();
        // Don't Reset() the SysEx service directly — SetConnectionStatus routes the
        // screen-drop through the coordinator, which keeps a standalone USB transport
        // alive (Auto/USB mode) instead of killing MIDI on every video hiccup.
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

    const int ModePollIntervalMs = 500;

    // The daemon's STATE command is the sole, unconditional mode/edit-context source —
    // no pixel scanning, no SysEx fallback. Polled continuously for as long as a stream
    // connection is live.
    async Task ModePollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var state = await QueryStateAsync().ConfigureAwait(false);
            if (state is { } s)
                await Dispatcher.InvokeAsync(() => ApplyDaemonState(s.Mode, s.EditCtx))
                    .Task.ConfigureAwait(false);
            try { await Task.Delay(ModePollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Ask the daemon for its current operating mode and edit context
    // ("STATE" → "MODE=<n> EDITCTX=<e>"). Returns null if the query failed or the
    // reply carried no MODE field.
    async Task<(Mode Mode, EditContext EditCtx)?> QueryStateAsync()
    {
        var resp = await _ctrl.QueryAsync(DaemonCommand.QueryState).ConfigureAwait(false);
        if (resp == null) return null;

        int mode = -1, editCtx = 0;
        foreach (var tok in resp.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            var val = tok[(eq + 1)..];
            if (tok.StartsWith("MODE=", StringComparison.Ordinal))
                int.TryParse(val, out mode);
            else if (tok.StartsWith("EDITCTX=", StringComparison.Ordinal))
                int.TryParse(val, out editCtx);
        }
        return mode >= 0 ? ((Mode)mode, (EditContext)editCtx) : null;
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
            if (!_detectedModeEver)
            {
                _detectedModeEver = true;
                _boot.Phase = false;   // dismiss overlay immediately — no fade
            }
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
        if (_receiver != null && frameSize > 0)
        {
            var buf = _frameBuf;
            if (buf == null || buf.Length != frameSize) { buf = new byte[frameSize]; _frameBuf = buf; }
            if (_receiver.TryCopyLatestFrame(buf)) { raw = buf; newFrame = true; }
        }

        if (newFrame && raw != null)
        {
            var fps = _fpsCounter.Tick(DateTime.Now);
            if (fps.HasValue) FpsText.Text = $"{fps.Value:F1} fps";

            _rawFrame = raw;
            ApplyLut();
            _frameIsMostlyBlack      = IsFrameMostlyBlack(raw, _lut);        // 90% — suppresses mode detection
            _frameIsLikelyBootScreen = IsFrameMostlyBlack(raw, _lut, _settings.BootScreenThreshold / 100.0);

            // Top-left 140×55 changed — re-check help-overlay state (rows 27–55 of the ROI).
            // Mode/edit-context no longer come from pixels at all — see ModePollLoop, which
            // polls the daemon's STATE command directly. Guard on !_frameIsMostlyBlack:
            // near-black reference pixels (dark help-banner text) score as false positives
            // against the black boot framebuffer, so skip detection until Eva's UI is visible
            // (at least 10% non-black pixels across the frame).
            if (TopLeftOcr.HasChanged(raw, _frameW) && !_frameIsMostlyBlack)
            {
                _helpActive       = HelpDetector.IsHelpActive(raw, _frameW, _lut);
                BTN_Help.IsActive = _helpActive;
            }
        }

        // Boot phase entry: enters immediately once connected (BootState.EntryDelaySec=0) if no mode detected.
        // Cleared instantly by SetModeButton the first time a valid mode is confirmed.
        if (_rawFrame != null && _boot.FirstFrame == DateTime.MinValue)
            _boot.FirstFrame = DateTime.Now;

        if (!_detectedModeEver && !_boot.Phase && _boot.FirstFrame != DateTime.MinValue &&
            (DateTime.Now - _boot.FirstFrame).TotalSeconds >= BootState.EntryDelaySec)
        {
            _boot.Phase         = true;
            _boot.PreloadTimerStart = DateTime.Now;
            BuildPreloadSchedule();
            ClearModeButtons();
        }

        // Boot load-phase detection — run on every new frame while the overlay is active.
        // Phases advance strictly forward; each detection latches its own timestamp.
        if (_boot.Phase && newFrame && _rawFrame != null)
        {
            var detected = BootPhaseDetector.Identify(_rawFrame, _frameW, _lut);
            if (detected == BootPhaseDetector.Phase.Finishing &&
                _boot.LoadPhase < BootPhaseDetector.Phase.Finishing)
            {
                _boot.FinishingFillFrac = ComputeBootFillFraction(); // freeze bar at current position
                _boot.LoadPhase  = BootPhaseDetector.Phase.Finishing;
            }
            else if (detected == BootPhaseDetector.Phase.BankData &&
                     _boot.LoadPhase < BootPhaseDetector.Phase.BankData)
            {
                _boot.LoadPhase      = BootPhaseDetector.Phase.BankData;
                _boot.BankDataDetectedAt = DateTime.Now;
            }
            else if (detected == BootPhaseDetector.Phase.PreloadKSC &&
                     _boot.LoadPhase < BootPhaseDetector.Phase.PreloadKSC)
            {
                _boot.LoadPhase = BootPhaseDetector.Phase.PreloadKSC;
                // Do NOT reset _boot.PreloadTimerStart — it was latched at boot entry to avoid
                // the bar jumping backward if detection fires a few seconds late.
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

        if (newFrame || _drag.Marker.HasValue || _boot.Phase)
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
    