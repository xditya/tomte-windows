using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace Tomte.Core.Rambler;

/// <summary>
/// The default microphone through WASAPI shared mode, event driven, on its own thread. Windows
/// converts to 16 kHz mono float (AUTOCONVERTPCM), the format whisper.cpp wants, so there is no
/// resampler here. Everything is created in <see cref="Start"/> and gone after <see cref="Stop"/>:
/// no capture object exists while nobody holds the chord.
/// </summary>
public sealed class MicCapture
{
    public const int SampleRate = 16000;

    // the whole recording stays in memory, 64 KB per second; a ten-minute ramble is 38 MB. Stream to disk past that.
    private readonly List<float> samples = new(SampleRate * 30);
    private readonly Action<float> onBuffer;
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly Thread thread;
    private Exception? failure;

    private MicCapture(Action<float> onBuffer)
    {
        this.onBuffer = onBuffer;
        thread = new Thread(Run) { IsBackground = true, Name = "tomte-mic" };
    }

    /// <summary>
    /// Starts recording. <paramref name="onBuffer"/> runs on the capture thread once per packet (about 10 ms)
    /// with the packet's RMS.
    /// </summary>
    public static MicCapture Start(Action<float> onBuffer)
    {
        var capture = new MicCapture(onBuffer);
        capture.thread.Start();
        return capture;
    }

    /// <summary>Stops, waits for the thread and returns the 16 kHz mono samples. Throws what the capture thread hit.</summary>
    public float[] Stop()
    {
        stopping.Set();
        thread.Join();
        stopping.Dispose();
        if (failure is not null)
            throw new InvalidOperationException($"The microphone could not record: {failure.Message}", failure);
        return [.. samples];
    }

    private unsafe void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? captureClient = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? ready = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device);
            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            client = (IAudioClient)activated;

            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = 4,
                nAvgBytesPerSec = SampleRate * 4,
            };
            const uint flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, &format, null);

            ready = PInvoke.CreateEvent(null, false, false, null);
            client.SetEventHandle((HANDLE)ready.DangerousGetHandle());
            var iid = typeof(IAudioCaptureClient).GUID;
            client.GetService(&iid, out void* service);
            captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown((nint)service);
            Marshal.Release((nint)service);
            client.Start();

            while (!stopping.IsSet)
            {
                // The event fires once per device period; the timeout only bounds how late a stop is noticed.
                PInvoke.WaitForSingleObject(ready, 100);
                Drain(captureClient);
            }

            client.Stop();
            Drain(captureClient);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            // Stop before the event handle closes: the client must not signal a closed handle.
            try
            {
                client?.Stop();
            }
            catch (COMException)
            {
                // Already stopped, or never started.
            }

            ready?.Dispose();
            foreach (var com in new object?[] { captureClient, client, device, enumerator })
            {
                if (com is not null)
                    Marshal.FinalReleaseComObject(com);
            }
        }
    }

    private unsafe void Drain(IAudioCaptureClient captureClient)
    {
        while (true)
        {
            captureClient.GetNextPacketSize(out uint pending);
            if (pending == 0)
                return;

            byte* data;
            captureClient.GetBuffer(&data, out uint frames, out uint bufferFlags, null, null);
            var packet = new ReadOnlySpan<float>(data, (int)frames);
            bool silent = (bufferFlags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            int start = samples.Count;
            if (silent)
            {
                CollectionsMarshal.SetCount(samples, start + (int)frames);
                CollectionsMarshal.AsSpan(samples)[start..].Clear();
            }
            else
            {
                samples.AddRange(packet);
            }
            captureClient.ReleaseBuffer(frames);

            onBuffer(silent ? 0 : Waveform.Rms(CollectionsMarshal.AsSpan(samples)[start..]));
        }
    }
}
