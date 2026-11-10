// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Voice.Audio;
using Acornsoft.MessageExplorer.Voice.PortAudio;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>
/// The real PortAudio backend on whatever machine runs the tests (CI has no sound card). It must answer the
/// probe with null or a plain reason and never throw or hang; it opens no device here.
/// </summary>
public sealed class PortAudioBackendTests
{
    [Fact]
    public async Task The_probe_answers_without_throwing_or_hanging()
    {
        var probe = Task.Run(() =>
        {
            using var backend = new PortAudioBackend();
            return (Reason: backend.Probe(), backend.Name);
        });

        var result = await probe.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("PortAudio", result.Name);
        if (result.Reason is { } reason)
            Assert.EndsWith("Type a question instead.", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapped_for_the_voice_tab_a_missing_device_becomes_the_audio_note()
    {
        using var audio = new PcmVoiceAudio(new PortAudioBackend());

        var reason = audio.UnavailableReason;

        Assert.True(reason is null || reason.Contains("question", StringComparison.Ordinal));
    }
}
