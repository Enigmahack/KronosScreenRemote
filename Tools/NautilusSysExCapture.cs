namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.Midi;

// Standalone, passive USB SysEx capture tool for real-hardware RE work against the Nautilus.
// Why it exists: Korg's own Nautilus MIDI Implementation doc prints a 6-byte Exclusive
// Header (F0 42 3g 00 01 5D) where Kronos's is 4 bytes (F0 42 3g 68), but the same Nautilus
// doc's own function-code table caption ("5th byte of exclusive message") only makes sense for
// the 4-byte form - the vendor doc contradicts itself, so this needs a real capture, not more
// PDF reading.
//
// Deliberately standalone, not routed through SysExToolWindow/MidiTransportCoordinator (that
// UI path assumes a daemon/TCP session is already established, which the Nautilus doesn't have
// yet - USB-MIDI only). Talks to NAudio/winmm directly via the same UsbMidiTransport class the
// main app uses for its Kronos-over-USB path (already solves SysEx reassembly across multiple
// driver buffers), so no daemon is needed here either. Never sends anything - purely a listener
// - so it carries none of the Store/erase risk of 0x76/0x7C.
static class NautilusSysExCapture
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    const int AttachParentProcess = -1;

    // The app builds as WinExe (WPF) - launched from a terminal, Windows doesn't attach a
    // console, so `dotnet run`/the .exe returns to the shell prompt immediately and every
    // Console.WriteLine/ReadLine would silently go nowhere. Attach to the launching terminal's
    // console explicitly, or open a fresh window if there isn't one (e.g. double-clicked),
    // before touching Console at all. Shared by every entry point in this file.
    internal static void AttachOrAllocConsole()
    {
        // If stdout is already redirected/piped (a file, `| grep`, a captured test run), the
        // inherited handle already works fine - AttachConsole can fail in that situation and
        // AllocConsole's fallback would then open a SEPARATE, invisible new console window and
        // silently steal Console.Out away from the redirect. Only do the attach/alloc dance for
        // the genuine bare-interactive-terminal case (no redirection at all) that motivated this.
        if (Console.IsOutputRedirected) return;

        if (!AttachConsole(AttachParentProcess)) AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.WriteLine();   // move off the shell's existing prompt line
    }

    // Opens either a USB or TCP transport depending on `match`'s format. A `match` starting
    // with "tcp:" (e.g. "tcp:10.0.0.5:7374") connects through a screenremote daemon's TCP
    // bridge - the SAME IMidiTransport backend already used for the Kronos when a daemon
    // relays its own local USB-MIDI connection over the network (see MidiTransportCoordinator).
    // Anything else is treated as a USB device-name substring match (original behavior, e.g.
    // "NAUTILUS"). Needed because a Nautilus can be reached either way depending on setup: a
    // direct USB-MIDI cable to this machine, or a daemon on a different machine relaying its
    // own local USB connection to the Nautilus over TCP - printing "no MIDI device found" in
    // the latter case (as this tool originally did, before it understood "tcp:") is misleading,
    // since the instrument genuinely has no local USB presence to find.
    // Kept alive for the process's lifetime (every caller ends in Environment.Exit, so explicit
    // disposal is unnecessary - the OS reclaims the sockets on exit, same convention as every
    // other resource in this file). Referenced here only so the GC never collects them while a
    // headless run is in flight - see EstablishStreamOwnership's own comment for why this exists.
    static ScreenSession? _ownershipSession;
    static CtrlClient? _ownershipCtrl;

    // The daemon's ctrl-port MIDI commands (MIDI_SEND, MIDI_STATUS - and everything else
    // touch/button/wheel/slider/knob-related) are "ownership-gated": per KronosScreenRemoteDaemon/
    // docs/api.md section 5, they're answered ONLY for the IP currently holding an authenticated
    // connection on the FRAMEBUFFER STREAM port (7373 by default - a completely different thing
    // from the port-9875 MIDI bridge, which needs no auth at all per the same doc). A short
    // allowlist (VERSION/MODEL/STATE/etc.) is answered from any IP regardless - which is exactly
    // why MODEL/STATE worked fine in this session's diagnosis while MIDI_SEND/MIDI_STATUS both
    // silently returned zero bytes: nothing in this standalone tool ever opened a stream-port
    // connection, so this IP was never a recognized owner. The full GUI app never hits this
    // because it always holds a live ScreenSession (that's the whole point of the app - screen
    // mirroring) as a side effect of normal operation.
    //
    // Reuses the app's own ScreenSession/CtrlClient/StreamReceiver classes (the exact same ones
    // MainWindow uses) rather than reimplementing the KSCR v3 stream handshake's binary hello +
    // credentials here - that protocol (KronosScreenRemoteDaemon/docs/api.md section 3) is
    // nontrivial and already correctly implemented; this just needs the connection alive, not its
    // pixel data. Credentials/host/port come from the same Storage.LoadSettings() the GUI uses -
    // matches the credentials the user already has saved and already proven to work against this
    // exact Nautilus (the live GUI session earlier this conversation).
    static bool EstablishStreamOwnership(string host, int timeoutMs = 8000)
    {
        var settings = Storage.LoadSettings();
        var ctrl = new CtrlClient(host, CtrlQuery.CtrlPort);
        var session = new ScreenSession(ctrl);

        var tcs = new TaskCompletionSource<bool>();
        session.Connected += _ => tcs.TrySetResult(true);
        session.ConnectionFailed += f => tcs.TrySetResult(false);

        Console.WriteLine($"Establishing stream-port ownership at {host}:{settings.StreamPort} " +
                           "(required before the daemon will answer any MIDI_SEND/MIDI_STATUS command)...");
        // The top-level Ftp* pair is whichever host the GUI last connected to; with two boards in
        // play that's often the other one, so prefer this host's own remembered credentials.
        var (user, pass) = settings.HostCredentials.TryGetValue(host, out var cred)
            ? (cred.Username, cred.Password)
            : (settings.FtpUsername, settings.FtpPassword);
        session.Start(new ScreenConnection(host, settings.StreamPort, settings.PullMode, settings.MaxFps,
            user, pass));

        // Safe to block synchronously here (no Task.Run wrapper needed, unlike SendAndWait above):
        // ScreenSession.Start's own ConnectAsync already runs via its own internal Task.Run, so
        // tcs is completed from an already-isolated background thread, not from a continuation
        // that needs this (WPF-dispatcher-bearing) thread to be free.
        bool ok = Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).GetAwaiter().GetResult() == tcs.Task
            && tcs.Task.Result;

        if (!ok)
        {
            Console.WriteLine("Stream-port ownership failed or timed out - check KronosHost/StreamPort/" +
                               "FtpUsername/FtpPassword in Settings match this Nautilus, and that no other " +
                               "client (the GUI app itself, another copy of this tool) already owns the stream.");
            session.Dispose();
            ctrl.Dispose();
            return false;
        }

        Console.WriteLine("Stream-port ownership established.");
        _ownershipSession = session;
        _ownershipCtrl = ctrl;
        return true;
    }

    static IMidiTransport? OpenTransport(string match)
    {
        if (match.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = match.Substring(4).Split(':');
            if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out int port))
            {
                Console.WriteLine($"Bad TCP address '{match}' - expected 'tcp:<host>:<port>', e.g. 'tcp:10.0.0.5:7374'.");
                Environment.Exit(1);
                return null;
            }
            if (!EstablishStreamOwnership(parts[0]))
            {
                Environment.Exit(1);
                return null;
            }
            Console.WriteLine($"Connecting to daemon at {parts[0]}:{port}...");
            var tcpTransport = new TcpMidiTransport(parts[0], port);
            // TCP's live inbound stream (the port-9875 monitor) must be explicitly enabled -
            // unlike USB, where the single device connection inherently carries replies (see
            // IMidiTransport.SetStreamEnabled's own doc comment). Order matters: matches
            // SysExService.Start's SetStreamEnabled-before-Start sequencing exactly.
            tcpTransport.SetStreamEnabled(true);
            tcpTransport.Start();
            // Both SetStreamEnabled and Start kick off MidiStreamMonitor's TCP connect to 9875
            // fire-and-forget (Task.Run, not awaited) - this call returns before that socket is
            // actually connected. Port 9875 is a hub per KronosScreenRemoteDaemon/docs/api.md
            // section 8 (broadcasts inbound MIDI to whichever clients are CURRENTLY joined, no
            // replay/buffering for one that joins late) - a request sent immediately after this
            // call risks the Nautilus's reply arriving before this socket finishes joining, which
            // is simply lost, not delivered late. Hardware-confirmed 2026-09-23: a raw MIDI_SEND
            // diagnostic got "OK" from the daemon (proving injection worked) while a full request/
            // reply round trip through this same OpenTransport timed out - the send succeeded,
            // the listener just wasn't ready yet. MidiStreamMonitor exposes no "connected" event
            // to await properly (would need a small addition to that shared, Kronos-live-path
            // class); this fixed delay is the low-risk fix for a standalone diagnostic tool where
            // an extra fraction of a second per invocation costs nothing. A LAN handshake to the
            // instrument's own embedded Linux should complete in well under this margin.
            Task.Delay(750).Wait();
            return tcpTransport;
        }

        Console.WriteLine("Available MIDI IN devices:");
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            Console.WriteLine($"  [{i}] {MidiIn.DeviceInfo(i).ProductName}");
        Console.WriteLine("Available MIDI OUT devices:");
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            Console.WriteLine($"  [{i}] {MidiOut.DeviceInfo(i).ProductName}");
        Console.WriteLine();

        int inIdx  = KronosMidiDevices.FindInputIndex(match);
        int outIdx = KronosMidiDevices.FindOutputIndex(match);
        if (inIdx < 0 || outIdx < 0)
        {
            Console.WriteLine($"No MIDI device matching '{match}' has both an input (found={inIdx >= 0}) " +
                               $"and output (found={outIdx >= 0}) port - see the device lists above and pass " +
                               "the exact/partial name as the first argument.");
            Environment.Exit(1);
            return null;
        }

        var transport = new UsbMidiTransport(match);
        transport.Start();
        if (!transport.CanStream)
        {
            Console.WriteLine($"Failed to open '{match}' - see the device lists above. Plugged in? " +
                               "Held open by another app (a DAW, or KronosScreenRemote itself)?");
            Environment.Exit(1);
            return null;
        }
        return transport;
    }

    public static void Run(string? match, string? outDir)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        outDir ??= Path.Combine(Path.GetTempPath(), "nautilus_sysex_capture");
        Directory.CreateDirectory(outDir);

        var transport = OpenTransport(match);
        if (transport == null) return;
        int count = 0;
        transport.SysExMessageReceived += msg =>
        {
            count++;
            var stamp = DateTime.Now.ToString("HHmmss_fff");
            var binPath = Path.Combine(outDir, $"capture_{stamp}_{count:000}.syx");
            var hexPath = Path.Combine(outDir, $"capture_{stamp}_{count:000}.hex.txt");
            File.WriteAllBytes(binPath, msg);
            File.WriteAllText(hexPath, MidiHex.ToHex(msg));

            Console.WriteLine($"[{count:000}] {msg.Length} bytes -> {Path.GetFileName(binPath)}");
            Console.WriteLine($"       first 16: {MidiHex.ToHex(msg, 16)}");
        };

        Console.WriteLine($"Listening on {transport.Description}. Saving captures to: {outDir}");
        Console.WriteLine("Purely passive - nothing will be sent to the instrument.");
        Console.WriteLine("Trigger a dump from the instrument's own menu now (e.g. Global/MIDI > Dump).");
        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();

        transport.Stop();
        Console.WriteLine($"Stopped. Captured {count} message(s) in {outDir}.");
        Environment.Exit(0);
    }

    // Above this many bytes, route the send through SendLargeSysExAsync instead of SendAsync -
    // see SendAndWait's own comment for why. Chosen well under every documented daemon limit
    // (KronosScreenRemoteDaemon/docs/api.md: ctrl-port MIDI_SEND's ~2 KB line length and 4096-
    // byte inbound decode buffer), with headroom - small requests (Object Dump Request, Store
    // Bank Request, Bank Digest Request - all well under 32 bytes) stay on the already-proven
    // SendAsync/ctrl-port path unchanged; only a real object body write needs this.
    const int LargeSendThreshold = 1024;

    // Shared "-> send, <- await first matching reply" wrapper for every request/reply method
    // below - a thin printing layer over MidiTransportReplyExtensions.AwaitReplyAsync (the same
    // subscribe/send/await/unsubscribe scaffold the Kronos live path already uses for exactly
    // this shape), replacing what used to be 4 independently hand-rolled TaskCompletionSource
    // closures with one shared call. `verbose=false` (used by RunBankDigestScan's per-bank loop)
    // suppresses the hex-dump lines so a multi-bank scan doesn't drown in per-request noise.
    //
    // Request size determines the send path: TcpMidiTransport.SendAsync's ctrl-port MIDI_SEND
    // sends the ENTIRE hex-encoded message as one line with no chunking - fine for the small
    // requests (Object Dump Request, Store Bank Request) this tool sends everywhere else, but a
    // full converted Program body (8-to-7 encoded, ~5.6 KB) blows past both of the daemon's
    // documented single-write caps (docs/api.md: ctrl-port ~2 KB line limit, 4096-byte inbound
    // decode buffer). Hardware-confirmed 2026-09-23: a real --nautilus-convert-and-write run sent
    // exactly this oversized write, got no reply at all (not an error - just silence, since the
    // daemon's own line/buffer limits were exceeded), and timed out - the target slot's original
    // content was correctly never touched (the code never reaches Store without a write reply),
    // but this needed to be diagnosed rather than assumed harmless. SendLargeSysExAsync already
    // exists for exactly this (prefers writing the whole object as one shot over the already-
    // connected 9875 socket, falling back to auto-chunked ctrl-port MIDI_SEND only if that fails)
    // - this was simply never wired up for the write step.
    //
    // The Task.Run wrapper is load-bearing, not decoration: this app is WPF, so OnStartup (where
    // every --nautilus-* CLI flag is dispatched, including this one) runs on a thread with an
    // active DispatcherSynchronizationContext. AwaitReplyAsync's `send()` delegate calls down
    // through TcpMidiTransport.SendAsync (correctly ConfigureAwait(false)'d) into
    // CtrlQuery.QueryAsync, whose OWN internal awaits (ConnectAsync/SendAllAsync/ReceiveAsync)
    // are NOT ConfigureAwait(false)'d - fine everywhere else in the app (nothing else blocks
    // synchronously on that chain), but blocking on it here via .GetAwaiter().GetResult() directly
    // deadlocks forever: those continuations try to resume on this very thread, which is busy
    // waiting for them. Hardware-confirmed 2026-09-22 - the very first live TCP test hung
    // indefinitely (25s+, no timeout message) at exactly this point until this fix.
    // Task.Run moves the whole async chain onto a thread-pool thread with no captured
    // SynchronizationContext, so every await in it resumes freely regardless of ConfigureAwait.
    static byte[]? SendAndWait(IMidiTransport transport, byte[] request, Func<byte[], bool> matchFn,
        string label, int timeoutMs = 5000, bool verbose = true)
    {
        if (verbose) Console.WriteLine($"-> {label}: {MidiHex.ToHex(request, 24)}");
        var reply = Task.Run(() => transport.AwaitReplyAsync(
            send: () => request.Length > LargeSendThreshold
                ? transport.SendLargeSysExAsync(request)
                : transport.SendAsync(request),
            match: (byte[] m) => matchFn(m) ? m : null,
            timeoutMs)).GetAwaiter().GetResult();
        if (reply == null) { if (verbose) Console.WriteLine($"   TIMEOUT waiting for reply to: {label}"); return null; }
        if (verbose) Console.WriteLine($"<- {reply.Length} bytes: {MidiHex.ToHex(reply, 16)}");
        return reply;
    }

    // Active request->reply round trip: sends a real func-0x72 Object Dump Request and waits for
    // the matching func-0x73 reply, parsing it, via the board-specific IKorgSysExCodec (Kronos
    // vs Nautilus header framing) rather than calling NautilusSysEx directly - this method (and
    // every other one below) is no longer Nautilus-only by construction, just Nautilus-only by
    // which codec it happens to resolve. Read-only on the instrument (0x72/0x73 only - no Store/
    // erase) but, unlike Run() above, this DOES transmit. Defaults to Program (obj=0) bank 0
    // index 0.
    public static void RunRequestReply(string? match, int obj, int bank, int index)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);

        var transport = OpenTransport(match);
        if (transport == null) return;

        Console.WriteLine($"Listening on {transport.Description}.");
        var request = codec.BuildObjectDumpRequest(obj, bank, index);
        var reply = SendAndWait(transport, request, m => codec.HasHeaderAt(m, 0, 0x73),
            $"Object Dump Request (obj=0x{obj:X2} bank={bank} index={index})");
        if (reply == null)
        {
            Console.WriteLine("Is SysEx Rx/Tx enabled on the instrument (Global/MIDI)? Is USB the active MIDI route?");
            transport.Stop();
            Environment.Exit(1);
            return;
        }

        var dump = codec.ParseObjectDump(reply);
        if (dump == null)
        {
            Console.WriteLine("Header matched func 0x73 but ParseObjectDump returned null - unexpected shape.");
        }
        else
        {
            string name = System.Text.Encoding.ASCII.GetString(
                dump.Body, 0, Math.Min(24, dump.Body.Length)).TrimEnd('\0', ' ');
            Console.WriteLine($"Parsed OK: obj=0x{dump.Obj:X2} bank={dump.Bank} index={dump.Index} " +
                               $"version={dump.Version} bodyLen={dump.Body.Length} name=\"{name}\"");
        }

        transport.Stop();
        Environment.Exit(0);
    }

    // Read-only sweep of 0x72 Object Dump Requests over obj codes [objFrom, objTo] at one
    // bank/index, accepting either a 0x73 dump or a 0x24 Reply - for finding object types the
    // vendor doc omits (ArpPattern.txt documents a 556-byte object but NAUTILUS_MIDI_SysEx.txt's
    // obj table lists no code for it). Each decoded body is saved to outDir for offline diffing.
    public static void RunObjectProbe(string? match, int objFrom, int objTo, int bank, int index, string? outDir,
        DeviceFamily family = DeviceFamily.Nautilus, int count = 1)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        outDir ??= Path.Combine(Path.GetTempPath(), "nautilus_obj_probe");
        Directory.CreateDirectory(outDir);
        var codec = KorgSysExCodecs.For(family);

        var transport = OpenTransport(match);
        if (transport == null) return;

        for (int obj = objFrom; obj <= objTo; obj++)
        for (int n = 0; n < count; n++)
        {
            int idx = index + n;
            var reply = SendAndWait(transport, codec.BuildObjectDumpRequest(obj, bank, idx),
                m => codec.HasHeaderAt(m, 0, 0x73) || codec.HasHeaderAt(m, 0, 0x24),
                $"obj=0x{obj:X2}", timeoutMs: 3000, verbose: false);
            if (reply == null) { Console.WriteLine($"obj=0x{obj:X2} bank=0x{bank:X2} idx={idx}: TIMEOUT"); continue; }
            var dump = codec.ParseObjectDump(reply);
            if (dump == null)
            {
                Console.WriteLine($"obj=0x{obj:X2} bank=0x{bank:X2} idx={idx}: Reply code=0x{codec.ParseReply(reply):X2}");
                continue;
            }
            string name = new string(System.Text.Encoding.ASCII.GetString(dump.Body, 0, Math.Min(24, dump.Body.Length))
                .Select(c => c >= ' ' && c < 0x7F ? c : '.').ToArray());
            File.WriteAllBytes(Path.Combine(outDir, $"obj{obj:X2}_b{bank:X2}_i{idx:0000}_v{dump.Version}.bin"), dump.Body);
            Console.WriteLine($"obj=0x{obj:X2} bank=0x{bank:X2} idx={idx}: DUMP version={dump.Version} " +
                               $"bodyLen={dump.Body.Length} name=\"{name}\"");
        }

        transport.Stop();
        Environment.Exit(0);
    }

    // Full write-path round trip: read a real object, write the SAME bytes back unchanged
    // (func 0x73), commit with Store Bank Request (func 0x76 - DESTRUCTIVE, this is the whole
    // point of the test), then re-read and byte-diff against the original. Zero content risk
    // if it works (nothing actually changes); exercises the real write/commit path a passive
    // capture or a read-only 0x72/0x73 round trip can't. Run only with a fresh backup in hand.
    public static void RunWriteBackTest(string? match, int obj, int bank, int index)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);

        var transport = OpenTransport(match);
        if (transport == null) return;

        var readReq = codec.BuildObjectDumpRequest(obj, bank, index);

        var readReply = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72)");
        if (readReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var original = codec.ParseObjectDump(readReply);
        if (original == null) { Console.WriteLine("Couldn't parse original dump."); transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Original: version={original.Version} bodyLen={original.Body.Length}");

        Console.WriteLine();
        Console.WriteLine("About to WRITE these same bytes back unchanged, then STORE (commits to");
        Console.WriteLine("non-volatile storage - this is the destructive step). Press Enter to continue, Ctrl+C to abort.");
        Console.ReadLine();

        var writeMsg = codec.BuildObjectDumpMessage(obj, bank, index, original.Version, original.Body);
        var writeReply = SendAndWait(transport, writeMsg, m => codec.ParseReply(m) != null, "Object Dump WRITE (0x73), bytes unchanged");
        if (writeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Write reply code: {codec.ParseReply(writeReply)} (0 = success)");

        var storeReq = codec.BuildStoreBankRequest(obj, bank);
        var storeReply = SendAndWait(transport, storeReq, m => codec.ParseReply(m) != null, "Store Bank Request (0x76)");
        if (storeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Store reply code: {codec.ParseReply(storeReply)} (0 = success)");

        var reReadReply = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72) - verify");
        if (reReadReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var after = codec.ParseObjectDump(reReadReply);
        bool identical = after != null && after.Version == original.Version && after.Body.AsSpan().SequenceEqual(original.Body);

        Console.WriteLine();
        Console.WriteLine(identical
            ? "CONFIRMED: byte-identical after write + store + re-dump round trip."
            : $"MISMATCH after round trip - version {after?.Version} vs {original.Version}, " +
              $"bodyLen {after?.Body.Length} vs {original.Body.Length}.");

        transport.Stop();
        Environment.Exit(identical ? 0 : 1);
    }

    // Saves the edit buffer for `obj` (0x74 request -> 0x75 reply: header, 0x75, obj, version,
    // 8-to-7 data) as a decoded body, so a UI-driven test can put it back afterwards.
    public static void RunSaveCurrentObject(string? match, int obj, string outPath)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);
        var transport = OpenTransport(match);
        if (transport == null) return;

        var reply = SendAndWait(transport, codec.KorgMessage(0x74, (byte)obj),
            m => codec.HasHeaderAt(m, 0, 0x75) || codec.HasHeaderAt(m, 0, 0x24), $"Current Object Dump Request obj=0x{obj:X2}");
        if (reply == null || !codec.HasHeaderAt(reply, 0, 0x75)) { Console.WriteLine("No 0x75 reply."); transport.Stop(); Environment.Exit(1); return; }
        byte version = reply[8];
        var body = KronosSysEx.Decode8to7(reply, 9, reply.Length - 10);
        File.WriteAllBytes(outPath, body);
        Console.WriteLine($"Saved edit buffer obj=0x{obj:X2} version={version} bodyLen={body.Length} -> {outPath}");
        transport.Stop();
        Environment.Exit(0);
    }

    // Replaces an edit buffer with a decoded body (0x75 Current Object Dump), then re-reads it
    // with 0x74 and byte-diffs - the undo for RunSaveCurrentObject. Nothing is stored.
    public static void RunSendCurrentObject(string? match, int obj, byte version, string bodyPath)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);
        byte[] body = File.ReadAllBytes(bodyPath);
        var transport = OpenTransport(match);
        if (transport == null) return;

        var payload = new byte[] { (byte)obj, version }.Concat(KronosSysEx.Encode7to8(body, 0, body.Length)).ToArray();
        var reply = SendAndWait(transport, codec.KorgMessage(0x75, payload), m => codec.ParseReply(m) != null,
            $"Current Object Dump obj=0x{obj:X2} len={body.Length}");
        Console.WriteLine($"Reply code: {(reply == null ? "none" : codec.ParseReply(reply).ToString())} (0 = success)");

        var check = SendAndWait(transport, codec.KorgMessage(0x74, (byte)obj), m => codec.HasHeaderAt(m, 0, 0x75), "verify 0x74");
        var after = check == null ? null : KronosSysEx.Decode8to7(check, 9, check.Length - 10);
        Console.WriteLine(after != null && after.AsSpan().SequenceEqual(body)
            ? "CONFIRMED: edit buffer is byte-identical to the sent body."
            : $"DIFF: edit buffer differs ({after?.Length} vs {body.Length} bytes).");
        transport.Stop();
        Environment.Exit(0);
    }

    // Writes an arbitrary decoded object body from a file into one slot (0x73), optionally
    // commits it (0x76 - DESTRUCTIVE), then re-reads and byte-diffs. No confirmation prompt:
    // callers are scripted load tests that have already taken a backup. The previous slot
    // content is saved next to the body file as <body>.before.bin so a test can be undone.
    public static void RunWriteObject(string? match, int obj, int bank, int index, byte version, string bodyPath, bool store)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);
        byte[] body = File.ReadAllBytes(bodyPath);

        var transport = OpenTransport(match);
        if (transport == null) return;
        int Fail() { transport.Stop(); Environment.Exit(1); return 1; }

        var readReq = codec.BuildObjectDumpRequest(obj, bank, index);
        var before = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73) || codec.HasHeaderAt(m, 0, 0x24), "read before");
        var beforeDump = before == null ? null : codec.ParseObjectDump(before);
        if (beforeDump != null) File.WriteAllBytes(bodyPath + ".before.bin", beforeDump.Body);
        else Console.WriteLine("No readable previous content (slot empty or invalid) - continuing.");

        var writeReply = SendAndWait(transport, codec.BuildObjectDumpMessage(obj, bank, index, version, body),
            m => codec.ParseReply(m) != null, $"WRITE obj=0x{obj:X2} bank=0x{bank:X2} idx={index} len={body.Length}");
        if (writeReply == null) { Fail(); return; }
        Console.WriteLine($"Write reply code: {codec.ParseReply(writeReply)} (0 = success)");

        if (store)
        {
            var storeReply = SendAndWait(transport, codec.BuildStoreBankRequest(obj, bank), m => codec.ParseReply(m) != null, "STORE (0x76)");
            if (storeReply == null) { Fail(); return; }
            Console.WriteLine($"Store reply code: {codec.ParseReply(storeReply)} (0 = success)");
        }

        var after = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73), "re-read");
        var afterDump = after == null ? null : codec.ParseObjectDump(after);
        if (afterDump == null) { Console.WriteLine("Re-read failed."); Fail(); return; }
        File.WriteAllBytes(bodyPath + ".after.bin", afterDump.Body);
        var diffs = Enumerable.Range(0, Math.Min(body.Length, afterDump.Body.Length)).Where(i => body[i] != afterDump.Body[i]).ToList();
        Console.WriteLine(diffs.Count == 0 && body.Length == afterDump.Body.Length
            ? "CONFIRMED: re-read body is byte-identical to what was written."
            : $"DIFF: {diffs.Count} byte(s) differ (len {afterDump.Body.Length} vs {body.Length}); first offsets: {string.Join(",", diffs.Take(16))}");

        transport.Stop();
        Environment.Exit(0);
    }

    // Shared load+convert step for both RunConvertDryRun and RunConvertAndWrite below - reads a
    // real Program from a real Kronos .pcg file (offline, no live Kronos or Nautilus needed) and
    // runs it through the shipped ProgramConverter.KronosToNautilus pipeline. Touches no
    // hardware at all - safe to call from anywhere, including outside this headless-tool file.
    // `programName` matches PcgObjectEntry.Name (trimmed, case-insensitive) - simpler than
    // requiring the caller to pre-decode the source bank/index for a one-off lookup.
    public static ProgramConverter.ConversionResult? LoadAndConvert(string kronosPcgPath, string programName, out string? sourceDescription,
        byte[]? nautilusTemplate = null)
    {
        sourceDescription = null;
        byte[] pcgData;
        try { pcgData = File.ReadAllBytes(kronosPcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{kronosPcgPath}': {ex.Message}"); return null; }

        var pcg = PcgFile.Open(pcgData);
        if (pcg == null) { Console.WriteLine($"'{kronosPcgPath}' is not a recognizable Kronos .pcg file."); return null; }

        var candidates = pcg.Objects.Where(o => o.Loc.ObjType == LibObj.Program &&
            string.Equals(o.Name.Trim(), programName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) { Console.WriteLine($"No Program named \"{programName}\" found in '{kronosPcgPath}'."); return null; }
        if (candidates.Count > 1) { Console.WriteLine($"{candidates.Count} Programs named \"{programName}\" found - name isn't unique in this file, aborting."); return null; }
        var source = candidates[0];

        byte[] wireBody;
        try { wireBody = ProgramFormatConverter.PcgToWire(source.Body, source.IsExi); }
        catch (ArgumentException ex) { Console.WriteLine($"Couldn't convert .pcg record to wire format: {ex.Message}"); return null; }

        string? engineName = source.IsExi ? LibRefs.ProgramEngineName(wireBody) : "HD-1";
        sourceDescription = $"\"{source.Name}\" ({(source.IsExi ? "EXi" : "HD-1")}{(engineName != null ? $"/{engineName}" : "")}), wire body {wireBody.Length} bytes";
        Console.WriteLine($"Source: {sourceDescription}.");

        var map = RomWaveformMap.FromCatalog();
        var exsCatalog = ExsOptionIndex.FromCatalog();
        var geCatalog = KarmaGeCatalog.FromCatalog();
        var result = ProgramConverter.KronosToNautilus(wireBody, map, exsCatalog, geCatalog, nautilusTemplate);

        foreach (var w in result.Warnings) Console.WriteLine($"WARNING: {w}");
        foreach (var r in result.Refusals) Console.WriteLine($"REFUSED: {r}");
        if (result.NautilusBody == null)
            Console.WriteLine("Conversion refused - see refusal(s) above.");
        else
            Console.WriteLine($"Converted OK: {result.NautilusBody.Length} bytes (was {wireBody.Length}).");
        return result;
    }

    // `--nautilus-convert-dry-run <kronos.pcg> <program-name>` - runs LoadAndConvert and prints
    // the result. No transport, no hardware, no write - safe to run anytime, as often as wanted,
    // to preview what a conversion would produce/warn/refuse before ever touching real hardware.
    public static void RunConvertDryRun(string kronosPcgPath, string programName)
    {
        AttachOrAllocConsole();
        var result = LoadAndConvert(kronosPcgPath, programName, out _);
        Environment.Exit(result?.NautilusBody != null ? 0 : 1);
    }

    // Writes the CONVERTED body (not a same-bytes echo, unlike RunWriteBackTest above) from
    // LoadAndConvert into a target slot on the real Nautilus, committing with a Store Bank
    // Request - same confirm-before-commit gate as RunWriteBackTest.
    // `namePrefix`, when given, overwrites the converted body's Name field (offset 0, 24 bytes,
    // ASCII, space-padded - see KarmaFieldStripper's header comment) with `namePrefix +
    // sourceName`, truncated to 24 chars - lets an exploratory write be found/identified later
    // (e.g. a "111-" search prefix) without touching the source .pcg file.
    public static void RunConvertAndWrite(string kronosPcgPath, string programName, int targetBank, int targetIndex, string? match, string? namePrefix = null)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);

        // Dry-run first so a refused conversion never opens the transport; the real conversion
        // below reuses the target slot's own Nautilus-only Scene/Step Seq region as its template.
        if (LoadAndConvert(kronosPcgPath, programName, out _)?.NautilusBody == null) { Environment.Exit(1); return; }

        var transport = OpenTransport(match);
        if (transport == null) return;

        const int obj = LibObj.Program;
        var readReq = codec.BuildObjectDumpRequest(obj, targetBank, targetIndex);
        var targetReply = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72) - reading TARGET slot first");
        if (targetReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var targetBefore = codec.ParseObjectDump(targetReply);
        if (targetBefore == null) { Console.WriteLine("Couldn't parse target slot's current dump."); transport.Stop(); Environment.Exit(1); return; }
        string targetName = System.Text.Encoding.ASCII.GetString(
            targetBefore.Body, 0, Math.Min(24, targetBefore.Body.Length)).TrimEnd('\0', ' ');
        Console.WriteLine($"Target slot (bank={targetBank} index={targetIndex}) currently holds: \"{targetName}\" " +
                           $"(version={targetBefore.Version}, {targetBefore.Body.Length} bytes) - this will be overwritten.");

        var result = LoadAndConvert(kronosPcgPath, programName, out _, targetBefore.Body);
        if (result?.NautilusBody == null) { transport.Stop(); Environment.Exit(1); return; }

        if (namePrefix != null)
        {
            string newName = (namePrefix + programName).PadRight(24)[..24];
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(newName);
            Array.Copy(nameBytes, 0, result.NautilusBody, 0, Math.Min(24, result.NautilusBody.Length));
            Console.WriteLine($"Renamed for this test write: \"{newName.TrimEnd()}\"");
        }

        Console.WriteLine();
        Console.WriteLine("About to write the converted Program above into the target slot, then commit with a");
        Console.WriteLine("Store Bank Request (non-volatile - confirm you have a fresh backup of the target bank).");
        Console.WriteLine("Press Enter to continue, Ctrl+C to abort.");
        Console.ReadLine();

        var writeMsg = codec.BuildObjectDumpMessage(obj, targetBank, targetIndex, targetBefore.Version, result.NautilusBody);
        var writeReply = SendAndWait(transport, writeMsg, m => codec.ParseReply(m) != null, "Object Dump WRITE (0x73), converted body");
        if (writeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Write reply code: {codec.ParseReply(writeReply)} (0 = success)");

        var storeReq = codec.BuildStoreBankRequest(obj, targetBank);
        var storeReply = SendAndWait(transport, storeReq, m => codec.ParseReply(m) != null, "Store Bank Request (0x76)");
        if (storeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Store reply code: {codec.ParseReply(storeReply)} (0 = success)");

        var reReadReply = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72) - verify");
        if (reReadReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var after = codec.ParseObjectDump(reReadReply);
        bool identical = after != null && after.Body.AsSpan().SequenceEqual(result.NautilusBody);
        string afterName = after == null ? "?" : System.Text.Encoding.ASCII.GetString(
            after.Body, 0, Math.Min(24, after.Body.Length)).TrimEnd('\0', ' ');

        Console.WriteLine();
        Console.WriteLine(identical
            ? $"CONFIRMED: re-dump byte-identical to the converted body just written. Name now reads \"{afterName}\"."
            : $"MISMATCH after write+store+re-dump - bodyLen {after?.Body.Length} vs {result.NautilusBody.Length}, name \"{afterName}\".");
        Console.WriteLine("Now go listen to it on the instrument - byte-identical only confirms the write stuck,");
        Console.WriteLine("not that it sounds or behaves correctly.");

        transport.Stop();
        Environment.Exit(identical ? 0 : 1);
    }

    // `--nautilus-convert-combi <kronos.pcg> <combi-name> [<device-match> <bank> <index>]` - converts one
    // Kronos Combi with CombiConverter. Without a target it only prints the result; with one it reads
    // the target slot first (its Nautilus-only Scene/ARP block is the template), writes, Store Banks
    // (DESTRUCTIVE) and re-reads. Default ArpConversionOptions (ARP A/B <- KARMA A/B, Scene 1).
    public static void RunConvertCombi(string kronosPcgPath, string combiName, string? match, int? targetBank, int? targetIndex)
    {
        AttachOrAllocConsole();
        var pcg = PcgFile.Open(File.ReadAllBytes(kronosPcgPath));
        if (pcg == null) { Console.WriteLine($"'{kronosPcgPath}' is not a recognizable Kronos .pcg file."); Environment.Exit(1); return; }
        var matches = pcg.Objects.Where(o => o.Loc.ObjType == LibObj.Combi &&
            string.Equals(o.Name.Trim(), combiName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) { Console.WriteLine($"{matches.Count} Combis named \"{combiName}\" in the file - need exactly one."); Environment.Exit(1); return; }
        var source = matches[0];
        Console.WriteLine($"Source: \"{source.Name}\" ({source.Body.Length} bytes)");
        var geCatalog = KarmaGeCatalog.FromCatalog();

        byte[]? template = null;
        IMidiTransport? transport = null;
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);
        if (match != null && targetBank is int tb && targetIndex is int ti)
        {
            transport = OpenTransport(match);
            if (transport == null) return;
            var before = SendAndWait(transport, codec.BuildObjectDumpRequest(LibObj.Combi, tb, ti), m => codec.HasHeaderAt(m, 0, 0x73), "read target Combi");
            var dump = before == null ? null : codec.ParseObjectDump(before);
            if (dump == null) { Console.WriteLine("Couldn't read the target slot."); transport.Stop(); Environment.Exit(1); return; }
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"nautilus_combi_b{tb:X2}_i{ti:000}_before.bin"), dump.Body);
            Console.WriteLine($"Target bank 0x{tb:X2} index {ti}: \"{System.Text.Encoding.ASCII.GetString(dump.Body, 0, 24).TrimEnd('\0', ' ')}\" " +
                $"(version {dump.Version}, {dump.Body.Length} bytes) - saved to %TEMP% before overwriting.");
            template = dump.Body;
        }

        // Names of the Programs actually in this .pcg, so only still-factory references get re-pointed.
        var progNames = pcg.Objects.Where(o => o.Loc.ObjType == LibObj.Program && KronosBanks.ObjBankToFunc33(1, o.Loc.Bank) >= 0)
            .GroupBy(o => (KronosBanks.ObjBankToFunc33(1, o.Loc.Bank), o.Loc.Number)).ToDictionary(g => g.Key, g => g.First().Name);
        var result = CombiConverter.KronosToNautilus(source.Body, geCatalog, template, null, KarmaGePatternLibrary.FromCatalog(),
            TimbreProgramRefs.FactoryEquivalents, ProgramRefMap.FromCatalog(), (b, i) => progNames.GetValueOrDefault((b, i)));
        foreach (var w in result.Warnings) Console.WriteLine($"  warning: {w}");
        foreach (var r in result.Refusals) Console.WriteLine($"  REFUSED: {r}");
        if (result.NautilusBody == null) { transport?.Stop(); Environment.Exit(1); return; }
        string outPath = Path.Combine(Path.GetTempPath(), "nautilus_combi_converted.bin");
        File.WriteAllBytes(outPath, result.NautilusBody);
        Console.WriteLine($"Converted: {result.NautilusBody.Length} bytes -> {outPath}");
        if (transport == null) { Environment.Exit(0); return; }

        int bank = targetBank!.Value, index = targetIndex!.Value;
        var w1 = SendAndWait(transport, codec.BuildObjectDumpMessage(LibObj.Combi, bank, index, 4, result.NautilusBody), m => codec.ParseReply(m) != null, "WRITE Combi (0x73)");
        var s1 = SendAndWait(transport, codec.BuildStoreBankRequest(LibObj.Combi, bank), m => codec.ParseReply(m) != null, "STORE (0x76)");
        Console.WriteLine($"write reply {(w1 == null ? "none" : codec.ParseReply(w1).ToString())}, store reply {(s1 == null ? "none" : codec.ParseReply(s1).ToString())} (0 = success)");
        var again = SendAndWait(transport, codec.BuildObjectDumpRequest(LibObj.Combi, bank, index), m => codec.HasHeaderAt(m, 0, 0x73), "re-read");
        var after = again == null ? null : codec.ParseObjectDump(again);
        Console.WriteLine(after != null && after.Body.AsSpan().SequenceEqual(result.NautilusBody)
            ? "CONFIRMED: re-read is byte-identical to the converted Combi."
            : "MISMATCH on re-read.");
        transport.Stop();
        Environment.Exit(0);
    }

    // `--combi-factory-compare <kronos PRELOAD.PCG> <nautilus PRELOAD.PCG>` - offline check of
    // CombiConverter against Korg's own ports: for every Combi name present once in both factory
    // sets, convert the Kronos record using the Nautilus record as the template, then report which
    // Nautilus byte offsets still differ (aggregated across the corpus, per region / Timbre offset).
    public static void RunCombiFactoryCompare(string kronosPcg, string nautilusPcg)
    {
        AttachOrAllocConsole();
        var k = PcgFile.Open(File.ReadAllBytes(kronosPcg));
        var nData = File.ReadAllBytes(nautilusPcg);
        if (k == null) { Console.WriteLine("Couldn't open the Kronos file."); Environment.Exit(1); return; }
        // NautilusPcgObjectExtractor deliberately skips CBK1 (Combi banks), so read them here: same
        // 24-byte bank header as every other bank chunk (BE32 count @0x0C, itemSize @0x10).
        static int BE32(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
        var nCombis = new List<(string Name, byte[] Body)>();
        for (int pos = 0; pos + 24 <= nData.Length; pos++)
        {
            if (nData[pos] != 'C' || nData[pos + 1] != 'B' || nData[pos + 2] != 'K' || nData[pos + 3] != '1') continue;
            int count = BE32(nData, pos + 0x0C), size = BE32(nData, pos + 0x10);
            if (count is < 1 or > 200 || size != CombiConverter.NautilusSize || pos + 24L + count * size > nData.Length) continue;
            for (int r = 0; r < count; r++)
            {
                var body = nData.AsSpan(pos + 24 + r * size, size).ToArray();
                nCombis.Add((System.Text.Encoding.ASCII.GetString(body, 0, 24).TrimEnd('\0', ' '), body));
            }
            pos += 24 + count * size - 1;
        }
        var kc = k.Objects.Where(o => o.Loc.ObjType == LibObj.Combi && o.Body.Length == CombiConverter.KronosSize)
            .GroupBy(o => o.Name.Trim()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Body);
        var nc = nCombis.GroupBy(o => o.Name.Trim()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Body);
        var names = kc.Keys.Intersect(nc.Keys).Where(nm => !nm.StartsWith("Init", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToList();
        Console.WriteLine($"Kronos factory Combis {kc.Count}, Nautilus {nc.Count}, shared names {names.Count}");
        var geCatalog = KarmaGeCatalog.FromCatalog();
        var perOffset = new int[CombiConverter.NautilusSize];
        int identical = 0, refsActive = 0, refsSame = 0;
        var refMap = ProgramRefMap.FromCatalog();
        foreach (var nm in names)
        {
            var body = CombiConverter.KronosToNautilus(kc[nm], geCatalog, nc[nm], null, null, TimbreProgramRefs.FactoryEquivalents, refMap).NautilusBody!;
            for (int t = 0; t < 16; t++)
            {
                int o = 2632 + 188 * t;
                if (nc[nm][o + 2] >> 5 == 0 || body[o + 2] >> 5 == 0) continue;
                refsActive++;
                if (body[o] == nc[nm][o] && body[o + 1] == nc[nm][o + 1]) refsSame++;
            }
            int diffs = 0;
            for (int i = 0; i < body.Length; i++) if (body[i] != nc[nm][i]) { perOffset[i]++; diffs++; }
            if (diffs == 0) identical++;
        }
        Console.WriteLine($"Byte-identical after conversion: {identical}/{names.Count}");
        Console.WriteLine($"Active-timbre Program references matching Korg's port: {refsSame}/{refsActive}");
        string Where(int o) => o < 1268 ? $"head+{o}" : o < 2620 ? $"middle+{o}" : o < 2632 ? $"tail+{o}" : $"timbre+{(o - 2632) % 188}";
        foreach (var g in Enumerable.Range(0, perOffset.Length).Where(i => perOffset[i] > 0)
                     .GroupBy(Where).Select(g => (Key: g.Key, Count: g.Sum(i => perOffset[i]), Offsets: g.ToList()))
                     .OrderByDescending(g => g.Count).Take(40))
            Console.WriteLine($"  {g.Key,-14} {g.Count,5} differing bytes (first offset {g.Offsets[0]})");
        Environment.Exit(0);
    }

    // Bank-byte ranges per NAUTILUS_MIDI_SysEx.txt *2* and *37* ("Digests are not available
    // for the GM program and drum kit banks" - so GM (0x10-0x1A Program, 0x10 DrumKit) is
    // deliberately excluded from all three, not just skipped by omission).
    static List<int> DigestScanBanksFor(int obj) => obj switch
    {
        LibObj.Program      => Enumerable.Range(0, 6).Concat(Enumerable.Range(0x40, 14)).ToList(),   // Int A-F, User G-T
        LibObj.DrumKit      => new List<int> { 0 }.Concat(Enumerable.Range(0x40, 14)).ToList(),      // Int A, User B-O
        LibObj.WaveSequence => new List<int> { 0 }.Concat(Enumerable.Range(0x40, 14)).ToList(),      // Int INT, User A-G/AA-GG
        _ => throw new ArgumentException($"No known bank-byte range for obj=0x{obj:X2}"),
    };

    // Read-only Bank Digest (func 0x37/0x38) scan across the documented bank-byte range for
    // the given object type. Purely a query, no write/store - safe to run anytime. Match the
    // printed sha1 values against Tools/NautilusBankDigestDump.cs's offline output for a real
    // .pcg file to decode BankIdRaw to a real bank identity (see the Program precedent in
    // Nautilus Research/MAIN/07_pcg_and_sampling/pcg_format_kronos_vs_nautilus.md).
    public static void RunBankDigestScan(string? match, int obj)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);

        var transport = OpenTransport(match);
        if (transport == null) return;

        var banks = DigestScanBanksFor(obj);
        Console.WriteLine($"Scanning {banks.Count} bank(s) for obj=0x{obj:X2} via func 0x37/0x38...");
        foreach (int bank in banks)
        {
            var request = codec.BuildBankDigestRequest(obj, bank);
            var reply = SendAndWait(transport, request, m =>
            {
                var d = codec.ParseBankDigest(m);
                return d != null && d.Value.Obj == obj && d.Value.Bank == bank;
            }, $"Bank Digest Request bank=0x{bank:X2}", timeoutMs: 2000, verbose: false);
            if (reply == null) { Console.WriteLine($"  bank=0x{bank:X2}  (no reply / not present)"); continue; }
            var d = codec.ParseBankDigest(reply)!.Value;
            Console.WriteLine($"  bank=0x{bank:X2}  sha1={Convert.ToHexString(d.Sha1)}");
        }

        transport.Stop();
        Environment.Exit(0);
    }

    // `--nautilus-diff-after-edit <kronos.pcg> <program-name> <post-edit.pcg> [post-edit-name]`
    // - offline, no hardware. Reproduces the exact body this tool would have written for
    // `programName` (same LoadAndConvert pipeline, template = the first "Init Program" found in
    // `post-edit.pcg` - a stand-in for whatever Init Program content the real target slot held
    // before the original live write, since Nautilus's factory Init Program pattern is the same
    // across slots), then byte-diffs that reproduction against the real object named
    // `post-edit-name` (default: same as `programName`) extracted from `post-edit.pcg` - i.e.
    // what the instrument's own front-panel Edit+Save actually wrote. Built to investigate the
    // 2026-09-23 "wrong category, present in Bank view but not Category view" finding: run after
    // pulling a post-edit .pcg back from the Nautilus to see exactly which bytes the front panel
    // changed relative to our conversion.
    public static void RunDiffAfterEdit(string kronosPcgPath, string programName, string postEditPcgPath, string? postEditName)
    {
        AttachOrAllocConsole();
        postEditName ??= programName;

        byte[] postEditData;
        try { postEditData = File.ReadAllBytes(postEditPcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{postEditPcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var objects = NautilusPcgObjectExtractor.Extract(postEditData);
        var template = objects.FirstOrDefault(o => o.ObjType == LibObj.Program &&
            string.Equals(o.Name.Trim(), "Init Program", StringComparison.OrdinalIgnoreCase));
        if (template == null) { Console.WriteLine($"No 'Init Program' found in '{postEditPcgPath}' to use as a template."); Environment.Exit(1); return; }
        Console.WriteLine($"Template: Init Program at bankIdRaw=0x{template.BankIdRaw:X} index={template.Index}, {template.Body.Length} bytes (.pcg slot).");

        var postEdit = objects.FirstOrDefault(o => o.ObjType == LibObj.Program &&
            string.Equals(o.Name.Trim(), postEditName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (postEdit == null) { Console.WriteLine($"No Program named \"{postEditName}\" found in '{postEditPcgPath}'."); Environment.Exit(1); return; }
        Console.WriteLine($"Post-edit: \"{postEdit.Name}\" at bankIdRaw=0x{postEdit.BankIdRaw:X} index={postEdit.Index}, {postEdit.Body.Length} bytes (.pcg slot).");

        byte[] templateWire, postEditWire;
        try
        {
            templateWire = NautilusProgramFormatConverter.PcgToWire(template.Body, template.IsExi);
            postEditWire = NautilusProgramFormatConverter.PcgToWire(postEdit.Body, postEdit.IsExi);
        }
        catch (ArgumentException ex) { Console.WriteLine($"Couldn't convert .pcg record to wire format: {ex.Message}"); Environment.Exit(1); return; }
        Console.WriteLine($"Trimmed to wire size: template {templateWire.Length} bytes, post-edit {postEditWire.Length} bytes.");

        var result = LoadAndConvert(kronosPcgPath, programName, out _, templateWire);
        if (result?.NautilusBody == null) { Console.WriteLine("Reproduction refused - see refusal(s) above."); Environment.Exit(1); return; }
        var repro = result.NautilusBody;
        var postEditBody = postEditWire;

        if (repro.Length != postEditBody.Length)
        {
            Console.WriteLine($"LENGTH MISMATCH: reproduced {repro.Length} bytes, post-edit {postEditBody.Length} bytes - byte-offset diff below won't align past the shorter length.");
        }

        int n = Math.Min(repro.Length, postEditBody.Length);
        int diffCount = 0;
        Console.WriteLine();
        Console.WriteLine("offset  repro  post-edit");
        for (int i = 0; i < n; i++)
        {
            if (repro[i] == postEditBody[i]) continue;
            diffCount++;
            Console.WriteLine($"0x{i:X4}  0x{repro[i]:X2}   0x{postEditBody[i]:X2}");
        }
        Console.WriteLine();
        Console.WriteLine(diffCount == 0
            ? "No byte differences in the overlapping range."
            : $"{diffCount} differing byte(s) out of {n} compared.");

        const int categoryOffsetNautilus = 2568 - ProgramConverter.NautilusSizeDelta; // 2562
        if (categoryOffsetNautilus < n)
        {
            byte r = repro[categoryOffsetNautilus], p = postEditBody[categoryOffsetNautilus];
            Console.WriteLine($"Category byte @0x{categoryOffsetNautilus:X4}: repro=0x{r:X2} (cat={r & 0x1F}, sub={(r >> 5) & 0x07})  " +
                               $"post-edit=0x{p:X2} (cat={p & 0x1F}, sub={(p >> 5) & 0x07})");
        }

        Environment.Exit(0);
    }

    // `--nautilus-convert-bank <kronos.pcg> <source-bank> <target-bank> [device-name-match]` -
    // DESTRUCTIVE: converts every Program in a whole Kronos wire bank (0-127, index-for-index)
    // and writes them into the same slot numbers in a Nautilus Program bank, committing with ONE
    // Store Bank Request at the end (per sysex_protocol_kronos_vs_nautilus.md: "not committed
    // until a Store Bank Request follows; can send multiple objects to one bank before a single
    // Store" - avoids 128 separate non-volatile-write cycles). Refused conversions are skipped
    // (target slot left untouched) and reported in the summary, never partially written. Pauses
    // once for Enter before any write begins - confirm a fresh backup of the TARGET bank exists
    // first, same standard as every other write path here.
    public static void RunConvertBank(string kronosPcgPath, int sourceBank, int targetBank, string? match)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";

        byte[] pcgData;
        try { pcgData = File.ReadAllBytes(kronosPcgPath); }
        catch (Exception ex) { Console.WriteLine($"Couldn't read '{kronosPcgPath}': {ex.Message}"); Environment.Exit(1); return; }

        var pcg = PcgFile.Open(pcgData);
        if (pcg == null) { Console.WriteLine($"'{kronosPcgPath}' is not a recognizable Kronos .pcg file."); Environment.Exit(1); return; }

        var sourceEntries = pcg.Objects
            .Where(o => o.Loc.ObjType == LibObj.Program && o.Loc.Bank == sourceBank)
            .OrderBy(o => o.Loc.Number)
            .ToList();
        if (sourceEntries.Count == 0) { Console.WriteLine($"No Program entries found in source bank {sourceBank} (0x{sourceBank:X2})."); Environment.Exit(1); return; }
        Console.WriteLine($"Source bank 0x{sourceBank:X2}: {sourceEntries.Count} Program record(s).");

        var map = RomWaveformMap.FromCatalog();
        var exsCatalog = ExsOptionIndex.FromCatalog();
        var geCatalog = KarmaGeCatalog.FromCatalog();
        var codec = KorgSysExCodecs.For(DeviceFamily.Nautilus);
        const int obj = LibObj.Program;

        Console.WriteLine();
        Console.WriteLine($"About to convert+write up to {sourceEntries.Count} Program(s) into Nautilus bank 0x{targetBank:X2},");
        Console.WriteLine("same slot numbers, committing with ONE Store Bank Request at the end (non-volatile -");
        Console.WriteLine("confirm you have a fresh backup of the TARGET bank).");
        Console.WriteLine("Press Enter to continue, Ctrl+C to abort.");
        Console.ReadLine();

        var transport = OpenTransport(match);
        if (transport == null) return;

        int written = 0, refused = 0, badRecord = 0, writeFailed = 0;
        var refusalLog = new List<string>();

        foreach (var entry in sourceEntries)
        {
            int index = entry.Loc.Number;
            byte[] wireBody;
            try { wireBody = ProgramFormatConverter.PcgToWire(entry.Body, entry.IsExi); }
            catch (ArgumentException ex)
            {
                badRecord++;
                Console.WriteLine($"[{index,3}] SKIP (bad source record): {ex.Message}");
                continue;
            }

            var readReq = codec.BuildObjectDumpRequest(obj, targetBank, index);
            var targetReply = SendAndWait(transport, readReq, m => codec.HasHeaderAt(m, 0, 0x73),
                $"[{index,3}] read target", timeoutMs: 5000, verbose: false);
            if (targetReply == null)
            {
                writeFailed++;
                Console.WriteLine($"[{index,3}] SKIP: couldn't read target slot (timeout).");
                continue;
            }
            var targetBefore = codec.ParseObjectDump(targetReply);
            if (targetBefore == null)
            {
                writeFailed++;
                Console.WriteLine($"[{index,3}] SKIP: couldn't parse target slot's current dump.");
                continue;
            }

            var result = ProgramConverter.KronosToNautilus(wireBody, map, exsCatalog, geCatalog, targetBefore.Body);
            if (result.NautilusBody == null)
            {
                refused++;
                string reason = string.Join("; ", result.Refusals);
                refusalLog.Add($"[{index,3}] \"{entry.Name.Trim()}\": {reason}");
                Console.WriteLine($"[{index,3}] REFUSED \"{entry.Name.Trim()}\": {reason}");
                continue;
            }

            var writeMsg = codec.BuildObjectDumpMessage(obj, targetBank, index, targetBefore.Version, result.NautilusBody);
            var writeReply = SendAndWait(transport, writeMsg, m => codec.ParseReply(m) != null,
                $"[{index,3}] write", timeoutMs: 5000, verbose: false);
            int? code = writeReply != null ? codec.ParseReply(writeReply) : null;
            if (code != 0)
            {
                writeFailed++;
                Console.WriteLine($"[{index,3}] WRITE FAILED \"{entry.Name.Trim()}\": reply code {code?.ToString() ?? "(timeout)"}");
                continue;
            }

            written++;
            Console.WriteLine($"[{index,3}] OK \"{entry.Name.Trim()}\"" +
                (result.Warnings.Count > 0 ? $" ({result.Warnings.Count} warning(s))" : ""));
        }

        Console.WriteLine();
        Console.WriteLine($"Converted+written: {written}  Refused: {refused}  Bad source record: {badRecord}  Write failed: {writeFailed}");

        if (written == 0)
        {
            Console.WriteLine("Nothing written - skipping Store Bank Request.");
            transport.Stop();
            Environment.Exit(refused + badRecord + writeFailed > 0 ? 1 : 0);
            return;
        }

        var storeReq = codec.BuildStoreBankRequest(obj, targetBank);
        var storeReply = SendAndWait(transport, storeReq, m => codec.ParseReply(m) != null, "Store Bank Request (0x76)");
        int? storeCode = storeReply != null ? codec.ParseReply(storeReply) : null;
        Console.WriteLine($"Store reply code: {storeCode?.ToString() ?? "(timeout)"} (0 = success)");

        if (refusalLog.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Refusals:");
            foreach (var line in refusalLog) Console.WriteLine("  " + line);
        }

        transport.Stop();
        Environment.Exit(storeCode == 0 ? 0 : 1);
    }
}
