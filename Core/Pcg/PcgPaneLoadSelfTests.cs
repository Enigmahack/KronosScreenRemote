namespace KronosScreenRemote;

using System.IO;
using System.Text;
using KronosScreenRemote.ViewModels;

// Off-hardware self-test for "the last attempted PCG load always wins" - the pane must never
// keep showing a previous file's tree once a new load has been attempted, whether that new
// attempt succeeds (replaces it) or fails (clears it). Drives PcgPaneViewModel.Load directly
// via LoadBytesForTesting, sidestepping the file-dialog/FTP-picker paths real callers need.
static class PcgPaneLoadSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        var pane = new PcgPaneViewModel();

        // Second successful load must fully replace the first, not merge or linger.
        pane.LoadBytesForTesting(BuildMinimalPcg("FIRST PROG"), "first.pcg");
        Check("first-load-name", pane.LoadedFileName == "first.pcg");
        Check("first-load-content", pane.Get(new ObjLoc(LibObj.Program, 0x00, 0))?.Name == "FIRST PROG");

        pane.LoadBytesForTesting(BuildMinimalPcg("SECOND PROG"), "second.pcg");
        Check("second-load-replaces-name", pane.LoadedFileName == "second.pcg");
        Check("second-load-replaces-content", pane.Get(new ObjLoc(LibObj.Program, 0x00, 0))?.Name == "SECOND PROG");
        Check("second-load-one-program-root", pane.Roots.Count(r => r.Label == "Programs") == 1);

        // Set Lists have no bank concept (unlike Programs/Combis) - the "Set Lists" type root
        // must carry its own BankRef and nest Set List objects directly underneath, NOT through
        // a redundant inner "Set Lists" bank node repeating the same label a second time (a
        // real regression this once had - see PcgPaneViewModel.BuildSetListSubtree).
        var setListsRoot = pane.Roots.FirstOrDefault(r => r.Label == "Set Lists");
        Check("setlists-root-present", setListsRoot != null);
        Check("setlists-root-is-selectable-bank-equivalent", setListsRoot?.BankRef != null);
        Check("setlists-no-redundant-nested-bank-node", setListsRoot?.Children.All(c => c.Label != "Set Lists") == true);
        Check("setlists-leaf-nests-directly-under-root", setListsRoot?.Children.Any(c => c.Loc != null) == true);

        // A THIRD load that fails (not a real .pcg) must clear the second file's tree, not
        // leave it displayed - a failed load is never "the previous file is still current."
        pane.LoadBytesForTesting(new byte[] { 1, 2, 3, 4 }, "not-a-pcg.pcg");
        Check("failed-load-clears-name", pane.LoadedFileName == null);
        Check("failed-load-clears-content", pane.Get(new ObjLoc(LibObj.Program, 0x00, 0)) == null);
        Check("failed-load-clears-tree", pane.Roots.Count == 0);

        // ── "Load from Kronos" now goes through IRemotePcgSource, so the login/browse/download
        //    branch that used to build a Window + hit the FTP server inline is finally reachable
        //    off-hardware via an in-memory fake source. ──
        var kpane = new PcgPaneViewModel();

        // A successful remote pick loads exactly what the source handed back.
        kpane.LoadFromKronosAsync(new FakeRemotePcgSource(
            RemotePcgPick.Ok(BuildMinimalPcg("KRONOS PROG"), "remote.pcg"))).GetAwaiter().GetResult();
        Check("kronos-pick-loads-name", kpane.LoadedFileName == "remote.pcg");
        Check("kronos-pick-loads-content", kpane.Get(new ObjLoc(LibObj.Program, 0x00, 0))?.Name == "KRONOS PROG");

        // A cancelled/failed pick sets the status and leaves the previously loaded file intact -
        // "the previously loaded file (if any) is unchanged," never a silent wipe.
        kpane.LoadFromKronosAsync(new FakeRemotePcgSource(
            RemotePcgPick.Failed("Load from Kronos cancelled."))).GetAwaiter().GetResult();
        Check("kronos-cancel-sets-status", kpane.StatusText == "Load from Kronos cancelled.");
        Check("kronos-cancel-keeps-previous-file", kpane.LoadedFileName == "remote.pcg");
        Check("kronos-cancel-keeps-previous-content", kpane.Get(new ObjLoc(LibObj.Program, 0x00, 0))?.Name == "KRONOS PROG");

        // A pick that returns bytes which aren't a real .pcg clears the tree - same "last
        // attempted load wins" rule the local path already honors.
        kpane.LoadFromKronosAsync(new FakeRemotePcgSource(
            RemotePcgPick.Ok(new byte[] { 1, 2, 3, 4 }, "bad.pcg"))).GetAwaiter().GetResult();
        Check("kronos-bad-bytes-clears-name", kpane.LoadedFileName == null);
        Check("kronos-bad-bytes-clears-tree", kpane.Roots.Count == 0);

        return fails;
    }

    // In-memory IRemotePcgSource for the from-Kronos self-test: hands back a pre-canned pick
    // with no FTP connection or Window - the whole point of the seam.
    sealed class FakeRemotePcgSource : IRemotePcgSource
    {
        readonly RemotePcgPick _result;
        public FakeRemotePcgSource(RemotePcgPick result) => _result = result;
        public Task<RemotePcgPick> PickAsync() => Task.FromResult(_result);
    }

    static byte[] BuildMinimalPcg(string programName)
    {
        const int programSize = ProgramFormatConverter.PcgSlotSize, setListSize = 700;
        var programBody = new byte[programSize];
        Encoding.ASCII.GetBytes(programName).CopyTo(programBody, 0);

        var setListBody = new byte[setListSize];
        Encoding.ASCII.GetBytes("A SET LIST").CopyTo(setListBody, 0);
        setListBody = SetListBody.WriteSlotName(setListBody, 0, "SLOT ONE");   // non-blank -> not IsEmpty

        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WriteBE32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        WriteAscii("KORG");
        ms.WriteByte(0x68); ms.WriteByte(0x00); ms.WriteByte(0x02); ms.WriteByte(0x01);
        ms.Write(new byte[8]);

        WriteAscii("MBK1");
        WriteBE32(0); WriteBE32(PcgFileSelfTests.ChunkChecksum(1, programSize, 0, programBody));
        WriteBE32(1); WriteBE32(programSize); WriteBE32(0);
        ms.Write(programBody);

        WriteAscii("SBK1");
        WriteBE32(0); WriteBE32(PcgFileSelfTests.ChunkChecksum(1, setListSize, 0, setListBody));
        WriteBE32(1); WriteBE32(setListSize); WriteBE32(0);
        ms.Write(setListBody);

        return ms.ToArray();
    }
}
