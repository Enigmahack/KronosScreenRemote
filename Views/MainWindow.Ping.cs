using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;

namespace KronosScreenRemote;

public partial class MainWindow
{
    CancellationTokenSource? _pingCts;

    void StartPing()
    {
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        _ = PingLoopAsync(_host, _pingCts.Token);
    }

    void StopPing()
    {
        _pingCts?.Cancel();
        _pingCts = null;
        if (Dispatcher.CheckAccess())
            PingText.Text = "";
        else
            Dispatcher.InvokeAsync(() => PingText.Text = "");
    }

    async Task PingLoopAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, 2000).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => ApplyPingResult(reply));
            }
            catch { }

            try { await Task.Delay(3000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    void ApplyPingResult(PingReply reply)
    {
        if (reply.Status == IPStatus.Success)
        {
            long ms = reply.RoundtripTime;
            PingText.Text = $"⇄ {ms}ms";
            PingText.Foreground = ms <= 15
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x40))
                : ms <= 50
                    ? new SolidColorBrush(Color.FromRgb(0xD8, 0xC8, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0xE8, 0x20, 0x00));
        }
        else
        {
            PingText.Text     = "⇄ ---";
            PingText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }
    }
}
