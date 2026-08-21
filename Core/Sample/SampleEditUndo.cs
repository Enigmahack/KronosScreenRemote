namespace KronosScreenRemote;

// A snapshot of everything Undo/Redo needs to restore for one sample: its PCM, plus the
// five fields (SampleStart/LoopStart/LoopEnd/Flags/PreservedLoopDuplicate) that marker
// drags and field edits can change WITHOUT touching PCM at all. Bundling both into one
// snapshot type - rather
// than a separate stack for "PCM edits" vs "field edits" - is what keeps Undo/Redo
// chronologically correct for free: whichever kind of edit happened most recently is
// simply the top of the one shared stack, no merge/interleave logic required.
readonly struct SampleFieldSnapshot
{
    public byte[] Pcm { get; }
    public uint SampleStart { get; }
    public uint LoopStart { get; }
    public uint LoopEnd { get; }
    public byte Flags { get; }
    // The offset-24 duplicate slot (KsfSample.PreservedLoopDuplicate) - null on 73/75
    // real files (mirrors LoopStart on save), a distinct stale value on 5 outliers.
    // Must round-trip through undo/redo same as the other four fields, or restoring
    // an edit on one of those 5 files silently changes bytes that used to be
    // byte-identical (ApplyTo would otherwise always re-clear it to null).
    public uint? PreservedLoopDuplicate { get; }

    public SampleFieldSnapshot(byte[] pcm, uint sampleStart, uint loopStart, uint loopEnd, byte flags, uint? preservedLoopDuplicate = null)
    {
        Pcm = pcm;
        SampleStart = sampleStart;
        LoopStart = loopStart;
        LoopEnd = loopEnd;
        Flags = flags;
        PreservedLoopDuplicate = preservedLoopDuplicate;
    }

    // Not a defensive .Clone() of Pcm - every PCM-mutating call site in
    // SampleEditorViewModel replaces KsfSample.Pcm/Samples() with a brand-new array
    // rather than mutating one in place (KsfSample.SetSamples' own contract), so the
    // reference captured here stays valid/unchanged for as long as this snapshot lives,
    // the same assumption the pre-existing (PCM-only) version of this type always made.
    public static SampleFieldSnapshot Of(KsfSample s) =>
        new(s.Pcm, s.SampleStart, s.LoopStart, s.LoopEnd, s.Flags, s.PreservedLoopDuplicate);

    public void ApplyTo(KsfSample s)
    {
        s.Pcm = Pcm;
        s.SampleStart = SampleStart;
        s.LoopStart = LoopStart;
        s.LoopEnd = LoopEnd;
        s.Flags = Flags;
        s.RestorePreservedLoopDuplicate(PreservedLoopDuplicate);
    }
}

// Bounded snapshot stack for waveform/field edits - deliberately NOT LibrarianUndo.cs's
// shape (an unbounded object-graph command journal, correct for small metadata
// records, wrong here). A waveform snapshot is a multi-MB PCM buffer, and most PCM
// edits here (crop, tempo/pitch, destructive filters) have no cheap analytic inverse, so
// this stores full pre-edit snapshots with a byte-size cap
// (AppSettings.SampleUndoByteCapMb), FIFO-evicting the oldest step once the cap is
// exceeded - not a step-count cap, since a single snapshot's size varies hugely with
// sample length. Cap accounting counts each snapshot's Pcm.Length even for field-only
// edits that reuse the same (unchanged) Pcm reference across several consecutive
// entries - a deliberate over-count (real memory cost of those entries is just a
// reference, not a fresh copy), traded for not needing separate accounting per edit
// kind; worst case this evicts a little more eagerly than strictly necessary.
sealed class SampleEditUndo(long byteCap)
{
    public const int DefaultByteCapMb = 256;

    readonly LinkedList<SampleFieldSnapshot> _undoStack = new();
    readonly LinkedList<SampleFieldSnapshot> _redoStack = new();
    long _undoBytes;
    long _redoBytes;

    public long ByteCap { get; set; } = byteCap;

    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    // Steps silently dropped past the cap since the last time this was read - the UI
    // reads and resets it so it can show "N earlier step(s) no longer available"
    // instead of undo just quietly stopping with no explanation. Eviction can only
    // ever happen on the undo side (RecordBeforeEdit) - Redo's own push onto the undo
    // stack after a successful Undo can also evict, which is why this is a single
    // running counter rather than split per-stack.
    public int EvictedSinceLastCheck { get; private set; }

    public int TakeEvictedCount()
    {
        var n = EvictedSinceLastCheck;
        EvictedSinceLastCheck = 0;
        return n;
    }

    // Call BEFORE applying an edit (PCM, fields, or both), with the sample's state as it
    // stands right now (about to become "the past"). Clears the redo stack - a fresh
    // edit invalidates whatever was previously undone, same convention every undo/redo
    // system uses.
    public void RecordBeforeEdit(SampleFieldSnapshot preEditState)
    {
        _redoStack.Clear();
        _redoBytes = 0;
        Push(_undoStack, ref _undoBytes, preEditState);
        EvictFifo(_undoStack, ref _undoBytes);
    }

    // currentState: the sample's state as it stands right now, BEFORE this call's
    // caller overwrites it - needed so a subsequent Redo can move forward again.
    // Returns null (no-op) if there's nothing to undo.
    public SampleFieldSnapshot? Undo(SampleFieldSnapshot currentState)
    {
        if (_undoStack.Count == 0) return null;
        var restored = Pop(_undoStack, ref _undoBytes);
        Push(_redoStack, ref _redoBytes, currentState);
        EvictFifo(_redoStack, ref _redoBytes);
        return restored;
    }

    public SampleFieldSnapshot? Redo(SampleFieldSnapshot currentState)
    {
        if (_redoStack.Count == 0) return null;
        var restored = Pop(_redoStack, ref _redoBytes);
        Push(_undoStack, ref _undoBytes, currentState);
        EvictFifo(_undoStack, ref _undoBytes);
        return restored;
    }

    static void Push(LinkedList<SampleFieldSnapshot> stack, ref long bytes, SampleFieldSnapshot data)
    {
        stack.AddLast(data);
        bytes += data.Pcm.Length;
    }

    static SampleFieldSnapshot Pop(LinkedList<SampleFieldSnapshot> stack, ref long bytes)
    {
        var last = stack.Last!.Value;
        stack.RemoveLast();
        bytes -= last.Pcm.Length;
        return last;
    }

    void EvictFifo(LinkedList<SampleFieldSnapshot> stack, ref long bytes)
    {
        while (bytes > ByteCap && stack.Count > 0)
        {
            var oldest = stack.First!.Value;
            stack.RemoveFirst();
            bytes -= oldest.Pcm.Length;
            EvictedSinceLastCheck++;
        }
    }
}
