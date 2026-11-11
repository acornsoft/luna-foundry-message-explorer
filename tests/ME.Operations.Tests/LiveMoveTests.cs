// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Operations.Tests;

/// <summary>
/// Opt-in, against a throwaway namespace you own. Set LFME_LIVE_MOVE=1, LFME_LIVE_MOVE_CONNECTION (a connection
/// string with Send and Listen; keep it in the environment, never in a file in the repo), and
/// LFME_LIVE_MOVE_SOURCE and LFME_LIVE_MOVE_TARGET (two empty queues). The test sends three marker
/// messages to the source, moves them, checks the target, and drains the target again.
/// </summary>
public class LiveMoveTests
{
    [LiveMoveFact]
    public async Task Moves_three_messages_between_two_live_queues_in_one_transaction_each()
    {
        var connectionString = Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_CONNECTION")!;
        var sourceName = Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_SOURCE")!;
        var targetName = Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_TARGET")!;
        var run = Guid.NewGuid().ToString("N")[..8];

        await using (var seed = new ServiceBusClient(connectionString))
        {
            await using var sender = seed.CreateSender(sourceName);
            for (var i = 1; i <= 3; i++)
                await sender.SendMessageAsync(new ServiceBusMessage($"{{\"n\":{i}}}") { MessageId = $"lfme-live-{run}-{i}", CorrelationId = run });
        }

        var transport = ServiceBusMoveTransport.Open(BrokerConnection.WithConnectionString("azure-service-bus", new RuntimeSecret(connectionString)));
        await using (transport)
        {
            var moveMode = new MoveModeSwitch();
            moveMode.TurnOn();
            var engine = new MoveEngine(moveMode, new MemoryMoveAuditLog());
            var plan = await engine.PrepareAsync(
                new MoveRequest(MoveEndpoint.Queue(transport.Namespace, sourceName), MoveEndpoint.Queue(transport.Namespace, targetName), MoveSelection.OldestFirst(3)),
                transport);
            Assert.True(plan.Atomic);
            plan.Confirm();
            var result = await engine.ExecuteAsync(plan);
            Assert.Equal(MoveOutcome.Completed, result.Outcome);
        }

        await using var check = new ServiceBusClient(connectionString);
        await using var receiver = check.CreateReceiver(targetName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
        var moved = await receiver.ReceiveMessagesAsync(10, TimeSpan.FromSeconds(10));
        Assert.Equal([$"lfme-live-{run}-1", $"lfme-live-{run}-2", $"lfme-live-{run}-3"], moved.Where(m => m.CorrelationId == run).Select(m => m.MessageId));
        Assert.All(moved, m => Assert.EndsWith("/" + sourceName, (string)m.ApplicationProperties[MessageCloner.MovedFromProperty], StringComparison.Ordinal));
    }
}

public sealed class LiveMoveFactAttribute : FactAttribute
{
    public LiveMoveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LFME_LIVE_MOVE") != "1"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_CONNECTION"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_SOURCE"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LFME_LIVE_MOVE_TARGET")))
        {
            Skip = "Live move test is opt-in: set LFME_LIVE_MOVE=1 and the LFME_LIVE_MOVE_* variables.";
        }
    }
}
