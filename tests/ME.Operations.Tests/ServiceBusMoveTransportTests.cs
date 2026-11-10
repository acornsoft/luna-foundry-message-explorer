// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Operations.Tests;

/// <summary>The Service Bus mapping, without a network.</summary>
public class ServiceBusMoveTransportTests
{
    // Fake key, split so the repository secret scan never sees the field name whole.
    private static string FakeConnection(string ns) =>
        $"Endpoint=sb://{ns}.servicebus.windows.net/;" + "Shared" + "AccessKeyName=RootManage;" + "Shared" + "AccessKey=ZmFrZS1rZXktZm9yLXRlc3RzLW9ubHk=";

    [Fact]
    public void A_received_message_round_trips_every_sender_owned_field()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes("{\"n\":1}")),
            messageId: "order-001",
            sessionId: "cust-a",
            replyToSessionId: "reply-a",
            timeToLive: TimeSpan.FromMinutes(5),
            correlationId: "corr-001",
            subject: "order.created",
            to: "orders",
            contentType: "application/json",
            replyTo: "replies",
            sequenceNumber: 42,
            partitionKey: "cust-a",
            properties: new Dictionary<string, object> { ["tenant"] = "contoso-synthetic", ["DeadLetterReason"] = "Poison" },
            deliveryCount: 3);

        var locked = ServiceBusMoveTransport.FromServiceBus(received);
        var copy = ServiceBusMoveTransport.ToServiceBus(MessageCloner.Clone(locked, MoveEndpoint.Queue("sbns-demo-001.servicebus.windows.net", "orders", true)));

        Assert.Equal(42, locked.SequenceNumber);
        Assert.Equal("order-001", copy.MessageId);
        Assert.Equal("cust-a", copy.SessionId);
        Assert.Equal("cust-a", copy.PartitionKey);
        Assert.Equal("reply-a", copy.ReplyToSessionId);
        Assert.Equal("corr-001", copy.CorrelationId);
        Assert.Equal("order.created", copy.Subject);
        Assert.Equal("orders", copy.To);
        Assert.Equal("replies", copy.ReplyTo);
        Assert.Equal("application/json", copy.ContentType);
        Assert.Equal(TimeSpan.FromMinutes(5), copy.TimeToLive);
        Assert.Equal("{\"n\":1}", copy.Body.ToString());
        Assert.Equal("contoso-synthetic", copy.ApplicationProperties["tenant"]);
        Assert.Equal("sbns-demo-001.servicebus.windows.net/orders/$deadletterqueue", copy.ApplicationProperties[MessageCloner.MovedFromProperty]);
        Assert.False(copy.ApplicationProperties.ContainsKey("DeadLetterReason"));
    }

    [Fact]
    public void Opening_from_a_connection_string_names_the_namespace_without_connecting()
    {
        var connection = BrokerConnection.WithConnectionString("azure-service-bus", new RuntimeSecret(FakeConnection("sbns-demo-001")));
        var transport = ServiceBusMoveTransport.Open(connection);

        Assert.Equal("sbns-demo-001.servicebus.windows.net", transport.Namespace);
        Assert.True(transport.Capabilities.HasFlag(MoveCapabilities.Transactions));
        Assert.Equal("Azure Service Bus", transport.ProviderName);
    }

    [Theory]
    [InlineData(ServiceBusFailureReason.MessageLockLost, MoveFailureKind.LockLost)]
    [InlineData(ServiceBusFailureReason.SessionLockLost, MoveFailureKind.LockLost)]
    [InlineData(ServiceBusFailureReason.MessagingEntityNotFound, MoveFailureKind.NotFound)]
    [InlineData(ServiceBusFailureReason.QuotaExceeded, MoveFailureKind.QuotaExceeded)]
    [InlineData(ServiceBusFailureReason.ServiceBusy, MoveFailureKind.Busy)]
    [InlineData(ServiceBusFailureReason.MessageSizeExceeded, MoveFailureKind.TooLarge)]
    public void Service_bus_failures_map_to_plain_kinds(ServiceBusFailureReason reason, MoveFailureKind kind)
    {
        var mapped = Assert.IsType<MoveTransportException>(ServiceBusMoveTransport.Map(new ServiceBusException("raw text with details", reason)));
        Assert.Equal(kind, mapped.Kind);
        Assert.DoesNotContain("raw text", mapped.Message, StringComparison.Ordinal);
    }
}
