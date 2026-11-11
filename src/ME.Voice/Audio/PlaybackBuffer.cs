// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Voice.Audio;

/// <summary>
/// The speaker's queue: the session writes decoded answer audio, the audio thread reads it. Bounded so a stuck
/// device can't grow memory without limit (the oldest audio is dropped first). Clear is barge-in.
/// </summary>
public sealed class PlaybackBuffer(int capacitySamples)
{
    private readonly object _gate = new();
    private readonly short[] _ring = new short[capacitySamples > 0 ? capacitySamples : throw new ArgumentOutOfRangeException(nameof(capacitySamples))];
    private int _start;
    private int _count;

    public int Capacity => _ring.Length;

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>Samples dropped because the buffer was full.</summary>
    public long Dropped { get; private set; }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            if (samples.Length > _ring.Length)
            {
                Dropped += samples.Length - _ring.Length;
                samples = samples[^_ring.Length..];
            }

            var overflow = _count + samples.Length - _ring.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _ring.Length;
                _count -= overflow;
                Dropped += overflow;
            }

            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - end);
            samples[..first].CopyTo(_ring.AsSpan(end));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
        }
    }

    /// <summary>Copies up to <paramref name="buffer"/>.Length samples out and returns how many.</summary>
    public int Read(Span<short> buffer)
    {
        lock (_gate)
        {
            var take = Math.Min(buffer.Length, _count);
            var first = Math.Min(take, _ring.Length - _start);
            _ring.AsSpan(_start, first).CopyTo(buffer);
            _ring.AsSpan(0, take - first).CopyTo(buffer[first..]);
            _start = (_start + take) % _ring.Length;
            _count -= take;
            return take;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }
}
