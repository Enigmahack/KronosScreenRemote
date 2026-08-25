namespace KronosScreenRemote;

using System.IO;
using System.Text;

// .KMP - multisample/keymap (binary, CKorgRiff-framed, big-endian). Chunk order:
// MSP1 -> MNO1 -> NAME -> RLP1 -> RLP3 -> RLP2. Direct port of Tools/sample_editor/
// kronos_ksc_format.py's KmpMultisample.
sealed class KmpMultisample
{
    public string Name = "New Multisample";
    public string Suffix = "";      // "", "-L", or "-R" - stereo channel marker
    public uint Mno1;               // the multisample's own numeric ID - matches the
                                     // number in its own zones' .KSF filenames
    public List<KmpZone> Zones = [];
    public string? Path;

    // Returns null if `data` isn't a recognizable .KMP (first chunk isn't MSP1) rather
    // than throwing - mirrors PcgFile.Open's contract.
    public static KmpMultisample? Open(byte[] data)
    {
        var chunks = KorgRiffChunk.ReadChunks(data);
        if (chunks.Count == 0 || chunks[0].Tag != "MSP1" || chunks[0].Payload.Length < 18) return null;

        var m = new KmpMultisample();
        var rlp1 = new List<(byte OrigKey, byte TopKey, string Filename, byte[] Unknown4)>();
        var rlp3 = new List<byte[]>();
        var rlp2 = new List<byte[]>();

        foreach (var (tag, payload) in chunks)
        {
            if (tag == "MSP1" && payload.Length >= 18)
            {
                // Name/Suffix come from the 24-byte NAME chunk below, not this 16-byte
                // short field - see KsfSample.Open for why (they're not simple
                // re-truncations of each other on a name longer than 14 base chars).
                // The trailing 2 bytes (payload offset 16-17) are NOT preserved from the
                // source file - see ToBytes(), which always recomputes them from
                // Zones.Count.
            }
            else if (tag == "MNO1" && payload.Length >= 4)
            {
                m.Mno1 = KorgRiffChunk.ReadU32BE(payload, 0);
            }
            else if (tag == "NAME" && payload.Length >= 24)
            {
                var (name, suffix) = KorgRiffChunk.SplitNameSuffix(Encoding.ASCII.GetString(payload, 0, 24));
                m.Name = name;
                m.Suffix = suffix;
            }
            else if (tag == "RLP1")
            {
                for (int i = 0; i + 18 <= payload.Length; i += 18)
                {
                    byte origKey = payload[i];
                    byte topKey = payload[i + 1];
                    var unknown4 = payload[(i + 2)..(i + 6)];
                    string fname = Encoding.ASCII.GetString(payload, i + 6, 12).TrimEnd('\0', ' ');
                    rlp1.Add((origKey, topKey, fname, unknown4));
                }
            }
            else if (tag == "RLP3")
            {
                for (int i = 0; i + 6 <= payload.Length; i += 6)
                    rlp3.Add(payload[i..(i + 6)]);
            }
            else if (tag == "RLP2")
            {
                for (int i = 0; i + 4 <= payload.Length; i += 4)
                    rlp2.Add(payload[i..(i + 4)]);
            }
        }

        for (int idx = 0; idx < rlp1.Count; idx++)
        {
            var (origKey, topKey, fname, unknown4) = rlp1[idx];
            var z = new KmpZone { OriginalKey = origKey, TopKey = topKey, Filename = fname, Unknown4 = unknown4 };
            if (idx < rlp3.Count) z.Rlp3 = rlp3[idx];
            if (idx < rlp2.Count) z.Rlp2 = rlp2[idx];
            m.Zones.Add(z);
        }
        return m;
    }

    public byte[] ToBytes()
    {
        // MSP1's trailing 2 bytes (payload offset 16-17): the multisample's own zone
        // count, little-endian u16 - hardware-confirmed 2026-08-24 by cross-referencing
        // 4 independent real fixture pairs (8/8, 9/9, 2/2, 1/1 zones) and by a live
        // re-save test: a KMP uploaded with this field left 0 (this class's old
        // behavior) registered on a real Kronos as a zero-zone multisample - the
        // underlying .KSF sample loaded fine as a standalone resource, but tapping the
        // multisample itself triggered a "Create New Sample" prompt instead of
        // selecting it. Korg's own Eva always recomputes this on save (confirmed: a
        // re-saved file had it corrected from 0 to the real count with no other change),
        // so this always derives it from Zones.Count rather than round-tripping
        // whatever was read - never trust a stale/loaded value here.
        var msp1Tail = new byte[2];
        msp1Tail[0] = (byte)Zones.Count;
        msp1Tail[1] = (byte)(Zones.Count >> 8);
        var msp1 = KorgRiffChunk.Concat(KorgRiffChunk.EncodeNameField(Name, Suffix, 16), msp1Tail);
        var mno1 = new byte[4];
        KorgRiffChunk.WriteU32BE(mno1, 0, Mno1);
        var nameChunk = KorgRiffChunk.EncodeNameField(Name, Suffix, 24);

        using var rlp1 = new MemoryStream();
        using var rlp3 = new MemoryStream();
        using var rlp2 = new MemoryStream();
        foreach (var z in Zones)
        {
            rlp1.WriteByte(z.OriginalKey);
            rlp1.WriteByte(z.TopKey);
            var u4 = new byte[4];
            Array.Copy(z.Unknown4, u4, Math.Min(4, z.Unknown4.Length));
            rlp1.Write(u4);
            rlp1.Write(KorgRiffChunk.PadBytes(z.Filename, 12));

            // Offset+5 is always written 0 - matches Korg's own WriteFile, which masks
            // it unconditionally regardless of the in-memory value (doc §2.1).
            var r3 = new byte[6];
            Array.Copy(z.Rlp3, r3, Math.Min(5, z.Rlp3.Length));
            r3[5] = 0;
            rlp3.Write(r3);

            var r2 = new byte[4];
            Array.Copy(z.Rlp2, r2, Math.Min(4, z.Rlp2.Length));
            rlp2.Write(r2);
        }

        return KorgRiffChunk.Concat(
            KorgRiffChunk.BuildChunk("MSP1", msp1),
            KorgRiffChunk.BuildChunk("MNO1", mno1),
            KorgRiffChunk.BuildChunk("NAME", nameChunk),
            KorgRiffChunk.BuildChunk("RLP1", rlp1.ToArray()),
            KorgRiffChunk.BuildChunk("RLP3", rlp3.ToArray()),
            KorgRiffChunk.BuildChunk("RLP2", rlp2.ToArray()));
    }

    public void Save(string? path = null)
    {
        path ??= Path;
        if (path is null) throw new InvalidOperationException("no path given and none stored");
        File.WriteAllBytes(path, ToBytes());
        Path = path;
    }

    // <first 5 chars of the multisample's own Name, sanitized+uppercased,
    // underscore-padded><MNO1:03d>.KMP - the real Kronos auto-naming convention for a
    // .KMP's own FILENAME, confirmed against 2 independent real fixture pairs: a
    // multisample named "NewMS______________000" auto-files as "NEWMS000.KMP"/
    // "NEWMS001.KMP", one named "GAGA LEAD" auto-files as "GAGA_000.KMP"/
    // "GAGA_001.KMP" (space -> underscore, uppercased, truncated to 5 chars) - the file
    // name derives from Name, NOT from Suffix. Hardware-confirmed 2026-08-24 this is
    // NOT cosmetic: a stereo pair saved as "<Name>-L.KMP"/"<Name>-R.KMP" (this app's own
    // prior behavior) registered its multisample entries correctly (once the MSP1 tail
    // fix above landed) but still failed to actually load its audio on a real Kronos,
    // while byte-identical content saved under this exact naming pattern loaded and
    // played correctly - the L/R distinction belongs ONLY in the internal Suffix field
    // (MSP1/NAME, doc §2.2), never baked into the .KMP's own filename.
    // Tiered for MNO1 >= 1000 (2026-08-25, doc's own "max of 3999 possible keymaps"
    // ceiling) - both tiers hardware-confirmed against real MASTER-LIBRARY content over
    // FTP: AIRTO097.KMP/BEER-000.KMP at the original 5-char-name+3-digit tier,
    // 24K_1028.KMP/24K_1029.KMP at the 4-char-name+4-digit tier once MNO1 reaches 1000.
    // Name width shrinks from 5 to 4 characters, index width grows from 3 to 4 digits,
    // always summing to the 8-character DOS 8.3 stem.
    public static string AutoFileName(string name, uint mno1)
    {
        var sanitized = new string(name.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        int nameWidth = mno1 <= 999 ? 5 : 4;
        int indexWidth = mno1 <= 999 ? 3 : 4;
        var prefix = sanitized.Length >= nameWidth ? sanitized[..nameWidth] : sanitized.PadRight(nameWidth, '_');
        return $"{prefix}{mno1.ToString($"D{indexWidth}")}.KMP";
    }

    // MS<multisample:03d><zone:03d>.KSF for MNO1 0-999, M<multisample:04d><zone:03d>.KSF
    // for MNO1 1000-3999 (2026-08-25 - hardware-confirmed both tiers over FTP against
    // real MASTER-LIBRARY content: AIRTO097/'s own zones are MS097000.KSF etc., while
    // 24K_1028/'s are M1028000.KSF etc. - "MS"/"M" both keep the prefix+MNO1 portion at
    // a fixed 5 characters, zone index always 3 digits either way, so
    // NextFreeZoneFileName's own length check below needs no tier-specific change).
    // The real naming convention, used when adding a brand-new zone. ONLY valid for
    // that case (appending) - Zones.Count is "the new zone's own future index" exactly
    // because the new zone hasn't been inserted yet. Do NOT reuse this for "replace an
    // EXISTING zone's sample": Count doesn't change across that operation, so calling
    // this for two different existing zones in the same session returns the SAME
    // string both times, and the second Save silently overwrites the first zone's own
    // file - see NextFreeZoneFileName below for that case instead (fixed 2026-08-22,
    // this was a real bug in KsfSample.ImportSampleIntoZone).
    public string NextKsfFilename() => $"{ZoneFilePrefix}{Zones.Count:D3}.KSF";

    // "MS<mno1:D3>" for MNO1 0-999, "M<mno1:D4>" for MNO1 1000-3999 - both 5 characters,
    // shared by NextKsfFilename/NextFreeZoneFileName so the tier logic lives in one
    // place.
    string ZoneFilePrefix => Mno1 <= 999 ? $"MS{Mno1:D3}" : $"M{Mno1:D4}";

    // Collision-free filename for "give an EXISTING zone new/different audio" - scans
    // every OTHER zone's own current Filename for the numeric suffix already in use
    // (rather than trusting Zones.Count, which says nothing about what's actually
    // referenced once zones have been replaced/reordered/soft-deleted) and returns the
    // smallest index not currently claimed by any zone in this multisample.
    public string NextFreeZoneFileName()
    {
        var prefix = ZoneFilePrefix;
        var used = new HashSet<int>();
        foreach (var z in Zones)
        {
            if (z.Filename.Length == prefix.Length + 7 // "<prefix>" (5 chars either tier) + "###" + ".KSF"
                && z.Filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && z.Filename.EndsWith(".KSF", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(z.Filename.AsSpan(prefix.Length, 3), out int idx))
            {
                used.Add(idx);
            }
        }
        for (int i = 0; ; i++)
            if (!used.Contains(i)) return $"{prefix}{i:D3}.KSF";
    }
}
