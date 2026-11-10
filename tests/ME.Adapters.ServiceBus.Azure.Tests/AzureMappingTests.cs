// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;
using Acornsoft.MessageExplorer.Scenarios.GslRegE;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure.Tests;

public class AzureMappingTests
{
    [Fact]
    public void Planned_message_maps_every_stamped_field()
    {
        var planned = new PlannedMessage("lfme-r1-001", "GSL-DSP-600001", "reg-e.deadlines.computed", "application/json", "{\"a\":1}",
            new Dictionary<string, object> { ["newAccount"] = true, ["investigationDueDate"] = "2026-11-24" }, "standard", ExpectedOutcome.Consumed, null);
        var message = AzureScenarioBroker.ToServiceBus(planned);
        Assert.Equal("lfme-r1-001", message.MessageId);
        Assert.Equal("GSL-DSP-600001", message.CorrelationId);
        Assert.Equal("reg-e.deadlines.computed", message.Subject);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal("{\"a\":1}", message.Body.ToString());
        Assert.Equal(true, message.ApplicationProperties["newAccount"]);
        Assert.Equal("2026-11-24", message.ApplicationProperties["investigationDueDate"]);
    }

    [Fact]
    public void Missing_correlation_id_stays_unset()
    {
        var planned = new PlannedMessage("m", null, "s", "application/json", "{}", new Dictionary<string, object>(), "x", ExpectedOutcome.DeadLettered, "MissingDisputeId");
        Assert.Null(AzureScenarioBroker.ToServiceBus(planned).CorrelationId);
    }

    [Fact]
    public void Received_dead_letter_maps_to_a_snapshot()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{\"x\":1}"),
            messageId: "m-9",
            correlationId: "",
            subject: "reg-e.deadlines.computed",
            deliveryCount: 2,
            sequenceNumber: 9,
            enqueuedTime: new DateTimeOffset(2026, 11, 10, 5, 0, 0, TimeSpan.Zero),
            properties: new Dictionary<string, object>
            {
                ["DeadLetterReason"] = "SchemaViolation",
                ["DeadLetterErrorDescription"] = "noticeDate must be a yyyy-MM-dd date",
                ["lfmeRunId"] = "r1",
            });
        var snapshot = AzureScenarioBroker.ToSnapshot(received);
        Assert.Equal(9, snapshot.SequenceNumber);
        Assert.Null(snapshot.CorrelationId);
        Assert.Equal("SchemaViolation", snapshot.DeadLetterReason);
        Assert.Equal("noticeDate must be a yyyy-MM-dd date", snapshot.DeadLetterDescription);
        Assert.Equal("r1", snapshot.RunId);
        Assert.Equal("{\"x\":1}", snapshot.Body);
    }

    [Fact]
    public void Address_derives_the_namespace_name() =>
        Assert.Equal("sbns-gsl-dev-eastus-001", new AzureQueueAddress("s", "rg", "sbns-gsl-dev-eastus-001.servicebus.windows.net", "q").NamespaceName);

    [Fact]
    public void Adapter_has_no_connection_string_constructor()
    {
        var constructors = typeof(AzureScenarioBroker).GetConstructors();
        Assert.All(constructors, c => Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(global::Azure.Core.TokenCredential)));
    }
}
