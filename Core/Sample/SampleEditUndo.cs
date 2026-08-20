namespace KronosScreenRemote;

// Bounded snapshot stack for waveform edits - deliberately NOT LibrarianUndo.cs's
// shape (an unbounded object-graph command journal, correct for small metadata
// records, wrong here). A waveform snapshot is a multi-MB PCM buffer, and most edits
// here (crop, tempo/pitch, destructive filters) have no cheap analytic inverse, so
// this stores full pre-edit byte[] snapshots with a byte-size cap
// (AppSettings.SampleUndoByteCapMb), FIFO-evicting the oldest step once the cap is
// exceeded - not a step-count cap, since a single snapshot's size varies hugely with
// sample length.
sealed class SampleEditUndo(long byteCap)
{
    public const int DefaultByteCapMb = 256;

    readonly LinkedList<byte[]> _undoStack = new();
    readonly LinkedList<byte[]> _redoStack = new();
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

    // Call BEFORE applying an edit, with the PCM as it stands right now (about to
    // become "the past"). Clears the redo stack - a fresh edit invalidates whatever
    // was previously undone, same convention every undo/redo system uses.
    public void RecordBeforeEdit(byte[] preEditPcm)
    {
        _redoStack.Clear();
        _redoBytes = 0;
        Push(_undoStack, ref _undoBytes, preEditPcm);
        EvictFifo(_undoStack, ref _undoBytes);
    }

    // currentPcm: the PCM as it stands right now, BEFORE this call overwrites it -
    // needed so a subsequent Redo can move forward again. Returns null (no-op) if
    // there's nothing to undo.
    public byte[]? Undo(byte[] currentPcm)
    {
        if (_undoStack.Count == 0) return null;
        var restored = Pop(_undoStack, ref _undoBytes);
        Push(_redoStack, ref _redoBytes, currentPcm);
        EvictFifo(_redoStack, ref _redoBytes);
        return restored;
    }

    public byte[]? Redo(byte[] currentPcm)
    {
        if (_redoStack.Count == 0) return null;
        var restored = Pop(_redoStack, ref _redoBytes);
        Push(_undoStack, ref _undoBytes, currentPcm);
        EvictFifo(_undoStack, ref _undoBytes);
        return restored;
    }

    static void Push(LinkedList<byte[]> stack, ref long bytes, byte[] data)
    {
        stack.AddLast(data);
        bytes += data.Length;
    }

    static byte[] Pop(LinkedList<byte[]> stack, ref long bytes)
    {
        var last = stack.Last!.Value;
        stack.RemoveLast();
        bytes -= last.Length;
        return last;
    }

    void EvictFifo(LinkedList<byte[]> stack, ref long bytes)
    {
        while (bytes > ByteCap && stack.Count > 0)
        {
            var oldest = stack.First!.Value;
            stack.RemoveFirst();
            bytes -= oldest.Length;
            EvictedSinceLastCheck++;
        }
    }
}
