using CrimsonPanel;

const string render = "{aaaaaaaa-bbbb-4444-8888-cccccccccccc}";
const string capture = "{aaaaaaaa-bbbb-4444-8888-dddddddddddd}";
const string thirdDevice = "{aaaaaaaa-bbbb-4444-8888-eeeeeeeeeeee}";
const string ownConfig = "# User configuration\r\nInclude: own-profile.txt\r\n";
const string legacy = "# BEGIN PFF_CRIMSON DEVICE ROUTING v4.6\r\n" +
    "Device: all\r\nInclude: pff_crimson_headphones.txt\r\n# END PFF_CRIMSON DEVICE ROUTING v4.6\r\n" +
    "# BEGIN Pff_crimson v5\r\nDevice: all\r\nInclude: pff_crimson_v5.txt\r\n# END Pff_crimson v5\r\n" +
    "Include: \"C:\\Users\\test\\AppData\\Local\\Pff_crimson\\apo\\microphone.txt\"\r\n";

var configDir = Directory.CreateTempSubdirectory("pff-routing-");
try
{
    File.WriteAllText(Path.Combine(configDir.FullName, "headphones.txt"), "# User file\r\nPreamp: 1 dB\r\n");
    File.WriteAllText(Path.Combine(configDir.FullName, "microphone.txt"),
        "# Pff_crimson device-scoped Equalizer APO profile\r\nPreamp: -12 dB\r\n");
    string originals = ownConfig + "Include: headphones.txt\r\n";
    string cleaned = ApoBridge.RemoveAppRoutes(originals + legacy + "Include: microphone.txt\r\n", configDir.FullName);
    Check(cleaned == originals, "Keep the user's files and remove Pff legacy routes.");
    const string oldExport = "# Pff_crimson device-scoped Equalizer APO profile\r\nPreamp: -12.0 dB\r\n" +
        "Filter: ON PK Fc 20 Hz Gain 6.0 dB Q 1.41\r\n";
    Check(ApoBridge.RemoveAppRoutes(oldExport, configDir.FullName) == "", "Remove a legacy export pasted as the root config.");
    Check(ApoBridge.RemoveAppRoutes(oldExport + legacy, configDir.FullName) == "", "Remove a pasted export followed by a newer Pff block.");
    Check(ApoBridge.RemoveAppRoutes(oldExport + ownConfig, configDir.FullName) == oldExport + ownConfig,
        "Preserve a manually extended root config.");

    string root = ApoBridge.AppendPlaybackRoute(cleaned, render);
    Check(IncludesFor(root, render).SequenceEqual(new[] { ApoProfiles.PlaybackProfileName }), "Route only the playback file.");
    Check(IncludesFor(root, capture).Length == 0 && IncludesFor(root, thirdDevice).Length == 0, "Leave other endpoints out of the APO route.");
    Check(ApoBridge.RemoveAppRoutes(root, configDir.FullName) == cleaned, "Undo only Pff routes.");
    var installation = new ApoBridge.Installation(configDir.FullName, configDir.FullName);
    File.WriteAllText(installation.ConfigFile, $"# BEGIN Pff_crimson v5.2\nDevice: {capture}\nInclude: {ApoProfiles.CaptureProfileName}\n# END Pff_crimson v5.2\n");
    Check(ApoBridge.ReadCaptureRoute(installation) == capture, "Recover the previous version's microphone route.");
    Check(ApoBridge.RemoveAppRoutes(File.ReadAllText(installation.ConfigFile), configDir.FullName) == "", "Remove the old physical capture route.");
    File.WriteAllText(installation.ConfigFile, cleaned);
    File.WriteAllText(Path.Combine(configDir.FullName, ApoProfiles.CaptureProfileName), $"# Pff_crimson\nDevice: {capture}\n");
    Check(ApoBridge.ReadCaptureRoute(installation) == capture, "Recover the microphone after bypass.");

    var output = new ApoProfiles.Settings(render, [3], -12, 50, 100);
    string outputText = ApoProfiles.Build(output, input: false, "C:/ReaComp.dll");
    Check(outputText.Contains($"Device: {render}") && !outputText.Contains(capture), "Playback profile has one device.");
    Check(outputText.Contains("If: stage == \"post-mix\"") && outputText.Contains("Preamp: -12 dB"), "Playback guard and gain.");
    Check(outputText.Contains("Convolution: pff-hall-v1-48000.wav"), "Hall impulse is selected by the playback sample rate.");
    Check(outputText.Contains("Copy: 1=0.5325*1+0.85*PFFHALLL"), "Hall preserves the dry signal.");
    Check(!ApoProfiles.Build(output with { Effect = 0 }, input: false, null).Contains("Convolution:"), "Zero reverb bypasses the hall.");
    Check(ApoProfiles.Frequencies.Length == 20 && ApoProfiles.Frequencies[0] == 20 && ApoProfiles.Frequencies[^1] == 20000, "Frequency range.");
    var migrated = ApoProfiles.MigrateBands([0, 6, 12]);
    Check(migrated[0] == 0 && migrated[1] > 6 && migrated[1] < 12 && migrated[2] == 12, "Interpolate legacy bands in log-frequency space.");

    using var wav = new MemoryStream();
    HallImpulse.Write(wav, 48000);
    wav.Position = 22;
    using var reader = new BinaryReader(wav);
    Check(reader.ReadInt16() == 2 && reader.ReadInt32() == 48000, "Hall WAV uses two channels at the selected sample rate.");
    wav.Position = 44;
    int frames = (int)((wav.Length - 44) / 4);
    bool stereoReflection = false, tail = false;
    for (int i = 0; i < frames; i++)
    {
        short left = reader.ReadInt16(), right = reader.ReadInt16();
        if (i < 960) Check(left == 0 && right == 0, "Wet impulse has predelay.");
        if (i < 4800 && left != right) stereoReflection = true;
        if (i > 48000 && (left != 0 || right != 0)) tail = true;
    }
    Check(stereoReflection && tail, "Stereo reflections and a measurable tail after one second.");
    Console.WriteLine("Routing regression checks passed.");
}
finally
{
    configDir.Delete(recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string[] IncludesFor(string root, string endpoint)
{
    const string prefix = "Include: pff_crimson_";
    bool matched = true;
    var files = new List<string>();
    foreach (string line in root.Split('\n', StringSplitOptions.TrimEntries))
    {
        if (line.StartsWith("Device: ", StringComparison.Ordinal))
            matched = line == "Device: all" || line == "Device: " + endpoint;
        if (matched && line.StartsWith(prefix, StringComparison.Ordinal))
            files.Add(line["Include: ".Length..]);
    }
    return files.ToArray();
}
