// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Voice;

/// <summary>One of xAI's built-in voices: the lowercase ID sent as <c>voice</c>, and its tone.</summary>
public sealed record GrokVoice(string Id, string Tone)
{
    public string Label => $"{char.ToUpperInvariant(Id[0])}{Id[1..]} - {Tone}";

    public override string ToString() => Label;
}

/// <summary>
/// xAI's built-in voices, from the public Voice overview (docs.x.ai, model-capabilities/audio/voice, updated
/// October 22, 2026). Only these can be picked: custom (cloned) voices are deliberately not offered.
/// </summary>
public static class GrokVoices
{
    public const string DefaultId = "eve";

    public static IReadOnlyList<GrokVoice> BuiltIn { get; } =
    [
        new("eve", "energetic and upbeat, British accent"),
        new("ara", "warm and friendly"),
        new("leo", "authoritative and strong, British accent"),
        new("rex", "confident and clear"),
        new("sal", "smooth and balanced"),
        new("carina", "soft, empathetic and soothing"),
        new("zagan", "powerful and dramatic"),
        new("helix", "bold and dynamic"),
        new("orion", "rich, cinematic and resonant"),
        new("luna", "gentle, patient and nurturing"),
        new("iris", "friendly, upbeat and charming"),
        new("altair", "elegant and refined"),
        new("zenith", "sharp, focused and driven"),
        new("perseus", "strong, confident and trustworthy"),
        new("helios", "upbeat, energetic and versatile"),
        new("lux", "grounded, calm and quietly wise"),
        new("kepler", "inventive and charismatic"),
        new("rigel", "precise and professional, Australian accent"),
        new("cosmo", "bright, curious and easy to follow"),
        new("celeste", "compassionate and reassuring"),
        new("ursa", "friendly, warm and steadfast"),
        new("sirius", "quick-witted and playful"),
        new("lumen", "warm, articulate and engaging"),
        new("castor", "charismatic and easygoing"),
        new("naksh", "warm and thoughtful, Indian accent"),
        new("atlas", "confident and commanding"),
        new("aurora", "serene, steady and radiant"),
        new("liora", "calm, grounded and luminous"),
    ];

    public static GrokVoice Default => BuiltIn[0];

    public static bool IsBuiltIn(string? id) => id is not null && BuiltIn.Any(v => v.Id == id);

    /// <summary>The built-in voice with this ID, or the default for anything else (including custom voice IDs).</summary>
    public static GrokVoice Find(string? id) => BuiltIn.FirstOrDefault(v => v.Id == id) ?? Default;
}
