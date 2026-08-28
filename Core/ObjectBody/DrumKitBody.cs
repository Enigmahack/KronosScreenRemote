namespace KronosScreenRemote;

// Raw-body accessors for a Drum Kit object (obj 0x04). Name plus - as of this session -
// per-zone SAMPLE reference decoding (kronosology/docs/interfaces/pcg_file_format.md §6,
// hardware/corpus-confirmed there). Full per-note editing still has no consumer.
static class DrumKitBody
{
    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static bool IsInit(byte[] body) => IsInitName(ReadName(body));

    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("DRUM", StringComparison.OrdinalIgnoreCase);
    }

    // ── Per-zone sample reference (Sample Bank UUID + Sample Id) ── §6: 24-byte name, then
    // 128 Notes x 300 bytes, each holding 8 Zones x 34 bytes. Zone layout: +0 Sample On/Off,
    // +1..+16 Bank UUID (16 bytes), +17 reserved/unexplained (same 1-byte gap found at every
    // other reference site this session - see SampleReferenceWalker), +18/+19 Sample Id (LE
    // u16). A kit can have up to 1024 zones (128 x 8) - a real user kit was corpus-confirmed
    // with 366 populated - so callers MUST dedupe by bank identity rather than show one row
    // per zone (SampleReferenceWalker does this).
    const int NoteBase = 24, NoteStride = 300, ZonesPerNote = 8, ZoneStride = 34;
    const int ZoneOnOffOffset = 0, ZoneUuidOffset = 1, ZoneUuidLength = 16, ZoneIdOffset = 18;
    public const int NoteCount = 128;

    public static IEnumerable<(int Note, int Zone, byte[] Uuid, int SampleId)> IterSampleZoneRefs(byte[] body)
    {
        for (int note = 0; note < NoteCount; note++)
        {
            int noteBase = NoteBase + note * NoteStride;
            for (int zone = 0; zone < ZonesPerNote; zone++)
            {
                int zoneBase = noteBase + zone * ZoneStride;
                if (zoneBase + ZoneIdOffset + 1 >= body.Length) yield break;
                if (body[zoneBase + ZoneOnOffOffset] == 0) continue;   // zone gate (advisor)
                var uuid = body[(zoneBase + ZoneUuidOffset)..(zoneBase + ZoneUuidOffset + ZoneUuidLength)];
                int idOff = zoneBase + ZoneIdOffset;
                int sampleId = body[idOff] | (body[idOff + 1] << 8);
                yield return (note, zone, uuid, sampleId);
            }
        }
    }
}
