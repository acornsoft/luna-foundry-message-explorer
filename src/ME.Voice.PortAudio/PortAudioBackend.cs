// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Voice.Audio;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace Acornsoft.MessageExplorer.Voice.PortAudio;

/// <summary>
/// The default microphone and speaker through PortAudio: mono, 16-bit, 24 kHz when the device allows it and the
/// device's own rate otherwise (<see cref="PcmVoiceAudio"/> resamples). Nothing opens until first use. If the
/// native library or a device is missing, <see cref="Probe"/> says so and the Voice panel takes typed questions.
/// </summary>
public sealed class PortAudioBackend : IAudioBackend
{
    private const double FrameSeconds = 0.02;
    private static readonly object InitGate = new();
    private static int _users;
    private bool _initialized;
    private bool _disposed;

    public string Name => "PortAudio";

    public string? Probe()
    {
        try
        {
            EnsureInitialized();
            if (PortAudioSharp.PortAudio.DefaultInputDevice == PortAudioSharp.PortAudio.NoDevice)
                return "No microphone was found. Type a question instead.";
            if (PortAudioSharp.PortAudio.DefaultOutputDevice == PortAudioSharp.PortAudio.NoDevice)
                return "No speaker was found. Type a question instead.";
            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            return "The PortAudio library could not load on this computer"
                + (OperatingSystem.IsLinux() ? " (it needs libasound2 and libjack)" : string.Empty)
                + ". Type a question instead.";
        }
        catch (PortAudioException exception)
        {
            return $"Audio is not available: {exception.Message}. Type a question instead.";
        }
    }

    public IAudioStream OpenCapture(int preferredRate, AudioSamples onSamples)
    {
        ArgumentNullException.ThrowIfNull(onSamples);
        EnsureInitialized();
        var device = PortAudioSharp.PortAudio.DefaultInputDevice;
        if (device == PortAudioSharp.PortAudio.NoDevice)
            throw new InvalidOperationException("No microphone was found.");
        var info = PortAudioSharp.PortAudio.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        PaStream.Callback callback = (IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user) =>
        {
            if (input != IntPtr.Zero && frames > 0)
                onSamples(Samples(input, frames));
            return StreamCallbackResult.Continue;
        };
        return Open(preferredRate, info.defaultSampleRate, rate => new PaStream(parameters, null, rate, Frames(rate), StreamFlags.ClipOff, callback, null), callback);
    }

    public IAudioStream OpenPlayback(int preferredRate, AudioFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        EnsureInitialized();
        var device = PortAudioSharp.PortAudio.DefaultOutputDevice;
        if (device == PortAudioSharp.PortAudio.NoDevice)
            throw new InvalidOperationException("No speaker was found.");
        var info = PortAudioSharp.PortAudio.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        PaStream.Callback callback = (IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, IntPtr user) =>
        {
            if (output == IntPtr.Zero || frames == 0)
                return StreamCallbackResult.Continue;
            var buffer = Samples(output, frames);
            var written = Math.Clamp(fill(buffer), 0, buffer.Length);
            buffer[written..].Clear();
            return StreamCallbackResult.Continue;
        };
        return Open(preferredRate, info.defaultSampleRate, rate => new PaStream(null, parameters, rate, Frames(rate), StreamFlags.ClipOff, callback, null), callback);
    }

    private static unsafe Span<short> Samples(IntPtr buffer, uint frames) => new((void*)buffer, checked((int)frames));

    private static uint Frames(double rate) => (uint)Math.Max(64, rate * FrameSeconds);

    private static Opened Open(int preferredRate, double deviceRate, Func<double, PaStream> create, PaStream.Callback keepAlive)
    {
        try
        {
            return new Opened(create(preferredRate), preferredRate, keepAlive);
        }
        catch (PortAudioException) when ((int)Math.Round(deviceRate) is var fallback and > 0 && fallback != preferredRate)
        {
            return new Opened(create(fallback), fallback, keepAlive);
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (InitGate)
        {
            if (_initialized)
                return;
            PortAudioSharp.PortAudio.Initialize();
            _initialized = true;
            _users++;
        }
    }

    public void Dispose()
    {
        lock (InitGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_initialized && --_users == 0)
                PortAudioSharp.PortAudio.Terminate();
        }
    }

    /// <summary>An open PortAudio stream. Holds the callback so the garbage collector can't take it from the native side.</summary>
    private sealed class Opened(PaStream stream, int rate, PaStream.Callback callback) : IAudioStream
    {
        private readonly object _gate = new();
        private bool _running;
        private bool _closed;

        public int SampleRate { get; } = rate;

        public PaStream.Callback Callback { get; } = callback;

        public void Start()
        {
            lock (_gate)
            {
                if (_running || _closed)
                    return;
                stream.Start();
                _running = true;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running || _closed)
                    return;
                stream.Stop();
                _running = false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                _running = false;
                stream.Dispose();
            }
        }
    }
}
