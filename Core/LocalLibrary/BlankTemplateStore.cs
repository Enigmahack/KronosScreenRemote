namespace KronosScreenRemote;

using System.IO;

// Durable store of a real "blank/initialized" object body per kind - the template written to a
// slot when a pending-delete is committed (requirement 2). The Kronos protocol has no delete and
// no "empty slot" encoding, so the only faithful way to blank a slot is to write back the exact
// bytes the instrument itself uses for a blank object. Those bytes never change for a given
// Kronos OS, so they're CAPTURED ONCE (from a currently-blank slot - via the already-synced local
// body, or a fresh SysEx dump - see BlankTemplates.EnsureAsync) and reused forever.
//
// Rooted at the local library's own directory, so it inherits that dir's isolation: a temp dir in
// the self-tests, {DataDir}/local_library in the app - never a process-wide global (which is how
// the program-bank-types cache bit a self-test, see that cache's own history).
sealed class BlankTemplateStore
{
    readonly string _dir;
    public BlankTemplateStore(string cacheRoot) => _dir = Path.Combine(cacheRoot, "blank_templates");

    // One template per kind. Programs split by wire format (a blank EXi program and a blank HD-1
    // program are different bodies); Combi/Set List have no such split.
    static string Key(int objType, bool isExi) => objType switch
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
}

// Captures + serves the blank template bodies BlankTemplateStore holds. The capture SOURCE slots
// below are ONLY a hint for where a blank object of each kind can be grabbed from right now - the
// captured DATA is what's stored and reused; the code never assumes these slots STAY blank, and
// EnsureAsync validates a candidate before trusting it (so a slot that's since been filled won't
// silently become the "blank" template). Re-capture, if ever needed, is deleting the stored .bin.
static class BlankTemplates
{
    // The user's current-blank slots (2026-07): U-EE000 EXi program, U-GG000 HD-1 program,
    // U-A000 combi, Set List 127. Bank numbers per KronosBanks (U-EE=0x4B, U-GG=0x4D, Combi
    // U-A=0x40). A capture hint only - see the class comment.
    static (int Bank, int Number)? SourceFor(int objType, bool isExi) => objType switch
    {
        LibObj.Program => isExi ? (0x4B, 0) : (0x4D, 0),
        LibObj.Combi   => (0x40, 0),
        LibObj.SetList => (0, 127),
        _              => null,
    };

    // Returns the blank template body for (objType, isExi), capturing + persisting it on first
    // use. Order: stored template -> already-synced local body of the source slot (works offline)
    // -> a fresh SysEx dump of the source slot. A candidate is only stored/returned if it passes
    // LooksBlank (right size, and - where cheaply checkable - actually empty), so a source slot
    // that's no longer blank falls through to null rather than poisoning the template. Null means
    // "no trustworthy blank available" - the caller falls back to EraseBody's derived blank.
    public static async Task<byte[]?> EnsureAsync(
        ILibrarianService sysEx, LocalLibraryCache cache, BlankTemplateStore store, int objType, bool isExi)
    {
        if (store.Get(objType, isExi) is { } stored) return stored;
        if (SourceFor(objType, isExi) is not (var bank, var number)) return null;

        // Prefer a body already in the local cache (no hardware round-trip, works offline).
        var local = cache.GetCurrentBody(objType, bank, number);
        if (local != null && LooksBlank(objType, isExi, local)) { store.Set(objType, isExi, local); return local; }

        // Else pull the source slot fresh.
        var dump = await sysEx.DumpObjectAsync(objType, bank, number).ConfigureAwait(false);
        if (dump?.Body is { } pulled && LooksBlank(objType, isExi, pulled)) { store.Set(objType, isExi, pulled); return pulled; }

        return null;
    }

    // A cheap, reliable sanity check that a captured body is a plausible blank of its kind - a
    // guard against capturing the wrong thing, NOT a full semantic "is this really the factory
    // init" test (which the bytes can't self-report). Programs: exact wire length for the format.
    // Set Lists: every slot blank-named (SetListData.IsEmpty). Combi: long enough to be a real
    // Combi body (no format/emptiness field to lean on - the source-slot hint carries the trust).
    static bool LooksBlank(int objType, bool isExi, byte[] body) => objType switch
    {
        LibObj.Program => body.Length == (isExi ? ProgramFormatConverter.WireSizeExi : ProgramFormatConverter.WireSizeHd1),
        LibObj.SetList => SetListBody.FromRawBody(0, body)?.IsEmpty ?? false,
        LibObj.Combi   => body.Length >= 7810,
        _              => false,
    };
}
