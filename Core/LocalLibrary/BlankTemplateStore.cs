namespace KronosScreenRemote;

using System.IO;

// Durable store of a "blank/initialized" object body per kind - the template written to a slot
// when a pending-delete is committed. The Kronos protocol has no delete and no "empty slot"
// encoding, so the only faithful way to blank a slot is to write back the exact bytes the
// instrument itself uses for a blank object (see BlankTemplates.EnsureAsync).
//
// Rooted at the keyboard library's own directory so it inherits that dir's isolation (a temp dir in
// the self-tests, {DataDir}/local_library in the app) rather than a process-wide global.
sealed class BlankTemplateStore
{
    readonly string _dir;
    public BlankTemplateStore(string cacheRoot) => _dir = Path.Combine(cacheRoot, "blank_templates");

    // One template per kind. Programs split by wire format (a blank EXi program and a blank HD-1
    // program are different bodies); Combi/Set List have no such split. Also names the embedded
    // resource BlankTemplates.Baked looks for ("{Key}_init.bin"), so the two stay in step.
    public static string Key(int objType, bool isExi) => objType switch
    {
        LibObj.Program => isExi ? "program_exi" : "program_hd1",
        LibObj.Combi   => "combi",
        LibObj.SetList => "setlist",
        _              => $"obj{objType:X2}",
    };

    string PathFor(int objType, bool isExi) => Path.Combine(_dir, Key(objType, isExi) + ".bin");

    public byte[]? Get(int objType, bool isExi)
    {
        var path = PathFor(objType, isExi);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) { AppLog.Warn($"[blank-template] read failed: {ex.Message}"); return null; }
    }

    public void Set(int objType, bool isExi, byte[] body)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(PathFor(objType, isExi), body);
        }
        catch (Exception ex) { AppLog.Warn($"[blank-template] write failed: {ex.Message}"); }
    }

    // Throw away a template that no longer validates. "Reused forever" is only safe while the
    // stored bytes really are a blank object - a template captured under a weaker check (see
    // BlankTemplates.LooksBlank) is a real patch that every future delete would stamp onto the
    // erased slot, and nothing else would ever dislodge it. Deleting it lets EnsureAsync
    // re-capture, so a poisoned file self-heals on the next delete instead of needing the user
    // to find and remove it by hand.
    public void Discard(int objType, bool isExi)
    {
        var path = PathFor(objType, isExi);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn($"[blank-template] discard failed: {ex.Message}"); }
    }
}

// Captures + serves the blank template bodies BlankTemplateStore holds. The capture SOURCE slots
// below are ONLY a hint for where a blank object of each kind can be grabbed from right now - the
// captured DATA is what's stored and reused; the code never assumes these slots STAY blank, and
// EnsureAsync validates a candidate before trusting it (so a slot that's since been filled won't
// silently become the "blank" template). Re-capture, if ever needed, is deleting the stored .bin.
static class BlankTemplates
{
    // Factory INIT bodies shipped with the app as embedded resources (see the .csproj) - real
    // bytes captured from a real Kronos, version-controlled and reviewable, rather than assumed
    // blank from whatever a source slot currently holds (see SourceFor). A source slot is not
    // guaranteed to stay blank on a live instrument, so relying on "capture it now" risks baking
    // in a real patch as if it were blank and stamping it onto every future delete.
    //
    // A Set List's default name encodes its own slot number, so the donor slot's name would
    // otherwise be stamped onto whatever slot is erased - ChangesetBuilder re-stamps the correct
    // per-slot name after this returns, which is why any blank donor slot will do.
    //
    // These bytes are specific to the OS revision they were captured from; the fallback chain in
    // EnsureAsync is what makes a mismatch on a different OS degrade rather than break.
    static readonly Dictionary<string, byte[]?> _baked = new();

    static byte[]? Baked(int objType, bool isExi)
    {
        string key = BlankTemplateStore.Key(objType, isExi);
        lock (_baked)
        {
            if (_baked.TryGetValue(key, out var cached)) return cached;
            byte[]? body = null;
            try
            {
                var asm = typeof(BlankTemplates).Assembly;
                // Suffix match, so the resource's namespace prefix isn't hard-coded here.
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith($".{key}_init.bin", StringComparison.Ordinal));
                if (name != null)
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        body = ms.ToArray();
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"[blank-template] embedded read failed for {key}: {ex.Message}"); }
            _baked[key] = body;
            return body;
        }
    }

    // The slots each shipped body was captured from, kept as a last-resort re-capture path if a
    // shipped body ever fails its own blank check (see EnsureAsync step 3) - normal operation
    // never reaches it. Bank numbers per KronosBanks (U-EE=0x4B, U-GG=0x4D, Combi I-E=0x04); slot
    // numbers are 0-based, exactly as ObjLoc.Label() renders them, so I-E:005 is bank 0x04 number 5.
    // No entry for Drum Kit/Wave Sequence: U-GG was blank in every real .pcg file checked
    // (see obj04_init.bin/obj05_init.bin's own capture), but that was this project's files,
    // not a guarantee about any given user's library - a production Kronos could have every
    // bank filled, including the last one. Baked-in bytes only for these two; no live
    // re-capture fallback (step 3 below just returns null, same as the "unknown type" case).
    static (int Bank, int Number)? SourceFor(int objType, bool isExi) => objType switch
    {
        LibObj.Program => isExi ? (0x4B, 0) : (0x4D, 0),
        LibObj.Combi   => (0x04, 5),
        LibObj.SetList => (0, 127),
        _              => null,
    };

    // Returns the blank template body for (objType, isExi). Order, strongest evidence first:
    //
    //   1. The BAKED-IN body for this kind - which today is every kind. Reviewed, version-
    //      controlled bytes beat anything discovered at runtime, and unlike a capture they don't
    //      depend on some slot still being blank. This is where every normal call ends.
    //   2. A previously STORED capture, re-validated (never trusted just for existing) and
    //      discarded if it isn't blank. Only reachable if step 1 has no body or fails its check;
    //      it stays so an already-poisoned install can self-heal even if step 1 ever goes away.
    //   3. A fresh capture from SourceFor's slot - the already-synced local body first (works
    //      offline), then a SysEx dump - validated before being stored.
    //   4. null: "no trustworthy blank available", and the caller falls back to EraseBody's
    //      derived blank.
    //
    // Steps 2-4 are defence in depth, not the normal route - before the bodies were baked in, they
    // were the whole mechanism.
    public static async Task<byte[]?> EnsureAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, BlankTemplateStore store, int objType, bool isExi)
    {
        // Validated on every use, not merely on capture: if a shipped body ever fails its own
        // check (a bad edit to the resource, a format assumption that moved), that must surface
        // as a log line and a fallback, not as a silently wrong body written to hardware.
        if (Baked(objType, isExi) is { } baked)
        {
            if (LooksBlank(objType, isExi, baked)) return baked;
            AppLog.Warn($"[blank-template] EMBEDDED template for obj {objType:X2} (exi={isExi}) failed its own blank check - ignoring it");
        }

        // A stored template is re-validated too. One captured under an older, weaker LooksBlank is
        // a real patch, and it would otherwise outrank every later step forever; discarding it
        // here is what lets an already-poisoned install self-heal.
        if (store.Get(objType, isExi) is { } stored)
        {
            if (LooksBlank(objType, isExi, stored)) return stored;
            AppLog.Warn($"[blank-template] stored template for obj {objType:X2} (exi={isExi}) is not blank - discarding and re-capturing");
            store.Discard(objType, isExi);
        }
        if (SourceFor(objType, isExi) is not (var bank, var number)) return null;

        // Prefer a body already in the local cache (no hardware round-trip, works offline).
        var local = cache.GetCurrentBody(objType, bank, number);
        if (local != null && LooksBlank(objType, isExi, local)) { store.Set(objType, isExi, local); return local; }

        // Else pull the source slot fresh.
        var dump = await sysEx.DumpObjectAsync(objType, bank, number).ConfigureAwait(false);
        if (dump?.Body is { } pulled && LooksBlank(objType, isExi, pulled)) { store.Set(objType, isExi, pulled); return pulled; }

        // Say WHY there's no template. The caller silently falls back to EraseBody's derived
        // blank, which is a perfectly safe result but a confusing one to see without explanation -
        // and "the source slot named above is no longer blank on this instrument" is exactly the
        // actionable fact (fix SourceFor) that the old silent null hid.
        AppLog.Warn($"[blank-template] no blank template for obj {objType:X2} (exi={isExi}): " +
                    $"source slot bank {bank:X2}:{number:D3} is unavailable or not blank - using the derived blank instead");
        return null;
    }

    // A cheap, reliable check that a captured body really is a blank of its kind. Structure
    // first (right wire length for the format), then INIT-ness via the same InitObjects
    // predicates the dependency walker and the placement orphan gate already use.
    //
    // Length alone isn't enough: every valid Combi is >= 7810 bytes and every valid Program is
    // exactly its format's wire size, so a length-only check would accept any patch sitting in
    // the source slot and enshrine it as "blank" (see SourceFor). Requiring InitObjects too means
    // a source slot that isn't actually blank falls through to null instead, and the caller uses
    // EraseBody's derived blank - a safe answer rather than a silently wrong one.
    static bool LooksBlank(int objType, bool isExi, byte[] body) => objType switch
    {
        LibObj.Program      => body.Length == (isExi ? ProgramFormatConverter.WireSizeExi : ProgramFormatConverter.WireSizeHd1)
                                && InitObjects.IsInit(LibObj.Program, body),
        LibObj.SetList      => InitObjects.IsInit(LibObj.SetList, body),
        LibObj.Combi        => body.Length >= 7810 && InitObjects.IsInit(LibObj.Combi, body),
        LibObj.DrumKit      => body.Length == 38424 && InitObjects.IsInit(LibObj.DrumKit, body),
        LibObj.WaveSequence => body.Length == 2216 && InitObjects.IsInit(LibObj.WaveSequence, body),
        _                   => false,
    };
}
