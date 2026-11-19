using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CrimsonPanel;

internal static class ApoBridge
{
    private const string RegistryPath = @"SOFTWARE\EqualizerAPO";
    private const string Begin = "# BEGIN Pff_crimson v5.3";
    private const string End = "# END Pff_crimson v5.3";
    private static readonly string[] LegacyProfiles =
        ["pff_crimson_v5.txt", "pff_crimson_headphones.txt", "pff_crimson_microphone.txt", "headphones.txt", "microphone.txt"];
    private const string LegacyProfileComment = "# Pff_crimson device-scoped Equalizer APO profile";
    private static readonly string BackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pff_crimson", "apo");
    private static readonly object WriteLock = new();

    internal sealed record Installation(string ConfigDir, string InstallDir)
    {
        public string ConfigFile => Path.Combine(ConfigDir, "config.txt");
        public string Selector => Path.Combine(InstallDir, "DeviceSelector.exe");
        public bool SupportsAutomaticRouting => File.Exists(Path.Combine(InstallDir, "EqualizerAPOHost.exe"))
            && File.Exists(Selector);
        public string? Compressor => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pff_crimson Audio", "reacomp-standalone.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VSTPlugins", "ReaPlugs", "reacomp-standalone.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VSTPlugins", "reacomp-standalone.dll")
        }.FirstOrDefault(File.Exists);
    }

    internal static Installation? Find()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var apo = machine.OpenSubKey(RegistryPath);
        var installDir = apo?.GetValue("InstallPath") as string;
        if (string.IsNullOrWhiteSpace(installDir))
            installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        var configDir = apo?.GetValue("ConfigPath") as string;
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(installDir, "config");
        var installation = new Installation(configDir, installDir);
        return File.Exists(installation.ConfigFile) && File.Exists(Path.Combine(installDir, "EqualizerAPO.dll"))
            ? installation : null;
    }

    private static RegistryKey? OpenEffects(RegistryKey machine, string guid, bool input)
    {
        return machine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{(input ? "Capture" : "Render")}\{Guid.Parse(guid):B}\FxProperties");
    }

    internal enum EndpointState { Missing, PlaybackPreMixOnly, Disabled, CommunicationsMissing, Registered }

    private static EndpointState GetEndpointState(Installation installation, string guid, bool input)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var properties = OpenEffects(machine, guid, input);
        bool disabled = properties?.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5") is int value && value != 0;
        const string effectKey = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
        // Capture runs in the input stream slot. Pff playback filters run after mixing.
        foreach (int n in input ? new[] { 13, 5, 1 } : new[] { 15, 14, 7, 6, 2, 13, 5, 1 })
        {
            object? entry = properties?.GetValue($"{effectKey},{n}");
            string[] registrations = entry switch { string s => [s], string[] entries => entries, _ => [] };
            foreach (string registration in registrations)
            {
                if (!Guid.TryParse(registration, out var clsid)) continue;
                using var server = machine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid:B}\InprocServer32");
                string? dll = (server?.GetValue(null) as string)?.Trim().Trim('"');
                if (dll is null || !string.Equals(Environment.ExpandEnvironmentVariables(dll),
                        Path.Combine(installation.InstallDir, "EqualizerAPO.dll"), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (input && n == 5 && HasDefaultCaptureModeOnly(properties!))
                    return EndpointState.CommunicationsMissing;
                if (!input && (n is 13 or 5 or 1)) return EndpointState.PlaybackPreMixOnly;
                if (disabled) return EndpointState.Disabled;
                return EndpointState.Registered;
            }
        }
        return EndpointState.Missing;
    }

    private static bool HasDefaultCaptureModeOnly(RegistryKey properties) =>
        properties.GetValue("{d3993a3f-99c2-4402-b5ec-a92a0367664b},5") is string[] { Length: 1 } modes &&
        Guid.TryParse(modes[0], out var onlyMode) &&
        onlyMode == new Guid("c18e2f7e-933d-4965-b7d1-1eef228d2af3");

    internal static async Task EnsurePlaybackAsync(Installation installation, string guid)
    {
        var state = GetEndpointState(installation, guid, input: false);
        if (state == EndpointState.Registered) return;
        if (state == EndpointState.PlaybackPreMixOnly)
            await RunSelectorAsync(installation, "--uninstall-endpoint", guid, skipTest: true).ConfigureAwait(false);

        await RunSelectorAsync(installation, "--install-endpoint", guid, skipTest: false).ConfigureAwait(false);
        if (GetEndpointState(installation, guid, input: false) != EndpointState.Registered)
            throw new InvalidOperationException("Windows не активировала обработку на этом устройстве.");
    }

    internal static async Task RemoveCaptureAsync(Installation installation, string guid)
    {
        if (GetEndpointState(installation, guid, input: true) == EndpointState.Missing) return;
        await RunSelectorAsync(installation, "--uninstall-endpoint", guid, skipTest: false).ConfigureAwait(false);
        if (GetEndpointState(installation, guid, input: true) != EndpointState.Missing)
            throw new InvalidOperationException("Движок остался в цепочке записи Windows.");
    }

    internal static void DisableCaptureEnhancements(string guid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{Guid.Parse(guid):B}\FxProperties";
        const string key = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        using var property = machine.OpenSubKey(path, writable: true);
        if (property?.GetValue(key) is int state && state == 1) return;
        property?.SetValue(key, 1, RegistryValueKind.DWord);
    }

    internal static string? ReadCaptureRoute(Installation installation)
    {
        // The route records exactly which recording endpoint was selected by Pff.
        string pattern = @"(?m)^Device: (\{[0-9a-fA-F-]{36}\})\r?\nInclude: " +
            Regex.Escape(ApoProfiles.CaptureProfileName) + @"\r?$";
        var matches = Regex.Matches(File.ReadAllText(installation.ConfigFile), pattern);
        if (matches.Count == 1 && Guid.TryParseExact(matches[0].Groups[1].Value, "B", out var guid))
            return guid.ToString("B");

        // Version 5.1 removed the Include on bypass but left its registration and profile.
        string profile = Path.Combine(installation.ConfigDir, ApoProfiles.CaptureProfileName);
        if (!File.Exists(profile)) return null;
        using var reader = new StreamReader(profile);
        if (reader.ReadLine() != "# Pff_crimson" ||
            reader.ReadLine() is not { } device || !device.StartsWith("Device: ", StringComparison.Ordinal) ||
            !Guid.TryParseExact(device["Device: ".Length..], "B", out guid))
            return null;
        return guid.ToString("B");
    }

    private static async Task RunSelectorAsync(Installation installation, string command, string guid, bool skipTest)
    {
        var start = new ProcessStartInfo(installation.Selector)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = installation.InstallDir
        };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(Guid.Parse(guid).ToString("B"));
        if (skipTest) start.ArgumentList.Add("--no-test");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить настройку звука.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var messages = await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string details = string.Join(Environment.NewLine, messages).Trim();
            throw new InvalidOperationException(
                $"Подключение к Windows Audio завершилось с кодом {process.ExitCode}.",
                details.Length == 0 ? null : new InvalidOperationException(details));
        }
    }

    internal static void Apply(Installation installation, ApoProfiles.Settings? playback)
    {
        lock (WriteLock)
        {
            byte[] originalBytes = File.ReadAllBytes(installation.ConfigFile);
            string current = ReadConfig(originalBytes);
            string clean = RemoveAppRoutes(current, installation.ConfigDir);
            string updated = AppendPlaybackRoute(clean, playback?.DeviceGuid);
            string? compressor = installation.Compressor;
            if (playback is { Effect: > 0 })
            {
                foreach (int rate in HallImpulse.Rates)
                {
                    string file = Path.Combine(installation.ConfigDir, HallImpulse.FileName(rate));
                    if (File.Exists(file)) continue;
                    using var wave = new MemoryStream();
                    HallImpulse.Write(wave, rate);
                    WriteBytes(file, wave.ToArray());
                }
            }
            string playbackProfile = ApoProfiles.Build(playback, input: false, compressor);
            WriteProfile(installation.ConfigDir, ApoProfiles.PlaybackProfileName, playbackProfile);
            string oldCaptureFile = Path.Combine(installation.ConfigDir, ApoProfiles.CaptureProfileName);
            if (File.Exists(oldCaptureFile))
                WriteProfile(installation.ConfigDir, ApoProfiles.CaptureProfileName, ApoProfiles.Build(null, input: true, null));
            if (updated != current)
            {
                if (!originalBytes.SequenceEqual(File.ReadAllBytes(installation.ConfigFile)))
                    throw new IOException("Конфигурация APO изменилась во время записи. Повторите применение.");
                Backup(originalBytes);
                WriteBytes(installation.ConfigFile, Encoding.UTF8.GetBytes(updated));
            }
            NeutralizeLegacyProfiles(installation.ConfigDir);
            NeutralizeLegacyProfiles(BackupDirectory);
        }
    }

    internal static string AppendPlaybackRoute(string clean, string? playback)
    {
        if (playback is null) return clean;
        string lineBreak = clean.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (clean.Length > 0 && !clean.EndsWith('\n')) clean += lineBreak;
        return clean + string.Join(lineBreak, Begin, "Device: all", "Stage: post-mix",
            $"Device: {Guid.ParseExact(playback, "B"):B}", "Include: " + ApoProfiles.PlaybackProfileName, End, "");
    }

    private static void WriteProfile(string directory, string name, string profile)
    {
        string path = Path.Combine(directory, name);
        string previous = File.Exists(path) ? File.ReadAllText(path) : "";
        if (previous != profile) WriteBytes(path, Encoding.UTF8.GetBytes(profile));
    }

    private static string ReadConfig(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void Disconnect(Installation installation)
    {
        lock (WriteLock)
        {
            byte[] contents = File.ReadAllBytes(installation.ConfigFile);
            string current = ReadConfig(contents);
            string updated = RemoveAppRoutes(current, installation.ConfigDir);
            if (current != updated)
            {
                if (!contents.SequenceEqual(File.ReadAllBytes(installation.ConfigFile)))
                    throw new IOException("Конфигурация APO изменилась во время записи. Повторите отключение.");
                Backup(contents);
                WriteBytes(installation.ConfigFile, Encoding.UTF8.GetBytes(updated));
            }
            NeutralizeLegacyProfiles(installation.ConfigDir);
            NeutralizeLegacyProfiles(BackupDirectory);
        }
    }

    private static void NeutralizeLegacyProfiles(string configDir)
    {
        foreach (string file in LegacyProfiles)
        {
            string path = Path.Combine(configDir, file);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? header = reader.ReadLine()?.Trim();
            if (header != "# Pff_crimson" && header != LegacyProfileComment) continue;
            Backup(bytes, Path.GetFileNameWithoutExtension(file));
            WriteBytes(path, Encoding.UTF8.GetBytes("# Pff_crimson 5.1 replaced this profile.\r\n"));
        }
    }

    internal static string RemoveAppRoutes(string current, string configDir)
    {
        var lines = Regex.Split(current, "(?<=\n)");
        var result = new StringBuilder();
        string? blockEnd = null;
        foreach (string line in lines)
        {
            string value = line.Trim();
            // Match known Pff-managed blocks, including both earlier routing implementations.
            if (value is "# BEGIN PFF_CRIMSON DEVICE ROUTING v4.6" or "# BEGIN Pff_crimson v5" or "# BEGIN Pff_crimson v5.1" or "# BEGIN Pff_crimson v5.2" or Begin)
            {
                if (blockEnd is not null) throw new InvalidDataException("Обнаружены вложенные блоки PFF.");
                blockEnd = value == Begin ? End : "# END " + value["# BEGIN ".Length..];
                continue;
            }
            if (value is "# END PFF_CRIMSON DEVICE ROUTING v4.6" or "# END Pff_crimson v5" or "# END Pff_crimson v5.1" or "# END Pff_crimson v5.2" or End)
            {
                if (value != blockEnd) throw new InvalidDataException("Повреждён блок PFF в APO.");
                blockEnd = null;
                continue;
            }
            if (blockEnd is not null || value.Equals("# Pff_crimson managed global EQ", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("# Pff_crimson headphones include (managed)", StringComparison.OrdinalIgnoreCase) ||
                IsAppInclude(value, configDir))
                continue;
            result.Append(line);
        }
        if (blockEnd is not null) throw new InvalidDataException("Обнаружен незавершённый блок PFF в APO.");
        string cleaned = result.ToString();
        // A previous workaround copied the generated profile onto config.txt.
        // Remove that standalone export even if a newer Pff block was appended to it.
        return IsStandaloneLegacyProfile(cleaned) ? "" : cleaned;
    }

    private static bool IsStandaloneLegacyProfile(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || lines[0] != LegacyProfileComment) return false;
        return lines.Skip(1).All(line => Regex.IsMatch(line,
            @"^(?:Preamp: -?\d+(?:\.\d+)? dB|Filter: ON PK Fc \d+ Hz Gain -?\d+(?:\.\d+)? dB Q 1\.41)$"));
    }

    private static bool IsAppInclude(string line, string configDir)
    {
        int colon = line.IndexOf(':');
        if (colon < 0 || !line[..colon].Trim().Equals("Include", StringComparison.OrdinalIgnoreCase))
            return false;
        string path = line[(colon + 1)..].Trim().Trim('"').Replace('\\', '/');
        path = path.ToLowerInvariant();
        string file = path.Split('/')[^1];
        if (file is "headphones.txt" or "microphone.txt")
        {
            if (path.EndsWith("/pff_crimson/apo/" + file, StringComparison.Ordinal)) return true;
            if (path != file) return false;
            string candidate = Path.Combine(configDir, file);
            if (!File.Exists(candidate)) return false;
            using var reader = new StreamReader(candidate);
            return reader.ReadLine()?.Trim() is LegacyProfileComment or "# Pff_crimson 5.1 replaced this profile.";
        }
        return file is ApoProfiles.PlaybackProfileName or ApoProfiles.CaptureProfileName ||
            LegacyProfiles.Contains(file, StringComparer.Ordinal);
    }

    private static void Backup(byte[] content, string name = "config")
    {
        Directory.CreateDirectory(BackupDirectory);
        string file = Path.Combine(BackupDirectory, $"{name}-before-v5-1-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(file, content);
    }

    private static void WriteBytes(string path, byte[] content)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temporary, path, destinationBackupFileName: null);
                    else
                        File.Move(temporary, path);
                    break;
                }
                catch (IOException ex) when (attempt < 4 && ((ex.HResult & 0xffff) is 32 or 33))
                {
                    Thread.Sleep(35);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
