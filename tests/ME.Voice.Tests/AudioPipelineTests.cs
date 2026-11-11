// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Voice.Audio;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>Push-to-talk audio over a fake device: chunking, resampling, playback queueing, barge-in and failures.</summary>
public sealed class AudioPipelineTests
{
    private readonly FakeAudioBackend _device = new();

    private static short[] Ramp(int count, int start = 0) => Enumerable.Range(start, count).Select(i => (short)(i % 30000)).ToArray();

    [Fact]
    public void Pcm16_round_trips_little_endian_and_ignores_a_trailing_odd_byte()
    {
        short[] samples = [0, 1, -1, short.MaxValue, short.MinValue, 0x1234];

        var bytes = Pcm16.ToBytes(samples);

        Assert.Equal(new byte[] { 0x34, 0x12 }, bytes[^2..]);
        Assert.Equal(samples, Pcm16.FromBytes(bytes));
        Assert.Equal(samples, Pcm16.FromBytes([.. bytes, 0x7F]));
    }

    [Theory]
    [InlineData(48000, 24000, 4800, 2400)]
    [InlineData(44100, 24000, 4410, 2400)]
    [InlineData(16000, 24000, 1600, 2400)]
    [InlineData(24000, 24000, 2400, 2400)]
    public void Resampling_keeps_the_duration(int from, int to, int length, int expected)
    {
        var result = Pcm16.Resample(Ramp(length), from, to);

        Assert.Equal(expected, result.Length);
    }

    [Fact]
    public void Resampling_a_constant_signal_keeps_it_constant()
    {
        var tone = Enumerable.Repeat((short)1200, 441).ToArray();

        Assert.All(Pcm16.Resample(tone, 44100, 24000), s => Assert.Equal(1200, s));
        Assert.All(Pcm16.Resample(tone, 24000, 48000), s => Assert.Equal(1200, s));
    }

    [Fact]
    public void The_playback_buffer_wraps_and_drops_the_oldest_audio_when_full()
    {
        var buffer = new PlaybackBuffer(5);
        buffer.Write([1, 2, 3]);
        var first = new short[2];
        Assert.Equal(2, buffer.Read(first));
        buffer.Write([4, 5, 6, 7, 8]);

        var all = new short[10];
        var read = buffer.Read(all);

        Assert.Equal([1, 2], first);
        Assert.Equal([4, 5, 6, 7, 8], all[..read]);
        Assert.Equal(1, buffer.Dropped);
        Assert.Equal(0, buffer.Read(all));
    }

    [Fact]
    public void Nothing_opens_until_the_talk_button_is_first_held()
    {
        using var audio = new PcmVoiceAudio(_device);

        Assert.Null(audio.UnavailableReason);
        Assert.Null(_device.Capture);
        Assert.Null(_device.Playback);
    }

    [Fact]
    public void Microphone_audio_goes_out_in_100_ms_chunks_in_order_and_the_tail_is_flushed_on_release()
    {
        using var audio = new PcmVoiceAudio(_device);
        var chunks = new List<byte[]>();
        var spoken = Ramp(5000);

        audio.StartCapture(chunks.Add);
        foreach (var piece in spoken.Chunk(480))
            _device.Speak(piece);
        Assert.Equal(2, chunks.Count);
        audio.StopCapture();

        Assert.Equal([4800, 4800, 400], chunks.Select(c => c.Length));
        Assert.Equal(spoken, chunks.SelectMany(c => Pcm16.FromBytes(c)));
        Assert.False(_device.Capture!.Running);
    }

    [Fact]
    public void Samples_after_release_are_not_sent_and_the_next_hold_starts_clean()
    {
        using var audio = new PcmVoiceAudio(_device);
        var first = new List<byte[]>();
        audio.StartCapture(first.Add);
        _device.Speak(Ramp(100));
        audio.StopCapture();
        _device.Speak(Ramp(100));

        var second = new List<byte[]>();
        audio.StartCapture(second.Add);
        _device.Speak(Ramp(10, 500));
        audio.StopCapture();

        Assert.Equal(200, Assert.Single(first).Length);
        Assert.Equal(Ramp(10, 500), Pcm16.FromBytes(Assert.Single(second)));
        Assert.Equal(2, _device.Capture!.Starts);
    }

    [Fact]
    public void A_48_kHz_microphone_is_resampled_to_24_kHz_before_it_is_sent()
    {
        _device.CaptureRate = 48000;
        using var audio = new PcmVoiceAudio(_device);
        var chunks = new List<byte[]>();

        audio.StartCapture(chunks.Add);
        foreach (var piece in Enumerable.Repeat((short)900, 9600).Chunk(960))
            _device.Speak(piece);
        audio.StopCapture();

        Assert.Equal(48000, audio.CaptureRate);
        Assert.Equal(4800, chunks.Sum(c => c.Length / 2));
        Assert.All(chunks.SelectMany(c => Pcm16.FromBytes(c)), s => Assert.Equal(900, s));
    }

    [Fact]
    public void Answer_audio_is_queued_for_the_speaker_and_silence_pads_an_underrun()
    {
        using var audio = new PcmVoiceAudio(_device);
        short[] answer = [10, 20, 30, 40];

        audio.Play(Pcm16.ToBytes(answer));
        var played = _device.Pull(6);

        Assert.True(_device.Playback!.Running);
        Assert.Equal([10, 20, 30, 40, 0, 0], played);
        Assert.Equal(0, audio.QueuedSamples);
    }

    [Fact]
    public void A_sample_split_across_two_deltas_is_put_back_together()
    {
        using var audio = new PcmVoiceAudio(_device);
        var bytes = Pcm16.ToBytes([0x0102, 0x0304, 0x0506]);

        audio.Play(bytes[..3]);
        audio.Play(bytes[3..]);

        Assert.Equal([0x0102, 0x0304, 0x0506], _device.Pull(3));
    }

    [Fact]
    public void A_44_1_kHz_speaker_gets_resampled_audio()
    {
        _device.PlaybackRate = 44100;
        using var audio = new PcmVoiceAudio(_device);

        audio.Play(Pcm16.ToBytes(Enumerable.Repeat((short)700, 2400).ToArray()));

        Assert.Equal(4410, audio.QueuedSamples);
        Assert.All(_device.Pull(4410), s => Assert.Equal(700, s));
    }

    [Fact]
    public void Barge_in_drops_queued_answer_audio()
    {
        using var audio = new PcmVoiceAudio(_device);
        audio.Play(Pcm16.ToBytes(Ramp(2400, 1)));

        audio.StopPlayback();

        Assert.Equal(0, audio.QueuedSamples);
        Assert.All(_device.Pull(100), s => Assert.Equal(0, s));
    }

    [Fact]
    public void No_device_means_a_plain_reason_and_nothing_opens()
    {
        _device.ProbeResult = "No microphone was found. Type a question instead.";
        using var audio = new PcmVoiceAudio(_device);
        var chunks = new List<byte[]>();

        audio.StartCapture(chunks.Add);
        audio.Play([1, 2]);
        audio.StopCapture();

        Assert.Equal("No microphone was found. Type a question instead.", audio.UnavailableReason);
        Assert.Null(_device.Capture);
        Assert.Null(_device.Playback);
        Assert.Empty(chunks);
        Assert.Equal(1, _device.ProbeCalls);
    }

    [Fact]
    public void A_microphone_that_fails_to_open_turns_into_a_reason_not_an_exception()
    {
        _device.CaptureFailure = new InvalidOperationException("device busy.");
        using var audio = new PcmVoiceAudio(_device);

        audio.StartCapture(_ => { });
        audio.StopCapture();

        Assert.Contains("Could not open the microphone", audio.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("device busy", audio.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_that_throws_turns_into_a_reason()
    {
        using var audio = new PcmVoiceAudio(new ThrowingBackend());

        Assert.Contains("Audio is not available", audio.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_stops_and_closes_both_devices()
    {
        var audio = new PcmVoiceAudio(_device);
        audio.StartCapture(_ => { });
        audio.Play(Pcm16.ToBytes([1]));

        audio.Dispose();
        audio.Dispose();

        Assert.True(_device.Capture!.Disposed);
        Assert.True(_device.Playback!.Disposed);
        Assert.True(_device.Disposed);
    }

    [Fact]
    public async Task Chunks_from_the_audio_thread_and_playback_from_the_socket_can_run_at_once()
    {
        using var audio = new PcmVoiceAudio(_device);
        var count = 0;
        audio.StartCapture(chunk => Interlocked.Add(ref count, chunk.Length / 2));

        var speak = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                _device.Speak(Ramp(240));
        });
        var play = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                audio.Play(Pcm16.ToBytes(Ramp(240)));
                _device.Pull(240);
            }
        });
        await Task.WhenAll(speak, play);
        audio.StopCapture();

        Assert.Equal(200 * 240, count);
    }

    private sealed class ThrowingBackend : IAudioBackend
    {
        public string Name => "broken";

        public string? Probe() => throw new InvalidOperationException("native library missing.");

        public IAudioStream OpenCapture(int preferredRate, AudioSamples onSamples) => throw new NotSupportedException();

        public IAudioStream OpenPlayback(int preferredRate, AudioFill fill) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
