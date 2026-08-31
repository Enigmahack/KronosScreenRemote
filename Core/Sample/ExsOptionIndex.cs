namespace KronosScreenRemote;

using System.IO;
using System.Reflection;
using System.Text.Json;

// A name index built from the shipped EXs product catalog (Resources/ExsCatalog.json) - answers
// "what sample bank does this byte-decoded EXs<N> or raw-UUID bank actually mean" for the Object
// Dependencies panel (LibrarianShellViewModel's AddSampleRows) and the user's own original
// question about naming a 3rd-party sample bank. See Core/LocalLibrary/SampleReferenceWalker.cs
// and kronosology/docs/interfaces/pcg_file_format.md's "Bank-UUID classifier"/"Formula for a
// librarian/editor" sections for why BOTH lookups (by EXs number AND by raw UUID) are needed:
// EXs1-126 use the legacy KORG/MS-prefixed form (byte-decodable to a bare number with no lookup
// at all), but EXs127+ AND every genuine 3rd-party/user bank share the SAME raw-16-byte-UUID form
// on disk - the only way to put a name on a raw UUID is to find an option file whose own line-4
// <id> names that exact UUID. A raw UUID with no match here is left exactly as
// SampleReferenceWalker already labels it (unresolved) - it may still be a real 3rd-party .KSC
// bank, just not one Korg publishes as an EXs product.
//
// The catalog holds the verbatim Sxxx option-file text of every EXs pack Korg publishes, so a hit
// IDENTIFIES a product - it is NOT evidence that the pack is installed on the connected
// instrument, and callers must not word it as if it were. That is a deliberate trade: the
// previous version of this class read /korg/rw/Options off the live unit over FTP, which did
// prove installation but could never work at all - the Kronos FTP server roots its tree at
// /korg/rw/HD, so /korg/rw/Options is unreachable over that transport and the index came back
// empty every time. Resolving names for real beats proving installation and resolving nothing.
// (Reading the unit's own Options folder through KronosScreenRemoteDaemon, which does have full
// filesystem access, would restore the stronger "installed HERE" semantics - future work.)
//
// Tools/exsCatalogGen.py regenerates the catalog from Korg's CDN without downloading the packs
// themselves (HTTP Range requests pull just the ~60-byte Sxxx file out of each multi-GB zip), so
// new EXs releases can be picked up without a rebuild: drop a regenerated exs_catalog.json next
// to the exe and it wins over the embedded copy.
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

    // Loose override next to the exe, same convention as palette_override.json - lets a user
    // refresh the catalog for packs released after this build without waiting for a new one.
    public static string OverridePath => Path.Combine(Storage.DataDir, "exs_catalog.json");

    // Whether the loaded catalog came from OverridePath rather than the embedded copy - the
    // Librarian says so in its status line, since a stale hand-dropped file is otherwise
    // invisible.
    public bool FromOverrideFile { get; private set; }

    // Pure, no network, no hardware: reads exs_catalog.json (override, else embedded) and parses
    // every entry through the same ExsOptionFile.Parse the FTP path used, so the option-file
    // format lives in exactly one place. `json` is only ever passed by the self-tests.
    public static ExsOptionIndex FromCatalog(string? json = null)
    {
        var index = new ExsOptionIndex();
        Dictionary<string, string>? entries = null;
        if (json != null)
        {
            entries = Deserialize(json);
        }
        else
        {
            // A hand-dropped override that won't parse must never shadow the perfectly good
            // embedded copy - a typo in it would otherwise silently turn the whole feature off.
            entries = Deserialize(ReadOverride());
            index.FromOverrideFile = entries != null;
            entries ??= Deserialize(ReadEmbedded());
        }
        if (entries == null) return index;

        foreach (var (key, text) in entries)
        {
            if (!int.TryParse(key, out int number)) continue;
            if (ExsOptionFile.Parse(number, text) is not { } file) continue;
            index._byNumber[number] = file.Name;

            if (RawUuidBytes(file.UuidId) is { } raw)
            {
                raw[15] &= 0xFE;   // mono/stereo flag - masked before use as a dedup/lookup key, same as DedupKey
                index._byUuidHex[Convert.ToHexString(raw)] = file.Name;
            }
        }
        return index;
    }

    static Dictionary<string, string>? Deserialize(string? json)
    {
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (Exception ex)
        {
            AppLog.Warn($"[exs-catalog] unreadable catalog: {ex.Message}");
            return null;
        }
    }

    // The 16 bytes an option file's "uuid:<uuid>" id has inside a PCG body: the UUID's own
    // string/RFC byte order, "8e7ab882-4abf-..." -> 8e 7a b8 82 4a bf ..., hardware-verified in
    // kronosology/docs/interfaces/pcg_file_format.md §7 against a real PRELOAD.PCG.
    // Deliberately NOT Guid.ToByteArray(), which byte-swaps the first three fields and would
    // therefore miss every single 3rd-party bank lookup - that's the whole EXs127+ population.
    public static byte[]? RawUuidBytes(string? uuid)
    {
        if (uuid == null) return null;
        string hex = uuid.Replace("-", "");
        if (hex.Length != 32) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    static string? ReadOverride()
    {
        try { return File.Exists(OverridePath) ? File.ReadAllText(OverridePath) : null; }
        catch (Exception ex)
        {
            AppLog.Warn($"[exs-catalog] override read failed: {ex.Message}");
            return null;
        }
    }

    static string? ReadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".ExsCatalog.json", StringComparison.Ordinal));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[exs-catalog] embedded read failed: {ex.Message}");
            return null;
        }
    }
}
