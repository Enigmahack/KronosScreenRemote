namespace KronosScreenRemote;

using System.IO;

// Builds brand-new .KSF + KmpZone content from already-decoded 44100 Hz PCM (see
// AudioImport) and adds it to target multisample(s) at a given key range - "import
// audio" is really just "construct real zones the same way the Kronos itself would,
// then let the existing Save path write them." Covers both mono (one multisample, one
// zone) and stereo (a matched pair of multisamples, one zone added to each) - see
// kronosology/docs/interfaces/ksc_kmp_ksf_file_format.md §2.2: a Kronos stereo
// instrument is two full multisamples with the same Name and opposite "-L"/"-R"
// Suffix, NOT two zones inside one .KMP (RLP1 zones have no channel field).
static class SampleImportBuilder
{
    // Hardware limit (doc §2.1's own zone-list shape - RLP1 has no room past 128
    // entries) - enforced here, the single place every zone-adding path funnels
    // through (AddSampleZone itself; AddStereoSampleZonePair calls it twice), so a
    // caller can't bypass the cap by using one entry point and not another.
    public const int MaxZonesPerMultisample = 128;

    // Inserts the new zone in TopKey order (not appended past a lower-keyed zone that
    // happens to come later in the list) - KmpZone's own doc comment: zone order IS
    // key-range order, each zone owning (previous zone's TopKey+1) through its own
    // TopKey. Writes the new .KSF into <kmp-dir>/<kmp-basename>/ per the standard
    // convention (KmpZone.KsfPath). Does NOT save the .KMP itself - that's the
    // caller's job, same as every other in-memory-edit-then-explicit-Save method in
    // SampleEditorViewModel. `suffix` bakes "-L"/"-R"/"" into the zone's own .KSF
    // Name+Suffix, matching MakeNameLeft/MakeNameRight/MakeName (doc §5).
    public static KmpZone AddSampleZone(KmpMultisample m, string kmpPath, string sampleName,
        short[] pcm, int sampleRate, int originalKey, int topKey, string suffix = "")
    {
        if (m.Zones.Count >= MaxZonesPerMultisample)
            throw new InvalidOperationException($"'{m.Name}{m.Suffix}' already has {MaxZonesPerMultisample} zones (the maximum) - remove one before adding another.");

        var filename = m.NextKsfFilename();
        var zone = new KmpZone
        {
            OriginalKey = (byte)Math.Clamp(originalKey, 0, 127),
            TopKey = (byte)Math.Clamp(topKey, 0, 127),
            Filename = filename,
        };

        int insertAt = m.Zones.FindIndex(z => z.TopKey > zone.TopKey);
        if (insertAt < 0) m.Zones.Add(zone); else m.Zones.Insert(insertAt, zone);

        // Flags = 0x81 deliberately, not inherited from anywhere: one-shot + +12dB boost
        // on, Reverse off, LoopTune 0 (field default) - the correct state for brand-new
        // imported audio, same as a real Kronos sampling a fresh WAV. NOT a copy of any
        // existing sample's state, so this can never accidentally carry over a Reverse/
        // boost/tune setting from whatever was previously loaded.
        // Sno1 must be unique across the collection (see KscCollection.NextFreeSno1's
        // own comment) - never leave it at the field's
        // default, which silently breaks .KSC bulk loading for any zone that collides.
        var contentDir = Path.GetDirectoryName(kmpPath) is { Length: > 0 } d ? d : ".";
        var ksf = new KsfSample
        {
            Name = sampleName, Suffix = suffix, SampleRate = (uint)sampleRate, Flags = 0x81,
            Sno1 = KscCollection.NextFreeSno1(contentDir),
        };
        ksf.SetSamples(pcm);
        var ksfPath = zone.KsfPath(kmpPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ksfPath)!);
        ksf.Save(ksfPath);

        return zone;
    }

    // Adds a matching zone (same key range, same base sample name, opposite -L/-R
    // suffix) to both halves of an already-existing stereo pair. Does NOT save either
    // .KMP - same explicit-Save discipline as AddSampleZone.
    public static (KmpZone left, KmpZone right) AddStereoSampleZonePair(
        KmpMultisample left, string leftKmpPath, KmpMultisample right, string rightKmpPath,
        string sampleName, short[] leftPcm, short[] rightPcm, int sampleRate, int originalKey, int topKey)
    {
        var l = AddSampleZone(left, leftKmpPath, sampleName, leftPcm, sampleRate, originalKey, topKey, "-L");
        var r = AddSampleZone(right, rightKmpPath, sampleName, rightPcm, sampleRate, originalKey, topKey, "-R");
        return (l, r);
    }

    // Default key range for a brand-new multisample's very first, auto-created zone -
    // C-1 (MIDI 0) to C2 (MIDI 36), matching real Kronos hardware behavior -
    // deliberately NOT the same "full 0-127 keyboard"
    // default AddPlaceholderZone gives a manually-added first zone; this is specifically
    // what Create Multisample (mono or stereo) auto-populates so the multisample editor
    // has something to select/import into immediately, without a separate "Add Zone"
    // step. Placeholder filename ("SKIPPEDSAMPLE") - same convention as
    // AddPlaceholderZone - real audio is attached afterward via Import Sample/Assign.
    public static KmpZone MakeDefaultFirstZone() => new()
    {
        Filename = "SKIPPEDSAMPLE",
        OriginalKey = 36, // C2
        TopKey = 36,      // C2
    };

    // Creates a brand-new stereo pair: two multisamples with identical Name, Suffix
    // "-L"/"-R", and MNO1 = mno1Left / mno1Left+1 (doc §2.2 - MNO1 adjacency matches
    // every real Kronos-authored pair examined, though nothing reads it as load-
    // bearing). The two .KMP filenames are free-form (see the doc's own note that
    // filenames carry no pairing meaning) - "<baseName>-L.KMP"/"-R.KMP" here, since
    // both multisamples share the same Name and would otherwise collide on disk.
    // Saves both .KMP files, adds both to the collection, and saves the collection.
    public static (KmpMultisample left, string leftPath, KmpMultisample right, string rightPath)
        CreateStereoMultisamplePair(KscCollection collection, string collectionPath, string baseName, uint mno1Left)
    {
        var kmpDir = KscCollection.ContentDirFor(collectionPath);
        Directory.CreateDirectory(kmpDir);

        var left = new KmpMultisample { Name = baseName, Suffix = "-L", Mno1 = mno1Left };
        var right = new KmpMultisample { Name = baseName, Suffix = "-R", Mno1 = mno1Left + 1 };

        // (KmpMultisample.AutoFileName's own comment): the .KMP's own filename must
        // follow Kronos's auto-naming convention (Name
        // prefix + MNO1), NOT bake -L/-R into the filename - a real Kronos silently
        // fails to load the audio behind a "<Name>-L.KMP"/"-R.KMP" pair even though
        // every other byte is correct.
        var leftFileName = KmpMultisample.AutoFileName(baseName, mno1Left);
        var rightFileName = KmpMultisample.AutoFileName(baseName, mno1Left + 1);
        var leftPath = Path.Combine(kmpDir, leftFileName);
        var rightPath = Path.Combine(kmpDir, rightFileName);
        left.Save(leftPath);
        right.Save(rightPath);

        collection.Entries.Add(leftFileName);
        collection.Entries.Add(rightFileName);
        collection.Save(collectionPath);

        return (left, leftPath, right, rightPath);
    }

    // Finds `m`'s stereo-pair sibling within the same collection: same Name, opposite
    // Suffix ("-L"<->"-R"), AND adjacent MNO1 (m.Mno1 +/- 1). Name+Suffix alone isn't
    // enough: several unrelated, never-renamed
    // multisamples in the same collection all carry the Kronos's own unedited default
    // name ("NewMS______________000"), so Name-only matching would happily "pair" two
    // multisamples that were never a real stereo instrument together. Every genuine
    // pair examined (doc §2.2's table) has adjacent MNO1; requiring it here rejects
    // the false-positive case instead of silently importing into the wrong sibling.
    // Returns (null, null) if `m` isn't "-L"/"-R" at all, or no matching sibling is
    // found (including a lone "-L" with no "-R" counterpart - doc §2.2's NEWMS002/003
    // case - which is valid and just plays as mono). Known limitation, accepted rather
    // than engineered around: if an unrelated mono "-L"/"-R" multisample happens to
    // land MNO1-adjacent to a real pair (e.g. NEWMS002 sitting at MNO1=2, one above
    // the real NEWMS000/001 pair's MNO1=1), this can
    // match the wrong sibling. The format has no stronger pairing signal to check
    // against (§2.2) - new pairs created via CreateStereoMultisamplePair always get
    // fresh, non-colliding IDs, so this only bites existing collections with pre-
    // existing ID collisions like the one found here.
    public static (KmpMultisample? sibling, string? siblingPath) FindStereoSibling(
        KscCollection collection, KmpMultisample m, string kmpPath)
    {
        if (m.Suffix is not ("-L" or "-R")) return (null, null);
        var wantSuffix = m.Suffix == "-L" ? "-R" : "-L";
        var kmpDir = Path.GetDirectoryName(kmpPath) ?? "";

        foreach (var entry in collection.Entries)
        {
            if (!entry.EndsWith(".KMP", StringComparison.OrdinalIgnoreCase)) continue;
            var candidatePath = Path.Combine(kmpDir, entry);
            if (string.Equals(candidatePath, kmpPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(candidatePath)) continue;

            KmpMultisample? candidate;
            try { candidate = KmpMultisample.Open(File.ReadAllBytes(candidatePath)); }
            catch { candidate = null; }
            if (candidate == null) continue;

            bool mno1Adjacent = candidate.Mno1 == m.Mno1 + 1 || (m.Mno1 > 0 && candidate.Mno1 == m.Mno1 - 1);
            if (candidate.Name == m.Name && candidate.Suffix == wantSuffix && mno1Adjacent)
                return (candidate, candidatePath);
        }
        return (null, null);
    }
}
