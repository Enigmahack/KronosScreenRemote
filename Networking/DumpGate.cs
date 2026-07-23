namespace KronosScreenRemote;

// Pauses SysExService's func-33 performance-poll loop while any bulk dump/write is in flight,
// so the poll's own reply can't capture (steal) one of the dump's 0x73/0x24 replies off the
// shared stream. Two properties matter, and both were racy in the old plain-bool `_dumping`:
//
//  1. OVERLAP. Set-List dumps, name syncs, and Librarian object dumps run from independent UI
//     surfaces with no shared IsBusy gate, so two can be in flight at once. A refcount keeps the
//     loop paused until the LAST one finishes; a bool let whichever finished first un-pause it
//     under the other (the original bug).
//
//  2. TRANSPORT SWITCH under an in-flight dump. When the transport is swapped (USB hot-plug,
//     screen connect/disconnect, settings change) the old dump is orphaned against a now-disposed
//     transport. Its End() must NOT decrement the NEW generation's depth (which NewGeneration
//     reset to 0) — that would un-pause the loop mid-dump on the new transport. Each dump captures
//     its generation epoch at Begin and End is a no-op once the epoch has moved on.
//
// All three of epoch, depth, and their transitions live under one lock, so Begin (capture-epoch +
// increment) and NewGeneration (bump-epoch + reset) are atomic against each other — without that,
// a Begin that read the old epoch but incremented after NewGeneration reset the depth would strand
// a phantom count and pause the loop forever.
sealed class DumpGate
{
    readonly object _lock = new();
    int _epoch;
    int _depth;

    // Marks the start of a bulk dump/write for the current transport generation. Returns the
    // epoch token to hand back to End; pair 1:1 with End in a finally.
    public int Begin()
    {
        lock (_lock) { _depth++; return _epoch; }
    }

    // Ends a dump. Unwinds the refcount only if the transport hasn't been switched since Begin —
    // an orphaned old-generation dump completing is a no-op here.
    public void End(int epoch)
    {
        lock (_lock) { if (epoch == _epoch) _depth--; }
    }

    // A new transport generation starts. Any dump still in flight belongs to the old generation
    // (its End is now a no-op), so the new one begins with a clean, un-paused count.
    public void NewGeneration()
    {
        lock (_lock) { _epoch++; _depth = 0; }
    }

    // True while at least one dump/write for the current generation is in flight.
    public bool Active
    {
        get { lock (_lock) return _depth > 0; }
    }
}
