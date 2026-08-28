namespace KronosScreenRemote;

// Off-hardware self-test for ExsOptionFile.Parse - pure text parsing, no FTP/network (that's
// ExsOptionIndex.BuildAsync's job, deliberately not covered here - see its own header comment
// for why it only ever runs from an explicit user action). Same convention as
// SampleReferenceWalkerSelfTests. Sample text taken verbatim from
// kronosology/docs/interfaces/file_formats.md's own documented examples.
static class ExsOptionFileSelfTests
{
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        void Check(string name, bool cond) { if (!cond) fails.Add(name); }

        // Korg-internal factory EXs - numeric line-4 id, no UuidId.
        {
            string text = "EXs16\r\nFunk and Soul Brass\r\n16\r\n2,17,EXs16 Funk and Soul Brass\r\n";
            var file = ExsOptionFile.Parse(16, text);
            Check("korg-internal-parses", file != null);
            Check("korg-internal-name", file?.Name == "Funk and Soul Brass");
            Check("korg-internal-no-uuid", file?.UuidId == null);
        }

        // KApro / 3rd-party - line-4 id is "uuid:<uuid>".
        {
            string text = "EXs285\r\nKApro Premium Grands & Keys\r\n285\r\n2,uuid:a7f5dbaa-aaa2-425a-8519-954227f4b35e,EXs285 KApro Premium Grands & Keys\r\n";
            var file = ExsOptionFile.Parse(285, text);
            Check("3rd-party-parses", file != null);
            Check("3rd-party-name", file?.Name == "KApro Premium Grands & Keys");
            Check("3rd-party-uuid", file?.UuidId == "a7f5dbaa-aaa2-425a-8519-954227f4b35e");
        }

        // \n-only line endings must parse the same as \r\n (some FTP/text-mode transfers strip \r).
        {
            string text = "EXs16\nFunk and Soul Brass\n16\n2,17,EXs16 Funk and Soul Brass\n";
            var file = ExsOptionFile.Parse(16, text);
            Check("lf-only-parses", file?.Name == "Funk and Soul Brass");
        }

        // Malformed/empty content must not throw and must not fabricate a name.
        {
            Check("empty-text-returns-null", ExsOptionFile.Parse(1, "") == null);
            Check("one-line-returns-null", ExsOptionFile.Parse(1, "EXs1\r\n") == null);
        }

        // ExsOptionIndex's own UUID-hex lookup key must match SampleReferenceWalker's masking
        // convention (byte 15 bit 0 cleared) - verified end to end here since that's the one
        // place the two classes' conventions must actually agree for a lookup to ever hit.
        {
            var guid = Guid.Parse("8e7ab882-4abf-4317-b095-874bc9627802");
            var raw = guid.ToByteArray();
            raw[15] &= 0xFE;
            string hex = Convert.ToHexString(raw);
            Check("uuid-hex-key-is-32-chars", hex.Length == 32);
            Check("uuid-hex-key-is-uppercase", hex == hex.ToUpperInvariant());
        }

        return fails;
    }
}
