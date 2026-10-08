using System.IO;
using System.Windows;
using KronosScreenRemote.ViewModels;

namespace KronosScreenRemote;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostic: `--librarian-selftest` runs the pure Librarian model
        // checks, writes OK / FAIL to a temp file, and exits without a window. Used to
        // verify the reference/bank/plan logic off-hardware (no unit-test project here).
        if (e.Args.Contains("--librarian-selftest"))
        {
            // Several Core/Sample/* self-tests drive a real SampleEditorViewModel
            // through OpenCollection, which writes Recent Files to the REAL
            // settings.json (Storage.SaveSettings has no test-injectable override).
            // Snapshot/restore around the whole run (not per-test) so a person running
            // --librarian-selftest on their own machine gets their real settings back
            // untouched, however many self-tests end up touching it. Safe to wrap with
            // a try/finally here specifically because Environment.Exit is called AFTER
            // this block returns, not from inside it - Environment.Exit doesn't run
            // pending finally blocks, so this only works because of that ordering.
            List<string> fails = null!;
            Storage.RunWithSettingsFileProtected(() =>
            {
            fails = Librarian.SelfTest();
            fails.AddRange(BatchLibrarian.SelfTest());
            fails.AddRange(ObjectBodySelfTests.SelfTest());
            fails.AddRange(SampleReferenceWalkerSelfTests.SelfTest());
            fails.AddRange(ExsOptionFileSelfTests.SelfTest());
            fails.AddRange(LocalLibrarySelfTests.SelfTest());
            fails.AddRange(LocalLibrarySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LocalEditOpsSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SyncPipelineSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(DataSafetySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(PcgFileSelfTests.SelfTest());
            fails.AddRange(PcgConversionSelfTests.SelfTest());
            fails.AddRange(PcgPaneLoadSelfTests.SelfTest());
            fails.AddRange(PcgSearchFilterSelfTests.SelfTest());
            fails.AddRange(CrossPanePlacementSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LocalCutCopyPasteSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(ReadOnlyBankBrowseSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LibrarianSettingsApplySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeCacheSelfTests.SelfTest());
            fails.AddRange(DependencyResolutionSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(LibrarianDependencyCacheSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeTreeVisibilitySelfTests.SelfTest());
            fails.AddRange(PaneSelectionSelfTests.SelfTest());
            fails.AddRange(RawKeyMapSelfTests.SelfTest());
            fails.AddRange(DetectionSelfTests.SelfTest());
            fails.AddRange(DumpGateSelfTests.SelfTest());
            fails.AddRange(MidiTransportCoordinatorSelfTests.SelfTest());
            fails.AddRange(ScreenSessionSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(StreamReceiverV3SelfTests.SelfTest());
            fails.AddRange(MidiTransportReplySelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeGroupPlacementSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeAutoFillSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(MergeOrderingSelfTests.SelfTest());
            fails.AddRange(MergePullCountSelfTests.SelfTest());
            fails.AddRange(LibrarianUndoSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(PlacementStalenessGateSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SyncCancellationSelfTests.SelfTestAsync().GetAwaiter().GetResult());
            fails.AddRange(SampleSelfTests.SelfTest());
            fails.AddRange(SampleDspSelfTests.SelfTest());
            fails.AddRange(SampleRemoteSelfTests.SelfTest());
            fails.AddRange(SampleTranscodeSelfTests.SelfTest());
            fails.AddRange(SampleStereoSelfTests.SelfTest());
            fails.AddRange(SampleTreeSelectionSelfTests.SelfTest());
            fails.AddRange(SamplePhase5SelfTests.SelfTest());
            fails.AddRange(SamplePhase6SelfTests.SelfTest());
            fails.AddRange(SamplePhase7SelfTests.SelfTest());
            fails.AddRange(SamplePhase8SelfTests.SelfTest());
            fails.AddRange(SamplePhase9SelfTests.SelfTest());
            fails.AddRange(SamplePhase10SelfTests.SelfTest());
            fails.AddRange(SamplePhase11SelfTests.SelfTest());
            fails.AddRange(SamplePhase12SelfTests.SelfTest());
            fails.AddRange(SamplePhase13SelfTests.SelfTest());
            fails.AddRange(SamplePhase14SelfTests.SelfTest());
            fails.AddRange(SamplePhase15SelfTests.SelfTest());
            fails.AddRange(SamplePhase16SelfTests.SelfTest());
            fails.AddRange(ReviewHardeningSelfTests.SelfTest());
            fails.AddRange(SamplePhase17SelfTests.SelfTest());
            fails.AddRange(SamplePhase18SelfTests.SelfTest());
            fails.AddRange(SamplePhase19SelfTests.SelfTest());
            fails.AddRange(SamplePhase20SelfTests.SelfTest());
            fails.AddRange(SamplePhase21SelfTests.SelfTest());
            fails.AddRange(FtpPathSafetySelfTests.SelfTest());
            });
            var outPath = Path.Combine(Path.GetTempPath(), "kronos_librarian_selftest.txt");
            File.WriteAllText(outPath, fails.Count == 0 ? "OK" : "FAIL: " + string.Join(", ", fails));
            Environment.Exit(fails.Count == 0 ? 0 : 1);
        }

        // Headless diagnostic: `--ui-theme-smoketest` constructs every Window/Dialog with
        // dummy args to catch XamlParseException from the Themes/Dark.xaml migration
        // (missing StaticResource, bad template part) without showing a window.
        if (e.Args.Contains("--ui-theme-smoketest"))
        {
            UiThemeSmokeTest.Run();
        }

        // Headless diagnostic: `--dump-pcg-refs <path-to.pcg> [name filter]` - see
        // Tools/PcgRefDump.cs for why this exists (settling a suspected Program-bank
        // off-by-one in Combi timbre reference decoding using a real file's own bytes).
        int dumpRefsIdx = Array.IndexOf(e.Args, "--dump-pcg-refs");
        if (dumpRefsIdx >= 0 && dumpRefsIdx + 1 < e.Args.Length)
        {
            string? filter = dumpRefsIdx + 2 < e.Args.Length ? e.Args[dumpRefsIdx + 2] : null;
            PcgRefDump.Run(e.Args[dumpRefsIdx + 1], filter);
        }

        // Headless diagnostic: `--dump-pcg-structure <path-to.pcg>` - see
        // Tools/PcgStructureDump.cs for why this exists (validating the DBK1/WBK1/GLB1
        // extractor additions against real hardware-written files).
        int dumpStructureIdx = Array.IndexOf(e.Args, "--dump-pcg-structure");
        if (dumpStructureIdx >= 0 && dumpStructureIdx + 1 < e.Args.Length)
        {
            PcgStructureDump.Run(e.Args[dumpStructureIdx + 1]);
        }

        // Headless diagnostic: `--dump-nautilus-pcg-structure <path-to.pcg>` - see
        // Tools/NautilusPcgStructureDump.cs for why this exists (validating
        // NautilusPcgObjectExtractor against real Nautilus-written files).
        int dumpNautilusStructureIdx = Array.IndexOf(e.Args, "--dump-nautilus-pcg-structure");
        if (dumpNautilusStructureIdx >= 0 && dumpNautilusStructureIdx + 1 < e.Args.Length)
        {
            NautilusPcgStructureDump.Run(e.Args[dumpNautilusStructureIdx + 1]);
        }

        // Headless diagnostic: `--nautilus-sysex-capture [device-name-match] [output-dir]` -
        // see Tools/NautilusSysExCapture.cs for why this exists (passive USB SysEx capture
        // against a real Nautilus, no daemon/TCP needed, settling the real Excl Header length).
        int nautilusSysexCaptureIdx = Array.IndexOf(e.Args, "--nautilus-sysex-capture");
        if (nautilusSysexCaptureIdx >= 0)
        {
            string? match  = nautilusSysexCaptureIdx + 1 < e.Args.Length ? e.Args[nautilusSysexCaptureIdx + 1] : null;
            string? outDir = nautilusSysexCaptureIdx + 2 < e.Args.Length ? e.Args[nautilusSysexCaptureIdx + 2] : null;
            NautilusSysExCapture.Run(match, outDir);
        }

        // Headless diagnostic: `--nautilus-sysex-roundtrip [device-name-match] [obj] [bank]
        // [index]` - active func 0x72 request / func 0x73 reply round trip against a real
        // Nautilus using the hardware-confirmed NautilusSysEx framing. Read-only (0x72/0x73
        // only - no Store/erase). Defaults to Program (obj=0) bank 0 index 0.
        int nautilusRoundtripIdx = Array.IndexOf(e.Args, "--nautilus-sysex-roundtrip");
        if (nautilusRoundtripIdx >= 0)
        {
            string? rtMatch = nautilusRoundtripIdx + 1 < e.Args.Length ? e.Args[nautilusRoundtripIdx + 1] : null;
            int rtObj   = nautilusRoundtripIdx + 2 < e.Args.Length ? int.Parse(e.Args[nautilusRoundtripIdx + 2]) : 0;
            int rtBank  = nautilusRoundtripIdx + 3 < e.Args.Length ? int.Parse(e.Args[nautilusRoundtripIdx + 3]) : 0;
            int rtIndex = nautilusRoundtripIdx + 4 < e.Args.Length ? int.Parse(e.Args[nautilusRoundtripIdx + 4]) : 0;
            NautilusSysExCapture.RunRequestReply(rtMatch, rtObj, rtBank, rtIndex);
        }

        // Headless diagnostic: `--nautilus-sysex-probe <device-name-match> <objFrom> <objTo>
        // [bank] [index] [output-dir] [nautilus|kronos] [count]` - read-only 0x72 sweep over obj
        // codes (and `count` consecutive indices), reporting 0x73 dumps or 0x24 reply codes.
        // Obj/bank accept hex with a 0x prefix.
        int nautilusProbeIdx = Array.IndexOf(e.Args, "--nautilus-sysex-probe");
        if (nautilusProbeIdx >= 0)
        {
            string? Arg(int k) => nautilusProbeIdx + k < e.Args.Length ? e.Args[nautilusProbeIdx + k] : null;
            static int Num(string? s, int dflt) => s == null ? dflt
                : s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s);
            NautilusSysExCapture.RunObjectProbe(Arg(1), Num(Arg(2), 0), Num(Arg(3), 0x1F),
                Num(Arg(4), 0), Num(Arg(5), 0), Arg(6),
                string.Equals(Arg(7), "kronos", StringComparison.OrdinalIgnoreCase) ? DeviceFamily.Kronos : DeviceFamily.Nautilus,
                Num(Arg(8), 1));
        }

        // Headless: `--karma-ge-verify <nautilusHost> <batchDir> <bSlot> <ge> [ge...]` - plays stored
        // transcriptions on the Nautilus and compares against the simulated playback model.
        int karmaVerifyIdx = Array.IndexOf(e.Args, "--karma-ge-verify");
        if (karmaVerifyIdx >= 0 && karmaVerifyIdx + 4 < e.Args.Length)
            KarmaGeTranscribe.RunVerify(e.Args[karmaVerifyIdx + 1], e.Args[karmaVerifyIdx + 2], int.Parse(e.Args[karmaVerifyIdx + 3]),
                e.Args.Skip(karmaVerifyIdx + 4).Select(int.Parse).ToList(), 6);

        // Headless: `--karma-ge-retranscribe <batchDir>` - offline re-run over saved captures.
        int karmaRetxIdx = Array.IndexOf(e.Args, "--karma-ge-retranscribe");
        if (karmaRetxIdx >= 0 && karmaRetxIdx + 1 < e.Args.Length)
            KarmaGeTranscribe.RunRetranscribe(e.Args[karmaRetxIdx + 1]);

        // Headless: `--karma-ge-pack <batchDir> <out.bin> [minScore]` - packs batch output into the
        // KarmaGePatternLibrary resource format.
        int karmaPackIdx = Array.IndexOf(e.Args, "--karma-ge-pack");
        if (karmaPackIdx >= 0 && karmaPackIdx + 2 < e.Args.Length)
            KarmaGeTranscribe.RunPack(e.Args[karmaPackIdx + 1], e.Args[karmaPackIdx + 2],
                karmaPackIdx + 3 < e.Args.Length ? double.Parse(e.Args[karmaPackIdx + 3], System.Globalization.CultureInfo.InvariantCulture) : 0);

        // Headless: `--kronos-restore-editbuf <kronosHost> <snapshot.bin>` - write back a saved edit buffer.
        int restoreIdx = Array.IndexOf(e.Args, "--kronos-restore-editbuf");
        if (restoreIdx >= 0 && restoreIdx + 2 < e.Args.Length)
            KarmaGeTranscribe.RunRestoreEditBuffer(e.Args[restoreIdx + 1], e.Args[restoreIdx + 2]);

        // Headless: `--karma-ge-batch <kronosHost> <from> <to> <outDir> [holdSec] [--redo-nonperiodic]` - Kronos-only
        // render + transcribe of a GE range to files, resumable. See Tools/KarmaGeTranscribe.cs.
        int karmaBatchIdx = Array.IndexOf(e.Args, "--karma-ge-batch");
        if (karmaBatchIdx >= 0 && karmaBatchIdx + 4 < e.Args.Length)
            KarmaGeTranscribe.RunBatch(e.Args[karmaBatchIdx + 1], int.Parse(e.Args[karmaBatchIdx + 2]), int.Parse(e.Args[karmaBatchIdx + 3]),
                e.Args[karmaBatchIdx + 4], karmaBatchIdx + 5 < e.Args.Length ? double.Parse(e.Args[karmaBatchIdx + 5], System.Globalization.CultureInfo.InvariantCulture) : 10,
                e.Args.Contains("--redo-nonperiodic"));

        // Headless: `--karma-ge-to-nautilus <kronosHost> <nautilusHost> <ge> <bSlot> [holdSec]` -
        // renders a KARMA GE on the Kronos, transcribes it to Nautilus Arp Pattern B<bSlot>
        // (stored), and A/B-plays it; both edit buffers are restored. See Tools/KarmaGeTranscribe.cs.
        int karmaGeIdx = Array.IndexOf(e.Args, "--karma-ge-to-nautilus");
        if (karmaGeIdx >= 0 && karmaGeIdx + 4 < e.Args.Length)
            KarmaGeTranscribe.Run(e.Args[karmaGeIdx + 1], e.Args[karmaGeIdx + 2], int.Parse(e.Args[karmaGeIdx + 3]),
                int.Parse(e.Args[karmaGeIdx + 4]), karmaGeIdx + 5 < e.Args.Length ? double.Parse(e.Args[karmaGeIdx + 5], System.Globalization.CultureInfo.InvariantCulture) : 8.5);

        // Headless: `--combi-factory-compare <kronos PRELOAD.PCG> <nautilus PRELOAD.PCG>` - offline
        // CombiConverter check against Korg's own Nautilus ports of the factory Combis.
        int combiCmpIdx = Array.IndexOf(e.Args, "--combi-factory-compare");
        if (combiCmpIdx >= 0 && combiCmpIdx + 2 < e.Args.Length)
            NautilusSysExCapture.RunCombiFactoryCompare(e.Args[combiCmpIdx + 1], e.Args[combiCmpIdx + 2]);

        // Headless: `--nautilus-convert-combi <kronos.pcg> <combi-name> [<device-match> <bank> <index>]`
        // - CombiConverter dry run, or write + Store (DESTRUCTIVE) into a Nautilus Combi slot.
        int convCombiIdx = Array.IndexOf(e.Args, "--nautilus-convert-combi");
        if (convCombiIdx >= 0 && convCombiIdx + 2 < e.Args.Length)
        {
            string? A(int k) => convCombiIdx + k < e.Args.Length ? e.Args[convCombiIdx + k] : null;
            static int? N(string? v) => v == null ? null : v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(v[2..], 16) : int.Parse(v);
            NautilusSysExCapture.RunConvertCombi(A(1)!, A(2)!, A(3), N(A(4)), N(A(5)));
        }

        // Headless diagnostic: `--nautilus-sysex-save-current <device-name-match> <obj> <out.bin>` -
        // read-only 0x74/0x75: saves an edit buffer's decoded body.
        int nautilusSaveCurIdx = Array.IndexOf(e.Args, "--nautilus-sysex-save-current");
        if (nautilusSaveCurIdx >= 0 && nautilusSaveCurIdx + 3 < e.Args.Length)
            NautilusSysExCapture.RunSaveCurrentObject(e.Args[nautilusSaveCurIdx + 1],
                int.Parse(e.Args[nautilusSaveCurIdx + 2]), e.Args[nautilusSaveCurIdx + 3]);

        // Headless diagnostic: `--nautilus-sysex-send-current <device-name-match> <obj> <version> <body.bin>`
        // - replaces an edit buffer via 0x75 and verifies via 0x74. Not stored.
        int nautilusSendCurIdx = Array.IndexOf(e.Args, "--nautilus-sysex-send-current");
        if (nautilusSendCurIdx >= 0 && nautilusSendCurIdx + 4 < e.Args.Length)
            NautilusSysExCapture.RunSendCurrentObject(e.Args[nautilusSendCurIdx + 1],
                int.Parse(e.Args[nautilusSendCurIdx + 2]), byte.Parse(e.Args[nautilusSendCurIdx + 3]), e.Args[nautilusSendCurIdx + 4]);

        // Headless diagnostic: `--nautilus-sysex-write-object <device-name-match> <obj> <bank>
        // <index> <version> <body.bin> [store]` - DESTRUCTIVE when `store` is given: writes a raw
        // decoded body into one slot, optionally Store Banks it, re-reads and byte-diffs.
        int nautilusWriteObjIdx = Array.IndexOf(e.Args, "--nautilus-sysex-write-object");
        if (nautilusWriteObjIdx >= 0)
        {
            string? Arg(int k) => nautilusWriteObjIdx + k < e.Args.Length ? e.Args[nautilusWriteObjIdx + k] : null;
            static int Num(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s);
            NautilusSysExCapture.RunWriteObject(Arg(1), Num(Arg(2)!), Num(Arg(3)!), Num(Arg(4)!), (byte)Num(Arg(5)!),
                Arg(6)!, string.Equals(Arg(7), "store", StringComparison.OrdinalIgnoreCase));
        }

        // Headless diagnostic: `--nautilus-sysex-writeback-test [device-name-match] [obj] [bank]
        // [index]` - DESTRUCTIVE: reads a real object, writes the SAME bytes back unchanged
        // (func 0x73), commits with Store Bank Request (func 0x76), re-reads, and byte-diffs
        // against the original. Only run with a fresh backup already in hand. Pauses for Enter
        // before the write/store step so it's never fired unattended.
        int nautilusWritebackIdx = Array.IndexOf(e.Args, "--nautilus-sysex-writeback-test");
        if (nautilusWritebackIdx >= 0)
        {
            string? wbMatch = nautilusWritebackIdx + 1 < e.Args.Length ? e.Args[nautilusWritebackIdx + 1] : null;
            int wbObj   = nautilusWritebackIdx + 2 < e.Args.Length ? int.Parse(e.Args[nautilusWritebackIdx + 2]) : 0;
            int wbBank  = nautilusWritebackIdx + 3 < e.Args.Length ? int.Parse(e.Args[nautilusWritebackIdx + 3]) : 0;
            int wbIndex = nautilusWritebackIdx + 4 < e.Args.Length ? int.Parse(e.Args[nautilusWritebackIdx + 4]) : 0;
            NautilusSysExCapture.RunWriteBackTest(wbMatch, wbObj, wbBank, wbIndex);
        }

        // Headless diagnostic: `--nautilus-convert-dry-run <kronos.pcg> <program-name>` - runs
        // ProgramConverter.KronosToNautilus against a real Kronos .pcg record and prints
        // warnings/refusals/result. No transport, no hardware, no write - safe to run anytime to
        // preview a conversion before committing to a hardware test. `<program-name>` must be
        // quoted if it contains spaces.
        int nautilusConvertDryRunIdx = Array.IndexOf(e.Args, "--nautilus-convert-dry-run");
        if (nautilusConvertDryRunIdx >= 0 && nautilusConvertDryRunIdx + 2 < e.Args.Length)
        {
            NautilusSysExCapture.RunConvertDryRun(e.Args[nautilusConvertDryRunIdx + 1], e.Args[nautilusConvertDryRunIdx + 2]);
        }

        // Headless diagnostic: `--nautilus-convert-and-write <kronos.pcg> <program-name>
        // <target-bank> <target-index> [device-name-match] [name-prefix]` - reads a real Program
        // from a real Kronos .pcg file, runs it through ProgramConverter.KronosToNautilus, and
        // writes the CONVERTED body (not a same-bytes echo, unlike --nautilus-sysex-writeback-test
        // above) into a target slot on the real Nautilus, committing with Store Bank Request.
        // Destructive - pauses for Enter before the write/store step. `<program-name>` must be
        // quoted if it contains spaces. Optional `[name-prefix]` renames the written copy to
        // `<prefix><program-name>` (e.g. "111-") so an exploratory test write is easy to find/
        // identify later without touching the source .pcg. See
        // Tools/NautilusSysExCapture.cs's RunConvertAndWrite.
        int nautilusConvertWriteIdx = Array.IndexOf(e.Args, "--nautilus-convert-and-write");
        if (nautilusConvertWriteIdx >= 0 && nautilusConvertWriteIdx + 4 < e.Args.Length)
        {
            string cwPath = e.Args[nautilusConvertWriteIdx + 1];
            string cwName = e.Args[nautilusConvertWriteIdx + 2];
            int cwBank  = int.Parse(e.Args[nautilusConvertWriteIdx + 3]);
            int cwIndex = int.Parse(e.Args[nautilusConvertWriteIdx + 4]);
            string? cwMatch = nautilusConvertWriteIdx + 5 < e.Args.Length ? e.Args[nautilusConvertWriteIdx + 5] : null;
            string? cwNamePrefix = nautilusConvertWriteIdx + 6 < e.Args.Length ? e.Args[nautilusConvertWriteIdx + 6] : null;
            NautilusSysExCapture.RunConvertAndWrite(cwPath, cwName, cwBank, cwIndex, cwMatch, cwNamePrefix);
        }

        // Headless diagnostic: `--nautilus-diff-after-edit <kronos.pcg> <program-name>
        // <post-edit.pcg> [post-edit-name]` - offline, no hardware. Reproduces the exact body
        // --nautilus-convert-and-write would have written and byte-diffs it against the real
        // post-front-panel-edit object pulled back from the Nautilus. See
        // Tools/NautilusSysExCapture.cs's RunDiffAfterEdit for the full rationale.
        int nautilusDiffAfterEditIdx = Array.IndexOf(e.Args, "--nautilus-diff-after-edit");
        if (nautilusDiffAfterEditIdx >= 0 && nautilusDiffAfterEditIdx + 3 < e.Args.Length)
        {
            string daePath = e.Args[nautilusDiffAfterEditIdx + 1];
            string daeName = e.Args[nautilusDiffAfterEditIdx + 2];
            string daePostEditPath = e.Args[nautilusDiffAfterEditIdx + 3];
            string? daePostEditName = nautilusDiffAfterEditIdx + 4 < e.Args.Length ? e.Args[nautilusDiffAfterEditIdx + 4] : null;
            NautilusSysExCapture.RunDiffAfterEdit(daePath, daeName, daePostEditPath, daePostEditName);
        }

        // Headless diagnostic: `--nautilus-convert-bank <kronos.pcg> <source-bank> <target-bank>
        // [device-name-match]` - DESTRUCTIVE: converts every Program in a whole Kronos wire bank
        // (decimal, e.g. 76 for 0x4C) into the same slot numbers on a Nautilus Program bank,
        // committing with one Store Bank Request at the end. Pauses once for Enter before any
        // write. See Tools/NautilusSysExCapture.cs's RunConvertBank.
        int nautilusConvertBankIdx = Array.IndexOf(e.Args, "--nautilus-convert-bank");
        if (nautilusConvertBankIdx >= 0 && nautilusConvertBankIdx + 3 < e.Args.Length)
        {
            string cbPath = e.Args[nautilusConvertBankIdx + 1];
            int cbSourceBank = int.Parse(e.Args[nautilusConvertBankIdx + 2]);
            int cbTargetBank = int.Parse(e.Args[nautilusConvertBankIdx + 3]);
            string? cbMatch = nautilusConvertBankIdx + 4 < e.Args.Length ? e.Args[nautilusConvertBankIdx + 4] : null;
            NautilusSysExCapture.RunConvertBank(cbPath, cbSourceBank, cbTargetBank, cbMatch);
        }

        // Headless diagnostic: `--nautilus-sysex-bank-digest-scan [device-name-match] [obj]` -
        // read-only func 0x37/0x38 Bank Digest scan across the documented Program bank-byte
        // ranges (Int A-F, User G-T). Match against `--dump-nautilus-bank-digests` to decode
        // BankIdRaw. Defaults to obj=0 (Program).
        int nautilusDigestScanIdx = Array.IndexOf(e.Args, "--nautilus-sysex-bank-digest-scan");
        if (nautilusDigestScanIdx >= 0)
        {
            string? dsMatch = nautilusDigestScanIdx + 1 < e.Args.Length ? e.Args[nautilusDigestScanIdx + 1] : null;
            int dsObj = nautilusDigestScanIdx + 2 < e.Args.Length ? int.Parse(e.Args[nautilusDigestScanIdx + 2]) : 0;
            NautilusSysExCapture.RunBankDigestScan(dsMatch, dsObj);
        }

        // Headless diagnostic: `--dump-nautilus-bank-digests <path-to.pcg>` - see
        // Tools/NautilusBankDigestDump.cs for why this exists (offline half of the BankIdRaw
        // decode - compute the same SHA-1 the instrument reports live, from a real .pcg file).
        int dumpNautilusDigestsIdx = Array.IndexOf(e.Args, "--dump-nautilus-bank-digests");
        if (dumpNautilusDigestsIdx >= 0 && dumpNautilusDigestsIdx + 1 < e.Args.Length)
        {
            NautilusBankDigestDump.Run(e.Args[dumpNautilusDigestsIdx + 1]);
        }

        // Headless diagnostic: `--dump-pcg-blanks <path-to.pcg>` - see
        // Tools/PcgBlankTemplateDump.cs for why this exists (finding the real Drum Kit/Wave
        // Sequence blank-template bytes from a real file instead of guessing).
        int dumpBlanksIdx = Array.IndexOf(e.Args, "--dump-pcg-blanks");
        if (dumpBlanksIdx >= 0 && dumpBlanksIdx + 1 < e.Args.Length)
        {
            PcgBlankTemplateDump.Run(e.Args[dumpBlanksIdx + 1]);
        }

        // Headless diagnostic: `--dump-pcg-osc-refs <path-to.pcg>` - see Tools/PcgOscRefDump.cs
        // for why this exists (settling how an HD-1 Program's oscillator references a Wave
        // Sequence/Drum Kit, undocumented in the text SysEx dump).
        int dumpOscRefsIdx = Array.IndexOf(e.Args, "--dump-pcg-osc-refs");
        if (dumpOscRefsIdx >= 0 && dumpOscRefsIdx + 1 < e.Args.Length)
        {
            PcgOscRefDump.Run(e.Args[dumpOscRefsIdx + 1]);
        }

        // Headless diagnostic: `--dump-pcg-drumwave-refs <path-to.pcg> <program-name-substring>`
        // - see Tools/PcgDrumWaveRefDump.cs (verifies the linear Drum Kit/Wave Seq addressing
        // from KRONOS_MIDI_SysEx.txt's [0x71] doc against real Program bytes).
        int dumpDrumWaveIdx = Array.IndexOf(e.Args, "--dump-pcg-drumwave-refs");
        if (dumpDrumWaveIdx >= 0 && dumpDrumWaveIdx + 2 < e.Args.Length)
        {
            PcgDrumWaveRefDump.Run(e.Args[dumpDrumWaveIdx + 1], e.Args[dumpDrumWaveIdx + 2]);
        }

        // Headless diagnostic: `--sample-format-fixture-check <folder>` - the runnable
        // acceptance gate for the Core/Sample/* format-layer port (byte-identical
        // round-trip against a local, gitignored folder of real Kronos fixtures).
        int fixtureCheckIdx = Array.IndexOf(e.Args, "--sample-format-fixture-check");
        if (fixtureCheckIdx >= 0 && fixtureCheckIdx + 1 < e.Args.Length)
        {
            SampleFormatFixtureCheck.Run(e.Args[fixtureCheckIdx + 1]);
        }

        // Headless diagnostic: `--sample-editor-smoketest <path-to.ksc>` - drives the
        // real SampleEditorViewModel end-to-end against a real fixture (see
        // Tools/SampleEditorSmokeTest.cs).
        int sampleEditorSmokeIdx = Array.IndexOf(e.Args, "--sample-editor-smoketest");
        if (sampleEditorSmokeIdx >= 0 && sampleEditorSmokeIdx + 1 < e.Args.Length)
        {
            SampleEditorSmokeTest.Run(e.Args[sampleEditorSmokeIdx + 1]);
        }

        // One-off visual verification, NOT a shipped feature: `--sample-editor-visual-check
        // <path.ksc>` shows the real SampleEditorWindow and screenshots it through a few
        // real selections (see Tools/SampleEditorVisualCheck.cs). Lets MainWindow's
        // normal StartupUri open too rather than fighting it - harmless, and simpler.
        int sampleEditorVisualIdx = Array.IndexOf(e.Args, "--sample-editor-visual-check");
        if (sampleEditorVisualIdx >= 0 && sampleEditorVisualIdx + 1 < e.Args.Length)
        {
            SampleEditorVisualCheck.Schedule(e.Args[sampleEditorVisualIdx + 1]);
        }

        // Headless diagnostic: `--sample-ftp-pull-check <remote-path.KSC-or-.KMP>` - pulls
        // that file plus its dependency closure from the configured Kronos over FTP (no
        // Window, no dialog - uses settings.json's saved FtpUsername/Password/KronosHost),
        // then re-opens everything from disk to confirm it parses. See
        // Tools/SampleFtpPullCheck.cs; this is the real-hardware counterpart to
        // --sample-format-fixture-check, which only ever reads already-local files.
        int sampleFtpPullIdx = Array.IndexOf(e.Args, "--sample-ftp-pull-check");
        if (sampleFtpPullIdx >= 0 && sampleFtpPullIdx + 1 < e.Args.Length)
        {
            var s = Storage.LoadSettings();
            SampleFtpPullCheck.Run(s.KronosHost, s.FtpPort, s.FtpUsername, s.FtpPassword, e.Args[sampleFtpPullIdx + 1]);
        }

        // One-off diagnostic, NOT a shipped feature: `--sample-userbank-probe-build
        // <outputDir>` (see Tools/SampleUserBankProbeBuild.cs) - builds a minimal mono
        // multisample + its .KSC + _UserBank.KSC sibling via the real production writer
        // path, for uploading to a real Kronos to test the doc's open "can a
        // hand-authored _UserBank.KSC work at all" question.
        int userBankProbeIdx = Array.IndexOf(e.Args, "--sample-userbank-probe-build");
        if (userBankProbeIdx >= 0 && userBankProbeIdx + 1 < e.Args.Length)
        {
            SampleUserBankProbeBuild.Run(e.Args[userBankProbeIdx + 1]);
        }

        // `--sample-userbank-probe-build-multizone <outputDir>` - the second probe:
        // one multisample, 32 zones spanning the full keyboard, 4 keys each.
        int userBankProbeMultiIdx = Array.IndexOf(e.Args, "--sample-userbank-probe-build-multizone");
        if (userBankProbeMultiIdx >= 0 && userBankProbeMultiIdx + 1 < e.Args.Length)
        {
            SampleUserBankProbeBuild.RunMultiZone(e.Args[userBankProbeMultiIdx + 1]);
        }

        // One-off reconnaissance, NOT a shipped feature: `--sample-stereo-scan <folder>`
        // (see Tools/SampleStereoScan.cs) - grounds Phase 5's stereo-pair-creation work
        // in what real Kronos-authored .KMP/.KSF fixtures actually do with -L/-R.
        int stereoScanIdx = Array.IndexOf(e.Args, "--sample-stereo-scan");
        if (stereoScanIdx >= 0 && stereoScanIdx + 1 < e.Args.Length)
        {
            SampleStereoScan.Run(e.Args[stereoScanIdx + 1]);
        }

        // Single source of truth for the app directory (settings, palette, cal, log all colocate).
        var logPath = Path.Combine(Storage.DataDir, "screenremote.log");
        AppLog.Init(logPath);

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Error($"[ui-crash] {ex.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            AppLog.Error($"[crash] {ex.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Error($"[task-crash] {ex.Exception}");
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Close();
        base.OnExit(e);
    }
}
