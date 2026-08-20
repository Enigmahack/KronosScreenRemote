namespace KronosScreenRemote;

using System.IO;

// Headless diagnostic ONLY (see App.xaml.cs's `--sample-ftp-pull-check` flag) - drives
// SampleFtpClosure.PullAsync against a REAL Kronos over FTP with no Window/dialog, so
// the wire-protocol side of Phase 2 (login, closure-walk pull) can be verified against
// real hardware without clicking through the UI. Re-opens everything it pulled from
// disk to confirm it actually parses, the same "open everything, report OK/FAIL" bar
// SampleFormatFixtureCheck holds real files to.
static class SampleFtpPullCheck
{
    public static void Run(string host, int port, string user, string pass, string remotePath)
    {
        try { RunAsync(host, port, user, pass, remotePath).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex}");
            Environment.Exit(1);
        }
    }

    static async Task RunAsync(string host, int port, string user, string pass, string remotePath)
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_ftp_pull_check");
        if (Directory.Exists(localRoot)) Directory.Delete(localRoot, recursive: true);
        Directory.CreateDirectory(localRoot);

        using var client = KronosFtpSession.CreateClient(host, port, user, pass);
        await client.Connect();
        Console.WriteLine($"Connected to {host}:{port}.");

        var (localPath, map) = await SampleFtpClosure.PullAsync(client, remotePath, localRoot, Console.WriteLine);

        try { await client.Disconnect(); } catch { }

        Console.WriteLine($"\nPulled {map.Count} file(s) into {localRoot}.");
        foreach (var (local, remote) in map)
            Console.WriteLine($"  {remote}  ->  {local}");

        int failCount = 0;
        foreach (var local in map.Keys)
        {
            var ext = Path.GetExtension(local).ToUpperInvariant();
            try
            {
                var bytes = File.ReadAllBytes(local);
                bool ok;
                switch (ext)
                {
                    case ".KSC": KscCollection.Open(bytes); ok = true; break;
                    case ".KMP": ok = KmpMultisample.Open(bytes) != null; break;
                    case ".KSF": ok = KsfSample.Open(bytes) != null; break;
                    default: ok = true; break;
                }
                Console.WriteLine((ok ? "OK   " : "FAIL ") + local);
                if (!ok) failCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {local}: {ex.Message}");
                failCount++;
            }
        }

        Console.WriteLine(failCount == 0
            ? $"\nALL {map.Count} PULLED FILE(S) PARSE OK - picked file: {localPath}"
            : $"\n{failCount} FILE(S) FAILED TO PARSE");
        Environment.Exit(failCount == 0 ? 0 : 1);
    }
}
