namespace KronosScreenRemote;

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.Midi;

// Standalone, passive USB SysEx capture tool for real-hardware RE work against the Nautilus.
// See scratch_debug/pcg-converter-grillme-capsule.md's "HARDWARE-INVESTIGATION SESSION" entry
// for why this exists: Korg's own Nautilus MIDI Implementation doc prints a 6-byte Exclusive
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
    static void AttachOrAllocConsole()
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

    // Opens a UsbMidiTransport on `match`, printing the device lists and a clear error (then
    // exiting 1) if either direction can't be found/opened. Returns null in that failure case.
    static UsbMidiTransport? OpenTransport(string match)
    {
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

    // Active request->reply round trip: sends a real func-0x72 Object Dump Request (built via
    // the newly hardware-confirmed NautilusSysEx 6-byte framing) and waits for the matching
    // func-0x73 reply, parsing it. Read-only on the instrument (0x72/0x73 only - no Store/erase)
    // but, unlike Run() above, this DOES transmit. Defaults to Program (obj=0) bank 0 index 0.
    public static void RunRequestReply(string? match, int obj, int bank, int index)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";

        var transport = OpenTransport(match);
        if (transport == null) return;

        var tcs = new TaskCompletionSource<byte[]>();
        transport.SysExMessageReceived += msg =>
        {
            if (NautilusSysEx.HasHeaderAt(msg, 0, 0x73)) tcs.TrySetResult(msg);
        };

        var request = NautilusSysEx.BuildObjectDumpRequest(obj, bank, index);
        Console.WriteLine($"Listening on {transport.Description}.");
        Console.WriteLine($"Sending Object Dump Request (obj=0x{obj:X2} bank={bank} index={index}): " +
                           MidiHex.ToHex(request));
        _ = transport.SendAsync(request);

        var winner = Task.WhenAny(tcs.Task, Task.Delay(5000)).GetAwaiter().GetResult();
        if (winner != tcs.Task)
        {
            Console.WriteLine("Timed out after 5s waiting for a func-0x73 reply. Is SysEx Rx/Tx " +
                               "enabled on the instrument (Global/MIDI)? Is USB the active MIDI route?");
            transport.Stop();
            Environment.Exit(1);
            return;
        }

        var reply = tcs.Task.Result;
        Console.WriteLine($"Reply: {reply.Length} bytes. First 16: {MidiHex.ToHex(reply, 16)}");

        var dump = NautilusSysEx.ParseObjectDump(reply);
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

    // Full write-path round trip: read a real object, write the SAME bytes back unchanged
    // (func 0x73), commit with Store Bank Request (func 0x76 - DESTRUCTIVE, this is the whole
    // point of the test), then re-read and byte-diff against the original. Zero content risk
    // if it works (nothing actually changes); exercises the real write/commit path a passive
    // capture or a read-only 0x72/0x73 round trip can't. Run only with a fresh backup in hand.
    public static void RunWriteBackTest(string? match, int obj, int bank, int index)
    {
        AttachOrAllocConsole();
        match ??= "NAUTILUS";

        var transport = OpenTransport(match);
        if (transport == null) return;

        TaskCompletionSource<byte[]>? pending = null;
        Func<byte[], bool>? pendingMatch = null;
        transport.SysExMessageReceived += msg =>
        {
            if (pendingMatch != null && pendingMatch(msg)) pending?.TrySetResult(msg);
        };

        byte[]? SendAndWait(byte[] request, Func<byte[], bool> matchFn, string label, int timeoutMs = 5000)
        {
            pending = new TaskCompletionSource<byte[]>();
            pendingMatch = matchFn;
            Console.WriteLine($"-> {label}: {MidiHex.ToHex(request, 24)}");
            _ = transport.SendAsync(request);
            var winner = Task.WhenAny(pending.Task, Task.Delay(timeoutMs)).GetAwaiter().GetResult();
            pendingMatch = null;
            if (winner != pending.Task) { Console.WriteLine($"   TIMEOUT waiting for reply to: {label}"); return null; }
            var reply = pending.Task.Result;
            Console.WriteLine($"<- {reply.Length} bytes: {MidiHex.ToHex(reply, 16)}");
            return reply;
        }

        var readReq = NautilusSysEx.BuildObjectDumpRequest(obj, bank, index);

        var readReply = SendAndWait(readReq, m => NautilusSysEx.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72)");
        if (readReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var original = NautilusSysEx.ParseObjectDump(readReply);
        if (original == null) { Console.WriteLine("Couldn't parse original dump."); transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Original: version={original.Version} bodyLen={original.Body.Length}");

        Console.WriteLine();
        Console.WriteLine("About to WRITE these same bytes back unchanged, then STORE (commits to");
        Console.WriteLine("non-volatile storage - this is the destructive step). Press Enter to continue, Ctrl+C to abort.");
        Console.ReadLine();

        var writeMsg = NautilusSysEx.BuildObjectDumpMessage(obj, bank, index, original.Version, original.Body);
        var writeReply = SendAndWait(writeMsg, m => NautilusSysEx.ParseReply(m) != null, "Object Dump WRITE (0x73), bytes unchanged");
        if (writeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Write reply code: {NautilusSysEx.ParseReply(writeReply)} (0 = success)");

        var storeReq = NautilusSysEx.BuildStoreBankRequest(obj, bank);
        var storeReply = SendAndWait(storeReq, m => NautilusSysEx.ParseReply(m) != null, "Store Bank Request (0x76)");
        if (storeReply == null) { transport.Stop(); Environment.Exit(1); return; }
        Console.WriteLine($"Store reply code: {NautilusSysEx.ParseReply(storeReply)} (0 = success)");

        var reReadReply = SendAndWait(readReq, m => NautilusSysEx.HasHeaderAt(m, 0, 0x73), "Object Dump Request (0x72) - verify");
        if (reReadReply == null) { transport.Stop(); Environment.Exit(1); return; }
        var after = NautilusSysEx.ParseObjectDump(reReadReply);
        bool identical = after != null && after.Version == original.Version && after.Body.AsSpan().SequenceEqual(original.Body);

        Console.WriteLine();
        Console.WriteLine(identical
            ? "CONFIRMED: byte-identical after write + store + re-dump round trip."
            : $"MISMATCH after round trip - version {after?.Version} vs {original.Version}, " +
              $"bodyLen {after?.Body.Length} vs {original.Body.Length}.");

        transport.Stop();
        Environment.Exit(identical ? 0 : 1);
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

        var transport = OpenTransport(match);
        if (transport == null) return;

        TaskCompletionSource<byte[]>? pending = null;
        int pendingBank = -1;
        transport.SysExMessageReceived += msg =>
        {
            var d = NautilusSysEx.ParseBankDigest(msg);
            if (d != null && d.Value.Obj == obj && d.Value.Bank == pendingBank) pending?.TrySetResult(msg);
        };

        var banks = DigestScanBanksFor(obj);
        Console.WriteLine($"Scanning {banks.Count} bank(s) for obj=0x{obj:X2} via func 0x37/0x38...");
        foreach (int bank in banks)
        {
            pending = new TaskCompletionSource<byte[]>();
            pendingBank = bank;
            _ = transport.SendAsync(NautilusSysEx.BuildBankDigestRequest(obj, bank));
            var winner = Task.WhenAny(pending.Task, Task.Delay(2000)).GetAwaiter().GetResult();
            if (winner != pending.Task) { Console.WriteLine($"  bank=0x{bank:X2}  (no reply / not present)"); continue; }
            var d = NautilusSysEx.ParseBankDigest(pending.Task.Result)!.Value;
            Console.WriteLine($"  bank=0x{bank:X2}  sha1={Convert.ToHexString(d.Sha1)}");
        }

        transport.Stop();
        Environment.Exit(0);
    }
}
