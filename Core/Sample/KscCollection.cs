namespace KronosScreenRemote;

using System.IO;
using System.Text;

// .KSC - collection manifest (plain CRLF text, NOT chunked). Header
// "#KORG Script Version 1.0" / "#v2" / "#uuid:<uuid>", then one plain line per owned
// .KMP/.KSF filename, then (REQUIRED, hardware-confirmed) one "#>User.0.2.<filename>"
// line per entry above, same order - omitting that block produces a file that loads
// with no error but shows zero content in the sample browser (doc §1.2).
//
// Read path stays permissive (must be able to open a real _UserBank.KSC for
// inspection). Write path refuses to target a _UserBank.KSC-suffixed filename -
// hardware-confirmed (doc §1.3) to be Kronos-generated output only; hand-authoring one
// produces "There is no readable data" on real hardware.
sealed class KscCollection
{
    public string? BankUuid;
    public List<string> Entries = [];
    public string? Path;

    public static KscCollection Open(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data);
        var lines = text.Split("\r\n");
        var k = new KscCollection();

        // header lines 0/1 are fixed literals; line 2 is #uuid: (not always present -
        // e.g. never in a _UserBank.KSC).
        int i;
        if (lines.Length > 2 && lines[2].StartsWith("#uuid:"))
        {
            k.BankUuid = lines[2]["#uuid:".Length..];
            i = 3;
        }
        else
        {
            i = 2;
        }
        while (i < lines.Length && lines[i].Length > 0 && !lines[i].StartsWith('#'))
        {
            k.Entries.Add(lines[i]);
            i++;
        }
        return k;
    }

    public byte[] ToBytes(string? targetFileName = null)
    {
        // Fall back to Path's own filename when the caller omits targetFileName, so
        // a bare ToBytes() on a collection whose Path is already _UserBank.KSC-suffixed
        // can't silently bypass the guard below - Save() is the common path and always
        // passes it explicitly, but this closes the gap for any other caller.
        targetFileName ??= Path != null ? System.IO.Path.GetFileName(Path) : null;
        if (targetFileName != null && targetFileName.EndsWith("_UserBank.KSC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to write a _UserBank.KSC - hardware-confirmed Kronos-generated " +
                "output only, never hand-authored (produces \"There is no readable data\" on real hardware).");

        BankUuid ??= GenBankUuid();
        var lines = new List<string> { "#KORG Script Version 1.0", "#v2", $"#uuid:{BankUuid}" };
        lines.AddRange(Entries);
        lines.AddRange(Entries.Select(e => $"#>User.0.2.{e}"));
        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }

    public void Save(string? path = null)
    {
        path ??= Path;
        if (path is null) throw new InvalidOperationException("no path given and none stored");
        var bytes = ToBytes(System.IO.Path.GetFileName(path));
        File.WriteAllBytes(path, bytes);
        Path = path;
    }

    // Standard generated UUID per pcg_file_format.md §7: byte 15 bit 0 must be 0 for
    // the mono/"bank identity" form. .NET's Guid byte array stores its last 8 bytes
    // (indices 8-15) in the same order as the RFC4122 string form, so masking index 15
    // here matches Python's uuid.UUID(bytes=...) semantics exactly.
    public static string GenBankUuid()
    {
        var b = Guid.NewGuid().ToByteArray();
        b[15] &= 0xFE;
        return new Guid(b).ToString();
    }

    // <ksc-dir>/<ksc-basename>/ - the collection's own content folder, holding every
    // .KMP/.KSF this .KSC's plain filename entries reference (confirmed by
    // CKorgResourceFile::GetPathInSubdirectoryFromFileName, kronosology doc §1.5).
    // Extracted 2026-08-22 (Opus redundancy review) from nine independent copies of this
    // exact two-call expression spread across this file, SampleEditorViewModel, and
    // SampleImportBuilder - one place to get the convention right.
    public static string ContentDirFor(string kscPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(kscPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(kscPath));

    // Build a fresh manifest by scanning <ksc-dir>/<ksc-basename>/ for .KMP/.KSF files
    // - the "generate .KSC for this folder" operation.
    public static KscCollection ForFolder(string kscPath)
    {
        var contentDir = ContentDirFor(kscPath);
        var entries = new List<string>();
        if (Directory.Exists(contentDir))
        {
            foreach (var f in Directory.GetFiles(contentDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(f);
                if (name.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".KSF", StringComparison.OrdinalIgnoreCase))
                    entries.Add(name);
            }
        }
        return new KscCollection { Path = kscPath, Entries = entries };
    }
}
