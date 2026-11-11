// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using BrokerOperation = Acornsoft.MessageExplorer.Model.BrokerOperation;
using ObserveMode = Acornsoft.MessageExplorer.Model.ObserveMode;

namespace Acornsoft.MessageExplorer.Operations.ServiceBus;

/// <summary>
/// The quick view's peek for Azure Service Bus: the main entity, <c>$DeadLetterQueue</c> and
/// <c>$Transfer/$DeadLetterQueue</c>, from a sequence number. It lives in this assembly only because every
/// other assembly must not reference a Service Bus receiver at all. It peeks and nothing else: no receive,
/// lock, renew, defer or settle (a test reads its IL to hold it to that). A peek takes no lock.
/// </summary>
public sealed class ServiceBusMessageBrowser : IMessageBrowser
{
    private readonly Func<ServiceBusClient> _clientFactory;
    private readonly Dictionary<(string Path, PeekSource Source), ServiceBusReceiver> _receivers = new();
    private ServiceBusClient? _client;

    public ServiceBusMessageBrowser(Func<ServiceBusClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>From the same connection the read-only inventory used. Nothing connects until the first peek.</summary>
    public static ServiceBusMessageBrowser Open(BrokerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var plan = ServiceBusBrokerProvider.Plan(connection);
        if (plan.Mode == AuthMode.Entra)
        {
            var credential = connection.Credential as TokenCredential ?? new Azure.Identity.DefaultAzureCredential();
            return new ServiceBusMessageBrowser(() => new ServiceBusClient(plan.FullyQualifiedNamespace, credential));
        }

        var text = ServiceBusConnectionInput.Parse(connection.Secret?.Reveal()).ToConnectionString();
        return new ServiceBusMessageBrowser(() => new ServiceBusClient(text));
    }

    /// <summary>For an Entra account (a namespace found on the Accounts page).</summary>
    public static ServiceBusMessageBrowser Open(string fullyQualifiedNamespace, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        return new ServiceBusMessageBrowser(() => new ServiceBusClient(fullyQualifiedNamespace, credential));
    }

    public string ProviderName => "Azure Service Bus";

    public bool CanPeek(BrowseEntity entity, PeekSource source) =>
        entity is { Kind: EntityKind.Queue } or { Kind: EntityKind.Subscription, Topic: not null };

    public async Task<IReadOnlyList<PeekedFrame>> PeekAsync(BrowseEntity entity, PeekSource source, long fromSequenceNumber, int max, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ObserveMode.Allow(BrokerOperation.Peek);
        if (!CanPeek(entity, source))
            throw new BrokerException(BrokerErrorKind.NotFound, "A topic can't be peeked; peek one of its subscriptions.");
        if (max is < 1 or > ServiceBusObserveClient.MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(max), "A peek is 1 to 100 messages.");
        try
        {
            var receiver = ReceiverFor(entity, source);
            var messages = await receiver.PeekMessagesAsync(max, fromSequenceNumber, cancellationToken).ConfigureAwait(false);
            return messages.Select(m => Frame(m, source)).ToList();
        }
        catch (Exception exception) when (exception is ServiceBusException or UnauthorizedAccessException)
        {
            throw Map(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var receiver in _receivers.Values)
            await receiver.DisposeAsync().ConfigureAwait(false);
        _receivers.Clear();
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }

    private ServiceBusReceiver ReceiverFor(BrowseEntity entity, PeekSource source)
    {
        var key = (entity.Path, source);
        if (_receivers.TryGetValue(key, out var existing))
            return existing;
        _client ??= _clientFactory();
        var options = new ServiceBusReceiverOptions
        {
            // Peek never locks, whatever the mode; peek-lock is the mode that could never delete by mistake.
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            SubQueue = source switch
            {
                PeekSource.DeadLetter => SubQueue.DeadLetter,
                PeekSource.TransferDeadLetter => SubQueue.TransferDeadLetter,
                _ => SubQueue.None,
            },
        };
        var receiver = entity.Kind == EntityKind.Subscription
            ? _client.CreateReceiver(entity.Topic!, entity.Name, options)
            : _client.CreateReceiver(entity.Name, options);
        _receivers[key] = receiver;
        return receiver;
    }

    internal static PeekedFrame Frame(ServiceBusReceivedMessage m, PeekSource source)
    {
        var lane = source switch
        {
            PeekSource.DeadLetter => MessageLane.DeadLetter,
            PeekSource.TransferDeadLetter => MessageLane.TransferDeadLetter,
            _ => m.State == ServiceBusMessageState.Scheduled ? MessageLane.Scheduled : MessageLane.Active,
        };
        var body = m.Body.ToMemory();
        var properties = m.ApplicationProperties
            .Where(p => p.Key is not ("DeadLetterReason" or "DeadLetterErrorDescription"))
            .ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal);
        return new PeekedFrame(
            m.SequenceNumber,
            lane,
            m.MessageId,
            m.EnqueuedTime == default ? null : m.EnqueuedTime,
            m.State == ServiceBusMessageState.Scheduled && m.ScheduledEnqueueTime != default ? m.ScheduledEnqueueTime : null,
            m.DeliveryCount,
            m.Subject,
            m.ContentType,
            m.CorrelationId,
            m.SessionId,
            body.Length,
            m.DeadLetterReason,
            m.DeadLetterErrorDescription,
            m.State == ServiceBusMessageState.Deferred,
            BodyFields.From(body),
            properties);
    }

    private static BrokerException Map(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new BrokerException(BrokerErrorKind.Forbidden, "This login can't peek here. It needs Listen on a key, or the Azure Service Bus Data Receiver role.", exception),
        ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound } => new BrokerException(BrokerErrorKind.NotFound, "The queue or subscription wasn't found.", exception),
        ServiceBusException { Reason: ServiceBusFailureReason.ServiceBusy } => new BrokerException(BrokerErrorKind.Throttled, "Service Bus is busy (throttling). Try again shortly.", exception),
        ServiceBusException { Reason: ServiceBusFailureReason.ServiceTimeout } => new BrokerException(BrokerErrorKind.Timeout, "Service Bus didn't answer in time.", exception),
        ServiceBusException { Reason: ServiceBusFailureReason.ServiceCommunicationProblem } => new BrokerException(BrokerErrorKind.Unreachable, "Couldn't reach Service Bus.", exception),
        ServiceBusException sb => new BrokerException(BrokerErrorKind.Unknown, $"Service Bus returned {sb.Reason}.", exception),
        _ => new BrokerException(BrokerErrorKind.Unknown, "The peek failed.", exception),
    };
}
