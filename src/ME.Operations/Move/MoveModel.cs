// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Operations.Move;

public enum MoveEntityKind
{
    Queue,
    Topic,
    Subscription,
}

/// <summary>
/// One end of a move: a queue, a topic, or a topic subscription, optionally its dead-letter queue.
/// <paramref name="Namespace"/> identifies the broker namespace (for Service Bus, the host name); two
/// endpoints with the same namespace can move inside one transaction.
/// </summary>
public sealed record MoveEndpoint(string Namespace, MoveEntityKind Kind, string Name, string? Topic = null, bool DeadLetter = false)
{
    public static MoveEndpoint Queue(string ns, string name, bool deadLetter = false) => new(ns, MoveEntityKind.Queue, name, null, deadLetter);

    public static MoveEndpoint TopicOf(string ns, string name) => new(ns, MoveEntityKind.Topic, name);

    public static MoveEndpoint Subscription(string ns, string topic, string name, bool deadLetter = false) =>
        new(ns, MoveEntityKind.Subscription, name, topic, deadLetter);

    /// <summary>The entity path the broker knows, without the namespace: orders, orders/$deadletterqueue, order-events/subscriptions/audit.</summary>
    public string EntityPath
    {
        get
        {
            var path = Kind == MoveEntityKind.Subscription ? $"{Topic}/subscriptions/{Name}" : Name;
            return DeadLetter ? path + "/$deadletterqueue" : path;
        }
    }

    /// <summary>Plain words for the confirm dialog: "dead-letter queue of orders".</summary>
    public string Describe()
    {
        var what = Kind switch
        {
            MoveEntityKind.Queue => $"queue {Name}",
            MoveEntityKind.Topic => $"topic {Name}",
            _ => $"subscription {Name} of topic {Topic}",
        };
        return DeadLetter ? $"dead-letter queue of {what}" : what;
    }

    /// <summary>The value written to <c>lfme-moved-from</c> on every moved copy.</summary>
    public string MovedFromValue => $"{Namespace}/{EntityPath}";

    public override string ToString() => MovedFromValue;
}

/// <summary>Which messages a drop moves.</summary>
public abstract record MoveSelection
{
    public const int MaxBatch = 100;

    /// <summary>The oldest <paramref name="Count"/> messages, in sequence order.</summary>
    public sealed record Oldest(int Count) : MoveSelection;

    /// <summary>Messages picked in the list, by broker sequence number. At most <see cref="MaxBatch"/>.</summary>
    public sealed record Picked(IReadOnlySet<long> SequenceNumbers) : MoveSelection;

    public int Limit => this switch
    {
        Oldest oldest => oldest.Count,
        Picked picked => picked.SequenceNumbers.Count,
        _ => 0,
    };

    public static MoveSelection OldestFirst(int count) => new Oldest(count);

    public static MoveSelection Of(IEnumerable<long> sequenceNumbers) => new Picked(new SortedSet<long>(sequenceNumbers));
}

/// <summary>What the list shows about a message. No body, no application properties.</summary>
public sealed record MoveMessageInfo(
    long SequenceNumber,
    string? MessageId,
    string? SessionId,
    DateTimeOffset EnqueuedTime,
    int DeliveryCount,
    string? DeadLetterReason);

/// <summary>A message held under a peek-lock by a receiver. <see cref="Native"/> is the transport's own object.</summary>
public sealed record LockedMessage(
    long SequenceNumber,
    string? MessageId,
    string? SessionId,
    string? CorrelationId,
    string? Subject,
    string? ContentType,
    string? To,
    string? ReplyTo,
    string? ReplyToSessionId,
    string? PartitionKey,
    TimeSpan? TimeToLive,
    DateTimeOffset EnqueuedTime,
    int DeliveryCount,
    string? DeadLetterReason,
    IReadOnlyDictionary<string, object?> ApplicationProperties,
    ReadOnlyMemory<byte> Body,
    object? Native = null)
{
    // The body is never part of ToString, so it can't reach a log by accident.
    public override string ToString() => $"message seq {SequenceNumber} id {MessageId ?? "(none)"}";
}

/// <summary>The copy that is sent to the target.</summary>
public sealed record OutgoingMessage(
    string? MessageId,
    string? SessionId,
    string? CorrelationId,
    string? Subject,
    string? ContentType,
    string? To,
    string? ReplyTo,
    string? ReplyToSessionId,
    string? PartitionKey,
    TimeSpan? TimeToLive,
    IReadOnlyDictionary<string, object?> ApplicationProperties,
    ReadOnlyMemory<byte> Body)
{
    public override string ToString() => $"outgoing id {MessageId ?? "(none)"}";
}

public static class MessageCloner
{
    public const string MovedFromProperty = "lfme-moved-from";

    /// <summary>Broker-set dead-letter keys. They describe the old failure, so the copy drops them; the audit log keeps the reason.</summary>
    public static IReadOnlyList<string> DroppedProperties { get; } = ["DeadLetterReason", "DeadLetterErrorDescription"];

    /// <summary>
    /// Copies everything the sender owns: body, message ID, session, correlation, subject, content type,
    /// To, ReplyTo, ReplyToSessionId, partition key, time to live and application properties, then stamps
    /// <c>lfme-moved-from</c>. Broker-owned values (sequence number, enqueued time, delivery count, lock) are
    /// set fresh by the target.
    /// </summary>
    public static OutgoingMessage Clone(LockedMessage message, MoveEndpoint source)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(source);
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in message.ApplicationProperties)
        {
            if (!DroppedProperties.Contains(key, StringComparer.Ordinal))
                properties[key] = value;
        }

        properties[MovedFromProperty] = source.MovedFromValue;
        return new OutgoingMessage(
            message.MessageId,
            message.SessionId,
            message.CorrelationId,
            message.Subject,
            message.ContentType,
            message.To,
            message.ReplyTo,
            message.ReplyToSessionId,
            message.PartitionKey,
            message.TimeToLive,
            properties,
            message.Body);
    }
}
