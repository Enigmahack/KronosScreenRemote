namespace KronosScreenRemote;

// The single centralized big-endian/little-endian boundary for .KSF sample data.
// KSF PCM is big-endian 16-bit signed on disk (see kronosology/docs/interfaces/
// ksc_kmp_ksf_file_format.md §3); NAudio, WAV, and every DSP library in this app are
// little-endian throughout. Every place PCM crosses into/out of the KSF layer
// (waveform render, playback, DSP, WAV import/export) must route through here -
// nowhere else should reinterpret KsfSample.Pcm's raw bytes directly.
static class KsfPcm
{
    public static short[] ToHostOrder(byte[] bigEndianPcm)
    {
        int n = bigEndianPcm.Length / 2;
        var result = new short[n];
        for (int i = 0; i < n; i++)
            result[i] = (short)((bigEndianPcm[i * 2] << 8) | bigEndianPcm[i * 2 + 1]);
        return result;
    }

    public static byte[] ToBigEndianBytes(short[] hostOrderPcm)
    {
        var result = new byte[hostOrderPcm.Length * 2];
        for (int i = 0; i < hostOrderPcm.Length; i++)
        {
            result[i * 2]     = (byte)(hostOrderPcm[i] >> 8);
            result[i * 2 + 1] = (byte)hostOrderPcm[i];
        }
        return result;
    }
}
