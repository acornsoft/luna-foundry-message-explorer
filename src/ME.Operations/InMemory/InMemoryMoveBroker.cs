// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.InMemory;

/// <summary>A message as the in-memory broker stores it.</summary>
public sealed record StoredMessage(long SequenceNumber, OutgoingMessage Message, DateTimeOffset EnqueuedTime, int DeliveryCount, string? DeadLetterReason, bool Locked);

/// <summary>
/// A broker namespace held in memory for tests and the synthetic demo. It behaves like a peek-lock broker:
/// receive locks messages oldest first, complete removes them, abandon releases them with one more delivery,
/// a receiver that closes releases what it still holds, sessions are locked one at a time, topics fan out to
/// every subscription, duplicate detection drops a repeated message ID, and a transaction applies all of
/// its sends and completes or none. <see cref="Fault"/> injects failures. Nothing here talks to a network.
/// </summary>
public sealed class InMemoryMoveBroker(string ns)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<(string Entity, string Session)> _lockedSessions = [];
    private long _sequence;

    public string Namespace { get; } = ns;

    /// <summary>Called before each send, complete, abandon and receive with the operation name and its 1-based count. Return an exception to fail it.</summary>
    public Func<string, int, Exception?>? Fault { get; set; }

    public DateTimeOffset Now { get; set; } = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    public InMemoryMoveBroker AddQueue(string name, bool sessions = false, bool duplicateDetection = false, bool canReceive = true, bool canSend = true)
    {
        lock (_gate)
            _entities[name] = new Entity(MoveEntityKind.Queue, name, null, sessions, duplicateDetection, canReceive, canSend);
        return this;
    }

    public InMemoryMoveBroker AddTopic(string name, bool duplicateDetection = false, bool canSend = true)
    {
        lock (_gate)
            _entities[name] = new Entity(MoveEntityKind.Topic, name, null, false, duplicateDetection, false, canSend);
        return this;
    }

    public InMemoryMoveBroker AddSubscription(string topic, string name, bool sessions = false, bool canReceive = true)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(topic, out var parent) || parent.Kind != MoveEntityKind.Topic)
                throw new InvalidOperationException($"Add topic {topic} first.");
            _entities[$"{topic}/subscriptions/{name}"] = new Entity(MoveEntityKind.Subscription, name, topic, sessions, false, canReceive, false);
        }

        return this;
    }

    /// <summary>Puts a message straight onto an entity or its dead-letter queue (test setup; skips faults and rights).</summary>
    public long Seed(MoveEndpoint endpoint, OutgoingMessage message, string? deadLetterReason = null)
    {
        lock (_gate)
        {
            var entity = Find(endpoint);
            var stored = new Stored(++_sequence, message, Now, endpoint.DeadLetter ? 1 : 0, deadLetterReason);
            (endpoint.DeadLetter ? entity.DeadLetters : entity.Messages).Add(stored);
            if (!endpoint.DeadLetter && message.MessageId is { } id)
                entity.SeenIds.Add(id);
            return stored.SequenceNumber;
        }
    }

    public static OutgoingMessage Message(string id, string? session = null, string? correlation = null, string body = "{}", IReadOnlyDictionary<string, object?>? properties = null) =>
        new(id, session, correlation, "synthetic", "application/json", null, null, null, session, null,
            properties ?? new Dictionary<string, object?>(StringComparer.Ordinal), System.Text.Encoding.UTF8.GetBytes(body));

    public IReadOnlyList<StoredMessage> Messages(MoveEndpoint endpoint)
    {
        lock (_gate)
        {
            var entity = Find(endpoint);
            return (endpoint.DeadLetter ? entity.DeadLetters : entity.Messages)
                .Select(s => new StoredMessage(s.SequenceNumber, s.Message, s.EnqueuedTime, s.DeliveryCount, s.DeadLetterReason, s.LockOwner is not null))
                .ToList();
        }
    }

    public int Count(MoveEndpoint endpoint) => Messages(endpoint).Count;

    public bool Exists(MoveEndpoint endpoint)
    {
        lock (_gate)
            return _entities.ContainsKey(Key(endpoint));
    }

    public bool RequiresSessions(MoveEndpoint endpoint)
    {
        lock (_gate)
            return !endpoint.DeadLetter && Find(endpoint).Sessions;
    }

    public bool DuplicateDetection(MoveEndpoint endpoint)
    {
        lock (_gate)
            return Find(endpoint).DuplicateDetection;
    }

    public RightCheck CanReceive(MoveEndpoint endpoint)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(Key(endpoint), out var entity))
                return RightCheck.No($"{endpoint.Describe()} wasn't found.");
            return entity.CanReceive ? RightCheck.Yes : RightCheck.No($"This login can't receive from {endpoint.Describe()}.");
        }
    }

    public RightCheck CanSend(MoveEndpoint endpoint)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(Key(endpoint), out var entity))
                return RightCheck.No($"{endpoint.Describe()} wasn't found.");
            return entity.CanSend ? RightCheck.Yes : RightCheck.No($"This login can't send to {endpoint.Describe()}.");
        }
    }

    internal IReadOnlyList<MoveMessageInfo> Peek(MoveEndpoint endpoint, int max)
    {
        lock (_gate)
        {
            return List(endpoint).OrderBy(s => s.SequenceNumber).Take(max)
                .Select(s => new MoveMessageInfo(s.SequenceNumber, s.Message.MessageId, s.Message.SessionId, s.EnqueuedTime, s.DeliveryCount, s.DeadLetterReason))
                .ToList();
        }
    }

    internal IReadOnlyList<LockedMessage> Receive(object owner, MoveEndpoint endpoint, string? session, int max)
    {
        lock (_gate)
        {
            Check("receive");
            var picked = List(endpoint)
                .Where(s => s.LockOwner is null && (session is null || s.Message.SessionId == session))
                .OrderBy(s => s.SequenceNumber)
                .Take(max)
                .ToList();
            foreach (var stored in picked)
            {
                stored.LockOwner = owner;
                stored.DeliveryCount++;
            }

            return picked.Select(s => ToLocked(s)).ToList();
        }
    }

    internal string? LockNextSession(MoveEndpoint endpoint)
    {
        lock (_gate)
        {
            var key = Key(endpoint);
            var next = List(endpoint)
                .Where(s => s.LockOwner is null && s.Message.SessionId is not null && !_lockedSessions.Contains((key, s.Message.SessionId)))
                .OrderBy(s => s.SequenceNumber)
                .Select(s => s.Message.SessionId)
                .FirstOrDefault();
            if (next is not null)
                _lockedSessions.Add((key, next));
            return next;
        }
    }

    internal void UnlockSession(MoveEndpoint endpoint, string session)
    {
        lock (_gate)
            _lockedSessions.Remove((Key(endpoint), session));
    }

    /// <summary>A send. Inside a transaction it is checked now and applied at commit.</summary>
    internal void Send(MoveEndpoint target, OutgoingMessage message, List<Action>? transaction)
    {
        lock (_gate)
        {
            Check("send");
            var entity = Find(target);
            if (!entity.CanSend)
                throw new MoveTransportException(MoveFailureKind.Unauthorized, $"This login can't send to {target.Describe()}.");
            Apply(transaction, () => Deliver(entity, message));
        }
    }

    internal void Complete(object owner, MoveEndpoint source, LockedMessage message, List<Action>? transaction)
    {
        lock (_gate)
        {
            Check("complete");
            var stored = Locked(owner, source, message);
            Apply(transaction, () => List(source).Remove(stored));
        }
    }

    internal void Abandon(object owner, MoveEndpoint source, LockedMessage message)
    {
        lock (_gate)
        {
            Check("abandon");
            Locked(owner, source, message).LockOwner = null;
        }
    }

    /// <summary>Like a lock running out: everything the owner still holds becomes visible again.</summary>
    internal void ReleaseAll(object owner)
    {
        lock (_gate)
        {
            foreach (var stored in _entities.Values.SelectMany(e => e.Messages.Concat(e.DeadLetters)).Where(s => s.LockOwner == owner))
                stored.LockOwner = null;
        }
    }

    /// <summary>Runs queued transaction actions together under the broker lock.</summary>
    internal void Commit(List<Action> actions)
    {
        lock (_gate)
        {
            Check("commit");
            foreach (var action in actions)
                action();
        }
    }

    private void Deliver(Entity entity, OutgoingMessage message)
    {
        if (entity.DuplicateDetection && message.MessageId is { } id && !entity.SeenIds.Add(id))
            return;
        var sequence = ++_sequence;
        if (entity.Kind == MoveEntityKind.Topic)
        {
            foreach (var subscription in _entities.Values.Where(e => e.Kind == MoveEntityKind.Subscription && e.Topic == entity.Name))
                subscription.Messages.Add(new Stored(sequence, message, Now, 0, null));
            return;
        }

        entity.Messages.Add(new Stored(sequence, message, Now, 0, null));
    }

    private static void Apply(List<Action>? transaction, Action action)
    {
        if (transaction is null)
            action();
        else
            transaction.Add(action);
    }

    private Stored Locked(object owner, MoveEndpoint source, LockedMessage message)
    {
        var stored = List(source).FirstOrDefault(s => s.SequenceNumber == message.SequenceNumber);
        if (stored is null || stored.LockOwner != owner)
            throw new MoveTransportException(MoveFailureKind.LockLost, "The lock on a message ran out before the move finished.");
        return stored;
    }

    private void Check(string operation)
    {
        var count = _calls[operation] = _calls.GetValueOrDefault(operation) + 1;
        if (Fault?.Invoke(operation, count) is { } failure)
            throw failure;
    }

    private List<Stored> List(MoveEndpoint endpoint)
    {
        var entity = Find(endpoint);
        return endpoint.DeadLetter ? entity.DeadLetters : entity.Messages;
    }

    private Entity Find(MoveEndpoint endpoint) =>
        _entities.TryGetValue(Key(endpoint), out var entity)
            ? entity
            : throw new MoveTransportException(MoveFailureKind.NotFound, $"{endpoint.Describe()} wasn't found.");

    private static string Key(MoveEndpoint endpoint) =>
        endpoint.Kind == MoveEntityKind.Subscription ? $"{endpoint.Topic}/subscriptions/{endpoint.Name}" : endpoint.Name;

    private static LockedMessage ToLocked(Stored s) => new(
        s.SequenceNumber, s.Message.MessageId, s.Message.SessionId, s.Message.CorrelationId, s.Message.Subject, s.Message.ContentType,
        s.Message.To, s.Message.ReplyTo, s.Message.ReplyToSessionId, s.Message.PartitionKey, s.Message.TimeToLive, s.EnqueuedTime,
        s.DeliveryCount, s.DeadLetterReason, s.Message.ApplicationProperties, s.Message.Body);

    private sealed class Entity(MoveEntityKind kind, string name, string? topic, bool sessions, bool duplicateDetection, bool canReceive, bool canSend)
    {
        public MoveEntityKind Kind { get; } = kind;

        public string Name { get; } = name;

        public string? Topic { get; } = topic;

        public bool Sessions { get; } = sessions;

        public bool DuplicateDetection { get; } = duplicateDetection;

        public bool CanReceive { get; } = canReceive;

        public bool CanSend { get; } = canSend;

        public List<Stored> Messages { get; } = [];

        public List<Stored> DeadLetters { get; } = [];

        public HashSet<string> SeenIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Stored(long sequenceNumber, OutgoingMessage message, DateTimeOffset enqueuedTime, int deliveryCount, string? deadLetterReason)
    {
        public long SequenceNumber { get; } = sequenceNumber;

        public OutgoingMessage Message { get; } = message;

        public DateTimeOffset EnqueuedTime { get; } = enqueuedTime;

        public int DeliveryCount { get; set; } = deliveryCount;

        public string? DeadLetterReason { get; } = deadLetterReason;

        public object? LockOwner { get; set; }
    }
}
