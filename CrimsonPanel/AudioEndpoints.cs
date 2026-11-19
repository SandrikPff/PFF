using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CrimsonPanel;

internal static class AudioEndpoints
{
    internal sealed record Endpoint(string Name, string Guid, bool IsDefault, string Id)
    {
        public override string ToString() => (IsDefault ? "По умолчанию: " : "") + Name;
    }

    internal static List<Endpoint> Enumerate(bool input)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = input ? DataFlow.Capture : DataFlow.Render;
        string? defaultId = input ? GetDefaultId(enumerator, flow, Role.Communications) : null;
        defaultId ??= GetDefaultId(enumerator, flow, Role.Multimedia);
        var result = new List<Endpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    string id = device.ID;
                    int start = id.LastIndexOf('{');
                    if (start <= 0 || !Guid.TryParseExact(id[start..], "B", out var guid))
                        continue;
                    result.Add(new Endpoint(device.FriendlyName, guid.ToString("B"),
                        string.Equals(defaultId, id, StringComparison.OrdinalIgnoreCase), id));
                }
                catch (COMException ex)
                {
                    App.LogError("Чтение аудиоустройства Windows", ex);
                }
            }
        }
        return result.OrderByDescending(endpoint => endpoint.IsDefault).ThenBy(endpoint => endpoint.Name).ToList();
    }

    internal static float ReadSample(byte[] buffer, int offset, WaveFormatEncoding encoding, int bytes) =>
        (encoding, bytes) switch
        {
            (WaveFormatEncoding.IeeeFloat, 4) => BitConverter.ToSingle(buffer, offset),
            (WaveFormatEncoding.IeeeFloat, 8) => (float)BitConverter.ToDouble(buffer, offset),
            (WaveFormatEncoding.Pcm, 1) => (buffer[offset] - 128) / 128f,
            (WaveFormatEncoding.Pcm, 2) => BitConverter.ToInt16(buffer, offset) / 32768f,
            (WaveFormatEncoding.Pcm, 3) =>
                ((buffer[offset + 2] << 24) | (buffer[offset + 1] << 16) | (buffer[offset] << 8)) / 2147483648f,
            (WaveFormatEncoding.Pcm, 4) => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => throw new NotSupportedException("Формат записи микрофона не поддерживается.")
        };

    private static string? GetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
            return endpoint.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
