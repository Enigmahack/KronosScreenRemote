namespace KronosScreenRemote;

// Read-only view over a loaded PcgFile, shaped for the UI's tree-building code (Phase 5/6)
// to iterate the same way it iterates the local cache. Strictly read-only per requirement
// 11 - nothing here, or anywhere in Core/Pcg, ever writes back into a .pcg file.
sealed class PcgLibraryView
{
    readonly Dictionary<ObjLoc, PcgObjectEntry> _byLoc;

    public PcgLibraryView(PcgFile file)
    {
        _byLoc = new Dictionary<ObjLoc, PcgObjectEntry>();
        foreach (var o in file.Objects) _byLoc[o.Loc] = o;   // last-wins on any duplicate (defensive)
    }

    public IEnumerable<ObjLoc> AllObjects => _byLoc.Keys;
    public PcgObjectEntry? Get(ObjLoc loc) => _byLoc.TryGetValue(loc, out var e) ? e : null;
    public byte[]? GetBody(ObjLoc loc) => Get(loc)?.Body;
    public string? GetName(ObjLoc loc) => Get(loc)?.Name;
}
