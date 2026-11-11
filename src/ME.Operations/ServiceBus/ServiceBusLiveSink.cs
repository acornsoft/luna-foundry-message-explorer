// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Live;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Operations.ServiceBus;

/// <summary>
/// The live job runner's Service Bus sink. Send only: it never receives, peeks, completes or settles.
/// Entra sign-in only (DefaultAzureCredential without the interactive browser); there is no connection-string
/// path. The namespace must be on the local live allow-list before a client is created. Each message carries
/// the run's TTL, its correlation ID, and lfme.kind / lfme.generator / lfme.synthetic properties.
/// </summary>
public sealed class ServiceBusLiveSink : IMessageSink
{
    private readonly ServiceBusClient _client;
    private readonly Dictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    internal ServiceBusLiveSink(ServiceBusClient client, string fullyQualifiedNamespace)
    {
        _client = client;
        Name = fullyQualifiedNamespace;
    }

    public string Name { get; }

    /// <summary>Checks the allow-list, then opens a client for the namespace with Entra credentials.</summary>
    public static ServiceBusLiveSink Open(string fullyQualifiedOrName, NamespaceAllowList allowList)
    {
        ArgumentNullException.ThrowIfNull(allowList);
        allowList.Check(fullyQualifiedOrName);
        var host = NamespaceAllowList.FullyQualified(fullyQualifiedOrName);
        return new ServiceBusLiveSink(new ServiceBusClient(host, new DefaultAzureCredential(CredentialOptions()), ClientOptions()), host);
    }

    /// <summary>Entra only, never an interactive browser prompt.</summary>
    public static DefaultAzureCredentialOptions CredentialOptions() => new() { ExcludeInteractiveBrowserCredential = true };

    /// <summary>AMQP over WebSockets (port 443), so a send works where outbound port 5671 is blocked.</summary>
    public static ServiceBusClientOptions ClientOptions() => new() { TransportType = ServiceBusTransportType.AmqpWebSockets };

    public async Task SendAsync(LiveMessage message, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_senders.TryGetValue(message.Entity, out var sender))
            _senders[message.Entity] = sender = _client.CreateSender(message.Entity);
        var outgoing = new ServiceBusMessage(BinaryData.FromString(message.Body.ToJsonString()))
        {
            CorrelationId = message.CorrelationId,
            ContentType = "application/json",
            TimeToLive = timeToLive,
            Subject = message.Kind == MessageKind.Poison ? "poison" : "good",
        };
        outgoing.ApplicationProperties["lfme.kind"] = message.Kind == MessageKind.Poison ? "poison" : "good";
        outgoing.ApplicationProperties["lfme.generator"] = message.Generator;
        outgoing.ApplicationProperties["lfme.synthetic"] = true;
        await sender.SendMessageAsync(outgoing, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
            await sender.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
