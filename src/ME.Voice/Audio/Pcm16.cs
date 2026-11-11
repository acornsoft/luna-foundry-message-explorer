// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;

namespace Acornsoft.MessageExplorer.Voice.Audio;

/// <summary>PCM16 little-endian helpers: bytes to samples and back, and a linear resampler for devices that won't open at 24 kHz.</summary>
public static class Pcm16
{
    public static byte[] ToBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    /// <summary>Whole samples only; a trailing odd byte is ignored.</summary>
    public static short[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
        return samples;
    }

    /// <summary>Linear interpolation from one rate to another. Same rate returns a copy.</summary>
    public static short[] Resample(ReadOnlySpan<short> samples, int fromRate, int toRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fromRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toRate, 0);
        if (fromRate == toRate || samples.Length == 0)
            return samples.ToArray();
        var length = (int)Math.Max(1, (long)samples.Length * toRate / fromRate);
        var result = new short[length];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < length; i++)
        {
            var position = i * step;
            var index = (int)position;
            if (index >= samples.Length - 1)
            {
                result[i] = samples[^1];
                continue;
            }

            var fraction = position - index;
            result[i] = (short)Math.Round(samples[index] + ((samples[index + 1] - samples[index]) * fraction));
        }

        return result;
    }
}
