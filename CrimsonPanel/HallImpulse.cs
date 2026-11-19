using System.IO;
using System.Text;

namespace CrimsonPanel;

internal static class HallImpulse
{
    internal static readonly int[] Rates =
        [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000];

    internal static string FileName(int rate) => $"pff-hall-v1-{rate}.wav";

    internal static void Write(Stream stream, int rate)
    {
        float[] left = BuildChannel(rate, right: false);
        float[] right = BuildChannel(rate, right: true);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int dataSize = left.Length * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (int i = 0; i < left.Length; i++)
        {
            writer.Write((short)Math.Round(left[i] * short.MaxValue));
            writer.Write((short)Math.Round(right[i] * short.MaxValue));
        }
    }

    private static float[] BuildChannel(int rate, bool right)
    {
        var impulse = new float[(int)(rate * 1.85)];
        var random = new Random(right ? 7343 : 5219);
        float low = 0;
        double smoothing = 1 - Math.Exp(-2 * Math.PI * 4600 / rate);
        for (int i = (int)(rate * .085); i < impulse.Length; i++)
        {
            double time = (double)i / rate;
            float noise = (float)(2 * random.NextDouble() - 1);
            low += (float)smoothing * (noise - low);
            double fadeIn = Math.Min(1, (time - .084) / .09);
            double fadeOut = Math.Min(1, (1.85 - time) / .08);
            impulse[i] = (float)(low * fadeIn * fadeOut * Math.Exp(-Math.Log(1000) * (time - .085) / 1.65));
        }

        double diffuseEnergy = impulse.Sum(x => (double)x * x);
        double scale = .62 / Math.Sqrt(diffuseEnergy);
        for (int i = 0; i < impulse.Length; i++) impulse[i] *= (float)scale;
        int[] milliseconds = right ? [34, 63, 102, 149, 219, 315] : [26, 51, 87, 121, 198, 283];
        float[] gains = [.34f, -.24f, .20f, .16f, -.13f, .09f];
        for (int i = 0; i < milliseconds.Length; i++)
            impulse[(int)(rate * milliseconds[i] / 1000.0)] += gains[i];
        return impulse;
    }
}
