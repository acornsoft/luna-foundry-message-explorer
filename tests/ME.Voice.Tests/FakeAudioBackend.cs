// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Voice.Audio;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>
/// A pretend microphone and speaker. A test "speaks" samples into the capture callback and pulls what the
/// speaker would play. Device rates are settable to exercise resampling; failures are settable too.
/// </summary>
public sealed class FakeAudioBackend : IAudioBackend
{
    public string Name => "fake audio";

    public string? ProbeResult { get; set; }

    public int CaptureRate { get; set; } = 24000;

    public int PlaybackRate { get; set; } = 24000;

    public Exception? CaptureFailure { get; set; }

    public Exception? PlaybackFailure { get; set; }

    public int ProbeCalls { get; private set; }

    public FakeStream? Capture { get; private set; }

    public FakeStream? Playback { get; private set; }

    public bool Disposed { get; private set; }

    private AudioSamples? _onSamples;
    private AudioFill? _fill;

    public string? Probe()
    {
        ProbeCalls++;
        return ProbeResult;
    }

    public IAudioStream OpenCapture(int preferredRate, AudioSamples onSamples)
    {
        if (CaptureFailure is { } failure)
            throw failure;
        _onSamples = onSamples;
        return Capture = new FakeStream(CaptureRate);
    }

    public IAudioStream OpenPlayback(int preferredRate, AudioFill fill)
    {
        if (PlaybackFailure is { } failure)
            throw failure;
        _fill = fill;
        return Playback = new FakeStream(PlaybackRate);
    }

    /// <summary>The audio thread delivering microphone samples (only while the stream is started, like a device).</summary>
    public void Speak(ReadOnlySpan<short> samples)
    {
        if (Capture is { Running: true })
            _onSamples?.Invoke(samples);
    }

    /// <summary>The speaker's audio thread asking for <paramref name="count"/> samples; silence pads the rest.</summary>
    public short[] Pull(int count)
    {
        var buffer = new short[count];
        if (_fill is { } fill)
            fill(buffer);
        return buffer;
    }

    public void Dispose() => Disposed = true;

    public sealed class FakeStream(int rate) : IAudioStream
    {
        public int SampleRate { get; } = rate;

        public bool Running { get; private set; }

        public int Starts { get; private set; }

        public bool Disposed { get; private set; }

        public void Start()
        {
            Running = true;
            Starts++;
        }

        public void Stop() => Running = false;

        public void Dispose() => Disposed = true;
    }
}
