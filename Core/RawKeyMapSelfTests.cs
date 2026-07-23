using System.Windows.Input;

namespace KronosScreenRemote;

// Off-hardware self-test for RawKeyMap's pure logic — upsert (replace-in-place vs append,
// shift-variant independence) and its legacy JSON round-trip / skip-bad-row resilience —
// none of which touch disk or the live Entries global. This is the "testable without
// touching disk" payoff of folding RawKeyMap onto JsonFileCache. Wired into App.xaml.cs's
// --librarian-selftest; returns failing check names (empty == pass).
static class RawKeyMapSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // ── UpsertInto: append, replace-same-key in place, shift-variant stays distinct ──
        {
            var list = new List<RawMapping>();
            RawKeyMap.UpsertInto(list, new RawMapping { HostKey = Key.A, HostShift = false, RawCode = 30 });
            Check("upsert-adds", list.Count == 1 && list[0].RawCode == 30);

            RawKeyMap.UpsertInto(list, new RawMapping { HostKey = Key.A, HostShift = false, RawCode = 42 });
            Check("upsert-replaces-same-key", list.Count == 1 && list[0].RawCode == 42);

            RawKeyMap.UpsertInto(list, new RawMapping { HostKey = Key.A, HostShift = true, RawCode = 99 });
            Check("upsert-shift-is-distinct", list.Count == 2);
        }

        // ── Serialize → Deserialize round-trips every field ──
        {
            var src = new List<RawMapping>
            {
                new() { HostKey = Key.Left, HostShift = true, RawCode = 105, RawShift = true, Label = "cursor" },
                new() { HostKey = Key.F1,   HostShift = false, RawCode = 59,  RawShift = false, Label = "" },
            };
            var round = RawKeyMap.Deserialize(RawKeyMap.Serialize(src));
            Check("roundtrip-count", round.Count == 2);
            Check("roundtrip-fields",
                round[0].HostKey == Key.Left && round[0].HostShift && round[0].RawCode == 105 &&
                round[0].RawShift && round[0].Label == "cursor" &&
                round[1].HostKey == Key.F1 && round[1].RawCode == 59);
        }

        // ── Legacy on-disk shape loads; unparseable Key and non-positive code are skipped ──
        {
            const string legacy = """
                [
                  { "host_key": "B", "host_shift": false, "raw_code": 48, "raw_shift": false, "label": "b" },
                  { "host_key": "NotAKey", "raw_code": 1 },
                  { "host_key": "C", "raw_code": 0 },
                  { "host_key": "D", "host_shift": true, "raw_code": 50 }
                ]
                """;
            var loaded = RawKeyMap.Deserialize(legacy);
            Check("legacy-skips-bad-rows",
                loaded.Count == 2 &&
                loaded[0].HostKey == Key.B && loaded[0].Label == "b" &&
                loaded[1].HostKey == Key.D && loaded[1].HostShift && loaded[1].RawCode == 50);
        }

        return fails;
    }
}
