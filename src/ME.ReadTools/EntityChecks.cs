// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.ReadTools;

/// <summary>Limits on message counts. A null limit is not checked.</summary>
public sealed record CountLimits(long? MaxDeadLetter = null, long? MaxActive = null, long? MaxScheduled = null);

/// <summary>One entity over one limit, in plain words ("dead-letter 3 &gt; 0").</summary>
public sealed record LimitBreach(string Path, string Reason);

public static class EntityChecks
{
    /// <summary>
    /// Entities that match any filter: a queue or topic name, a topic name (which takes its subscriptions
    /// too) or a full path such as <c>orders-events/subscriptions/billing</c>. No filters keeps everything.
    /// </summary>
    public static IReadOnlyList<EntitySnapshot> Filter(IReadOnlyList<EntitySnapshot> entities, IReadOnlyCollection<string>? filters)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (filters is null || filters.Count == 0)
            return entities;
        return entities.Where(e => filters.Any(f => Matches(e, f))).ToList();
    }

    public static IReadOnlyList<LimitBreach> Breaches(IEnumerable<EntitySnapshot> entities, CountLimits limits)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(limits);
        var breaches = new List<LimitBreach>();
        foreach (var e in entities)
        {
            Check(e, "dead-letter", e.Counts.DeadLetter, limits.MaxDeadLetter);
            Check(e, "active", e.Counts.Active, limits.MaxActive);
            Check(e, "scheduled", e.Counts.Scheduled, limits.MaxScheduled);
        }

        return breaches;

        void Check(EntitySnapshot e, string what, long? count, long? max)
        {
            if (max is { } limit && count is { } value && value > limit)
                breaches.Add(new LimitBreach(e.Path, $"{what} {value} > {limit}"));
        }
    }

    private static bool Matches(EntitySnapshot e, string filter) =>
        string.Equals(e.Path, filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || (e.Parent is null && string.Equals(e.Name, filter.Trim(), StringComparison.OrdinalIgnoreCase))
        || (e.Parent is not null && string.Equals(e.Parent, filter.Trim(), StringComparison.OrdinalIgnoreCase));
}
