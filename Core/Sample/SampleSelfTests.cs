namespace KronosScreenRemote;

using System.IO;

// Off-hardware round-trip checks for the Core/Sample/* format layer (Open -> ToBytes
// byte-identical, plus the specific hardware-confirmed behaviors from
// kronosology/docs/interfaces/ksc_kmp_ksf_file_format.md this port must not silently
// drop). No real Kronos files here - see SampleFormatFixtureCheck for that against a
// local, gitignored fixture folder. Wired into App.xaml.cs's --librarian-selftest.
static class SampleSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── KsfSample: build a minimal valid sample in memory, round-trip it ──
        {
            var s = new KsfSample
            {
                Name = "Test Piano",
                Suffix = "-L",
                Sno1 = 42,
                SampleRate = 48000,
                Channels = 1,
                Bits = 16,
                SampleStart = 500,
                LoopStart = 1000,
                LoopEnd = 5000,
                Flags = 0x01, // loop enabled
            };
            s.SetSamples([100, -200, 300, -400, 32767, -32768]);
            var bytes = s.ToBytes();
            var reopened = KsfSample.Open(bytes);
            Check("ksf-reopen-not-null", reopened != null);
            if (reopened != null)
            {
                Check("ksf-round-trip-bytes-identical", bytes.AsSpan().SequenceEqual(reopened.ToBytes()));
                Check("ksf-name", reopened.Name == "Test Piano");
                Check("ksf-suffix", reopened.Suffix == "-L");
                Check("ksf-sno1", reopened.Sno1 == 42);
                Check("ksf-sample-rate", reopened.SampleRate == 48000);
                Check("ksf-frame-count", reopened.FrameCount == 6);
                Check("ksf-samples", reopened.Samples().SequenceEqual(new short[] { 100, -200, 300, -400, 32767, -32768 }));
                Check("ksf-loop-enabled", reopened.IsLoopEnabled);
                Check("ksf-sample-start", reopened.SampleStart == 500);
                Check("ksf-loop-start", reopened.LoopStart == 1000);
                Check("ksf-loop-end", reopened.LoopEnd == 5000);
            }
        }

        // ── KsfSample: LoopEnd is written exactly as stored, never silently recomputed
        //    from FrameCount (a real header-only-corrupted fixture has FrameCount==0
        //    but a stale nonzero LoopEnd left over from before the corruption - see
        //    ToBytes) ──
        {
            var s = new KsfSample { Flags = 0x81, LoopStart = 999, LoopEnd = 999999 }; // loop off
            s.SetSamples([1, 2, 3, 4, 5]);
            var reopened = KsfSample.Open(s.ToBytes());
            Check("ksf-loopend-not-auto-recomputed", reopened != null && reopened.LoopEnd == 999999 && !reopened.IsLoopEnabled);
        }

        // ── KsfSample: empty-PCM case is detected as header-only. NOTE: a real
        //    header-only-corrupted .KSF observed on hardware (doc §3.3) is 124 bytes,
        //    not this synthetic minimal one - the real file has an extra SMF1 chunk
        //    (12 bytes, sandwiched between NAME and SMD1, contents still unconfirmed -
        //    doc's "optional trailing chunk" note undersells where it actually sits)
        //    that this library doesn't write. IsHeaderOnly only depends on PCM length,
        //    so detection is unaffected; SMF1 itself stays out of scope. ──
        {
            var s = new KsfSample { Name = "Corrupt", Pcm = [] };
            var reopened = KsfSample.Open(s.ToBytes());
            Check("ksf-header-only-detected", reopened != null && reopened.IsHeaderOnly);
        }

        // ── KsfSample: an unmirrored dup slot (real anomaly, e.g. legacy 27778Hz
        //    samples) is preserved verbatim, not forced to mirror LoopStart ──
        {
            var s = new KsfSample { LoopStart = 0xFFFBED64, LoopEnd = 84552 };
            s.SetSamples(new short[10]);
            var tail = new byte[16];
            KorgRiffChunk.WriteU32BE(tail, 0, 0);
            KorgRiffChunk.WriteU32BE(tail, 4, 0xFFFBED64);
            KorgRiffChunk.WriteU32BE(tail, 8, 1); // deliberately NOT a mirror
            KorgRiffChunk.WriteU32BE(tail, 12, 84552);
            var smp1 = KorgRiffChunk.Concat(KorgRiffChunk.EncodeNameField("X", "", 16), tail);
            var bytes = KorgRiffChunk.Concat(
                KorgRiffChunk.BuildChunk("SMP1", smp1),
                KorgRiffChunk.BuildChunk("SNO1", new byte[4]),
                KorgRiffChunk.BuildChunk("NAME", KorgRiffChunk.EncodeNameField("X", "", 24)),
                KorgRiffChunk.BuildChunk("SMD1", KorgRiffChunk.Concat(new byte[] { 0, 0, 0xAC, 0x44, 0x81, 0, 1, 0x10 }, new byte[] { 0, 0, 0, 10 }, new byte[20])));
            var opened = KsfSample.Open(bytes);
            Check("ksf-dup-preserved-not-mirrored", opened != null && bytes.AsSpan().SequenceEqual(opened.ToBytes()));
            if (opened != null)
            {
                opened.ClearPreservedLoopDuplicate();
                var resynced = KorgRiffChunk.ReadU32BE(opened.ToBytes(), 8 + 24);
                Check("ksf-dup-clear-resyncs", resynced == opened.LoopStart);
            }
        }

        // ── KsfSample.Open: not a .KSF at all ──
        {
            Check("ksf-open-garbage-null", KsfSample.Open([1, 2, 3]) == null);
            Check("ksf-open-wrong-first-chunk-null", KsfSample.Open(KorgRiffChunk.BuildChunk("XXXX", new byte[4])) == null);
        }

        // ── KsfSample.Open: a truncated file (valid SMP1, no SMD1 at all) must fail,
        //    not silently produce a default-valued object indistinguishable from a
        //    real header-only-corrupted .KSF - this matters for Phase 2's FTP pulls,
        //    where a cut-off transfer is the expected truncation failure mode ──
        {
            var smp1Only = KorgRiffChunk.BuildChunk("SMP1", new byte[32]);
            Check("ksf-open-truncated-no-smd1-null", KsfSample.Open(smp1Only) == null);
        }

        // ── KmpMultisample: round-trip with 2 real zones + 1 skipped ──
        {
            var m = new KmpMultisample { Name = "Test MS", Mno1 = 7 };
            m.Zones.Add(new KmpZone { OriginalKey = 60, TopKey = 60, Filename = "MS007000.KSF" });
            m.Zones.Add(new KmpZone { OriginalKey = 61, TopKey = 61, Filename = "MS007001.KSF" });
            m.Zones.Add(new KmpZone { OriginalKey = 62, TopKey = 62, Filename = "SKIPPEDSAMPLE" });
            var bytes = m.ToBytes();
            var reopened = KmpMultisample.Open(bytes);
            Check("kmp-reopen-not-null", reopened != null);
            if (reopened != null)
            {
                Check("kmp-round-trip-bytes-identical", bytes.AsSpan().SequenceEqual(reopened.ToBytes()));
                Check("kmp-zone-count", reopened.Zones.Count == 3);
                Check("kmp-zone0-origkey", reopened.Zones[0].OriginalKey == 60);
                Check("kmp-zone0-topkey", reopened.Zones[0].TopKey == 60);
                Check("kmp-zone0-filename", reopened.Zones[0].Filename == "MS007000.KSF");
                Check("kmp-zone2-skipped", reopened.Zones[2].IsSkipped);
                Check("kmp-next-ksf-filename", m.NextKsfFilename() == "MS007003.KSF");
            }
        }

        // ── KmpMultisample: RLP3 offset+5 is always forced 0 on write, regardless of input ──
        {
            var m = new KmpMultisample { Mno1 = 1 };
            m.Zones.Add(new KmpZone { Filename = "MS001000.KSF", Rlp3 = [1, 2, 3, 4, 5, 0xFF] });
            var reopened = KmpMultisample.Open(m.ToBytes());
            Check("kmp-rlp3-offset5-forced-zero", reopened != null && reopened.Zones[0].Rlp3[5] == 0);
            Check("kmp-rlp3-other-bytes-preserved", reopened != null &&
                reopened.Zones[0].Rlp3[0] == 1 && reopened.Zones[0].Rlp3[4] == 5);
        }

        // ── KmpMultisample.Open: not a .KMP at all ──
        {
            Check("kmp-open-garbage-null", KmpMultisample.Open([1, 2, 3]) == null);
        }

        // ── KscCollection: round-trip including the required #>User.0.2. companion block ──
        {
            var k = new KscCollection { BankUuid = "abc-123", Entries = ["Foo.KMP", "Bar.KSF"] };
            var bytes = k.ToBytes("Test.KSC");
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Check("ksc-has-header", text.StartsWith("#KORG Script Version 1.0\r\n#v2\r\n#uuid:abc-123\r\n"));
            Check("ksc-has-companion-block", text.Contains("#>User.0.2.Foo.KMP") && text.Contains("#>User.0.2.Bar.KSF"));
            var reopened = KscCollection.Open(bytes);
            Check("ksc-round-trip-entries", reopened.Entries.SequenceEqual(k.Entries));
            Check("ksc-round-trip-uuid", reopened.BankUuid == "abc-123");
        }

        // ── KscCollection: normal-mode ToBytes()/Save() refuse a _UserBank.KSC-suffixed
        //    target - that write mode's own format is real Kronos-generated output only ──
        {
            var k = new KscCollection { Entries = ["Foo.KMP"] };
            bool threw = false;
            try { k.ToBytes("SomeBank_UserBank.KSC"); }
            catch (InvalidOperationException) { threw = true; }
            Check("ksc-refuses-userbank-write", threw);
        }

        // ── KscCollection: the guard can't be bypassed by a bare ToBytes() when Path is
        //    already set to a _UserBank.KSC-suffixed target ──
        {
            var k = new KscCollection { Path = @"C:\x\SomeBank_UserBank.KSC", Entries = ["Foo.KMP"] };
            bool threw = false;
            try { k.ToBytes(); }
            catch (InvalidOperationException) { threw = true; }
            Check("ksc-refuses-userbank-write-bare-tobytes", threw);
        }

        // ── KscCollection.ToUserBankBytes: the dedicated _UserBank.KSC writer (doc §1.3
        //    own-bank case) - synthetic-only, no real fixture dependency (this file's own
        //    design boundary, see header comment). Two multisamples with a deliberate gap
        //    in Mno1 (0 and 5) plus one bare-.KSF entry, so the test actually exercises the
        //    "MS<n>/DS<n> is positional emission order, not Mno1/Sno1" finding rather than
        //    passing by coincidence the way a gapless fixture would. Format/name-encoding
        //    themselves were cross-checked by hand against real _UserBank.KSC fixtures
        //    (Test2-kronos, SMPTEST/LOOP, SMPTEST/NOLOOP) during this feature's development -
        //    not re-asserted here since SampleFixtures/ is local-only and gitignored.
        {
            var scratchRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kronos_userbank_selftest");
            if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
            var contentDir = System.IO.Path.Combine(scratchRoot, "UBTEST");
            Directory.CreateDirectory(contentDir);

            var m0 = new KmpMultisample { Name = "First One", Suffix = "", Mno1 = 0 };
            m0.Zones.Add(new KmpZone { OriginalKey = 60, TopKey = 72, Filename = "MS000000.KSF" });
            File.WriteAllBytes(System.IO.Path.Combine(contentDir, "MS000000.KMP"), m0.ToBytes());

            var m1 = new KmpMultisample { Name = "Second One", Suffix = "-L", Mno1 = 5 }; // gap vs m0
            m1.Zones.Add(new KmpZone { OriginalKey = 60, TopKey = 72, Filename = "MS005000.KSF" });
            File.WriteAllBytes(System.IO.Path.Combine(contentDir, "MS005000.KMP"), m1.ToBytes());

            var ksf = new KsfSample { Name = "A Drum Hit", Suffix = "", Sno1 = 9 };
            ksf.SetSamples([1, -1, 2, -2]);
            File.WriteAllBytes(System.IO.Path.Combine(contentDir, "Hit.KSF"), ksf.ToBytes());

            var kscPath = System.IO.Path.Combine(scratchRoot, "UBTEST.KSC");
            var k = new KscCollection
            {
                Path = kscPath,
                BankUuid = "dead1234-0000-4000-8000-000000000000",
                Entries = ["MS000000.KMP", "MS005000.KMP", "Hit.KSF"],
            };

            var text = System.Text.Encoding.ASCII.GetString(k.ToUserBankBytes());
            var expectedM0Name = System.Text.Encoding.ASCII.GetString(KorgRiffChunk.EncodeNameField("First One", "", 24));
            var expectedM1Name = System.Text.Encoding.ASCII.GetString(KorgRiffChunk.EncodeNameField("Second One", "-L", 24));
            var expectedDsName = System.Text.Encoding.ASCII.GetString(KorgRiffChunk.EncodeNameField("A Drum Hit", "", 24));

            Check("userbank-header", text.StartsWith("#KORG Script Version 1.0\r\n#v2\r\n"));
            Check("userbank-no-plain-uuid-line", !text.Contains("\r\n#uuid:dead1234"));
            Check("userbank-ms0-positional-not-mno1", text.Contains($"#>>uuid:dead1234-0000-4000-8000-000000000000.MS0.1.0.{expectedM0Name}\r\n"));
            Check("userbank-ms1-positional-not-mno1-5", text.Contains($"#>>uuid:dead1234-0000-4000-8000-000000000000.MS1.1.0.{expectedM1Name}\r\n"));
            Check("userbank-ds0", text.Contains($"#>>uuid:dead1234-0000-4000-8000-000000000000.DS0.1.0.{expectedDsName}\r\n"));
            Check("userbank-summary-line", text.Contains("#>uuid:dead1234-0000-4000-8000-000000000000.2.1.UBTEST\r\n"));
            Check("userbank-ends-with-summary", text.TrimEnd('\r', '\n').EndsWith(".2.1.UBTEST"));

            Directory.Delete(scratchRoot, recursive: true);
        }

        return fails;
    }
}
