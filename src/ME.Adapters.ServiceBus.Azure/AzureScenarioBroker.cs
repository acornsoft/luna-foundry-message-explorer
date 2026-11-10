// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Scenarios.GslRegE;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;

/// <summary>Where the broker lives. Control-plane coordinates come from the command line, never from a scenario file.</summary>
public sealed record AzureQueueAddress(string SubscriptionId, string ResourceGroup, string FullyQualifiedNamespace, string Queue)
{
    public string NamespaceName => FullyQualifiedNamespace.Split('.')[0];
}

/// <summary>
/// Live <see cref="IScenarioBroker"/> on Azure Service Bus with a Microsoft Entra token credential
/// (DefaultAzureCredential in the console runner). No SAS keys or connection strings.
/// Data plane: send, peek, and dead-letter receive need Azure Service Bus Data Sender and Data Receiver.
/// Control plane: tags, disableLocalAuth, and queue counts are read through Azure Resource Manager (Reader).
/// AMQP over WebSockets (port 443) by default so it works behind firewalls that block 5671.
/// </summary>
public sealed class AzureScenarioBroker : IScenarioBroker, IAsyncDisposable
{
    private readonly AzureQueueAddress _address;
    private readonly ArmClient _arm;
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public AzureScenarioBroker(AzureQueueAddress address, TokenCredential credential, bool webSockets = true)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        ArgumentNullException.ThrowIfNull(credential);
        _arm = new ArmClient(credential, address.SubscriptionId);
        _client = new ServiceBusClient(
            address.FullyQualifiedNamespace,
            credential,
            new ServiceBusClientOptions { TransportType = webSockets ? ServiceBusTransportType.AmqpWebSockets : ServiceBusTransportType.AmqpTcp });
        _sender = _client.CreateSender(address.Queue);
    }

    public async Task<NamespaceFacts> DescribeNamespaceAsync(CancellationToken cancellationToken)
    {
        var ns = await NamespaceAsync(cancellationToken).ConfigureAwait(false);
        var tags = new Dictionary<string, string>(ns.Data.Tags, StringComparer.Ordinal);
        return new NamespaceFacts(ns.Data.Name, _address.SubscriptionId, tags, ns.Data.DisableLocalAuth == true);
    }

    public async Task<QueueCounts> GetCountsAsync(CancellationToken cancellationToken)
    {
        var ns = await NamespaceAsync(cancellationToken).ConfigureAwait(false);
        var queue = (await ns.GetServiceBusQueueAsync(_address.Queue, cancellationToken).ConfigureAwait(false)).Value;
        var details = queue.Data.CountDetails;
        return new QueueCounts(details?.ActiveMessageCount ?? 0, details?.DeadLetterMessageCount ?? 0);
    }

    public Task SendAsync(PlannedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _sender.SendMessageAsync(ToServiceBus(message), cancellationToken);
    }

    public async Task<IReadOnlyList<DeadLetterSnapshot>> PeekDeadLettersAsync(int max, CancellationToken cancellationToken)
    {
        await using var receiver = _client.CreateReceiver(_address.Queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var result = new List<DeadLetterSnapshot>();
        long from = 0;
        while (result.Count < max)
        {
            var page = await receiver.PeekMessagesAsync(Math.Min(100, max - result.Count), from, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                break;
            result.AddRange(page.Select(ToSnapshot));
            from = page[^1].SequenceNumber + 1;
        }

        return result;
    }

    public async Task<IReadOnlyList<ResubmitOutcome>> ResubmitDeadLettersAsync(
        Func<DeadLetterSnapshot, PlannedMessage?> fix,
        int max,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fix);
        await using var receiver = _client.CreateReceiver(
            _address.Queue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var locked = new List<ServiceBusReceivedMessage>();
        while (locked.Count < max)
        {
            var batch = await receiver.ReceiveMessagesAsync(Math.Min(50, max - locked.Count), TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            locked.AddRange(batch);
        }

        var outcomes = new List<ResubmitOutcome>();
        var untouched = new List<ServiceBusReceivedMessage>();
        foreach (var received in locked)
        {
            var snapshot = ToSnapshot(received);
            var replacement = fix(snapshot);
            if (replacement is null)
            {
                untouched.Add(received);
                outcomes.Add(new ResubmitOutcome(received.SequenceNumber, received.MessageId, null, false, "not selected or not fixable"));
                continue;
            }

            // Send first, then complete: a crash in between leaves a duplicate, never a loss.
            await _sender.SendMessageAsync(ToServiceBus(replacement), cancellationToken).ConfigureAwait(false);
            await receiver.CompleteMessageAsync(received, cancellationToken).ConfigureAwait(false);
            var note = replacement.ApplicationProperties.TryGetValue(RegEContract.FixProperty, out var why) ? why?.ToString() ?? "" : "";
            outcomes.Add(new ResubmitOutcome(received.SequenceNumber, received.MessageId, replacement.MessageId, true, note));
        }

        foreach (var received in untouched)
            await receiver.AbandonMessageAsync(received, cancellationToken: cancellationToken).ConfigureAwait(false);

        return outcomes;
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    internal static ServiceBusMessage ToServiceBus(PlannedMessage message)
    {
        var result = new ServiceBusMessage(BinaryData.FromString(message.Body))
        {
            MessageId = message.MessageId,
            Subject = message.Subject,
            ContentType = message.ContentType,
        };
        if (message.CorrelationId is not null)
            result.CorrelationId = message.CorrelationId;
        foreach (var pair in message.ApplicationProperties)
            result.ApplicationProperties[pair.Key] = pair.Value;
        return result;
    }

    internal static DeadLetterSnapshot ToSnapshot(ServiceBusReceivedMessage message) =>
        new(
            message.SequenceNumber,
            message.MessageId,
            string.IsNullOrEmpty(message.CorrelationId) ? null : message.CorrelationId,
            message.Subject,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.DeliveryCount,
            message.EnqueuedTime,
            message.Body.ToString(),
            new Dictionary<string, object>(message.ApplicationProperties, StringComparer.Ordinal));

    private async Task<ServiceBusNamespaceResource> NamespaceAsync(CancellationToken cancellationToken)
    {
        var id = ServiceBusNamespaceResource.CreateResourceIdentifier(_address.SubscriptionId, _address.ResourceGroup, _address.NamespaceName);
        var ns = _arm.GetServiceBusNamespaceResource(id);
        return (await ns.GetAsync(cancellationToken).ConfigureAwait(false)).Value;
    }
}
