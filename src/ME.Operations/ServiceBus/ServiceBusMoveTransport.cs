// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Transactions;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.Move;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Acornsoft.MessageExplorer.Operations.ServiceBus;

/// <summary>
/// Move mode for Azure Service Bus. Rights are probed without changing anything: a peek proves Listen on
/// the source, and opening a send link (creating an empty batch) proves Send on the target. The work client
/// turns on cross-entity transactions, and the source is always touched first, so the source is the
/// transaction's via entity and each send and complete commit together.
/// </summary>
public sealed class ServiceBusMoveTransport : IMoveTransport
{
    private readonly Func<ServiceBusClientOptions, ServiceBusClient> _clientFactory;
    private readonly Func<ServiceBusAdministrationClient>? _adminFactory;
    private ServiceBusClient? _probe;
    private ServiceBusClient? _work;

    public ServiceBusMoveTransport(string fullyQualifiedNamespace, Func<ServiceBusClientOptions, ServiceBusClient> clientFactory, Func<ServiceBusAdministrationClient>? adminFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        Namespace = fullyQualifiedNamespace;
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _adminFactory = adminFactory;
    }

    /// <summary>
    /// Opens a transport from the same connection the read-only inventory used: an Entra account, a namespace or
    /// entity key, a SAS token, or the emulator. Nothing connects until the first call.
    /// </summary>
    public static ServiceBusMoveTransport Open(BrokerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var plan = ServiceBusBrokerProvider.Plan(connection);
        var fqdn = plan.FullyQualifiedNamespace;
        if (plan.Mode == AuthMode.Entra)
        {
            var credential = connection.Credential as TokenCredential ?? new Azure.Identity.DefaultAzureCredential();
            return new ServiceBusMoveTransport(fqdn, options => new ServiceBusClient(fqdn, credential, options), () => new ServiceBusAdministrationClient(fqdn, credential));
        }

        var text = ServiceBusConnectionInput.Parse(connection.Secret?.Reveal()).ToConnectionString();
        Func<ServiceBusAdministrationClient>? admin = plan.EntityPath is null && plan.Flavor != ConnectionFlavor.SasToken
            ? () => new ServiceBusAdministrationClient(text)
            : null;
        return new ServiceBusMoveTransport(fqdn, options => new ServiceBusClient(text, options), admin);
    }

    /// <summary>Opens a transport for an Entra account (a namespace found on the Accounts page).</summary>
    public static ServiceBusMoveTransport Open(string fullyQualifiedNamespace, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new ServiceBusMoveTransport(
            fullyQualifiedNamespace,
            options => new ServiceBusClient(fullyQualifiedNamespace, credential, options),
            () => new ServiceBusAdministrationClient(fullyQualifiedNamespace, credential));
    }

    public string ProviderName => "Azure Service Bus";

    public string Namespace { get; }

    public MoveCapabilities Capabilities =>
        MoveCapabilities.Move | MoveCapabilities.Transactions | MoveCapabilities.Sessions | MoveCapabilities.DeadLetter | MoveCapabilities.Topics;

    private ServiceBusClient Probe => _probe ??= _clientFactory(new ServiceBusClientOptions());

    private ServiceBusClient Work => _work ??= _clientFactory(new ServiceBusClientOptions { EnableCrossEntityTransactions = true });

    public async Task<RightCheck> CheckReceiveAsync(MoveEndpoint source, CancellationToken cancellationToken)
    {
        try
        {
            await using var receiver = CreateReceiver(Probe, source, ServiceBusReceiveMode.PeekLock);
            await receiver.PeekMessageAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return RightCheck.Yes;
        }
        catch (UnauthorizedAccessException)
        {
            return RightCheck.No($"This login can't receive from {source.Describe()}. It needs Listen on a key, or the Azure Service Bus Data Receiver role.");
        }
        catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return RightCheck.No($"{source.Describe()} wasn't found.");
        }
    }

    public async Task<RightCheck> CheckSendAsync(MoveEndpoint target, CancellationToken cancellationToken)
    {
        try
        {
            await using var sender = Probe.CreateSender(target.EntityPath);
            using var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
            return RightCheck.Yes;
        }
        catch (UnauthorizedAccessException)
        {
            return RightCheck.No($"This login can't send to {target.Describe()}. It needs Send on a key, or the Azure Service Bus Data Sender role.");
        }
        catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return RightCheck.No($"{target.Describe()} wasn't found.");
        }
    }

    public async Task<bool> RequiresSessionsAsync(MoveEndpoint source, CancellationToken cancellationToken)
    {
        if (source.DeadLetter)
            return false;
        if (_adminFactory is not null)
        {
            try
            {
                var admin = _adminFactory();
                return source.Kind == MoveEntityKind.Subscription
                    ? (await admin.GetSubscriptionAsync(source.Topic, source.Name, cancellationToken).ConfigureAwait(false)).Value.RequiresSession
                    : (await admin.GetQueueAsync(source.Name, cancellationToken).ConfigureAwait(false)).Value.RequiresSession;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ServiceBusException or Azure.RequestFailedException)
            {
                // Reading entity settings needs Manage. Fall back to what a peek shows; the engine corrects itself if wrong.
            }
        }

        var peeked = await PeekAsync(source, 10, cancellationToken).ConfigureAwait(false);
        return peeked.Any(m => m.SessionId is not null);
    }

    public async Task<bool?> DuplicateDetectionAsync(MoveEndpoint target, CancellationToken cancellationToken)
    {
        if (_adminFactory is null)
            return null;
        try
        {
            var admin = _adminFactory();
            return target.Kind == MoveEntityKind.Topic
                ? (await admin.GetTopicAsync(target.Name, cancellationToken).ConfigureAwait(false)).Value.RequiresDuplicateDetection
                : (await admin.GetQueueAsync(target.Name, cancellationToken).ConfigureAwait(false)).Value.RequiresDuplicateDetection;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ServiceBusException or Azure.RequestFailedException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MoveMessageInfo>> PeekAsync(MoveEndpoint source, int max, CancellationToken cancellationToken)
    {
        try
        {
            await using var receiver = CreateReceiver(Probe, source, ServiceBusReceiveMode.PeekLock);
            // One peek call can return fewer than asked even when more are waiting, so keep going until the
            // count is reached or a call comes back empty.
            var want = Math.Clamp(max, 1, MoveSelection.MaxBatch);
            var messages = new List<ServiceBusReceivedMessage>();
            long? from = null;
            while (messages.Count < want)
            {
                var batch = await receiver.PeekMessagesAsync(want - messages.Count, from, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                    break;
                messages.AddRange(batch);
                from = batch.Max(m => m.SequenceNumber) + 1;
            }

            return messages.OrderBy(m => m.SequenceNumber)
                .Select(m => new MoveMessageInfo(m.SequenceNumber, m.MessageId, m.SessionId, m.EnqueuedTime, m.DeliveryCount, m.DeadLetterReason))
                .ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Map(exception);
        }
    }

    public Task<IMoveReceiver> OpenReceiverAsync(MoveEndpoint source, CancellationToken cancellationToken) =>
        Task.FromResult<IMoveReceiver>(new Receiver(CreateReceiver(Work, source, ServiceBusReceiveMode.PeekLock), null));

    public async Task<IMoveReceiver?> AcceptNextSessionAsync(MoveEndpoint source, CancellationToken cancellationToken)
    {
        try
        {
            var options = new ServiceBusSessionReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock };
            var receiver = source.Kind == MoveEntityKind.Subscription
                ? await Work.AcceptNextSessionAsync(source.Topic, source.Name, options, cancellationToken).ConfigureAwait(false)
                : await Work.AcceptNextSessionAsync(source.Name, options, cancellationToken).ConfigureAwait(false);
            return new Receiver(receiver, receiver.SessionId);
        }
        catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            throw new MoveSessionModeException(requiresSessions: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Map(exception);
        }
    }

    public IMoveSender OpenSender(MoveEndpoint target) => new Sender(Work.CreateSender(target.EntityPath));

    public async Task RunInTransactionAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await work().ConfigureAwait(false);
            scope.Complete();
        }
        catch (TransactionAbortedException exception)
        {
            throw new MoveTransportException(MoveFailureKind.Other, "The transaction was rolled back, so the message stayed in the source.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_probe is not null)
            await _probe.DisposeAsync().ConfigureAwait(false);
        if (_work is not null)
            await _work.DisposeAsync().ConfigureAwait(false);
    }

    private static ServiceBusReceiver CreateReceiver(ServiceBusClient client, MoveEndpoint source, ServiceBusReceiveMode mode)
    {
        var options = new ServiceBusReceiverOptions
        {
            ReceiveMode = mode,
            SubQueue = source.DeadLetter ? Azure.Messaging.ServiceBus.SubQueue.DeadLetter : Azure.Messaging.ServiceBus.SubQueue.None,
        };
        return source.Kind == MoveEntityKind.Subscription
            ? client.CreateReceiver(source.Topic, source.Name, options)
            : client.CreateReceiver(source.Name, options);
    }

    internal static Exception Map(Exception exception) => exception switch
    {
        MoveTransportException or MoveSessionModeException => exception,
        UnauthorizedAccessException => new MoveTransportException(MoveFailureKind.Unauthorized, "Service Bus said this login isn't allowed to do that.", exception),
        ServiceBusException sb => sb.Reason switch
        {
            ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost =>
                new MoveTransportException(MoveFailureKind.LockLost, "The lock on a message ran out before the move finished.", exception),
            ServiceBusFailureReason.MessagingEntityNotFound => new MoveTransportException(MoveFailureKind.NotFound, "The queue, topic or subscription wasn't found.", exception),
            ServiceBusFailureReason.MessageSizeExceeded => new MoveTransportException(MoveFailureKind.TooLarge, "A message is larger than the target allows.", exception),
            ServiceBusFailureReason.QuotaExceeded => new MoveTransportException(MoveFailureKind.QuotaExceeded, "The target is full (quota exceeded).", exception),
            ServiceBusFailureReason.ServiceBusy => new MoveTransportException(MoveFailureKind.Busy, "Service Bus is busy (throttling). Try again shortly.", exception),
            ServiceBusFailureReason.ServiceCommunicationProblem => new MoveTransportException(MoveFailureKind.Network, "Couldn't reach Service Bus.", exception),
            _ => new MoveTransportException(MoveFailureKind.Other, $"Service Bus returned {sb.Reason}.", exception),
        },
        _ => exception,
    };

    internal static ServiceBusMessage ToServiceBus(OutgoingMessage message)
    {
        var outgoing = new ServiceBusMessage(new BinaryData(message.Body))
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            Subject = message.Subject,
            ContentType = message.ContentType,
            To = message.To,
            ReplyTo = message.ReplyTo,
        };
        if (message.SessionId is not null)
            outgoing.SessionId = message.SessionId;
        if (message.ReplyToSessionId is not null)
            outgoing.ReplyToSessionId = message.ReplyToSessionId;
        if (message.PartitionKey is not null && (message.SessionId is null || message.PartitionKey == message.SessionId))
            outgoing.PartitionKey = message.PartitionKey;
        if (message.TimeToLive is { } ttl && ttl < TimeSpan.MaxValue && ttl > TimeSpan.Zero)
            outgoing.TimeToLive = ttl;
        foreach (var (key, value) in message.ApplicationProperties)
            outgoing.ApplicationProperties[key] = value;
        return outgoing;
    }

    internal static LockedMessage FromServiceBus(ServiceBusReceivedMessage m) => new(
        m.SequenceNumber, m.MessageId, m.SessionId, m.CorrelationId, m.Subject, m.ContentType, m.To, m.ReplyTo, m.ReplyToSessionId,
        m.PartitionKey, m.TimeToLive, m.EnqueuedTime, m.DeliveryCount, m.DeadLetterReason,
        m.ApplicationProperties.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal), m.Body.ToMemory(), m);

    private sealed class Receiver(ServiceBusReceiver receiver, string? sessionId) : IMoveReceiver
    {
        public string? SessionId { get; } = sessionId;

        public async Task<IReadOnlyList<LockedMessage>> ReceiveAsync(int max, TimeSpan wait, CancellationToken cancellationToken)
        {
            try
            {
                var messages = await receiver.ReceiveMessagesAsync(max, wait, cancellationToken).ConfigureAwait(false);
                return messages.OrderBy(m => m.SequenceNumber).Select(FromServiceBus).ToList();
            }
            catch (InvalidOperationException) when (SessionId is null)
            {
                throw new MoveSessionModeException(requiresSessions: true);
            }
            catch (ServiceBusException exception) when (SessionId is null && exception.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                && exception.Reason == ServiceBusFailureReason.GeneralError)
            {
                throw new MoveSessionModeException(requiresSessions: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Map(exception);
            }
        }

        public async Task CompleteAsync(LockedMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await receiver.CompleteMessageAsync((ServiceBusReceivedMessage)message.Native!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Map(exception);
            }
        }

        public async Task AbandonAsync(LockedMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await receiver.AbandonMessageAsync((ServiceBusReceivedMessage)message.Native!, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Map(exception);
            }
        }

        public ValueTask DisposeAsync() => receiver.DisposeAsync();
    }

    private sealed class Sender(ServiceBusSender sender) : IMoveSender
    {
        public async Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await sender.SendMessageAsync(ToServiceBus(message), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Map(exception);
            }
        }

        public ValueTask DisposeAsync() => sender.DisposeAsync();
    }
}
