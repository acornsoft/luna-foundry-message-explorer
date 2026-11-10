// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Bus;

/// <summary>
/// A broker that lives only in memory, for test jobs. Each entity is a log. Every consumer (a simulator
/// subscription or an observer) gets its own copy of each message, like a topic subscription. A message
/// becomes visible at its <see cref="BusMessage.AvailableAt"/> time on the simulated clock.
/// Nothing here talks to a network.
/// </summary>
public sealed class InMemoryBus
{
    private readonly Dictionary<string, List<BusMessage>> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Consumer, string Entity), HashSet<long>> _taken = [];
    private long _sequence;

    public BusMessage Enqueue(string entity, string correlationId, JsonObject body, DateTimeOffset availableAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var message = new BusMessage(entity, ++_sequence, correlationId, availableAt, (JsonObject)body.DeepClone());
        if (!_entities.TryGetValue(entity, out var log))
            _entities[entity] = log = [];
        log.Add(message);
        return message;
    }

    /// <summary>Messages on <paramref name="entity"/> visible by <paramref name="now"/> that this consumer has not taken yet, oldest first.</summary>
    public IReadOnlyList<BusMessage> TakeDue(string consumer, string entity, DateTimeOffset now)
    {
        if (!_entities.TryGetValue(entity, out var log))
            return [];
        var key = (consumer, entity);
        if (!_taken.TryGetValue(key, out var taken))
            _taken[key] = taken = [];
        var due = log
            .Where(message => message.AvailableAt <= now && !taken.Contains(message.Sequence))
            .OrderBy(message => message.AvailableAt)
            .ThenBy(message => message.Sequence)
            .ToList();
        foreach (var message in due)
            taken.Add(message.Sequence);
        return due;
    }

    /// <summary>The earliest time after <paramref name="now"/> at which a message becomes visible, if any.</summary>
    public DateTimeOffset? NextAvailableAfter(DateTimeOffset now) =>
        _entities.Values.SelectMany(log => log).Where(message => message.AvailableAt > now).Select(message => (DateTimeOffset?)message.AvailableAt).Min();

    public IReadOnlyList<BusMessage> Messages(string entity) =>
        _entities.TryGetValue(entity, out var log) ? log.ToList() : [];

    public IReadOnlyCollection<string> Entities => _entities.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    public long TotalMessages => _sequence;
}
