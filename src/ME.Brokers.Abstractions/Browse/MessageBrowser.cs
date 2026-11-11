// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers.Browse;

/// <summary>
/// The four message types a quick view shows for one entity. A broker that has no such type for an entity
/// reports a null count and the lane says so; nothing here is specific to one broker.
/// </summary>
public enum MessageLane
{
    Active,
    DeadLetter,
    Scheduled,
    TransferDeadLetter,
}

/// <summary>
/// Where a peek reads from. The main entity holds active, deferred and scheduled messages; the two dead-letter
/// sub-queues hold the rest. Each one gets its own sequence cursor.
/// </summary>
public enum PeekSource
{
    Main,
    DeadLetter,
    TransferDeadLetter,
}

/// <summary>A queue, or a subscription of a topic. Topics themselves can't be peeked.</summary>
public sealed record BrowseEntity(EntityKind Kind, string Name, string? Topic = null)
{
    public static BrowseEntity Queue(string name) => new(EntityKind.Queue, name);

    public static BrowseEntity Subscription(string topic, string name) => new(EntityKind.Subscription, name, topic);

    /// <summary>The entity path, for example <c>orders</c> or <c>order-events/Subscriptions/billing</c>.</summary>
    public string Path => Kind == EntityKind.Subscription ? $"{Topic}/Subscriptions/{Name}" : Name;

    /// <summary>The path of a sub-queue, for example <c>orders/$DeadLetterQueue</c>.</summary>
    public string PathOf(PeekSource source) => source switch
    {
        PeekSource.DeadLetter => Path + "/$DeadLetterQueue",
        PeekSource.TransferDeadLetter => Path + "/$Transfer/$DeadLetterQueue",
        _ => Path,
    };
}

/// <summary>
/// One peeked message, before redaction. It never reaches a view: the quick view redacts it first.
/// <paramref name="Lane"/> is Active or Scheduled for the main entity (deferred messages are Active with
/// <paramref name="Deferred"/> set), and DeadLetter or TransferDeadLetter for the sub-queues.
/// </summary>
public sealed record PeekedFrame(
    long SequenceNumber,
    MessageLane Lane,
    string? MessageId,
    DateTimeOffset? EnqueuedAt,
    DateTimeOffset? ScheduledFor,
    int DeliveryCount,
    string? Subject,
    string? ContentType,
    string? CorrelationId,
    string? SessionId,
    long? SizeInBytes,
    string? DeadLetterReason,
    string? DeadLetterDescription,
    bool Deferred,
    IReadOnlyDictionary<string, string> Body,
    IReadOnlyDictionary<string, string> ApplicationProperties);

/// <summary>
/// Peek only. There is no receive, lock, renew, defer, complete, abandon or dead-letter here, and there must
/// never be: a peek takes no lock and changes nothing on the broker. <see cref="PeekAsync"/> may return fewer
/// than <paramref name="max"/> frames even when more are waiting; an empty list means the end.
/// </summary>
public interface IMessageBrowser : IAsyncDisposable
{
    /// <summary>The broker family, in words (for the status line).</summary>
    string ProviderName { get; }

    /// <summary>True when this browser can peek the sub-queue of the entity.</summary>
    bool CanPeek(BrowseEntity entity, PeekSource source);

    Task<IReadOnlyList<PeekedFrame>> PeekAsync(BrowseEntity entity, PeekSource source, long fromSequenceNumber, int max, CancellationToken cancellationToken = default);
}
