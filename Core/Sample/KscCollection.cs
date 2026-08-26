namespace KronosScreenRemote;

using System.IO;
using System.Text;

// .KSC - collection manifest (plain CRLF text, NOT chunked). Header
// "#KORG Script Version 1.0" / "#v2" / "#uuid:<uuid>", then one plain line per owned
// .KMP/.KSF filename, then (REQUIRED) one "#>User.0.2.<filename>" line per entry
// above, same order - omitting that block produces a file that loads with no error
// but shows zero content in the sample browser (doc §1.2).
//
// Read path stays permissive (must be able to open a real _UserBank.KSC for
// inspection). ToBytes()/Save() (normal/"mFieldA==false" mode, doc §1.3) refuse to
// target a _UserBank.KSC-suffixed filename - that mode's own plain-filename-list +
// #uuid: format is real Kronos-generated output only. See ToUserBankBytes() below for
// the dedicated, correctly-formatted writer.
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
                "Refusing to write a _UserBank.KSC via normal-mode ToBytes()/Save() - that " +
                "format (plain filename list + #uuid:) is real Kronos-generated output only. " +
                "Use ToUserBankBytes()/SaveUserBank() for the dedicated #>>uuid:-reference format.");

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

    // Writes the OWN-BANK case of the _UserBank.KSC "reference-export" format (doc
    // §1.3, mFieldA==true): one "#>>uuid:<BankUuid>.MS<n>.1.0.<name>" line per .KMP
    // entry and one "#>>uuid:<BankUuid>.DS<n>.1.0.<name>" line per bare-.KSF entry (n =
    // 0-based, separately counted per type - positional/emission-order, NOT tied to a
    // multisample's own Mno1 or a sample's own Sno1), followed by one closing
    // "#>uuid:<BankUuid>.<MsCount>.<DsCount>.<own .KSC base filename>" summary line -
    // the bare-filename form doc §1.3 specifies for a bank referencing itself (as
    // opposed to the "HDD:INTERNAL HD:..."/"EXs<N> ..." forms used for an external
    // dependency bank, which this writer doesn't produce). <name> is each referenced
    // .KMP/.KSF's own 24-byte NAME chunk re-encoded via EncodeNameField (space-padded,
    // suffix right-aligned into the 24 bytes, e.g. "ClaudeTestLoopOFF     -L"), not a
    // trimmed/re-derived string.
    //
    // Deliberately does NOT attempt to replicate a real Eva-generated _UserBank.KSC's
    // full contents: a real one reflects whatever the Kronos sampling engine currently
    // has RESIDENT IN RAM at Save time, which can include extra MS/DS entries from
    // unrelated, previously-loaded content never part of this .KSC's own file list.
    // This writer only ever emits this collection's own on-disk entries - the "genuine
    // disk-pointer/streaming" case the doc describes as the feature's actual point.
    //
    // See kronosology doc's Open Questions ("Can a tool hand-author a working
    // _UserBank.KSC?") for this format's verification status.
    public byte[] ToUserBankBytes()
    {
        if (Path is null) throw new InvalidOperationException("ToUserBankBytes needs Path set to resolve entry files");
        if (BankUuid is null) throw new InvalidOperationException("ToUserBankBytes needs BankUuid set");

        var contentDir = ContentDirFor(Path);
        var msLines = new List<string>();
        var dsLines = new List<string>();
        foreach (var entry in Entries)
        {
            var entryPath = System.IO.Path.Combine(contentDir, entry);
            if (entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase))
            {
                var kmp = KmpMultisample.Open(File.ReadAllBytes(entryPath))
                    ?? throw new InvalidOperationException($"not a recognizable .KMP: {entry}");
                var name = Encoding.ASCII.GetString(KorgRiffChunk.EncodeNameField(kmp.Name, kmp.Suffix, 24));
                msLines.Add($"#>>uuid:{BankUuid}.MS{msLines.Count}.1.0.{name}");
            }
            else if (entry.EndsWith(".KSF", StringComparison.OrdinalIgnoreCase))
            {
                var ksf = KsfSample.Open(File.ReadAllBytes(entryPath))
                    ?? throw new InvalidOperationException($"not a recognizable .KSF: {entry}");
                var name = Encoding.ASCII.GetString(KorgRiffChunk.EncodeNameField(ksf.Name, ksf.Suffix, 24));
                dsLines.Add($"#>>uuid:{BankUuid}.DS{dsLines.Count}.1.0.{name}");
            }
        }

        var lines = new List<string> { "#KORG Script Version 1.0", "#v2" };
        lines.AddRange(msLines);
        lines.AddRange(dsLines);
        lines.Add($"#>uuid:{BankUuid}.{msLines.Count}.{dsLines.Count}.{System.IO.Path.GetFileNameWithoutExtension(Path)}");
        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }

    // <ksc-dir>/<ksc-basename>_UserBank.KSC - the sibling path a real Kronos always
    // places this file at, next to the normal .KSC sharing the same #uuid: (doc §1.3).
    public string UserBankPath =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) ?? "",
            System.IO.Path.GetFileNameWithoutExtension(Path) + "_UserBank.KSC");

    public void SaveUserBank() => File.WriteAllBytes(UserBankPath, ToUserBankBytes());

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
    // .KMP/.KSF this .KSC's plain filename entries reference
    // (CKorgResourceFile::GetPathInSubdirectoryFromFileName, kronosology doc §1.5).
    public static string ContentDirFor(string kscPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(kscPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(kscPath));

    // Smallest Sno1 not currently used by any .KSF anywhere under `contentDir` (scanned
    // recursively - zone subfolders and bare/repository files alike). A .KSF's own
    // SNO1 chunk is what CKorgFileKSF::GetSampleNumber actually reads (kronosology doc
    // §1.6); leaving it at the field's default (0) makes a .KSC bulk-load silently
    // drop all but one of the identically-numbered zones' audio. Every new sample this
    // app writes must get a real, collection-unique value - callers pass
    // `Path.GetDirectoryName(kmpPath)` or `ContentDirFor(collectionPath)`.
    // Disk-scanning (not an in-memory counter) so it stays correct across app restarts
    // and multi-session edits - Sno1 isn't otherwise held in memory (KmpZone/
    // KmpMultisample never carry it).
    public static uint NextFreeSno1(string contentDir)
    {
        uint? max = null;
        if (Directory.Exists(contentDir))
        {
            foreach (var path in Directory.EnumerateFiles(contentDir, "*.KSF", SearchOption.AllDirectories))
            {
                KsfSample? s;
                try { s = KsfSample.Open(File.ReadAllBytes(path)); }
                catch { continue; }
                if (s == null) continue;
                if (max == null || s.Sno1 > max) max = s.Sno1;
            }
        }
        return max == null ? 0 : max.Value + 1;
    }

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
