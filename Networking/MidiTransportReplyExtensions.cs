namespace KronosScreenRemote;

// Shared "subscribe → send → await first matching reply → unsubscribe" scaffold for the
// Kronos's ASYNCHRONOUS SysEx replies. A write (MIDI_SEND) is fire-and-forget on the wire; the
// func-0x24 Reply / bank digest / bank-types answer arrives later on the transport's live
// SysExMessageReceived stream. Every caller that awaits one used to hand-roll the same
// TaskCompletionSource + Task.WhenAny(timeout) + event-unsubscribe dance - it appeared four
// times (SysExDumpCollector's two Send-and-await paths, SysExService's digest and bank-types
// queries), each its own subtly-different copy. This centralizes the scaffold; a caller supplies
// only what actually differs (how to send, how to recognize its reply via a nullable `match`)
// and keeps its own serialization gate - the helper is deliberately ignorant of that.
//
// Split into struct/class overloads on purpose. A single `where T : notnull` method returning
// `Task<T?>` looks right but is a TRAP: for an unconstrained/notnull T the `?` is annotation-only
// and erased for value types, so `AwaitReplyAsync<int>` would return `Task<int>` and a timeout
// would yield 0 (a valid reply code = success!) instead of null. The `where T : struct` overload
// makes `T?` a real `Nullable<T>`; `match`'s `T?` return also differs between the two (Nullable<T>
// vs. an annotated reference), which is exactly what lets them coexist as overloads.
static class MidiTransportReplyExtensions
{
    // Value-type replies (int reply code, ProgramBankTypes): `match` returns Nullable<T>, and a
    // send failure or timeout returns a genuine null Nullable<T>.
    public static async Task<T?> AwaitReplyAsync<T>(
        this IKronosMidiTransport transport,
        Func<Task<bool>> send,
        Func<byte[], T?> match,
        int timeoutMs)
        where T : struct
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMsg(byte[] m) { if (match(m) is { } value) tcs.TrySetResult(value); }

        transport.SysExMessageReceived += OnMsg;
        try
        {
            if (!await send().ConfigureAwait(false)) return null;
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return winner == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            transport.SysExMessageReceived -= OnMsg;
        }
    }

    // Reference-type replies (a byte[] digest): `match` returns a nullable reference, and a send
    // failure or timeout returns null.
    public static async Task<T?> AwaitReplyAsync<T>(
        this IKronosMidiTransport transport,
        Func<Task<bool>> send,
        Func<byte[], T?> match,
        int timeoutMs)
        where T : class
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMsg(byte[] m) { if (match(m) is { } value) tcs.TrySetResult(value); }

        transport.SysExMessageReceived += OnMsg;
        try
        {
            if (!await send().ConfigureAwait(false)) return null;
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return winner == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            transport.SysExMessageReceived -= OnMsg;
        }
    }
}
