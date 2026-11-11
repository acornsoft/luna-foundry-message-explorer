// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Voice.Audio;

/// <summary>
/// A platform's microphone and speaker, mono 16-bit. <see cref="PcmVoiceAudio"/> turns it into the 24 kHz
/// push-to-talk audio the voice session needs. Tests supply a fake; desktop heads supply PortAudio.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>For the status line, for example "PortAudio (Core Audio)".</summary>
    string Name { get; }

    /// <summary>Null when a default microphone and speaker are usable; otherwise why not, in plain words. Never throws.</summary>
    string? Probe();

    /// <summary>
    /// Opens the default microphone. The backend tries <paramref name="preferredRate"/> first and may fall back to
    /// the device's own rate; <see cref="IAudioStream.SampleRate"/> says which. <paramref name="onSamples"/> runs on
    /// the audio thread and must return quickly.
    /// </summary>
    IAudioStream OpenCapture(int preferredRate, AudioSamples onSamples);

    /// <summary>
    /// Opens the default speaker. <paramref name="fill"/> runs on the audio thread, writes up to the span's length
    /// and returns how many samples it wrote; the backend plays silence for the rest.
    /// </summary>
    IAudioStream OpenPlayback(int preferredRate, AudioFill fill);
}

/// <summary>Microphone samples, on the audio thread.</summary>
public delegate void AudioSamples(ReadOnlySpan<short> samples);

/// <summary>Fills a playback buffer; returns the number of samples written.</summary>
public delegate int AudioFill(Span<short> buffer);

/// <summary>An open device stream. Start and Stop may be called repeatedly; Stop waits for the audio thread to finish its current buffer.</summary>
public interface IAudioStream : IDisposable
{
    int SampleRate { get; }

    void Start();

    void Stop();
}
