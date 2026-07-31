using System.ComponentModel;

namespace KronosScreenRemote;

// The SysEx service, decomposed into role interfaces. SysExService implements the whole
// thing; ISysExService is nothing but the composition of the roles below, so consumers can
// (and increasingly do) depend on just the slice they use:
//
//   • IPerformanceFollow  - live program/combi follow view-state (PerfStatusBar binding).
//   • IMidiBackendControl - start/stop/configure the MIDI backend.
//   • IRawMidiSend        - fire raw MIDI + observe the traffic stream (SysExToolWindow).
//   • ILibrarianService   - the instrument-facing read+write surface the Librarian pipelines
//                            drive (IMoveExecutor write side + IBankDumpService read side).
//
// Migrating a call site to the narrowest role it needs shrinks its fakes to that slice -
// e.g. FakeMoveExecutor implements ILibrarianService, not the whole service.

// Live performance-follow surface: what the footer's PerfStatusBar shows and the value-slider
// follow. Extends INotifyPropertyChanged because PerformanceDisplay is data-bound.
interface IPerformanceFollow : INotifyPropertyChanged
{
    string PerformanceDisplay { get; }

    // CC# the Kronos VALUE slider transmits (default 18). Incoming CCs with this
    // controller number drive ValueSliderChanged so the UI slider can follow the
    // hardware. Assignment-dependent on the Kronos; settable to match.
    int ValueSliderCc { get; set; }

    // When true, entering a program/combi whose name isn't cached triggers a single
    // debounced func-0x72 name fetch so the footer fills in without a full Sync.
    bool PullNamesOnChange { get; set; }

    void RefreshNow();

    void NotifyUserActivity();

    // Fired (on the UI thread) when an incoming CC matching ValueSliderCc is
    // seen on the live MIDI stream. Argument is the 0-127 controller value.
    event Action<int>? ValueSliderChanged;
}

// Start/stop/configure the MIDI backend. Owned by MidiTransportCoordinator (Start/Reset) and
// the settings apply path (ApplyMidiSettings).
interface IMidiBackendControl
{
    bool IsAvailable { get; }

    // Start (or switch) the MIDI backend. The transport is chosen by the caller
    // (TCP daemon or direct USB); every SysEx feature works over either. Disposes
    // any previously-running transport.
    void Start(IKronosMidiTransport transport);

    void Reset();

    // Apply MIDI/SysEx settings. Safe to call before or after Start().
    // midiMonitorEnabled - when false, the MIDI stream monitor is stopped.
    // proactivePoll      - when true, polls on a fixed interval; otherwise only on-change triggers.
    void ApplyMidiSettings(bool midiMonitorEnabled, bool proactivePoll, int pollIntervalSec, bool pollOnChanges);
}

// Fire raw MIDI and observe the traffic stream - the exact slice SysExToolWindow needs.
interface IRawMidiSend
{
    // Send raw MIDI bytes via MIDI_SEND on the control port.
    // Fires SysExTraffic for both the TX bytes and the OK/ERR response.
    Task<bool> SendMidiAsync(string hexBytes);

    event Action<SysExTrafficEntry>? SysExTraffic;
}

// The instrument's read/query surface: bank + object + set-list dumps and bank-type probes.
// Paired with IMoveExecutor (the write side) to form ILibrarianService.
interface IBankDumpService
{
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

    // Request every program/combi bank's names and capture them into the cache,
    // so program-change follow shows names with no per-change SysEx query. Reports
    // (banks done, banks total, names captured). Returns the final name count.
    Task<int> SyncNamesAsync(IProgress<(int Done, int Total, int Names)>? progress, CancellationToken ct);

    // Dump one full object (obj 0x00 Program / 0x01 Combi / 0x0D Set List) by
    // bank+index, parsed into header + decoded body. Null if unavailable/no reply.
    Task<ObjectDump?> DumpObjectAsync(int obj, int bank, int index);

    // Attempts every object of `obj` in `bank` via ONE func-0x77 Dump Bank Request
    // instead of `count` individual func-0x72 round-trips - much faster when it works.
    // HW-UNVERIFIED for full objects and for USER banks: the only existing func-0x77
    // caller (SyncNamesAsync) is Name-object-only and confirmed REJECTED for USER banks
    // there, and nothing in this codebase has tried 0x77 against 0x00/0x01/0x0D before.
    // Returns whatever the bulk reply actually contained (0 to `count`, keyed by index)
    // - callers MUST treat a missing index as "needs an individual DumpObjectAsync
    // fallback," never as "confirmed empty": a rejected/unsupported bulk request looks
    // identical to a fully-empty bank at this layer (zero results either way).
    Task<Dictionary<int, ObjectDump>> DumpBankBulkAsync(int obj, int bank, int count);

    // Best-effort current performance as an ObjLoc (for the live 0x43 dual-write).
    // Null if unknown. (The remaining Librarian primitives - object write, Store,
    // digest, backup, raw send - come from the IMoveExecutor half of ILibrarianService.)
    ObjLoc? CurrentPerformanceLoc();

    // Bulk HD-1/EXi type query for every program bank (func 0x60/0x61) - a single
    // cheap, non-destructive request, unlike the deprecated per-bank 0x7D/0x7E query.
    // Null if unavailable/no reply.
    Task<ProgramBankTypes?> RequestProgramBankTypesAsync();
}

// Everything the Librarian's read+write pipeline touches on the instrument: the write/apply
// side (IMoveExecutor - write, store, digest, backup, raw) plus the read/query side
// (IBankDumpService). ChangesetBuilder / LibraryPullPipeline / SyncPipeline / the Librarian
// ViewModel + Window, and their FakeMoveExecutor, all depend on exactly this - not on the
// full ISysExService.
interface ILibrarianService : IMoveExecutor, IBankDumpService
{
}

interface ISysExService : IPerformanceFollow, IMidiBackendControl, IRawMidiSend, ILibrarianService
{
}
