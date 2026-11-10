// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.InMemory;

/// <summary>Move mode over an <see cref="InMemoryMoveBroker"/>.</summary>
public sealed class InMemoryMoveTransport(InMemoryMoveBroker broker, MoveCapabilities? capabilities = null, string providerName = "In-memory broker") : IMoveTransport
{
    private static readonly AsyncLocal<List<Action>?> Transaction = new();

    public const MoveCapabilities All = MoveCapabilities.Move | MoveCapabilities.Transactions | MoveCapabilities.Sessions | MoveCapabilities.DeadLetter | MoveCapabilities.Topics;

    public InMemoryMoveBroker Broker { get; } = broker ?? throw new ArgumentNullException(nameof(broker));

    public string ProviderName { get; } = providerName;

    public string Namespace => Broker.Namespace;

    public MoveCapabilities Capabilities { get; } = capabilities ?? All;

    public int TransactionsCommitted { get; private set; }

    public Task<RightCheck> CheckReceiveAsync(MoveEndpoint source, CancellationToken cancellationToken) => Task.FromResult(Broker.CanReceive(source));

    public Task<RightCheck> CheckSendAsync(MoveEndpoint target, CancellationToken cancellationToken) => Task.FromResult(Broker.CanSend(target));

    public Task<bool> RequiresSessionsAsync(MoveEndpoint source, CancellationToken cancellationToken) => Task.FromResult(Broker.RequiresSessions(source));

    public Task<bool?> DuplicateDetectionAsync(MoveEndpoint target, CancellationToken cancellationToken) => Task.FromResult<bool?>(Broker.DuplicateDetection(target));

    public Task<IReadOnlyList<MoveMessageInfo>> PeekAsync(MoveEndpoint source, int max, CancellationToken cancellationToken) => Task.FromResult(Broker.Peek(source, max));

    public Task<IMoveReceiver> OpenReceiverAsync(MoveEndpoint source, CancellationToken cancellationToken)
    {
        if (Broker.RequiresSessions(source))
            throw new MoveSessionModeException(requiresSessions: true);
        return Task.FromResult<IMoveReceiver>(new Receiver(Broker, source, null));
    }

    public Task<IMoveReceiver?> AcceptNextSessionAsync(MoveEndpoint source, CancellationToken cancellationToken)
    {
        if (!Broker.RequiresSessions(source))
            throw new MoveSessionModeException(requiresSessions: false);
        var session = Broker.LockNextSession(source);
        return Task.FromResult<IMoveReceiver?>(session is null ? null : new Receiver(Broker, source, session));
    }

    public IMoveSender OpenSender(MoveEndpoint target) => new Sender(Broker, target);

    public async Task RunInTransactionAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Transaction.Value is not null)
            throw new InvalidOperationException("Transactions don't nest.");
        var actions = new List<Action>();
        Transaction.Value = actions;
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            Transaction.Value = null;
        }

        Broker.Commit(actions);
        TransactionsCommitted++;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Receiver(InMemoryMoveBroker broker, MoveEndpoint source, string? session) : IMoveReceiver
    {
        public string? SessionId { get; } = session;

        public Task<IReadOnlyList<LockedMessage>> ReceiveAsync(int max, TimeSpan wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(broker.Receive(this, source, SessionId, max));
        }

        public Task CompleteAsync(LockedMessage message, CancellationToken cancellationToken)
        {
            broker.Complete(this, source, message, Transaction.Value);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(LockedMessage message, CancellationToken cancellationToken)
        {
            broker.Abandon(this, source, message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            broker.ReleaseAll(this);
            if (SessionId is not null)
                broker.UnlockSession(source, SessionId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Sender(InMemoryMoveBroker broker, MoveEndpoint target) : IMoveSender
    {
        public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            broker.Send(target, message, Transaction.Value);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
