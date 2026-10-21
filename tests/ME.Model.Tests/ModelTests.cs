// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Model.Tests;

public class ModelTests
{
    [Fact]
    public void Extension_names_reject_underscores()
    {
        Assert.Throws<ArgumentException>(() => CloudEventsNames.RequireExtensionName("delivery_count"));
        CloudEventsNames.RequireExtensionName("deliverycount");
    }

    [Fact]
    public void Observed_envelope_uses_legal_extension_names()
    {
        var envelope = CloudEventEnvelope.Observed(
            "servicebus",
            "disputes/case-sync",
            new Position("sequence", "15"),
            SubQueue.DeadLetter,
            MessageState.Active,
            deliveryCount: 3,
            expiresAt: null,
            deadLetterReason: "CaseNotFound",
            traceParent: null,
            redactionProfile: "dispute",
            time: new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),
            subject: null,
            data: new Dictionary<string, string> { ["status"] = "open" });

        Assert.Equal("1.0", envelope.SpecVersion);
        Assert.Equal("15", envelope.Extensions["positionvalue"]);
        Assert.Equal("deadletter", envelope.Extensions["subqueue"]);
        Assert.Equal("3", envelope.Extensions["deliverycount"]);
        Assert.Equal("CaseNotFound", envelope.Extensions["dlqreason"]);
        Assert.All(envelope.Extensions.Keys, CloudEventsNames.RequireExtensionName);
    }

    [Fact]
    public void Observe_mode_allows_only_peek_and_log_read()
    {
        foreach (var operation in Enum.GetValues<BrokerOperation>())
        {
            var allowed = operation switch
            {
                BrokerOperation.Peek => true,
                BrokerOperation.LogRead => true,
                BrokerOperation.Receive => false,
                BrokerOperation.Complete => false,
                BrokerOperation.Abandon => false,
                BrokerOperation.DeadLetter => false,
                BrokerOperation.Defer => false,
                BrokerOperation.RenewLock => false,
                BrokerOperation.ReceiveDeferred => false,
                BrokerOperation.Schedule => false,
                BrokerOperation.CancelScheduled => false,
                BrokerOperation.Checkpoint => false,
                BrokerOperation.EpochAcquire => false,
                BrokerOperation.CreateEventSubscription => false,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown broker operation."),
            };

            if (allowed)
            {
                ObserveMode.Allow(operation);
            }
            else
            {
                var violation = Assert.Throws<ObserveModeViolationException>(() => ObserveMode.Allow(operation));
                Assert.Equal(operation, violation.Operation);
            }
        }
    }

    [Fact]
    public void Provider_matrix_records_the_azure_rows()
    {
        Assert.Equal(4, ProviderMatrix.Rows.Count);
        Assert.True(ProviderMatrix.ServiceBus.PeekSideEffectFree);
        Assert.True(ProviderMatrix.ServiceBus.AdapterInThisSkeleton);
        Assert.Contains("fromSequenceNumber", ProviderMatrix.ServiceBus.SafeReadMethod, StringComparison.Ordinal);
        Assert.True(ProviderMatrix.EventHubs.PeekSideEffectFree);
        Assert.Contains("cg-message-explorer", ProviderMatrix.EventHubs.SafeReadMethod, StringComparison.Ordinal);
        Assert.False(ProviderMatrix.EventGrid.PeekSideEffectFree);
        Assert.True(ProviderMatrix.EventGrid.WriteNeededToSetUp);
        Assert.False(ProviderMatrix.StorageQueues.AdapterInThisSkeleton);
        Assert.Equal("Storage Queue Data Reader", ProviderMatrix.StorageQueues.RequiredRole);
        Assert.Contains("32", ProviderMatrix.StorageQueues.SafeReadMethod, StringComparison.Ordinal);
    }
}
