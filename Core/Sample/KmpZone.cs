namespace KronosScreenRemote;

// One RLP1 record (18 bytes) inside a .KMP - a single keymap zone. Field offsets
// hardware-confirmed 2026-08-19 (see kronosology/docs/interfaces/
// ksc_kmp_ksf_file_format.md §2.1); OriginalKey/TopKey correct an earlier decompiled
// guess ("key-range high"/"mUnknownLow") that a controlled hardware test disproved.
sealed class KmpZone
{
    public byte OriginalKey = 60;  // RLP1 record offset 0 - root/tracking key
    public byte TopKey = 60;       // RLP1 record offset 1 - top of this zone's trigger
                                    // key range; there is no separate bottom-key field -
                                    // the range runs from (previous zone's TopKey + 1)
                                    // to this zone's own TopKey
    public string Filename = "";   // up to 12 bytes, e.g. "MS001000.KSF"
    public byte[] Unknown4 = [0x00, 0x00, 0x40, 0x00]; // RLP1 bytes 2-5 - constant in
        // every real record seen; separately confirmed NOT original-key (§5.1). Write
        // this constant unless a future test finds real meaning in it.
    public byte[] Rlp3 = new byte[6]; // opaque; offset+5 forced 0 on write regardless
    public byte[] Rlp2 = new byte[4]; // opaque; contents unconfirmed

    // A deliberately-unsampled key position - real files mark these with the literal
    // filename "SKIPPEDSAMPLE" (truncated to 12 chars, doc §2.1); no real .KSF backs
    // it, don't try to open one.
    public bool IsSkipped => Filename.StartsWith("SKIPPEDSAMPL", StringComparison.OrdinalIgnoreCase);

    // Resolve this zone's .KSF path per the confirmed folder convention:
    // <kmp-dir>/<kmp-basename-no-ext>/<filename>.
    public string KsfPath(string kmpPath)
    {
        var kmpDir = System.IO.Path.GetDirectoryName(kmpPath) ?? "";
        var kmpBase = System.IO.Path.GetFileNameWithoutExtension(kmpPath);
        return System.IO.Path.Combine(kmpDir, kmpBase, Filename);
    }
}
