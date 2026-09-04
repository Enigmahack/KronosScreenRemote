namespace KronosScreenRemote;

using System.IO;

// Resolves a "header-only" .KSF's SMF1 cross-reference (doc §3.2) to the real .KSF
// holding its PCM, for THIS APP's own read-side purposes (waveform/playback/export).
// This is NOT Eva's own data-loss failure mode (doc §3.3, a genuine re-save bug that
// also leaves frame_count==0) - it's the format's real "this zone shares audio with
// another sample, with its own loop points/flags" mechanism.
//
// IMPORTANT, ground-truthed 2026-09-03/04 across 10 RE sessions (doc §3.2/§7,
// kronosology's own OA.ko + Eva decompile - see the doc for full addresses/citations):
// the Kronos itself NEVER reads SMF1 anywhere reachable on either binary - it
// resolves shared audio by SNO1 collision in CKorgFileKSF::ImportToBank at BULK
// .KSC-IMPORT time (first zone processed for a given SNO1 claims the resident slot).
// SMF1 is Eva-side-only bookkeeping with no OA.ko consumer, on either the import or
// (session 10) the runtime playback path.
//
// Correction, session 10: actual PLAYBACK doesn't even consult that bulk-import
// residency flag - it runs off a SEPARATE per-bank on-disk cache
// (CSTGMultisampleBank::LoadBankMetaData), built once per session per bank. This
// dissolved an apparent contradiction: hardware testing showed a real-audio zone
// arriving AFTER an already-claimed-empty stub still played its own real audio
// correctly - the "silent audio loss" framing doc §3.2 documents is a real, confirmed
// behavior of the bulk-import bookkeeping pass, but isn't necessarily what a live
// instrument ends up sounding once its own per-bank cache is built. Practical
// takeaway for this resolver, unchanged either way: SMF1-then-SNO1-verify remains the
// right TOOL-side heuristic - it's a convenient, filename-keyed way to find the SAME
// file real hardware's own SNO1-based mechanism reliably lands on in every real
// fixture examined, independent of exactly which layer (import-time bookkeeping vs.
// playback-time cache) ultimately governs what plays. Confirmed against three real
// hardware collections (not just the doc's own fixtures), cross-checked directly:
//   - LD-MAIN.KSC, LIVIN004 (all 6 zones stubs) -> LIVIN002 (a different multisample's
//     own zone folder) - the "different multisample number" case, doc §3.2's first
//     example.
//   - SGC SAMPLES.KSC, LETS_032 (zones 13-14, 16-23 stubs) -> other zones of that SAME
//     multisample (e.g. zone 22 -> zone 14) - doc §3.2's "same multisample" example.
//   - CONTEMP-SAMPLES.KSC, DARKH056 (zone 3 stub) -> zone 2 of the same multisample.
// Every case resolved in a single hop with byte-identical Name/Sno1/loop points between
// stub and target (nothing was found to actually differ in these fixtures - the stub's
// own fields are still what must be read for playback, since the doc's own writer
// guidance (§3.1) establishes they're stored independently and CAN diverge; this
// resolver never assumes they match).
static class SampleLinkResolver
{
    // Sample: the resolved target, holding real PCM. TargetPath: where it was found.
    // Sno1Verified: true when the target's own SNO1 matches the stub's - the doc's
    // §1.6 "collection-unique Sno1" requirement makes this the strongest available
    // confirmation that the filename match found the RIGHT file, not a same-named file
    // from an unrelated multisample. False just means "found by filename alone" -
    // still shown, not discarded, since SMF1's payload is itself nothing but a
    // filename and every real fixture resolves this way.
    public sealed record Result(KsfSample Sample, string TargetPath, bool Sno1Verified);

    // stub: the header-only .KSF to resolve. kmpPath: the STUB's own owning .KMP path -
    // used only to find the collection's content directory (KscCollection.ContentDirFor
    // - every multisample's zone folder and every bare/repository .KSF live as siblings
    // under one such directory, the same one KscCollection.NextFreeSno1 already scans
    // recursively). The target's OWN folder membership is unrelated to the stub's -
    // doc §3.2's cross-multisample example - so this is searched collection-wide, not
    // just the stub's own zone folder.
    //
    // Doesn't chase multi-hop chains (stub -> stub -> real audio) - unobserved in every
    // fixture examined, and a stub target is skipped outright below rather than
    // recursed into.
    public static Result? Resolve(KsfSample stub, string kmpPath)
    {
        if (stub.StubTargetFilename is not { Length: > 0 } targetName) return null;

        var contentDir = Path.GetDirectoryName(kmpPath);
        if (contentDir == null || !Directory.Exists(contentDir)) return null;

        KsfSample? firstPlayable = null;
        string? firstPlayablePath = null;
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(contentDir, targetName, SearchOption.AllDirectories))
            {
                KsfSample? opened;
                try { opened = KsfSample.Open(File.ReadAllBytes(candidate)); }
                catch (Exception ex) { AppLog.Warn($"[sample-link] couldn't read candidate '{candidate}': {ex.Message}"); continue; }
                if (opened == null || opened.IsHeaderOnly) continue; // a stub pointing at another stub - not chased, see comment above

                if (opened.Sno1 == stub.Sno1) return new Result(opened, candidate, Sno1Verified: true);
                firstPlayable ??= opened;
                firstPlayablePath ??= candidate;
            }
        }
        catch (Exception ex) { AppLog.Warn($"[sample-link] search under '{contentDir}' for '{targetName}' failed: {ex.Message}"); }

        return firstPlayable != null ? new Result(firstPlayable, firstPlayablePath!, Sno1Verified: false) : null;
    }

    // Convenience for every read-only consumer (waveform decode, transport playback,
    // export) that just wants "something with real PCM to read, honoring this zone's
    // own loop points/flags" - `stub` unchanged when it already has real PCM or nothing
    // resolves, otherwise a synthetic view combining the stub's own fields with the
    // target's PCM. NEVER assign the result back into a zone's live KsfSample (e.g.
    // `_selectedSample`) - it carries no SMF1 chunk and no preserved-loop-duplicate
    // slot, so saving it would silently turn a legitimate link into a full duplicate
    // copy on disk instead of round-tripping the stub. Read-only use only.
    public static KsfSample ResolvePlayable(KsfSample stub, string kmpPath)
    {
        if (!stub.IsHeaderOnly) return stub;
        var link = Resolve(stub, kmpPath);
        return link == null ? stub : BuildPlayableView(stub, link.Sample);
    }

    // Split out of ResolvePlayable so a caller that already has a Result (e.g. one that
    // also needs TargetPath/Sno1Verified for a status display) can build the same view
    // without a second directory scan.
    public static KsfSample BuildPlayableView(KsfSample stub, KsfSample linkedAudio)
    {
        var view = new KsfSample
        {
            Name = stub.Name,
            Suffix = stub.Suffix,
            Sno1 = stub.Sno1,
            SampleRate = stub.SampleRate,
            Flags = stub.Flags,
            Channels = stub.Channels,
            Bits = stub.Bits,
            Pcm = linkedAudio.Pcm,
            SampleStart = stub.SampleStart,
            LoopStart = stub.LoopStart,
            LoopEnd = stub.LoopEnd,
        };
        view.RestoreLoopTune(stub.LoopTune);
        return view;
    }
}
