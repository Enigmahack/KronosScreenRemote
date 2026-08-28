namespace KronosScreenRemote;

// Raw-body accessors for a Wave Sequence object (obj 0x05). Name plus - as of this session -
// per-step SAMPLE reference decoding (kronosology/docs/interfaces/pcg_file_format.md §7,
// hardware/corpus-confirmed there). Full per-step editing still has no consumer.
static class WaveSequenceBody
{
    public static string ReadName(byte[] body) => Librarian.ReadName(body);
    public static byte[] WriteName(byte[] body, string name) => Librarian.BuildRenamedBody(body, name);

    public static bool IsInit(byte[] body) => IsInitName(ReadName(body));

    public static bool IsInitName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Contains("INIT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("WAVE", StringComparison.OrdinalIgnoreCase);
    }

    // ── Per-step sample reference (Bank UUID + Multisample Select) ── §7: 24-byte name,
    // 16-byte Common (offsets 24-39), then 64 Steps x 34 bytes starting at offset 40. Step
    // layout: +0 Step Type (bits 1-0; 0=Multisample - Rest/Tie steps reference nothing, per
    // advisor), +1..+16 Bank UUID (16 bytes), +17 reserved/unexplained (same 1-byte gap as
    // every other site), +18/+19 Multisample Select (LE u16).
    const int StepBase = 40, StepStride = 34, StepTypeOffset = 0, StepUuidOffset = 1, StepUuidLength = 16, StepSelectOffset = 18;
    public const int StepCount = 64;

    public static IEnumerable<(int Step, byte[] Uuid, int Select)> IterSampleStepRefs(byte[] body)
    {
        for (int step = 0; step < StepCount; step++)
        {
            int stepBase = StepBase + step * StepStride;
            if (stepBase + StepSelectOffset + 1 >= body.Length) yield break;
            if ((body[stepBase + StepTypeOffset] & 0x03) != 0) continue;   // 0 = Multisample
            var uuid = body[(stepBase + StepUuidOffset)..(stepBase + StepUuidOffset + StepUuidLength)];
            int selOff = stepBase + StepSelectOffset;
            int select = body[selOff] | (body[selOff + 1] << 8);
            yield return (step, uuid, select);
        }
    }
}
