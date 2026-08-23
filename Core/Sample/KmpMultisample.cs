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

    // MSP1's 2 trailing bytes (payload offset 16-17): present and non-zero in every
    // real file seen (e.g. "03 00"), meaning still unconfirmed. Preserved raw on load
    // rather than guessed at.
    byte[] _msp1Tail = [0x00, 0x00];

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
                m._msp1Tail = payload[16..18];
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
        var msp1 = KorgRiffChunk.Concat(KorgRiffChunk.EncodeNameField(Name, Suffix, 16), _msp1Tail);
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

    // MS<multisample:03d><zone:03d>.KSF - the real naming convention, used when
    // adding a brand-new zone. ONLY valid for that case (appending) - Zones.Count is
    // "the new zone's own future index" exactly because the new zone hasn't been
    // inserted yet. Do NOT reuse this for "replace an EXISTING zone's sample": Count
    // doesn't change across that operation, so calling this for two different existing
    // zones in the same session returns the SAME string both times, and the second
    // Save silently overwrites the first zone's own file - see NextFreeZoneFileName
    // below for that case instead (fixed 2026-08-22, this was a real bug in
    // KsfSample.ImportSampleIntoZone).
    public string NextKsfFilename() => $"MS{Mno1:D3}{Zones.Count:D3}.KSF";

    // Collision-free filename for "give an EXISTING zone new/different audio" - scans
    // every OTHER zone's own current Filename for the numeric suffix already in use
    // (rather than trusting Zones.Count, which says nothing about what's actually
    // referenced once zones have been replaced/reordered/soft-deleted) and returns the
    // smallest index not currently claimed by any zone in this multisample.
    public string NextFreeZoneFileName()
    {
        var prefix = $"MS{Mno1:D3}";
        var used = new HashSet<int>();
        foreach (var z in Zones)
        {
            if (z.Filename.Length == prefix.Length + 7 // "MS###" + "###" + ".KSF"
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
