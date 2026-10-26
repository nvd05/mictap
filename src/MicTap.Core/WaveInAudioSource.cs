using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicTap.Core;

/// <summary>A WinMM capture device by its number and full endpoint name.</summary>
public sealed record AudioDevice(int Number, string Name);

public enum CaptureFailure
{
    DeviceNotFound,
    StartFailed,
    StopFailed,
}

/// <summary>A capture device is missing or refused a command.</summary>
public sealed class AudioCaptureException(CaptureFailure failure, string deviceName, Exception? inner = null)
    : Exception($"{failure} on capture device '{deviceName}'" + (inner is null ? "." : $": {inner.Message}"), inner)
{
    public CaptureFailure Failure { get; } = failure;

    public string DeviceName { get; } = deviceName;
}

/// <summary>Microphone capture through WinMM <see cref="WaveInEvent"/> in <see cref="PcmFormat.Mono16k"/>.</summary>
public sealed class WaveInAudioSource : IAudioSource
{
    private readonly WaveInEvent _waveIn;

    private WaveInAudioSource(AudioDevice device)
    {
        Device = device;
        PcmFormat format = PcmFormat.Mono16k;

        // WaveInEvent posts RecordingStopped to the SynchronizationContext it is created on (the UI thread
        // when called from WPF). Creating it without one keeps every callback on the capture thread.
        SynchronizationContext? context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = device.Number,
                WaveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels),
                BufferMilliseconds = 100,
                NumberOfBuffers = 10, // a second of slack in the driver if this process stalls
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }

        _waveIn.DataAvailable += (_, e) => DataAvailable?.Invoke(this, new AudioBuffer(e.Buffer, e.BytesRecorded));
        _waveIn.RecordingStopped += (_, e) => Stopped?.Invoke(this, new CaptureStopped(e.Exception));
    }

    public event EventHandler<AudioBuffer>? DataAvailable;

    public event EventHandler<CaptureStopped>? Stopped;

    public AudioDevice Device { get; }

    public static IReadOnlyList<AudioDevice> ListDevices()
    {
        // WinMM cuts names to 31 characters; the Core Audio endpoint carries the full one it starts with.
        List<string> endpoints = CaptureEndpointNames();
        var devices = new List<AudioDevice>();
        for (int number = 0; number < WaveInEvent.DeviceCount; number++)
        {
            string shortName = WaveInEvent.GetCapabilities(number).ProductName;
            string name = endpoints.FirstOrDefault(e => e == shortName)
                ?? endpoints.FirstOrDefault(e => e.StartsWith(shortName, StringComparison.Ordinal))
                ?? shortName;
            endpoints.Remove(name);
            devices.Add(new AudioDevice(number, name));
        }

        return devices;
    }

    /// <summary>Finds the device by name: device numbers shift when devices are plugged in or out.</summary>
    public static WaveInAudioSource Open(string deviceName)
    {
        AudioDevice device = ListDevices().FirstOrDefault(d => d.Name == deviceName)
            ?? throw new AudioCaptureException(CaptureFailure.DeviceNotFound, deviceName);
        return new WaveInAudioSource(device);
    }

    public void Start()
    {
        try
        {
            _waveIn.StartRecording();
        }
        catch (MmException ex)
        {
            throw new AudioCaptureException(CaptureFailure.StartFailed, Device.Name, ex);
        }
    }

    public void Stop()
    {
        try
        {
            _waveIn.StopRecording();
        }
        catch (MmException ex)
        {
            throw new AudioCaptureException(CaptureFailure.StopFailed, Device.Name, ex);
        }
    }

    public void Dispose() => _waveIn.Dispose();

    private static List<string> CaptureEndpointNames()
    {
        var names = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (endpoint)
            {
                names.Add(endpoint.FriendlyName);
            }
        }

        return names;
    }
}
