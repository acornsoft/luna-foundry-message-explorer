// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Jobs.Live;

/// <summary>
/// rate.perMinute on the wall clock: turn n may start no sooner than first turn + n × (60 s / perMinute).
/// Time a slow send already used is not waited again; a late turn does not make later ones burst.
/// </summary>
public sealed class RateLimiter
{
    private readonly ILiveClock _clock;
    private readonly TimeSpan _interval;
    private DateTimeOffset? _next;

    public RateLimiter(int perMinute, ILiveClock clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perMinute, LiveSendOptions.MaxPerMinute);
        _clock = clock;
        _interval = TimeSpan.FromMinutes(1) / perMinute;
    }

    public TimeSpan Interval => _interval;

    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (_next is { } next && next > now)
        {
            await _clock.DelayAsync(next - now, cancellationToken).ConfigureAwait(false);
            now = _clock.UtcNow;
        }

        var due = _next is { } previous && previous > now ? previous : now;
        _next = due + _interval;
    }
}
