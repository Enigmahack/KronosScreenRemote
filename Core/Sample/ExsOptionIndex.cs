namespace KronosScreenRemote;

using System.IO;
using FluentFTP;

// A name index built from every /korg/rw/Options/Sxxx file actually installed on the connected
// Kronos - answers "what sample bank does this byte-decoded EXs<N> or raw-UUID bank actually
// mean" for the Object Dependencies panel (LibrarianShellViewModel's AddSampleRows) and the
// user's own original question about naming a 3rd-party sample bank. See
// Core/LocalLibrary/SampleReferenceWalker.cs and kronosology/docs/interfaces/
// pcg_file_format.md's "Bank-UUID classifier"/"Formula for a librarian/editor" sections for why
// BOTH lookups (by EXs number AND by raw UUID) are needed: EXs1-126 use the legacy KORG/MS-
// prefixed form (byte-decodable to a bare number with no network access at all), but EXs127+
// AND every genuine 3rd-party/user bank share the SAME raw-16-byte-UUID form on disk - the only
// way to tell "this raw UUID is actually an installed EXs pack" from "this is some other,
// un-installed sample bank" is to check whether an Options file's own line-4 <id> names that
// exact UUID. A raw UUID with no match here is left exactly as SampleReferenceWalker already
// labels it (unresolved) - it may still be a real 3rd-party .KSC bank, just not one installed
// as a licensed EX product on THIS instrument; resolving that case needs a full SSD .KSC scan,
// which is real future work, not attempted here (see the investigation capsule's own "NEXT").
//
// Built ONCE per explicit user action (the Librarian's "Resolve Sample Bank Names..." button) -
// deliberately NEVER automatically and NEVER on the Object-Dependencies selection-change path:
// it needs a live FTP connection (possibly an interactive login prompt), which must never block
// or surprise-pop a dialog just because the user clicked a tree row. A real unit's Options
// folder holds a handful to a few dozen files (one per installed EXs/user product), not
// hundreds - listing the directory once and reading each file it actually contains is cheap,
// unlike a hypothetical brute-force probe of every possible S001..S999 filename.
sealed class ExsOptionIndex
{
    readonly Dictionary<int, string> _byNumber = new();
    readonly Dictionary<string, string> _byUuidHex = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byNumber.Count;

    public string? NameForExsNumber(int number) => _byNumber.GetValueOrDefault(number);

    // `hex` must already be masked the same way SampleReferenceWalker.DedupKey masks byte 15
    // bit 0 (the mono/stereo flag) before formatting - callers pass a UserOrThirdParty row's own
    // Key, which already is that masked hex string.
    public string? NameForUuidHex(string hex) => _byUuidHex.GetValueOrDefault(hex);

    public static async Task<ExsOptionIndex> BuildAsync(AsyncFtpClient client, CancellationToken ct = default)
    {
        var index = new ExsOptionIndex();
        const string dir = "/korg/rw/Options";
        FtpListItem[] listing;
        try { listing = await client.GetListing(dir, ct); }
        catch { return index; }   // folder missing/unreadable - callers just see an empty index

        string scratchDir = Path.Combine(Path.GetTempPath(), "kronos_exs_options");
        Directory.CreateDirectory(scratchDir);

        foreach (var item in listing)
        {
            if (ct.IsCancellationRequested) break;
            if (item.Type != FtpObjectType.File) continue;
            // Filenames are "S<digits>" (e.g. S016, S285) - anything else in this folder isn't
            // an option file this app understands.
            if (item.Name.Length < 2 || (item.Name[0] != 'S' && item.Name[0] != 's')) continue;
            if (!int.TryParse(item.Name.AsSpan(1), out int number)) continue;

            string localPath = Path.Combine(scratchDir, item.Name);
            string text;
            try
            {
                await client.DownloadFile(localPath, item.FullName, FtpLocalExists.Overwrite, token: ct);
                text = await File.ReadAllTextAsync(localPath, System.Text.Encoding.ASCII, ct);
            }
            catch { continue; }   // one unreadable file shouldn't abort the whole index

            if (ExsOptionFile.Parse(number, text) is not { } file) continue;
            index._byNumber[number] = file.Name;

            if (file.UuidId != null && Guid.TryParse(file.UuidId, out var guid))
            {
                var raw = guid.ToByteArray();
                raw[15] &= 0xFE;   // mono/stereo flag - masked before use as a dedup/lookup key, same as DedupKey
                index._byUuidHex[Convert.ToHexString(raw)] = file.Name;
            }
        }
        return index;
    }
}
