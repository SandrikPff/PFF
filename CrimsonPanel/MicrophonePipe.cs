using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CrimsonPanel;

internal sealed class MicrophonePipe : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _source;
    private MMDevice? _sink;
    private WasapiCapture? _capture;
    private WasapiOut? _playback;
    private volatile bool _disposed;
    private readonly BufferedWaveProvider _queue = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
    {
        BufferDuration = TimeSpan.FromMilliseconds(240),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };
    private readonly float[] _monoSamples = new float[4096];
    private readonly byte[] _monoBytes = new byte[4096 * sizeof(float)];
    private readonly float[] _previousBands = new float[ApoProfiles.Frequencies.Length];
    private readonly BiQuadFilter?[] _filters = new BiQuadFilter?[ApoProfiles.Frequencies.Length];
    private readonly float[] _delay = new float[48000 / 3];
    private int _delayIndex;
    private float _envelope;
    private ApoProfiles.Settings? _settings;
    private ApoProfiles.Settings? _lastSettings;
    private float _gain = 1;
    private float _threshold = 1;
    private float _ratio = 1;
    private float _wet;

    internal string SourceGuid { get; }
    internal event Action<Exception>? Failed;

    internal static bool IsCable(AudioEndpoints.Endpoint endpoint, bool input)
    {
        return endpoint.Name.Contains(input ? "CABLE Output" : "CABLE Input", StringComparison.OrdinalIgnoreCase)
            && endpoint.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
            && !endpoint.Name.Contains("CABLE-A", StringComparison.OrdinalIgnoreCase)
            && !endpoint.Name.Contains("CABLE-B", StringComparison.OrdinalIgnoreCase);
    }

    internal MicrophonePipe(AudioEndpoints.Endpoint microphone, AudioEndpoints.Endpoint cable, ApoProfiles.Settings settings)
    {
        SourceGuid = microphone.Guid;
        Update(settings);
        try
        {
            _source = _enumerator.GetDevice(microphone.Id);
            _sink = _enumerator.GetDevice(cable.Id);
            _capture = new WasapiCapture(_source);
            WaveFormat format = _capture.WaveFormat.AsStandardWaveFormat();
            _capture.DataAvailable += (_, args) => Capture(args.Buffer, args.BytesRecorded, format);
            _capture.RecordingStopped += (_, args) => ReportStopped(args.Exception);

            ISampleProvider samples = new WaveToSampleProvider(_queue);
            WaveFormat sinkFormat = _sink.AudioClient.MixFormat;
            if (sinkFormat.Channels == 2)
                samples = new MonoToStereoSampleProvider(samples);
            else if (sinkFormat.Channels != 1)
                throw new InvalidOperationException("Для VB-CABLE требуется формат моно или стерео.");
            if (sinkFormat.SampleRate != 48000)
                samples = new WdlResamplingSampleProvider(samples, sinkFormat.SampleRate);
            _playback = new WasapiOut(_sink, AudioClientShareMode.Shared, true, 50);
            _playback.PlaybackStopped += (_, args) => ReportStopped(args.Exception);
            _playback.Init(new SampleToWaveProvider(samples));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Start()
    {
        _capture!.StartRecording();
        _playback!.Play();
    }

    internal void Update(ApoProfiles.Settings settings) => Volatile.Write(ref _settings, settings);

    private void ReportStopped(Exception? error)
    {
        if (!_disposed)
            Failed?.Invoke(error ?? new InvalidOperationException("Аудиоустройство отключилось."));
    }

    private void RefreshFilters()
    {
        var settings = Volatile.Read(ref _settings);
        if (settings is null || ReferenceEquals(settings, _lastSettings)) return;
        _lastSettings = settings;
        for (int i = 0; i < _filters.Length; i++)
        {
            float band = (float)Math.Clamp(settings.Eq[i], -12, 12);
            if (band == _previousBands[i]) continue;
            _previousBands[i] = band;
            _filters[i] = band == 0 ? null : BiQuadFilter.PeakingEQ(48000, ApoProfiles.Frequencies[i], 2.7f, band);
        }
        _gain = (float)Math.Pow(10, Math.Clamp(settings.Gain, -12, 24) / 20);
        double compression = Math.Clamp(settings.Compression, 0, 100) / 100;
        _ratio = (float)(1 + 7 * compression);
        _threshold = (float)Math.Pow(10, (-9 - 19 * compression) / 20);
        _wet = (float)Math.Clamp(settings.Effect / 100, 0, 1);
    }

    private float _lastSample;
    private long _resamplePhase;

    private void Capture(byte[] data, int length, WaveFormat format)
    {
        if (_disposed) return;
        try
        {
            RefreshFilters();
            int sampleBytes = format.BitsPerSample / 8;
            if (format.SampleRate <= 0 || format.Channels <= 0 || sampleBytes <= 0 || format.BlockAlign < format.Channels * sampleBytes)
                throw new NotSupportedException("Windows вернула неверный формат микрофона.");
            int count = 0;
            for (int frame = 0; frame + format.BlockAlign <= length; frame += format.BlockAlign)
            {
                float sample = 0;
                for (int channel = 0; channel < format.Channels; channel++)
                    sample += AudioEndpoints.ReadSample(data, frame + channel * sampleBytes, format.Encoding, sampleBytes);
                sample = float.IsFinite(sample) ? sample / format.Channels : 0;
                _resamplePhase += 48000;
                while (_resamplePhase >= format.SampleRate)
                {
                    _resamplePhase -= format.SampleRate;
                    float fraction = 1 - _resamplePhase / 48000f;
                    _monoSamples[count++] = Process(_lastSample + (sample - _lastSample) * fraction);
                    if (count != _monoSamples.Length) continue;
                    Enqueue(count);
                    count = 0;
                }
                _lastSample = sample;
            }
            if (count != 0) Enqueue(count);
        }
        catch (Exception ex) { ReportStopped(ex); }
    }

    private void Enqueue(int count)
    {
        Buffer.BlockCopy(_monoSamples, 0, _monoBytes, 0, count * sizeof(float));
        if (_queue.BufferedDuration.TotalMilliseconds > 160) _queue.ClearBuffer();
        _queue.AddSamples(_monoBytes, 0, count * sizeof(float));
    }

    private float Process(float sample)
    {
        sample *= _gain;
        foreach (var band in _filters)
            if (band is not null) sample = band.Transform(sample);
        float level = Math.Abs(sample);
        _envelope += (level - _envelope) * (level > _envelope ? .003f : .00022f);
        if (_ratio > 1 && _envelope > _threshold)
            sample *= MathF.Pow(_threshold / _envelope, 1 - 1 / _ratio);

        float reflections = .35f * Tap(29) + .26f * Tap(53) + .18f * Tap(89)
            + .13f * Tap(137) + .08f * Tap(193);
        _delay[_delayIndex] = sample + .28f * reflections;
        _delayIndex = (_delayIndex + 1) % _delay.Length;
        float output = sample * (1 - .3f * _wet) + reflections * .5f * _wet;
        return float.IsFinite(output) ? Math.Clamp(output, -1, 1) : 0;
    }

    private float Tap(int milliseconds) => _delay[(_delayIndex + _delay.Length - milliseconds * 48) % _delay.Length];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.Dispose(); }
        finally
        {
            try { _playback?.Dispose(); }
            finally
            {
                _source?.Dispose();
                _sink?.Dispose();
                _enumerator.Dispose();
            }
        }
    }
}
