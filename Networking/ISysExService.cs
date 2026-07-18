using System.ComponentModel;

namespace KronosScreenRemote;

interface ISysExService : INotifyPropertyChanged, IMoveExecutor
{
    string PerformanceDisplay { get; }

    bool IsAvailable { get; }

    // CC# the Kronos VALUE slider transmits (default 18). Incoming CCs with this
    // controller number drive ValueSliderChanged so the UI slider can follow the
    // hardware. Assignment-dependent on the Kronos; settable to match.
    int ValueSliderCc { get; set; }

    // When true, entering a program/combi whose name isn't cached triggers a single
    // debounced func-0x72 name fetch so the footer fills in without a full Sync.
    bool PullNamesOnChange { get; set; }

    // Start (or switch) the MIDI backend. The transport is chosen by the caller
    // (TCP daemon or direct USB); every SysEx feature works over either. Disposes
    // any previously-running transport.
    void Start(IKronosMidiTransport transport);

    void Reset();

    void RefreshNow();

    void NotifyUserActivity();

    // True when bulk SysEx dumps are possible (MIDI monitor enabled).
    bool CanDump { get; }

    // Dump one Set List (obj 0x0D) by number; null if unavailable. Collected off
    // the live stream, so it bypasses the daemon's single-message SYSEX limit.
    Task<SetListData?> DumpSetListAsync(int number);

    // Sweep every set list (obj 0x0D, 0..127) in one pass for "Sync All". Reports
    // (done, total, found-with-content); cancellable between set lists (progress is
    // preserved in the returned result). See SetListSyncResult for the Found /
    // ConfirmedEmpty / no-response split.
    Task<SetListSyncResult> DumpAllSetListsAsync(
        IProgress<(int Done, int Total, int Found)>? progress, CancellationToken ct);

    // Write a Set List slot's Name and/or Notes (Comments) via Object Dump (obj
    // 0x11 / 0x10 — bank=set list, index=slot), then commit with a Store Bank
    // Request (func 0x76). Pass null for a field to leave it unchanged.
    //
    // Performance (type/bank/index) is intentionally NOT writable here: the only
    // SysEx path for it (Parameter Change, func 0x43) edits whichever Set List is
    // currently active on the Kronos's own screen, not an arbitrary bank+index
    // like this write — there's no safe way to target a background Set List.
    Task<SetListSlotWriteResult> WriteSetListSlotAsync(int setListNumber, int slotNumber, string? name, string? comments);

    // Request every program/combi bank's names and capture them into the cache,
    // so program-change follow shows names with no per-change SysEx query. Reports
    // (banks done, banks total, names captured). Returns the final name count.
    Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct);

    // Fired (on the UI thread) when an incoming CC matching ValueSliderCc is
    // seen on the live MIDI stream. Argument is the 0-127 controller value.
    event Action<int>? ValueSliderChanged;

    event Action<SysExTrafficEntry>? SysExTraffic;

    // Apply MIDI/SysEx settings. Safe to call before or after Start().
    // midiMonitorEnabled — when false, the MIDI stream monitor is stopped.
    // proactivePoll      — when true, polls on a fixed interval; otherwise only on-change triggers.
    void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges);

    // Send raw MIDI bytes via MIDI_SEND on the control port.
    // Fires SysExTraffic for both the TX bytes and the OK/ERR response.
    Task<bool> SendMidiAsync(string hexBytes);

    // ── Librarian ────────────────────────────────────────────────────────────
    // Dump one full object (obj 0x00 Program / 0x01 Combi / 0x0D Set List) by
    // bank+index, parsed into header + decoded body. Null if unavailable/no reply.
    Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index);

    // Best-effort current performance as an ObjLoc (for the live 0x43 dual-write).
    // Null if unknown. (The remaining Librarian primitives — object write, Store,
    // digest, backup, raw send — come from the IMoveExecutor base interface.)
    ObjLoc? CurrentPerformanceLoc();

    // Bulk HD-1/EXi type query for every program bank (func 0x60/0x61) — a single
    // cheap, non-destructive request, unlike the deprecated per-bank 0x7D/0x7E query.
    // Null if unavailable/no reply.
    Task<ProgramBankTypes?> RequestProgramBankTypesAsync();
}
