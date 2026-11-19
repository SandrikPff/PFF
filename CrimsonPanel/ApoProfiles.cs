using System.Globalization;
using System.Text;

namespace CrimsonPanel;

internal static class ApoProfiles
{
    internal const string PlaybackProfileName = "pff_crimson_v5_1_render.txt";
    internal const string CaptureProfileName = "pff_crimson_v5_1_capture.txt";
    internal static readonly int[] Frequencies = [20, 29, 41, 60, 86, 125, 180, 250, 360, 520, 750, 1100, 1600, 2300, 3300, 4700, 6800, 9700, 14000, 20000];
    private static readonly int[] LegacyFrequencies = [20, 25, 31, 40, 50, 63, 80, 100, 125, 160, 200, 315, 500, 800, 1250, 2000, 3150, 5000, 8000, 16000];
    internal sealed record Settings(string DeviceGuid, double[] Eq, double Gain, double Compression, double Effect);

    internal static string Build(Settings? settings, bool input, string? compressorPath)
    {
        var text = new StringBuilder("# Pff_crimson\n");
        if (settings is not null)
            AppendDevice(text, settings, input, compressorPath);
        return text.ToString();
    }

    private static void AppendDevice(StringBuilder text, Settings settings, bool input, string? compressorPath)
    {
        var guid = Guid.ParseExact(settings.DeviceGuid, "B");
        text.AppendLine($"Device: {guid:B}");
        string stage = input ? "capture" : "post-mix";
        text.AppendLine($"If: stage == \"{stage}\"");
        text.AppendLine("Channel: all");
        text.AppendLine($"Preamp: {Number(Math.Clamp(settings.Gain, -12, 24))} dB");
        for (int i = 0; i < Frequencies.Length; i++)
        {
            double gain = i < settings.Eq.Length ? Math.Clamp(settings.Eq[i], -12, 12) : 0;
            if (gain == 0) continue;
            text.AppendLine($"If: sampleRate > {Frequencies[i] * 2}");
            text.AppendLine($"Filter: ON PK Fc {Frequencies[i]} Hz Gain {Number(gain)} dB Q 2.7");
            text.AppendLine("EndIf:");
        }

        double compression = Math.Clamp(settings.Compression, 0, 100) / 100;
        if (compression > 0 && compressorPath is not null)
        {
            // ReaComp normalized VST2 parameters, based on APO's exported ReaComp configuration.
            double threshold = Math.Pow(10, (-9 - 19 * compression) / 20);
            double ratio = 7 * compression / 99;
            text.AppendLine($"VSTPlugin: Library \"{compressorPath}\" Thresh {Number(threshold)} Ratio {Number(ratio)} Attack 0.06 Release 0.006 AutoMkUp 0 Pre-comp 0 Dry 0 Wet 1");
        }

        if (settings.Effect > 0)
            AppendReverb(text, Math.Clamp(settings.Effect, 0, 100) * .85 / 100);
        text.AppendLine("EndIf:");
    }

    private static void AppendReverb(StringBuilder text, double wet)
    {
        // Convolve the wet channels only. If the APO cannot load its impulse,
        // playback still has the separate, undelayed physical channels.
        string dry = Number(1 - .55 * wet);
        string gain = Number(wet);
        foreach (int rate in HallImpulse.Rates)
        {
            text.AppendLine($"If: sampleRate == {rate}");
            text.AppendLine("If: outputChannelCount == 1");
            text.AppendLine("NewChannel: PFFHALLMONO");
            text.AppendLine("Copy: PFFHALLMONO=1");
            text.AppendLine("Channel: PFFHALLMONO");
            text.AppendLine($"Convolution: {HallImpulse.FileName(rate)}");
            text.AppendLine($"Copy: 1={dry}*1+{gain}*PFFHALLMONO");
            text.AppendLine("Else:");
            text.AppendLine("NewChannel: PFFHALLL PFFHALLR");
            text.AppendLine("Copy: PFFHALLL=0.78*1+0.22*2 PFFHALLR=0.22*1+0.78*2");
            text.AppendLine("Channel: PFFHALLL PFFHALLR");
            text.AppendLine($"Convolution: {HallImpulse.FileName(rate)}");
            text.AppendLine($"Copy: 1={dry}*1+{gain}*PFFHALLL 2={dry}*2+{gain}*PFFHALLR");
            text.AppendLine("EndIf:");
            text.AppendLine("EndIf:");
        }
        text.AppendLine("Channel: all");
    }

    internal static double[] MigrateBands(double[]? oldBands)
    {
        var migrated = new double[Frequencies.Length];
        if (oldBands is null || oldBands.Length == 0) return migrated;
        int last = Math.Min(oldBands.Length, LegacyFrequencies.Length) - 1;
        for (int i = 0; i < migrated.Length; i++)
        {
            int frequency = Frequencies[i];
            int match = Array.BinarySearch(LegacyFrequencies, 0, last + 1, frequency);
            if (match >= 0) { migrated[i] = oldBands[match]; continue; }
            int upper = ~match;
            if (upper > last) { migrated[i] = oldBands[last]; continue; }
            int lower = upper - 1;
            double fraction = Math.Log((double)frequency / LegacyFrequencies[lower])
                / Math.Log((double)LegacyFrequencies[upper] / LegacyFrequencies[lower]);
            migrated[i] = oldBands[lower] + (oldBands[upper] - oldBands[lower]) * fraction;
        }
        return migrated;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
