namespace KronosScreenRemote;

// Off-hardware self-test for ExsOptionFile.Parse and the catalog-backed ExsOptionIndex - pure
// text parsing plus one read of the shipped Resources/ExsCatalog.json, no hardware and no
// network. Same convention as SampleReferenceWalkerSelfTests. Sample text taken verbatim from
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
        // convention (byte 15 bit 0 cleared) AND the byte order a PCG body actually stores -
        // the one place the two classes' conventions must agree for a lookup to ever hit.
        // The expected bytes are the ones found live in PRELOAD.PCG at 0x1266deb, quoted in
        // pcg_file_format.md §7: the UUID's string order, NOT Guid.ToByteArray()'s.
        {
            var raw = ExsOptionIndex.RawUuidBytes("8e7ab882-4abf-4317-b095-874bc9627802")!;
            raw[15] &= 0xFE;
            string hex = Convert.ToHexString(raw);
            Check("uuid-hex-key-is-string-order", hex == "8E7AB8824ABF4317B095874BC9627802");
            Check("uuid-hex-key-is-32-chars", hex.Length == 32);
            Check("uuid-hex-key-is-uppercase", hex == hex.ToUpperInvariant());
            // The stereo member of the same bank differs only in byte 15 bit 0 and must key the same.
            var stereo = ExsOptionIndex.RawUuidBytes("8e7ab882-4abf-4317-b095-874bc9627803")!;
            stereo[15] &= 0xFE;
            Check("uuid-hex-key-masks-stereo-bit", Convert.ToHexString(stereo) == hex);
            Check("uuid-hex-rejects-junk", ExsOptionIndex.RawUuidBytes("not-a-uuid") == null);
        }

        // Catalog builder: both lookups off a hand-written catalog, including the UUID form the
        // Object Dependencies panel actually queries with (masked hex, not the uuid string).
        {
            string json = """
                {
                 "16": "EXs16\nFunk and Soul Brass\n16\n2,17,EXs16 Funk and Soul Brass\n",
                 "285": "EXs285\nKApro Premium Grands & Keys\n285\n2,uuid:a7f5dbaa-aaa2-425a-8519-954227f4b35e,EXs285 KApro Premium Grands & Keys\n",
                 "not-a-number": "junk\njunk\n"
                }
                """;
            var index = ExsOptionIndex.FromCatalog(json);
            Check("catalog-skips-bad-key", index.Count == 2);
            Check("catalog-by-number", index.NameForExsNumber(16) == "Funk and Soul Brass");
            Check("catalog-unknown-number", index.NameForExsNumber(999) == null);

            // Queried exactly the way the Object Dependencies panel does: SampleReferenceWalker's
            // DedupKey hex of the raw PCG bytes, masked.
            var raw = ExsOptionIndex.RawUuidBytes("a7f5dbaa-aaa2-425a-8519-954227f4b35e")!;
            raw[15] &= 0xFE;
            Check("catalog-by-uuid", index.NameForUuidHex(Convert.ToHexString(raw)) == "KApro Premium Grands & Keys");

            Check("catalog-malformed-json-is-empty", ExsOptionIndex.FromCatalog("{ not json").Count == 0);
        }

        // The shipped catalog itself - a build that embeds nothing, or embeds something this
        // parser can't read, must fail here rather than silently resolving no names at runtime.
        {
            var shipped = ExsOptionIndex.FromCatalog();
            Check("shipped-catalog-loads", shipped.Count > 250);
            Check("shipped-catalog-has-korg-internal", shipped.NameForExsNumber(16) == "Funk and Soul Brass");

            // EXs145 PCreek Clavinet C - one of the 3rd-party banks found by raw-UUID scan in a
            // real .PCG (Z:/TEST PCG/T8_RESAVE.PCG), i.e. a lookup this panel genuinely performs.
            var raw = ExsOptionIndex.RawUuidBytes("e9432454-fbd7-405c-ab98-4f3bb339482e")!;
            raw[15] &= 0xFE;
            Check("shipped-catalog-has-3rd-party-uuid", shipped.NameForUuidHex(Convert.ToHexString(raw)) == "PCreek Clavinet C");
        }

        return fails;
    }
}
