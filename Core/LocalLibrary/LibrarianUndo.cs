namespace KronosScreenRemote;

// One local slot's state BEFORE an undoable action touched it. Entry == null means the slot
// didn't exist at all (a placement into a previously-empty slot), which undo restores by
// removing the index entry again. LocalIndexEntry is an immutable record, so holding the
// reference captures Version/BaselineHash/CurrentHash/DisplayName/Conflicted/PendingDelete
// exactly as they were - never reconstruct it field-by-field on restore.
sealed record LocalSlotSnapshot(int ObjType, int Bank, int Number, LocalIndexEntry? Entry);

// One undoable user action, as the state it needs to be rolled back TO (not as a reverse
// operation). Snapshot-of-what-was-touched, not full-library: a placement into a bank captures
// only that bank's affected slots, so a step costs a handful of small index records plus - only
// when the Merge Window actually changed - one merge staging snapshot.
sealed class LibrarianUndoStep
{
    public required string Description { get; init; }
    public required List<LocalSlotSnapshot> LocalSlots { get; init; }

    // Captured lazily, ONLY if the action actually mutated the merge cache (MergeCache.Mutating) -
    // a rename must not pay for copying every staged body. Null = the Merge Window wasn't touched.
    public MergeCacheSnapshot? Merge { get; init; }

    // Prior in-memory pending-dependency list (LibrarianShellViewModel's SessionDependencyClipboard),
    // always captured: any placement can add to it, and it's tiny.
    public required List<SessionDependencyEntry> SessionDependencies { get; init; }

    // Only PlaceMergeBankWithTypeChange sets one; Prior == null means "there was none" (the
    // dictionary had no entry for this bank), so undo clears it again.
    public (int Bank, bool? Prior)? PendingBankTypeChange { get; init; }

    // A step that captured nothing means the action mutated nothing (e.g. a placement REFUSEd by
    // the orphan gate before writing) - never pushed onto the stack, so Ctrl+Z can't consume a
    // no-op step and look broken.
    public bool CapturedNothing => LocalSlots.Count == 0 && Merge == null && PendingBankTypeChange == null;
}

// Linear undo for the Librarian's LOCAL (pre-Commit) state - the answer to "I dragged a whole
// bank out of the Merge Window by accident and had to start over."
//
// Capture is observational, not per-call-site: LocalLibraryCache raises SlotMutating before every
// index write/removal and MergeCache raises Mutating before every staging change, so an action
// only has to open a scope (Begin) and EVERY local edit it performs - however deep inside
// LocalEditOps/BatchLibrarian it happens - lands in the step automatically. First-prior-per-slot
// wins, because one user action legitimately touches the same slot twice (ToggleDelete does
// Discard then SetPendingDelete).
//
// A scope pushes its step iff it captured something, with no explicit Commit: "captured" means
// the mutation already happened, so a partially-completed action (PlaceMergeBankWithTypeChange
// wipes the destination bank BEFORE BatchPlace can refuse) stays recoverable instead of being
// discarded as a failure.
//
// Deliberately NOT undone: the persisted displaced-occupant clipboard (Core/BatchMoveModel.cs).
// Undo restores the occupant to its slot, which makes the clipboard copy redundant - but that
// clipboard IS the safety net, and undo removing entries from it (by count, or by rewinding the
// whole file) could delete a copy some later action put there. Undo never deletes a safety copy.
//
// Also NOT undoable: anything already pushed to hardware. Sync/Commit clears the stack (see
// LibrarianShellViewModel) - a local rollback across a hardware write isn't representable here.
// Clear History is likewise excluded: it deletes oplog.jsonl outright.
sealed class LibrarianUndoRecorder : IDisposable
{
    // Enough to walk back a run of accidental drops; bounded because a step can hold a merge
    // staging snapshot (bodies), and the oldest steps are the least likely to be wanted.
    public const int MaxSteps = 20;

    readonly LocalLibraryCache _cache;
    readonly Func<MergeCacheSnapshot> _snapshotMerge;
    readonly Action<MergeCacheSnapshot> _restoreMerge;
    readonly Func<IReadOnlyList<SessionDependencyEntry>> _readSessionDeps;
    readonly Action<IReadOnlyList<SessionDependencyEntry>> _restoreSessionDeps;
    readonly Action<Action> _subscribeMergeMutating;
    readonly Action<Action> _unsubscribeMergeMutating;
    readonly Action _onMergeMutating;

    readonly List<LibrarianUndoStep> _steps = new();
    Capture? _active;
    bool _restoring;

    // Raised whenever CanUndo/TopDescription may have changed, so the UI can re-evaluate the
    // Undo command's enabled state and its label.
    public event Action? Changed;

    public bool CanUndo => _steps.Count > 0;
    public int Depth => _steps.Count;
    public string? TopDescription => _steps.Count > 0 ? _steps[^1].Description : null;

    public LibrarianUndoRecorder(
        LocalLibraryCache cache,
        Func<MergeCacheSnapshot> snapshotMerge, Action<MergeCacheSnapshot> restoreMerge,
        Action<Action> subscribeMergeMutating, Action<Action> unsubscribeMergeMutating,
        Func<IReadOnlyList<SessionDependencyEntry>> readSessionDeps,
        Action<IReadOnlyList<SessionDependencyEntry>> restoreSessionDeps)
    {
        _cache = cache;
        _snapshotMerge = snapshotMerge;
        _restoreMerge = restoreMerge;
        _readSessionDeps = readSessionDeps;
        _restoreSessionDeps = restoreSessionDeps;
        _subscribeMergeMutating = subscribeMergeMutating;
        _unsubscribeMergeMutating = unsubscribeMergeMutating;

        _cache.SlotMutating += OnSlotMutating;
        _onMergeMutating = OnMergeMutating;
        _subscribeMergeMutating(_onMergeMutating);
    }

    // The cache outlives this window (it's constructed by the caller and reused), so a recorder
    // that never unsubscribed would keep observing every edit made by a LATER Librarian session.
    public void Dispose()
    {
        _cache.SlotMutating -= OnSlotMutating;
        _unsubscribeMergeMutating(_onMergeMutating);
    }

    // Opens a capture scope around one user action. Nested Begins (an action implemented in terms
    // of another undoable one) join the OUTER step rather than splitting into two, so one gesture
    // is always one Ctrl+Z.
    public IDisposable Begin(string description)
    {
        if (_active != null) return NoOpScope.Instance;
        _active = new Capture(this, description, _readSessionDeps());
        return _active;
    }

    // Called by PlaceMergeBankWithTypeChange immediately before it stages a whole-bank HD-1/EXi
    // reformat - event-driven capture can't see this one (it's index metadata, not a slot write).
    public void CapturePendingBankTypeChange(int bank)
    {
        if (_restoring) return;
        _active?.CaptureBankType(bank, _cache.PendingBankTypeChange(bank));
    }

    // Rolls the most recent step back and returns its description (null if the stack was empty -
    // nothing is mutated in that case). Restores, in order: local slots (one op-log entry, so the
    // rollback is auditable history and index.json stays a valid fold of the log), the pending
    // bank-type-change intent, the Merge Window's staged contents, and the pending-dependency
    // list. Capture is suppressed throughout - an undo is never itself an undoable step.
    public string? Undo()
    {
        if (_steps.Count == 0) return null;
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);

        _restoring = true;
        try
        {
            if (step.LocalSlots.Count > 0)
                _cache.RestoreSlots(step.LocalSlots, $"Undid: {step.Description}", DateTime.UtcNow);
            if (step.PendingBankTypeChange is { } bt)
            {
                if (bt.Prior is bool prior) _cache.SetPendingBankTypeChange(bt.Bank, prior);
                else _cache.ClearPendingBankTypeChange(bt.Bank);
                _cache.Save();
            }
            if (step.Merge is { } merge) _restoreMerge(merge);
            _restoreSessionDeps(step.SessionDependencies);
        }
        finally { _restoring = false; }

        Changed?.Invoke();
        return step.Description;
    }

    // Called after a successful Sync/Commit: every step below describes local state that has now
    // been written to hardware, and this stack can't roll a hardware write back.
    public void Clear()
    {
        if (_steps.Count == 0) return;
        _steps.Clear();
        Changed?.Invoke();
    }

    void OnSlotMutating(int objType, int bank, int number, LocalIndexEntry? prior)
    {
        if (_restoring) return;
        _active?.CaptureSlot(objType, bank, number, prior);
    }

    void OnMergeMutating()
    {
        if (_restoring) return;
        _active?.CaptureMerge(_snapshotMerge);
    }

    void End(Capture capture)
    {
        if (!ReferenceEquals(_active, capture)) return;
        _active = null;
        var step = capture.Build();
        if (step.CapturedNothing) return;
        _steps.Add(step);
        if (_steps.Count > MaxSteps) _steps.RemoveAt(0);
        Changed?.Invoke();
    }

    sealed class Capture : IDisposable
    {
        readonly LibrarianUndoRecorder _owner;
        readonly string _description;
        readonly List<SessionDependencyEntry> _sessionDeps;
        // Keyed so the FIRST prior state per slot wins - a single user action can write the same
        // slot more than once (ToggleDelete: Discard then SetPendingDelete).
        readonly Dictionary<string, LocalSlotSnapshot> _slots = new();
        MergeCacheSnapshot? _merge;
        (int Bank, bool? Prior)? _bankType;
        bool _disposed;

        public Capture(LibrarianUndoRecorder owner, string description, IReadOnlyList<SessionDependencyEntry> sessionDeps)
        {
            _owner = owner;
            _description = description;
            _sessionDeps = sessionDeps.ToList();
        }

        public void CaptureSlot(int objType, int bank, int number, LocalIndexEntry? prior)
        {
            string key = LocalLibraryIndex.Key(objType, bank, number);
            if (!_slots.ContainsKey(key)) _slots[key] = new LocalSlotSnapshot(objType, bank, number, prior);
        }

        public void CaptureMerge(Func<MergeCacheSnapshot> snapshot) => _merge ??= snapshot();

        public void CaptureBankType(int bank, bool? prior) => _bankType ??= (bank, prior);

        public LibrarianUndoStep Build() => new()
        {
            Description = _description,
            LocalSlots = _slots.Values.ToList(),
            Merge = _merge,
            SessionDependencies = _sessionDeps,
            PendingBankTypeChange = _bankType,
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.End(this);
        }
    }

    // Handed out for a nested Begin - the outer scope owns the step, so this does nothing.
    sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}
