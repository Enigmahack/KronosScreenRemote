namespace KronosScreenRemote;

// Parses one /korg/rw/Options/Sxxx file - plain text, 4-5 lines, no signature (the signature
// lives in the .exsins install manifest instead). Format hardware-confirmed and documented at
// kronosology/docs/interfaces/file_formats.md's "Option files" section:
//
//   line 1: short identifier "EXs<num>" (matches the filename's own digits)
//   line 2: friendly name, free-form, e.g. "Funk and Soul Brass"
//   line 3: bank number (decimal) - the PCM bank index, same as the filename's digits
//   line 4: "2,<id>,<long name>" - <id> is a decimal number for a Korg-internal/factory EXs
//           bank, or "uuid:<uuid>" for a 3rd-party pack (KApro, Soundiron, ...) whose PCG-body
//           Bank UUID is the raw 16 bytes of that <uuid> rather than the legacy KORG/MS-prefixed
//           form (see Core/LocalLibrary/SampleReferenceWalker.cs and pcg_file_format.md's own
//           "Bank-UUID classifier" section for why that split exists at all).
//
// Read-only - this app never writes one (that's the instrument's own EXs installer's job).
sealed class ExsOptionFile
{
    public required int Number { get; init; }     // from the S<NNN> filename
    public required string Name { get; init; }     // line 2
    public string? UuidId { get; init; }            // line 4's <id>, only when it's the "uuid:..." 3rd-party form

    public static ExsOptionFile? Parse(int number, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2) return null;
        string name = lines[1].Trim();
        if (name.Length == 0) return null;

        string? uuidId = null;
        if (lines.Length > 3)
        {
            var fields = lines[3].Split(',');
            if (fields.Length >= 2 && fields[1].StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
                uuidId = fields[1]["uuid:".Length..].Trim();
        }
        return new ExsOptionFile { Number = number, Name = name, UuidId = uuidId };
    }
}
