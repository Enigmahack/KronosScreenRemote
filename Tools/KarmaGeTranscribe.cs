namespace KronosScreenRemote;

using System.IO;
using System.Net.Sockets;

// `--karma-ge-to-nautilus <kronosHost> <nautilusHost> <ge> <bSlot> [holdSec]` - headless
// render-and-transcribe of one Kronos KARMA GE into Nautilus Arp Pattern bank B (obj 0x19,
// bank 0x40), then an A/B playback check on the Nautilus. Both boards' Program edit buffers are
// saved first and restored (and re-verified) at the end; the only lasting change is the stored
// B<bSlot> pattern. Talks to each daemon's port-9875 MIDI bridge directly (no auth, inject +
// capture on one socket) - see nautilus_arp_engine_architecture.md section 9.
static class KarmaGeTranscribe
{
    static readonly int[] Held = { 60, 64, 67 };
    const int RenderTempo = 120;

    // One 9875 connection: raw writes in, parsed + timestamped messages out.
    sealed class Bridge : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _net;
        readonly MidiStreamParser _parser = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object _lock = new();
        readonly List<(double Sec, byte[] Msg)> _messages = new();
        readonly CancellationTokenSource _cts = new();

        public Bridge(string host)
        {
            _tcp = new TcpClient { NoDelay = true };
            _tcp.Connect(host, 9875);
            _net = _tcp.GetStream();
            _parser.MessageReceived += m => { lock (_lock) _messages.Add((_clock.Elapsed.TotalSeconds, m)); };
            Task.Run(async () =>
            {
                var buf = new byte[65536];
                try { int n; while ((n = await _net.ReadAsync(buf, _cts.Token)) > 0) _parser.Feed(buf, 0, n); }
                catch (Exception) { }
            });
            Thread.Sleep(300);
        }

        public double Now => _clock.Elapsed.TotalSeconds;

        public void Send(params byte[] msg)
        {
            for (int i = 0; i < msg.Length; i += 4096) _net.Write(msg, i, Math.Min(4096, msg.Length - i));
        }

        public List<(double Sec, byte[] Msg)> Since(double t)
        {
            lock (_lock) return _messages.Where(m => m.Sec >= t).ToList();
        }

        public byte[]? Request(byte[] msg, Func<byte[], bool> match, int timeoutMs = 8000)
        {
            double t = Now;
            Send(msg);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var hit = Since(t).Select(m => m.Msg).FirstOrDefault(match);
                if (hit != null) return hit;
                Thread.Sleep(20);
            }
            return null;
        }

        public void Dispose() { _cts.Cancel(); _tcp.Dispose(); }
    }

    // Kronos header F0 42 3g 68 (function at 4), Nautilus F0 42 3g 00 01 5D (function at 6).
    sealed record Board(string Name, IKorgSysExCodec Codec, int FuncIndex, byte ProgVersion);
    static readonly Board Kronos = new("Kronos", KronosSysExCodec.Instance, 4, 5);
    static readonly Board Nautilus = new("Nautilus", NautilusSysExCodec.Instance, 6, 6);

    static byte[]? ReadEditBuffer(Bridge b, Board board, int obj)
    {
        var r = b.Request(board.Codec.KorgMessage(0x74, (byte)obj), m => board.Codec.HasHeaderAt(m, 0, 0x75));
        int data = board.FuncIndex + 3;   // func, obj, version
        return r == null ? null : KronosSysEx.Decode8to7(r, data, r.Length - data - 1);
    }

    static bool WriteEditBuffer(Bridge b, Board board, int obj, byte version, byte[] body)
    {
        var payload = new byte[] { (byte)obj, version }.Concat(KronosSysEx.Encode7to8(body, 0, body.Length)).ToArray();
        var r = b.Request(board.Codec.KorgMessage(0x75, payload), m => board.Codec.ParseReply(m) != null);
        return r != null && board.Codec.ParseReply(r) == 0;
    }

    static List<(double, byte[])> Play(Bridge b, double holdSec)
    {
        Thread.Sleep(500);
        double t0 = b.Now;
        foreach (int n in Held) b.Send(0x90, (byte)n, 100);
        Thread.Sleep((int)(holdSec * 1000));
        foreach (int n in Held) b.Send(0x80, (byte)n, 0);
        Thread.Sleep(1000);
        return b.Since(t0).Where(m => (m.Msg[0] & 0xE0) == 0x80).Select(m => (m.Sec - t0, m.Msg)).ToList();
    }

    sealed record Render(string Name, bool DrumParams, List<(double, byte[])> Events, double Tempo);

    // GE RTP Info (obj 0x0C, index = GE Number): name + the 32 RT params' defaults. Null when the
    // index has no GE.
    static ObjectDump? ReadGeRtp(Bridge kb, int ge)
    {
        var reply = kb.Request(Kronos.Codec.BuildObjectDumpRequest(0x0C, 0, ge),
            m => Kronos.Codec.HasHeaderAt(m, 0, 0x73) || Kronos.Codec.HasHeaderAt(m, 0, 0x24));
        if (reply == null) throw new TimeoutException("Kronos did not answer a GE RTP Info request");
        return Kronos.Codec.ParseObjectDump(reply);
    }

    // The 32-byte name field is NUL-terminated; bytes after the NUL are stale buffer contents.
    static string GeName(byte[] rtpBody)
    {
        int end = Array.IndexOf(rtpBody, (byte)0, 0, 32);
        return System.Text.Encoding.Latin1.GetString(rtpBody, 0, end < 0 ? 32 : end).TrimEnd();
    }

    static string CleanName(string logged) { int nul = logged.IndexOf('\0'); return (nul < 0 ? logged : logged[..nul]).TrimEnd(); }

    // Plays the held chord through GE `ge` and restores the Kronos edit buffer before returning.
    static Render? RenderGe(Bridge kb, int ge, byte[] kronosBase, double holdSec)
    {
        var rtp = ReadGeRtp(kb, ge);
        if (rtp == null || rtp.Body.Length < 1952) return null;
        string name = GeName(rtp.Body);
        if (name.Length == 0) return null;
        bool drumParams = Enumerable.Range(0, 32).Any(i =>
            System.Text.Encoding.ASCII.GetString(rtp.Body, 32 + i * 60, 48).StartsWith("Drum", StringComparison.OrdinalIgnoreCase));

        var render = (byte[])kronosBase.Clone();
        render[1814] = (byte)ge; render[1815] = (byte)(ge >> 8);
        render[1306] |= 0x80;   // KARMA on
        // Every render runs at RenderTempo whatever Program happens to be loaded, so batches resumed
        // after a reboot stay comparable.
        render[1304] = RenderTempo * 100 & 0xFF; render[1305] = RenderTempo * 100 >> 8;
        for (int i = 0; i < 32; i++)
        {
            // A GE reads the Program's 32 "GE - All" RT slots as its own parameters, so the
            // previous GE's values must be replaced with this GE's defaults.
            int r = 32 + i * 60 + 48, o = 1844 + i * 8;
            render[o] = 0xFF; render[o + 1] = 0;
            Array.Copy(rtp.Body, r + 4, render, o + 2, 6);   // default min, max, value (LE s16 each)
        }
        try
        {
            if (!WriteEditBuffer(kb, Kronos, 0, Kronos.ProgVersion, render)) throw new TimeoutException("Kronos did not accept the render edit buffer");
            var events = Play(kb, holdSec);
            return new Render(name, drumParams, events, RenderTempo);
        }
        finally { WriteEditBuffer(kb, Kronos, 0, Kronos.ProgVersion, kronosBase); }
    }

    // Drum GEs (by RT param names, or when most output falls outside the held chord's pitch
    // classes) don't follow the chord, so the Fixed encoding is preferred for them.
    static ArpPatternTranscriber.Result? TranscribeRender(Render r, int ge, out bool drum)
    {
        var notes = ArpPatternTranscriber.NotesFromMidi(r.Events);
        var heldPcs = Held.Select(h => h % 12).ToHashSet();
        drum = r.DrumParams || (notes.Count > 0 && notes.Count(n => !heldPcs.Contains(n.Pitch % 12)) * 2 > notes.Count);
        return ArpPatternTranscriber.Transcribe(notes, Held, r.Tempo, $"K{ge:0000} {r.Name}", preferFixed: drum);
    }

    static bool VerifyRestored(Bridge b, Board board, byte[] original) =>
        ReadEditBuffer(b, board, 0)?.AsSpan().SequenceEqual(original) == true;

    public static void Run(string kronosHost, string nautilusHost, int ge, int slot, double holdSec)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        if (slot is < 0 or > 127) { Console.WriteLine("bSlot must be 0-127."); Environment.Exit(1); }
        using var kb = new Bridge(kronosHost);
        using var nb = new Bridge(nautilusHost);

        var kronosBase = ReadEditBuffer(kb, Kronos, 0);
        var nautilusBase = ReadEditBuffer(nb, Nautilus, 0);
        if (kronosBase == null || nautilusBase == null) { Console.WriteLine("Couldn't read an edit buffer - aborting before any change."); Environment.Exit(1); return; }

        var render = RenderGe(kb, ge, kronosBase, holdSec);
        Console.WriteLine(VerifyRestored(kb, Kronos, kronosBase) ? "Kronos edit buffer restored." : "WARNING: Kronos edit buffer restore NOT verified.");
        if (render == null) { Console.WriteLine($"GE {ge}: no GE at this index, or the Kronos rejected the render."); Environment.Exit(1); return; }
        Console.WriteLine($"GE {ge} \"{render.Name}\"");
        var result = TranscribeRender(render, ge, out bool drum);
        if (result == null) { Console.WriteLine("Nothing to transcribe (no notes captured - is KARMA output reaching MIDI out?)."); Environment.Exit(1); return; }
        Console.WriteLine($"Transcribed: length {result.Length} steps, grid 1/{result.StepsPerQuarter * 4}, {result.Mode}{(drum ? " (drum)" : "")}, " +
            $"simulated score {result.Score:P0} (normal {result.NormalScore:P0}, fixed {result.FixedScore:P0})");
        foreach (var l in result.Lossy) Console.WriteLine($"  lossy: {l}");

        var write = nb.Request(Nautilus.Codec.BuildObjectDumpMessage(0x19, 0x40, slot, 0, result.Pattern), m => Nautilus.Codec.ParseReply(m) != null);
        var store = nb.Request(Nautilus.Codec.BuildStoreBankRequest(0x19, 0x40), m => Nautilus.Codec.ParseReply(m) != null);
        Console.WriteLine($"Nautilus B{slot:000}: write {Nautilus.Codec.ParseReply(write ?? [])}, store {Nautilus.Codec.ParseReply(store ?? [])} (0 = success)");

        var nautilusPlay = new List<(double, byte[])>();
        try
        {
            var play = (byte[])nautilusBase.Clone();
            int pn = ArpConversion.BankBPatternNumberBase + slot;
            play[ArpConversion.SceneBase] = (byte)pn; play[ArpConversion.SceneBase + 1] = (byte)(pn >> 8);
            play[1349] |= 0x02;   // Arp switch
            play[ArpConversion.SceneBase + 2] = (byte)((play[ArpConversion.SceneBase + 2] & ~0x1C) | ArpPatternTranscriber.ResolutionIndex(result.StepsPerQuarter) << 2);
            play[ArpConversion.SceneBase + 4] = 100;    // gate: pattern's own
            play[ArpConversion.SceneBase + 5] = 0x81;   // velocity: Step
            if (WriteEditBuffer(nb, Nautilus, 0, Nautilus.ProgVersion, play)) nautilusPlay = Play(nb, holdSec);
        }
        finally
        {
            bool ok = WriteEditBuffer(nb, Nautilus, 0, Nautilus.ProgVersion, nautilusBase) && VerifyRestored(nb, Nautilus, nautilusBase);
            Console.WriteLine(ok ? "Nautilus edit buffer restored." : "WARNING: Nautilus edit buffer restore NOT verified.");
        }

        // Step-aligned A/B: bucket both captures onto the grid, search the best whole-step
        // offset (Kronos input quantize / latency), compare each step's pitch set and velocities.
        double step = 60.0 / render.Tempo / result.StepsPerQuarter;
        static Dictionary<int, Dictionary<int, int>> Buckets(List<(double, byte[])> ev, double step, double hold)
        {
            var ons = ev.Where(e => (e.Item2[0] & 0xF0) == 0x90 && e.Item2[2] > 0).ToList();
            var b = new Dictionary<int, Dictionary<int, int>>();
            if (ons.Count == 0) return b;
            double t0 = ons[0].Item1;
            foreach (var (t, m) in ons.Where(e => e.Item1 - ons[0].Item1 < hold - 0.1))
            {
                int s = (int)Math.Round((t - t0) / step);
                if (!b.TryGetValue(s, out var d)) b[s] = d = new();
                d[m[1]] = m[2];
            }
            return b;
        }
        var kb2 = Buckets(render.Events, step, holdSec); var nb2 = Buckets(nautilusPlay, step, holdSec);
        bool SameSet(int s, int off) => nb2.TryGetValue(s + off, out var d) && d.Keys.ToHashSet().SetEquals(kb2[s].Keys);
        int best = Enumerable.Range(-3, 7).OrderByDescending(off => kb2.Keys.Count(s => SameSet(s, off))).First();
        int same = kb2.Keys.Count(s => SameSet(s, best));
        int velOk = kb2.Keys.Count(s => SameSet(s, best) && kb2[s].All(kv => Math.Abs(kv.Value - nb2[s + best][kv.Key]) <= 2));
        Console.WriteLine($"A/B: {kb2.Count} steps compared (offset {best:+0;-0;0}) - pitch sets {same}/{kb2.Count}, velocities {velOk}/{kb2.Count}");
        Environment.Exit(0);
    }

    static bool IsLinkFailure(Exception ex) =>
        ex is TimeoutException or IOException or SocketException or ObjectDisposedException
        || ex.InnerException is SocketException;

    // Replaces a dead bridge: up to 10 attempts with growing waits (30 s .. 5 min), then puts the
    // Kronos edit buffer back to the run's snapshot, since the dropped render may have left its patch.
    static bool Reconnect(ref Bridge kb, string host, byte[] kronosBase)
    {
        try { kb.Dispose(); } catch (Exception) { }
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(300, 30 * attempt)));
            try
            {
                var fresh = new Bridge(host);
                if (ReadEditBuffer(fresh, Kronos, 0) != null
                    && WriteEditBuffer(fresh, Kronos, 0, Kronos.ProgVersion, kronosBase) && VerifyRestored(fresh, Kronos, kronosBase))
                {
                    Console.WriteLine($"Reconnected (attempt {attempt}); Kronos edit buffer restored.");
                    kb = fresh;
                    return true;
                }
                fresh.Dispose();
            }
            catch (Exception ex) when (IsLinkFailure(ex)) { }
            Console.WriteLine($"Reconnect attempt {attempt} failed.");
        }
        Console.WriteLine("Kronos unreachable after 10 attempts - stopping; rerun to resume.");
        return false;
    }

    // `--kronos-restore-editbuf <kronosHost> <snapshot.bin>` - writes a saved Program edit buffer
    // (e.g. a batch run's kronos_editbuf_<timestamp>.bin) back to the Kronos and verifies it.
    public static void RunRestoreEditBuffer(string kronosHost, string snapshotPath)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        var snapshot = File.ReadAllBytes(snapshotPath);
        using var kb = new Bridge(kronosHost);
        var before = ReadEditBuffer(kb, Kronos, 0);
        if (before == null) { Console.WriteLine("Couldn't read the Kronos edit buffer."); Environment.Exit(1); return; }
        Console.WriteLine($"Current edit buffer: {before.Length} bytes, \"{System.Text.Encoding.ASCII.GetString(before, 0, 24).TrimEnd('\0', ' ')}\"; snapshot {snapshot.Length} bytes.");
        if (before.AsSpan().SequenceEqual(snapshot)) { Console.WriteLine("Already identical - nothing to do."); Environment.Exit(0); return; }
        if (before.Length != snapshot.Length) { Console.WriteLine("Size differs from the snapshot (a different Program type is loaded) - not writing."); Environment.Exit(1); return; }
        bool ok = WriteEditBuffer(kb, Kronos, 0, Kronos.ProgVersion, snapshot) && VerifyRestored(kb, Kronos, snapshot);
        Console.WriteLine(ok ? "Restored and verified." : "Restore NOT verified.");
        Environment.Exit(ok ? 0 : 1);
    }

    // results.jsonl can hold several lines per GE (a redo pass appends a newer one): the last wins.
    static Dictionary<int, System.Text.Json.JsonElement> LatestEntries(string log) =>
        File.Exists(log)
            ? File.ReadLines(log).Where(l => l.Length > 0).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .GroupBy(j => j.GetProperty("ge").GetInt32()).ToDictionary(g => g.Key, g => g.Last())
            : new();

    // `--karma-ge-batch <kronosHost> <from> <to> <outDir> [holdSec]` - renders and transcribes a
    // GE range on the Kronos only. Per GE: raw/GE####.txt (the capture, so everything can be
    // re-transcribed offline), GE####.bin (the pattern) and one JSON line in results.jsonl.
    // Resumes by skipping GEs already logged; restores the Kronos edit buffer after every GE (each
    // run's starting edit buffer is also saved as kronos_editbuf_<timestamp>.bin in outDir).
    // redoNonPeriodic: re-render only GEs whose latest entry found no loop and was recorded with a
    // shorter hold than holdSec (a longer capture can reveal periods up to 64 steps at coarse grids).
    public static void RunBatch(string kronosHost, int from, int to, string outDir, double holdSec, bool redoNonPeriodic = false)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        Directory.CreateDirectory(Path.Combine(outDir, "raw"));
        string log = Path.Combine(outDir, "results.jsonl");
        var latest = LatestEntries(log);
        // Two full 64-step loops at the GE's own grid, plus a second of margin, capped at holdSec.
        double HoldFor(System.Text.Json.JsonElement j) =>
            Math.Min(holdSec, 2 * ArpPatternTranscriber.MaxSteps * 60.0 / RenderTempo / (int.Parse(j.GetProperty("grid").GetString()!.Split('/')[1]) / 4) + 1);
        bool NeedsRedo(System.Text.Json.JsonElement j) =>
            j.GetProperty("status").GetString() == "ok"
            && j.TryGetProperty("periodic", out var per) && !per.GetBoolean()
            && (j.TryGetProperty("hold", out var h) ? h.GetDouble() : 10) < HoldFor(j) - 0.5;
        var done = redoNonPeriodic
            ? latest.Where(kv => !NeedsRedo(kv.Value)).Select(kv => kv.Key).ToHashSet()
            : latest.Keys.ToHashSet();
        if (redoNonPeriodic) Console.WriteLine($"Redo pass: {latest.Count(kv => NeedsRedo(kv.Value))} non-periodic GEs to re-render (hold per grid, max {holdSec} s).");

        var kb = new Bridge(kronosHost);
        var kronosBase = ReadEditBuffer(kb, Kronos, 0);
        if (kronosBase == null) { Console.WriteLine("Couldn't read the Kronos edit buffer - aborting before any change."); Environment.Exit(1); return; }
        // One snapshot per run: the Kronos may have booted into a different Program since the last one.
        // kronos_editbuf_before.bin (first run only) stays as the tempo fallback for old log lines.
        string basePath = Path.Combine(outDir, $"kronos_editbuf_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
        File.WriteAllBytes(basePath, kronosBase);
        string firstRunPath = Path.Combine(outDir, "kronos_editbuf_before.bin");
        if (!File.Exists(firstRunPath)) File.WriteAllBytes(firstRunPath, kronosBase);
        int exitCode = 0;

        try
        {
            for (int ge = from; ge <= to; ge++)
            {
                if (done.Contains(ge)) continue;
                var line = new Dictionary<string, object?> { ["ge"] = ge };
                try
                {
                    double hold = redoNonPeriodic && latest.TryGetValue(ge, out var prev) ? HoldFor(prev) : holdSec;
                    var render = RenderGe(kb, ge, kronosBase, hold);
                    if (render == null) line["status"] = "no-ge";
                    else
                    {
                        line["name"] = render.Name;
                        File.WriteAllLines(Path.Combine(outDir, "raw", $"GE{ge:0000}.txt"),
                            render.Events.Select(e => $"{e.Item1.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} {Convert.ToHexString(e.Item2)}"));
                        var result = TranscribeRender(render, ge, out bool drum);
                        line["drum"] = drum;
                        line["tempo"] = render.Tempo;
                        line["hold"] = hold;
                        if (result == null) line["status"] = "silent";
                        else
                        {
                            File.WriteAllBytes(Path.Combine(outDir, $"GE{ge:0000}.bin"), result.Pattern);
                            line["status"] = "ok";
                            line["mode"] = result.Mode.ToString();
                            line["grid"] = $"1/{result.StepsPerQuarter * 4}";
                            line["length"] = result.Length;
                            line["score"] = Math.Round(result.Score, 3);
                            line["normalScore"] = Math.Round(result.NormalScore, 3);
                            line["fixedScore"] = Math.Round(result.FixedScore, 3);
                            line["lossySteps"] = result.Lossy.Count;
                            line["introSteps"] = result.IntroSteps;
                            line["periodic"] = result.Periodic;
                        }
                    }
                }
                catch (Exception ex) when (IsLinkFailure(ex))
                {
                    // Not a property of this GE: the bridge dropped the connection, or the Kronos is
                    // rebooting/busy. Reconnect, put the edit buffer back, and retry the same GE.
                    Console.WriteLine($"GE {ge}: link failure ({ex.GetType().Name}: {ex.Message}) - reconnecting.");
                    if (!Reconnect(ref kb, kronosHost, kronosBase)) { exitCode = 3; break; }
                    ge--;
                    continue;
                }
                catch (Exception ex) { line["status"] = "error"; line["error"] = ex.Message; }
                // A non-ok result is only trusted if the Kronos is demonstrably still answering.
                if ((string?)line["status"] != "ok")
                {
                    bool alive;
                    try { alive = ReadEditBuffer(kb, Kronos, 0) != null; } catch (Exception ex) when (IsLinkFailure(ex)) { alive = false; }
                    if (!alive)
                    {
                        Console.WriteLine($"GE {ge}: Kronos stopped answering - not logging this GE; reconnecting.");
                        if (!Reconnect(ref kb, kronosHost, kronosBase)) { exitCode = 3; break; }
                        ge--;
                        continue;
                    }
                }
                string json = System.Text.Json.JsonSerializer.Serialize(line);
                File.AppendAllText(log, json + "\n");
                Console.WriteLine(json);
            }
        }
        finally
        {
            bool ok;
            try { ok = WriteEditBuffer(kb, Kronos, 0, Kronos.ProgVersion, kronosBase) && VerifyRestored(kb, Kronos, kronosBase); }
            catch (Exception ex) when (IsLinkFailure(ex)) { ok = false; }
            Console.WriteLine(ok ? "Kronos edit buffer restored." : "WARNING: Kronos edit buffer restore NOT verified - original saved in " + basePath +
                " (restore with --kronos-restore-editbuf)");
            kb.Dispose();
        }
        Environment.Exit(exitCode);
    }

    // `--karma-ge-pack <batchDir> <out.bin> [minScore]` - packs a --karma-ge-batch output folder
    // into the KarmaGePatternLibrary format (Resources/KronosKarmaGePatterns.bin). Only "ok"
    // entries scoring at least minScore (default 0) are included; the converter applies its own
    // threshold on top.
    public static void RunPack(string batchDir, string outPath, double minScore)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        var entries = new List<KarmaGePatternLibrary.Entry>();
        foreach (var j in LatestEntries(Path.Combine(batchDir, "results.jsonl")).Values)
        {
            if (j.GetProperty("status").GetString() != "ok") continue;
            int ge = j.GetProperty("ge").GetInt32();
            double score = j.GetProperty("score").GetDouble();
            string bin = Path.Combine(batchDir, $"GE{ge:0000}.bin");
            if (score < minScore || !File.Exists(bin)) continue;
            int spq = int.Parse(j.GetProperty("grid").GetString()!.Split('/')[1]) / 4;
            var mode = Enum.Parse<ArpPatternTranscriber.Encoding>(j.GetProperty("mode").GetString()!);
            entries.Add(new KarmaGePatternLibrary.Entry(ge, mode, spq, score, File.ReadAllBytes(bin)));
        }
        File.WriteAllBytes(outPath, KarmaGePatternLibrary.Pack(entries));
        Console.WriteLine($"Packed {entries.Count} GE patterns -> {outPath}");
        Environment.Exit(0);
    }

    // `--karma-ge-retranscribe <batchDir>` - re-runs the transcriber over a batch's saved raw
    // captures (no hardware), rewriting GE####.bin and results.jsonl. `drum` is taken from the
    // existing log, since the RT-param half of that hint isn't in the capture.
    public static void RunRetranscribe(string batchDir)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        string log = Path.Combine(batchDir, "results.jsonl");
        var baseBody = File.ReadAllBytes(Path.Combine(batchDir, "kronos_editbuf_before.bin"));
        double tempo = (baseBody[1304] | baseBody[1305] << 8) / 100.0;
        var lines = new List<string>();
        int changed = 0;
        foreach (var j in LatestEntries(log).OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            string raw = j.GetRawText();
            int ge = j.GetProperty("ge").GetInt32();
            string capture = Path.Combine(batchDir, "raw", $"GE{ge:0000}.txt");
            if (!j.TryGetProperty("name", out var nameEl) || !File.Exists(capture)) { lines.Add(raw); continue; }
            var events = File.ReadLines(capture).Where(l => l.Length > 0).Select(l =>
            {
                var parts = l.Split(' ');
                return (double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(parts[1]));
            }).ToList();
            double lineTempo = j.TryGetProperty("tempo", out var te) ? te.GetDouble() : tempo;
            var render = new Render(CleanName(nameEl.GetString()!), j.TryGetProperty("drum", out var d) && d.GetBoolean(), events, lineTempo);
            var result = TranscribeRender(render, ge, out bool drum);
            var line = new Dictionary<string, object?> { ["ge"] = ge, ["name"] = render.Name, ["drum"] = drum, ["tempo"] = lineTempo,
                ["hold"] = j.TryGetProperty("hold", out var ho) ? ho.GetDouble() : 10.0 };
            if (result == null) line["status"] = "silent";
            else
            {
                File.WriteAllBytes(Path.Combine(batchDir, $"GE{ge:0000}.bin"), result.Pattern);
                line["status"] = "ok";
                line["mode"] = result.Mode.ToString();
                line["grid"] = $"1/{result.StepsPerQuarter * 4}";
                line["length"] = result.Length;
                line["score"] = Math.Round(result.Score, 3);
                line["normalScore"] = Math.Round(result.NormalScore, 3);
                line["fixedScore"] = Math.Round(result.FixedScore, 3);
                line["lossySteps"] = result.Lossy.Count;
                line["introSteps"] = result.IntroSteps;
                line["periodic"] = result.Periodic;
            }
            string json = System.Text.Json.JsonSerializer.Serialize(line);
            if (json != raw) changed++;
            lines.Add(json);
        }
        File.WriteAllLines(log, lines);
        Console.WriteLine($"Re-transcribed {lines.Count} entries, {changed} changed.");
        Environment.Exit(0);
    }

    // `--karma-ge-verify <nautilusHost> <batchDir> <bSlot> <ge> [ge...]` - Nautilus-only check of the
    // playback model: each GE####.bin is stored to B<bSlot>, played with the held chord, and the
    // Nautilus output is compared step by step with ArpPatternTranscriber.SimulateStep. Together with
    // the batch's offline score (simulation vs the Kronos capture) this covers the whole chain without
    // needing the Kronos. The Nautilus edit buffer is restored after each GE.
    public static void RunVerify(string nautilusHost, string batchDir, int slot, IEnumerable<int> ges, double holdSec)
    {
        NautilusSysExCapture.AttachOrAllocConsole();
        var grids = LatestEntries(Path.Combine(batchDir, "results.jsonl")).Values
            .Where(j => j.GetProperty("status").GetString() == "ok")
            .ToDictionary(j => j.GetProperty("ge").GetInt32(), j => int.Parse(j.GetProperty("grid").GetString()!.Split('/')[1]) / 4);
        using var nb = new Bridge(nautilusHost);
        var nautilusBase = ReadEditBuffer(nb, Nautilus, 0);
        if (nautilusBase == null) { Console.WriteLine("Couldn't read the Nautilus edit buffer."); Environment.Exit(1); return; }
        double tempo = (nautilusBase[1350] | nautilusBase[1351] << 8) / 100.0;
        try
        {
            foreach (int ge in ges)
            {
                string bin = Path.Combine(batchDir, $"GE{ge:0000}.bin");
                if (!File.Exists(bin) || !grids.TryGetValue(ge, out int spq)) { Console.WriteLine($"GE {ge}: not in batch"); continue; }
                var pattern = File.ReadAllBytes(bin);
                nb.Request(Nautilus.Codec.BuildObjectDumpMessage(0x19, 0x40, slot, 0, pattern), m => Nautilus.Codec.ParseReply(m) != null);
                var play = (byte[])nautilusBase.Clone();
                ArpConversion.SetProgramSceneTranscribed(play, 0b0001, slot, spq);
                play[1349] |= 0x02;   // Arp switch
                if (!WriteEditBuffer(nb, Nautilus, 0, Nautilus.ProgVersion, play)) { Console.WriteLine($"GE {ge}: edit buffer rejected"); continue; }
                var ev = Play(nb, holdSec);
                double step = 60.0 / tempo / spq;
                var ons = ev.Where(e => (e.Item2[0] & 0xF0) == 0x90 && e.Item2[2] > 0).ToList();
                var got = new Dictionary<int, HashSet<int>>();
                if (ons.Count > 0)
                    foreach (var (t, m) in ons.Where(e => e.Item1 - ons[0].Item1 < holdSec - 0.1))
                    {
                        int st = (int)Math.Round((t - ons[0].Item1) / step);
                        if (!got.TryGetValue(st, out var set)) got[st] = set = new();
                        set.Add(m[1]);
                    }
                // The first sounding step is step 0 of the capture; leading rests shift it.
                int first = Enumerable.Range(0, Math.Max(1, (int)pattern[25])).FirstOrDefault(i => ArpPatternTranscriber.SimulateStep(pattern, Held, i).Count > 0);
                int n = (int)((holdSec - 0.2) / step);
                int match = Enumerable.Range(0, n).Count(i =>
                    ArpPatternTranscriber.SimulateStep(pattern, Held, first + i).SetEquals(got.GetValueOrDefault(i) ?? new HashSet<int>()));
                Console.WriteLine($"GE {ge}: Nautilus vs simulation {match}/{n} steps (grid 1/{spq * 4}, {((pattern[24] & 0x10) != 0 ? "Fixed" : "Normal")})");
            }
        }
        finally
        {
            bool ok = WriteEditBuffer(nb, Nautilus, 0, Nautilus.ProgVersion, nautilusBase) && VerifyRestored(nb, Nautilus, nautilusBase);
            Console.WriteLine(ok ? "Nautilus edit buffer restored." : "WARNING: Nautilus edit buffer restore NOT verified.");
        }
        Environment.Exit(0);
    }
}
