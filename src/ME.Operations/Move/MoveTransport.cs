// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Operations.Move;

/// <summary>What a broker family can do in Move mode.</summary>
[Flags]
public enum MoveCapabilities
{
    None = 0,

    /// <summary>Lock a message, send a copy, then remove the original.</summary>
    Move = 1 << 0,

    /// <summary>Send and complete in one transaction when both ends are in the same namespace.</summary>
    Transactions = 1 << 1,

    /// <summary>Ordered sessions (lock one session at a time).</summary>
    Sessions = 1 << 2,

    /// <summary>A dead-letter sub-queue per queue and subscription.</summary>
    DeadLetter = 1 << 3,

    /// <summary>Topics that fan out to subscriptions.</summary>
    Topics = 1 << 4,
}

/// <summary>One right (receive on the source, or send on the target), checked before any lock is taken.</summary>
public sealed record RightCheck(bool Allowed, string? Problem = null)
{
    public static RightCheck Yes { get; } = new(true);

    public static RightCheck No(string problem) => new(false, problem);
}

public enum MoveFailureKind
{
    Unauthorized,
    NotFound,
    LockLost,
    Busy,
    TooLarge,
    QuotaExceeded,
    Network,
    Other,
}

/// <summary>A broker failure in plain words. Transports throw this; the message never carries a secret or a body.</summary>
public sealed class MoveTransportException(MoveFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public MoveFailureKind Kind { get; } = kind;
}

/// <summary>The source turned out to use sessions (or not) after all. The engine switches mode once.</summary>
public sealed class MoveSessionModeException(bool requiresSessions) : Exception(requiresSessions ? "The source uses sessions." : "The source doesn't use sessions.")
{
    public bool RequiresSessions { get; } = requiresSessions;
}

/// <summary>
/// One broker namespace, seen by Move mode. Every method that changes state is here and nowhere else in the
/// app. An implementation must hand out messages under a peek-lock in sequence order and must not remove a
/// message until <see cref="IMoveReceiver.CompleteAsync"/>.
/// </summary>
public interface IMoveTransport : IAsyncDisposable
{
    /// <summary>A plain name for the broker family, for messages: "Azure Service Bus".</summary>
    string ProviderName { get; }

    /// <summary>The namespace this transport talks to. Endpoints with the same value share transactions.</summary>
    string Namespace { get; }

    MoveCapabilities Capabilities { get; }

    /// <summary>Checks the right to receive from the source without locking or moving anything.</summary>
    Task<RightCheck> CheckReceiveAsync(MoveEndpoint source, CancellationToken cancellationToken);

    /// <summary>Checks the right to send to the target without sending anything.</summary>
    Task<RightCheck> CheckSendAsync(MoveEndpoint target, CancellationToken cancellationToken);

    /// <summary>True when the source hands out messages by session (a session-enabled queue or subscription).</summary>
    Task<bool> RequiresSessionsAsync(MoveEndpoint source, CancellationToken cancellationToken);

    /// <summary>Duplicate detection on the target: true, false, or null when it can't be read.</summary>
    Task<bool?> DuplicateDetectionAsync(MoveEndpoint target, CancellationToken cancellationToken);

    /// <summary>Reads message metadata without locking. Never returns bodies or properties.</summary>
    Task<IReadOnlyList<MoveMessageInfo>> PeekAsync(MoveEndpoint source, int max, CancellationToken cancellationToken);

    /// <summary>A receiver for a source that doesn't use sessions.</summary>
    Task<IMoveReceiver> OpenReceiverAsync(MoveEndpoint source, CancellationToken cancellationToken);

    /// <summary>Locks the next session that has messages, or returns null when none is left.</summary>
    Task<IMoveReceiver?> AcceptNextSessionAsync(MoveEndpoint source, CancellationToken cancellationToken);

    IMoveSender OpenSender(MoveEndpoint target);

    /// <summary>
    /// Runs <paramref name="work"/> as one transaction: its sends and completes all happen, or none do.
    /// Only called when <see cref="MoveCapabilities.Transactions"/> is set and both ends are in this namespace.
    /// </summary>
    Task RunInTransactionAsync(Func<Task> work);
}

public interface IMoveReceiver : IAsyncDisposable
{
    /// <summary>The locked session, or null for a source without sessions.</summary>
    string? SessionId { get; }

    /// <summary>Up to <paramref name="max"/> messages under a peek-lock, oldest first. Empty when nothing arrives within the wait.</summary>
    Task<IReadOnlyList<LockedMessage>> ReceiveAsync(int max, TimeSpan wait, CancellationToken cancellationToken);

    Task CompleteAsync(LockedMessage message, CancellationToken cancellationToken);

    Task AbandonAsync(LockedMessage message, CancellationToken cancellationToken);
}

public interface IMoveSender : IAsyncDisposable
{
    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>A move that was refused before anything was locked. The message is plain and safe to show.</summary>
public sealed class MoveRefusedException(string message) : InvalidOperationException(message);
