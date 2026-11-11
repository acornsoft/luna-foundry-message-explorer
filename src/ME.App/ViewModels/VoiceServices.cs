// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Voice;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>What the Voice panel talks through: the platform's audio (or none) and how it gets a short-lived token.</summary>
public sealed record VoiceServices(IVoiceAudio Audio, IVoiceTokenMinter? Tokens)
{
    /// <summary>Tests and windows without voice: typed questions, and the key is used directly (no network to mint).</summary>
    public static VoiceServices None() => new(new NoVoiceAudio(), null);

    /// <summary>The desktop app: the head's audio if it has one, and xAI short-lived tokens for the socket.</summary>
    public static VoiceServices ForThisComputer(Func<IVoiceAudio>? platformAudio)
    {
        IVoiceAudio audio;
        try
        {
            audio = platformAudio?.Invoke() ?? new NoVoiceAudio();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            audio = new NoVoiceAudio($"Audio could not start ({exception.Message}). Type a question instead.");
        }

        return new VoiceServices(audio, XaiClientSecrets.Shared);
    }
}
