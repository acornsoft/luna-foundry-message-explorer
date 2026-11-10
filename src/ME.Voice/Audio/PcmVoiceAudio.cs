// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Voice.Audio;

/// <summary>
/// Push-to-talk audio over any <see cref="IAudioBackend"/>: microphone samples become 100 ms PCM16 chunks at
/// 24 kHz (resampled if the device opened at another rate), and answer audio is queued for the speaker.
/// Devices open on first use, not at start-up, so the macOS microphone prompt appears only when you first hold
/// the talk button. If a device fails, <see cref="UnavailableReason"/> says why and the panel falls back to
/// typed questions. Nothing here throws into the UI.
/// </summary>
public sealed class PcmVoiceAudio : IVoiceAudio, IDisposable
{
    public const int Rate = GrokVoiceSession.SampleRate;
    public const int ChunkMilliseconds = 100;
    public const int ChunkSamples = Rate * ChunkMilliseconds / 1000;

    private readonly IAudioBackend _backend;
    private readonly int _playbackSeconds;
    private readonly object _gate = new();
    private readonly List<short> _pending = new(ChunkSamples * 2);
    private readonly Lazy<string?> _probe;
    private string? _failure;
    private IAudioStream? _capture;
    private IAudioStream? _playback;
    private PlaybackBuffer? _buffer;
    private Action<byte[]>? _onChunk;
    private int? _oddByte;
    private bool _disposed;

    public PcmVoiceAudio(IAudioBackend backend, int playbackSeconds = 120)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentOutOfRangeException.ThrowIfLessThan(playbackSeconds, 1);
        _playbackSeconds = playbackSeconds;
        _probe = new Lazy<string?>(SafeProbe);
    }

    public string BackendName => _backend.Name;

    /// <inheritdoc />
    public string? UnavailableReason => _failure ?? _probe.Value;

    /// <summary>Samples waiting for the speaker.</summary>
    public int QueuedSamples => _buffer?.Count ?? 0;

    /// <summary>The rate the microphone opened at (0 before first use).</summary>
    public int CaptureRate => _capture?.SampleRate ?? 0;

    /// <summary>The rate the speaker opened at (0 before first use).</summary>
    public int PlaybackRate => _playback?.SampleRate ?? 0;

    public void StartCapture(Action<byte[]> onChunk)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        if (UnavailableReason is not null)
            return;
        IAudioStream? stream;
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.Clear();
            _onChunk = onChunk;
            stream = _capture;
        }

        try
        {
            stream ??= _capture = _backend.OpenCapture(Rate, OnSamples);
            stream.Start();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail("Could not open the microphone", exception);
            lock (_gate)
                _onChunk = null;
        }
    }

    public void StopCapture()
    {
        try
        {
            // Outside the lock: Stop waits for the audio thread, which takes the lock in OnSamples.
            _capture?.Stop();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail("The microphone stopped with an error", exception);
        }

        Action<byte[]>? sink;
        byte[]? tail = null;
        lock (_gate)
        {
            sink = _onChunk;
            _onChunk = null;
            if (_pending.Count > 0)
            {
                tail = Pcm16.ToBytes(_pending.ToArray());
                _pending.Clear();
            }
        }

        // The last partial chunk goes out before StopCapture returns, so a commit right after includes it.
        if (sink is not null && tail is not null)
            sink(tail);
    }

    public void Play(byte[] pcm16)
    {
        if (pcm16 is null || pcm16.Length == 0 || UnavailableReason is not null)
            return;
        var bytes = pcm16.AsSpan();
        byte[]? joined = null;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_oddByte is { } carry)
            {
                joined = new byte[bytes.Length + 1];
                joined[0] = (byte)carry;
                bytes.CopyTo(joined.AsSpan(1));
                bytes = joined;
                _oddByte = null;
            }

            if (bytes.Length % 2 == 1)
            {
                _oddByte = bytes[^1];
                bytes = bytes[..^1];
            }
        }

        var samples = Pcm16.FromBytes(bytes);
        try
        {
            if (_playback is null)
            {
                lock (_gate)
                {
                    if (_playback is null)
                    {
                        var stream = _backend.OpenPlayback(Rate, Fill);
                        _buffer = new PlaybackBuffer(stream.SampleRate * _playbackSeconds);
                        _playback = stream;
                        stream.Start();
                    }
                }
            }

            var rate = _playback!.SampleRate;
            _buffer!.Write(rate == Rate ? samples : Pcm16.Resample(samples, Rate, rate));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail("Could not open the speaker", exception);
        }
    }

    /// <summary>Barge-in: drop whatever is still queued for the speaker.</summary>
    public void StopPlayback()
    {
        _buffer?.Clear();
        lock (_gate)
            _oddByte = null;
    }

    private void OnSamples(ReadOnlySpan<short> samples)
    {
        List<byte[]>? ready = null;
        Action<byte[]>? sink;
        lock (_gate)
        {
            sink = _onChunk;
            if (sink is null || samples.IsEmpty)
                return;
            var rate = _capture?.SampleRate ?? Rate;
            if (rate == Rate)
            {
                foreach (var sample in samples)
                    _pending.Add(sample);
            }
            else
            {
                _pending.AddRange(Pcm16.Resample(samples, rate, Rate));
            }

            while (_pending.Count >= ChunkSamples)
            {
                (ready ??= []).Add(Pcm16.ToBytes(_pending.GetRange(0, ChunkSamples).ToArray()));
                _pending.RemoveRange(0, ChunkSamples);
            }
        }

        if (ready is not null)
        {
            foreach (var chunk in ready)
                sink(chunk);
        }
    }

    private int Fill(Span<short> buffer) => _buffer?.Read(buffer) ?? 0;

    private string? SafeProbe()
    {
        try
        {
            return _backend.Probe();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"Audio is not available ({_backend.Name}): {exception.Message} Type a question instead.";
        }
    }

    private void Fail(string what, Exception exception) =>
        _failure = $"{what} ({_backend.Name}): {exception.Message} Type a question instead.";

    public void Dispose()
    {
        IAudioStream? capture, playback;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _onChunk = null;
            capture = _capture;
            playback = _playback;
        }

        foreach (var stream in new[] { capture, playback })
        {
            try
            {
                stream?.Stop();
                stream?.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Closing a device that already went away.
            }
        }

        _backend.Dispose();
    }
}
