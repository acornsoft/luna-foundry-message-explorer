// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

public enum EntityKind
{
    Queue,
    Topic,
    Subscription,
}

/// <summary>Message counts. A count the broker does not report is null, not zero.</summary>
public sealed record EntityCounts(
    long? Active,
    long? DeadLetter,
    long? Scheduled,
    long? TransferDeadLetter,
    long? Total)
{
    public static EntityCounts Unknown { get; } = new(null, null, null, null, null);

    public bool HasDeadLetters => DeadLetter > 0 || TransferDeadLetter > 0;
}

/// <summary>One row of a read-only inventory. Status and size are null when not reported.</summary>
public sealed record EntitySnapshot(
    EntityKind Kind,
    string Name,
    string? Parent,
    EntityCounts Counts,
    string? Status,
    long? SizeInBytes,
    int? SubscriptionCount = null)
{
    public string Path => Parent is null ? Name : $"{Parent}/subscriptions/{Name}";
}

/// <summary>
/// The result of one inventory read, with the number of broker pages it took. <paramref name="Notice"/> is a
/// plain sentence to show above the grid when the connection can only see part of the picture.
/// </summary>
public sealed record InventoryResult(IReadOnlyList<EntitySnapshot> Entities, int PagesRead, string? Notice = null);

/// <summary>
/// Read-only entity inventory. There is no send, receive, lock, settle, create, update, or delete here,
/// and there must never be. Tests reflect over this type to keep it that way.
/// </summary>
public interface IEntityInventory : IAsyncDisposable
{
    BrokerProviderDescriptor Provider { get; }

    Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default);

    Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default);

    Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default);
}

/// <summary>A broker family. It validates a connection and opens a read-only inventory.</summary>
public interface IBrokerProvider
{
    BrokerProviderDescriptor Descriptor { get; }

    /// <summary>Returns null when the connection is valid, or a plain message that says what to fix.</summary>
    string? Validate(BrokerConnection connection);

    IEntityInventory OpenInventory(BrokerConnection connection);
}
