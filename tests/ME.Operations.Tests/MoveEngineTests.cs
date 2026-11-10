// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.Tests;

public class MoveEngineTests
{
    [Fact]
    public void Move_mode_starts_off()
    {
        var moveMode = new MoveModeSwitch();
        Assert.False(moveMode.IsOn);
        Assert.Throws<MoveRefusedException>(moveMode.EnsureOn);
    }

    [Fact]
    public async Task Prepare_refuses_while_move_mode_is_off()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(2);
        lab.Switch.TurnOff();

        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(2)), lab.Transport));
        Assert.Contains("Move mode is off", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, lab.Broker.Count(lab.OrdersDlq));
    }

    [Fact]
    public async Task Execute_refuses_an_unconfirmed_plan_and_a_plan_after_move_mode_turns_off()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(2);
        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(2)), lab.Transport);

        await Assert.ThrowsAsync<MoveRefusedException>(() => lab.Engine.ExecuteAsync(plan));
        plan.Confirm();
        lab.Switch.TurnOff();
        await Assert.ThrowsAsync<MoveRefusedException>(() => lab.Engine.ExecuteAsync(plan));

        Assert.Equal(2, lab.Broker.Count(lab.OrdersDlq));
        Assert.Empty(lab.Audit.Entries);
    }

    [Fact]
    public async Task Dead_letters_go_back_to_their_queue_oldest_first_with_ids_session_correlation_and_properties()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(5);

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(3));

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Moved);
        var moved = lab.Broker.Messages(lab.Orders);
        Assert.Equal(["order-001", "order-002", "order-003"], moved.Select(m => m.Message.MessageId));
        Assert.Equal(["corr-001", "corr-002", "corr-003"], moved.Select(m => m.Message.CorrelationId));
        Assert.True(moved.Select(m => m.SequenceNumber).SequenceEqual(moved.Select(m => m.SequenceNumber).Order()));
        var first = moved[0].Message;
        Assert.Equal("contoso-synthetic", first.ApplicationProperties["tenant"]);
        Assert.Equal(1, first.ApplicationProperties["attempt"]);
        Assert.Equal($"{Lab.Ns}/orders/$deadletterqueue", first.ApplicationProperties[MessageCloner.MovedFromProperty]);
        Assert.False(first.ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.False(first.ApplicationProperties.ContainsKey("DeadLetterErrorDescription"));
        Assert.Equal("{\"secretBody\":\"BODY-1\"}", Encoding.UTF8.GetString(first.Body.Span));
        Assert.Equal(["order-004", "order-005"], lab.Broker.Messages(lab.OrdersDlq).Select(m => m.Message.MessageId));
        Assert.All(lab.Broker.Messages(lab.OrdersDlq), m => Assert.False(m.Locked));
        Assert.Equal(3, lab.Transport.TransactionsCommitted);
    }

    [Fact]
    public async Task Queue_to_queue_and_subscription_to_queue_move()
    {
        var lab = new Lab();
        lab.Broker.Seed(lab.Orders, InMemoryMoveBroker.Message("o-1"));
        lab.Broker.Seed(lab.Billing, InMemoryMoveBroker.Message("b-1"));

        Assert.Equal(MoveOutcome.Completed, (await lab.MoveAsync(lab.Orders, lab.Payments, MoveSelection.OldestFirst(1))).Outcome);
        Assert.Equal(MoveOutcome.Completed, (await lab.MoveAsync(lab.Billing, lab.Payments, MoveSelection.OldestFirst(1))).Outcome);

        Assert.Equal(["o-1", "b-1"], lab.Broker.Messages(lab.Payments).Select(m => m.Message.MessageId));
        Assert.Equal(0, lab.Broker.Count(lab.Orders));
        Assert.Equal(0, lab.Broker.Count(lab.Billing));
        Assert.Equal($"{Lab.Ns}/order-events/subscriptions/billing", lab.Broker.Messages(lab.Payments)[1].Message.ApplicationProperties[MessageCloner.MovedFromProperty]);
    }

    [Fact]
    public async Task Subscription_dead_letters_go_back_to_the_topic_and_the_plan_warns_about_fan_out()
    {
        var lab = new Lab();
        lab.Broker.Seed(lab.BillingDlq, InMemoryMoveBroker.Message("evt-1"), "Poison");

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.BillingDlq, lab.Topic, MoveSelection.OldestFirst(1)), lab.Transport);
        Assert.Contains(plan.Warnings, w => w.Contains("Every subscription of topic order-events gets a copy", StringComparison.Ordinal));
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(["evt-1"], lab.Broker.Messages(lab.Billing).Select(m => m.Message.MessageId));
        Assert.Equal(["evt-1"], lab.Broker.Messages(lab.Audit_).Select(m => m.Message.MessageId));
        Assert.Equal(0, lab.Broker.Count(lab.BillingDlq));
    }

    [Fact]
    public async Task Picked_messages_move_in_order_and_the_others_are_released_untouched()
    {
        var lab = new Lab();
        var sequences = lab.SeedDeadLetters(6);
        var picks = new[] { sequences[4], sequences[1], sequences[3] };

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.Of(picks)), lab.Transport);
        Assert.Contains(plan.Warnings, w => w.Contains("Messages ahead of your picks are locked", StringComparison.Ordinal));
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(["order-002", "order-004", "order-005"], lab.Broker.Messages(lab.Orders).Select(m => m.Message.MessageId));
        var left = lab.Broker.Messages(lab.OrdersDlq);
        Assert.Equal(["order-001", "order-003", "order-006"], left.Select(m => m.Message.MessageId));
        Assert.All(left, m => Assert.False(m.Locked));
    }

    [Fact]
    public async Task Picking_the_oldest_messages_has_no_lock_warning()
    {
        var lab = new Lab();
        var sequences = lab.SeedDeadLetters(4);

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.Of(sequences.Take(2))), lab.Transport);

        Assert.DoesNotContain(plan.Warnings, w => w.Contains("ahead of your picks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "at least one")]
    [InlineData(101, "at most 100")]
    public async Task Batch_size_is_one_to_one_hundred(int count, string expected)
    {
        var lab = new Lab();
        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(count)), lab.Transport));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hundred_picked_is_allowed_and_a_hundred_and_one_is_not()
    {
        var lab = new Lab();
        var sequences = lab.SeedDeadLetters(101);

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.Of(sequences.Take(100))), lab.Transport);
        await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.Of(sequences)), lab.Transport));

        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);
        Assert.Equal(100, result.Moved);
        Assert.Equal(["order-101"], lab.Broker.Messages(lab.OrdersDlq).Select(m => m.Message.MessageId));
    }

    [Theory]
    [InlineData(false, true, "can't receive")]
    [InlineData(true, false, "can't send")]
    public async Task Missing_send_or_receive_rights_refuse_before_any_lock(bool canReceive, bool canSend, string expected)
    {
        var lab = new Lab();
        lab.SeedDeadLetters(2);
        var transport = new LyingTransport(lab.Transport, canReceive: canReceive, canSend: canSend);

        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(2)), transport));

        Assert.Contains("needs both receive rights on the source and send rights on the target", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.All(lab.Broker.Messages(lab.OrdersDlq), m => Assert.Equal(1, m.DeliveryCount));
    }

    [Fact]
    public async Task Broker_level_rights_come_from_the_entity()
    {
        var broker = new InMemoryMoveBroker("sbns-demo-001").AddQueue("orders", canReceive: false).AddQueue("payments");
        var lab = new Lab();
        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() => lab.Engine.PrepareAsync(
            new MoveRequest(MoveEndpoint.Queue("sbns-demo-001", "orders"), MoveEndpoint.Queue("sbns-demo-001", "payments"), MoveSelection.OldestFirst(1)),
            new InMemoryMoveTransport(broker)));
        Assert.Contains("can't receive from queue orders", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MoveCapabilities.None, "can't move messages")]
    [InlineData(MoveCapabilities.Move | MoveCapabilities.Topics, "has no dead-letter queues to move from")]
    public async Task A_provider_without_the_capability_refuses_plainly(MoveCapabilities capabilities, string expected)
    {
        var lab = new Lab();
        var transport = new InMemoryMoveTransport(lab.Broker, capabilities, "Synthetic log broker");

        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(1)), transport));

        Assert.Equal($"Synthetic log broker {expected}.", refused.Message);
    }

    [Fact]
    public async Task A_provider_without_topics_refuses_a_topic_target()
    {
        var lab = new Lab();
        var transport = new InMemoryMoveTransport(lab.Broker, MoveCapabilities.Move | MoveCapabilities.DeadLetter, "Queue-only broker");
        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.BillingDlq, lab.Topic, MoveSelection.OldestFirst(1)), transport));
        Assert.Equal("Queue-only broker has no topics to move to.", refused.Message);
    }

    [Fact]
    public async Task Provider_refusals_are_plain()
    {
        Assert.Contains("append-only", MoveProviders.Refusal("kafka"), StringComparison.Ordinal);
        Assert.Contains("doesn't store messages", MoveProviders.Refusal("azure-event-grid"), StringComparison.Ordinal);
        Assert.Contains("isn't built yet", MoveProviders.Refusal("aws-sqs-sns"), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_send_stops_the_move_and_releases_the_rest()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(5);
        lab.Broker.Fault = (operation, count) => operation == "send" && count == 3 ? new MoveTransportException(MoveFailureKind.QuotaExceeded, "The target is full (quota exceeded).") : null;

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(5));

        Assert.Equal(MoveOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.Moved);
        Assert.Contains("Stopped after 2 of 5: The target is full (quota exceeded).", result.Message, StringComparison.Ordinal);
        Assert.Equal(["order-001", "order-002"], lab.Broker.Messages(lab.Orders).Select(m => m.Message.MessageId));
        var left = lab.Broker.Messages(lab.OrdersDlq);
        Assert.Equal(["order-003", "order-004", "order-005"], left.Select(m => m.Message.MessageId));
        Assert.All(left, m => Assert.False(m.Locked));
        Assert.Null(result.PossibleDuplicateMessageId);
        Assert.Equal("Failed", lab.Audit.Entries[^1].Outcome);
    }

    [Fact]
    public async Task A_failed_commit_leaves_the_message_in_the_source_and_nothing_in_the_target()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(3);
        lab.Broker.Fault = (operation, count) => operation == "commit" && count == 2 ? new MoveTransportException(MoveFailureKind.Other, "The transaction was rolled back, so the message stayed in the source.") : null;

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(3));

        Assert.Equal(MoveOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.Moved);
        Assert.Equal(["order-001"], lab.Broker.Messages(lab.Orders).Select(m => m.Message.MessageId));
        Assert.Equal(["order-002", "order-003"], lab.Broker.Messages(lab.OrdersDlq).Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task Across_namespaces_the_move_is_at_least_once_and_says_so()
    {
        var lab = new Lab();
        lab.Broker.Seed(lab.Orders, InMemoryMoveBroker.Message("o-1"));
        var other = new InMemoryMoveBroker("sbns-demo-002.servicebus.windows.net").AddQueue("orders", duplicateDetection: false);
        var otherTransport = new InMemoryMoveTransport(other);
        var target = MoveEndpoint.Queue(other.Namespace, "orders");

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.Orders, target, MoveSelection.OldestFirst(1)), lab.Transport, otherTransport);

        Assert.False(plan.Atomic);
        Assert.Equal("at-least-once", plan.ModeName);
        Assert.Contains("at-least-once", plan.HowText, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate detection is off for the target", StringComparison.Ordinal));
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);
        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(0, lab.Transport.TransactionsCommitted);
        Assert.Equal(["o-1"], other.Messages(target).Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task Across_namespaces_a_failed_complete_after_a_send_reports_the_possible_duplicate()
    {
        var lab = new Lab();
        lab.Broker.Seed(lab.Orders, InMemoryMoveBroker.Message("o-1"));
        lab.Broker.Seed(lab.Orders, InMemoryMoveBroker.Message("o-2"));
        var other = new InMemoryMoveBroker("sbns-demo-002.servicebus.windows.net").AddQueue("orders", duplicateDetection: true);
        var target = MoveEndpoint.Queue(other.Namespace, "orders");
        lab.Broker.Fault = (operation, count) => operation == "complete" && count == 1 ? new MoveTransportException(MoveFailureKind.LockLost, "The lock on a message ran out before the move finished.") : null;

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.Orders, target, MoveSelection.OldestFirst(2)), lab.Transport, new InMemoryMoveTransport(other));
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate detection is on for the target", StringComparison.Ordinal));
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(MoveOutcome.Failed, result.Outcome);
        Assert.Equal("o-1", result.PossibleDuplicateMessageId);
        Assert.Contains("may have a duplicate", result.Message, StringComparison.Ordinal);
        Assert.Equal(["o-1"], other.Messages(target).Select(m => m.Message.MessageId));
        Assert.Equal(["o-1", "o-2"], lab.Broker.Messages(lab.Orders).Select(m => m.Message.MessageId));

        // Run again: duplicate detection on the target drops the repeat of o-1.
        lab.Broker.Fault = null;
        var again = await lab.MoveAsync(lab.Orders, target, MoveSelection.OldestFirst(2), new InMemoryMoveTransport(other));
        Assert.Equal(MoveOutcome.Completed, again.Outcome);
        Assert.Equal(["o-1", "o-2"], other.Messages(target).Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task Dead_letter_to_parent_with_duplicate_detection_warns()
    {
        var broker = new InMemoryMoveBroker(Lab.Ns).AddQueue("orders", duplicateDetection: true);
        var lab = new Lab();
        broker.Seed(lab.OrdersDlq, InMemoryMoveBroker.Message("o-1"), "Poison");
        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(1)), new InMemoryMoveTransport(broker));
        Assert.Contains(plan.Warnings, w => w.Contains("detection window", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sessions_move_one_session_at_a_time_in_order()
    {
        var lab = new Lab();
        foreach (var (id, session) in new[] { ("a1", "cust-a"), ("b1", "cust-b"), ("a2", "cust-a"), ("c1", "cust-c"), ("b2", "cust-b"), ("a3", "cust-a") })
            lab.Broker.Seed(lab.SessionQueue, InMemoryMoveBroker.Message(id, session));

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.SessionQueue, lab.SessionTarget, MoveSelection.OldestFirst(6)), lab.Transport);
        Assert.True(plan.Sessions);
        Assert.Contains("one session at a time", plan.OrderText, StringComparison.Ordinal);
        plan.Confirm();
        var sessionsSeen = new List<string?>();
        var result = await lab.Engine.ExecuteAsync(plan, new InlineProgress(p => sessionsSeen.Add(p.SessionId)));

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        var moved = lab.Broker.Messages(lab.SessionTarget);
        Assert.Equal(["a1", "a2", "a3", "b1", "b2", "c1"], moved.Select(m => m.Message.MessageId));
        Assert.All(moved, m => Assert.Equal(m.Message.MessageId![0], m.Message.SessionId![^1]));
        Assert.Equal(["cust-a", "cust-a", "cust-a", "cust-b", "cust-b", "cust-c"], sessionsSeen);
        foreach (var session in new[] { "cust-a", "cust-b", "cust-c" })
        {
            var inSession = moved.Where(m => m.Message.SessionId == session).Select(m => m.SequenceNumber).ToList();
            Assert.Equal(inSession.Order(), inSession);
        }
    }

    [Fact]
    public async Task Dead_letters_of_a_session_queue_keep_their_sessions_and_relative_order()
    {
        var lab = new Lab();
        var dlq = MoveEndpoint.Queue(lab.NsName, "orders-by-customer", deadLetter: true);
        foreach (var (id, session) in new[] { ("a1", "cust-a"), ("b1", "cust-b"), ("a2", "cust-a") })
            lab.Broker.Seed(dlq, InMemoryMoveBroker.Message(id, session), "Poison");

        var result = await lab.MoveAsync(dlq, lab.SessionQueue, MoveSelection.OldestFirst(3));

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(["a1", "b1", "a2"], lab.Broker.Messages(lab.SessionQueue).Select(m => m.Message.MessageId));
        Assert.Equal(["cust-a", "cust-b", "cust-a"], lab.Broker.Messages(lab.SessionQueue).Select(m => m.Message.SessionId));
    }

    [Fact]
    public async Task When_the_session_guess_is_wrong_the_engine_switches_mode_once()
    {
        var lab = new Lab();
        lab.Broker.Seed(lab.SessionQueue, InMemoryMoveBroker.Message("a1", "cust-a"));
        var lying = new LyingTransport(lab.Transport, requiresSessions: false);

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.SessionQueue, lab.SessionTarget, MoveSelection.OldestFirst(1)), lying);
        Assert.False(plan.Sessions);
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(["a1"], lab.Broker.Messages(lab.SessionTarget).Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task Cancel_finishes_the_message_in_flight_and_releases_the_rest()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(5);
        using var cancel = new CancellationTokenSource();
        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(5)), lab.Transport);
        plan.Confirm();

        var result = await lab.Engine.ExecuteAsync(plan, new InlineProgress(p =>
        {
            if (p.Moved == 2)
                cancel.Cancel();
        }), cancel.Token);

        Assert.Equal(MoveOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, result.Moved);
        Assert.Contains("No message was left half-moved", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, lab.Broker.Count(lab.Orders));
        var left = lab.Broker.Messages(lab.OrdersDlq);
        Assert.Equal(3, left.Count);
        Assert.All(left, m => Assert.False(m.Locked));
        Assert.Equal("Cancelled", lab.Audit.Entries[^1].Outcome);
    }

    [Fact]
    public async Task Asking_for_more_than_are_there_plans_only_what_is_there()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(3);
        lab.Switch.TurnOn();

        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(10)), lab.Transport, lab.Transport);
        plan.Confirm();
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(3, plan.Count);
        Assert.Equal("Move 3 messages?", plan.Title);
        Assert.Equal(MoveOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Moved);
    }

    [Fact]
    public async Task An_empty_source_is_refused()
    {
        var lab = new Lab();
        lab.Switch.TurnOn();

        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() =>
            lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(10)), lab.Transport, lab.Transport));

        Assert.Contains("no messages to move", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_gone_before_the_move_runs_make_a_partial_move()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(3);
        lab.Switch.TurnOn();
        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(3)), lab.Transport, lab.Transport);
        plan.Confirm();

        await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(1));
        var result = await lab.Engine.ExecuteAsync(plan);

        Assert.Equal(MoveOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Moved);
        Assert.Contains("The source had no more messages", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picks_that_are_gone_make_a_partial_move()
    {
        var lab = new Lab();
        var sequences = lab.SeedDeadLetters(2);

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.Of([sequences[0], 999]));

        Assert.Equal(MoveOutcome.Partial, result.Outcome);
        Assert.Equal(1, result.Moved);
        Assert.Contains("weren't found", result.Message, StringComparison.Ordinal);
        Assert.Equal(["order-002"], lab.Broker.Messages(lab.OrdersDlq).Select(m => m.Message.MessageId));
        Assert.All(lab.Broker.Messages(lab.OrdersDlq), m => Assert.False(m.Locked));
    }

    [Fact]
    public async Task Plan_text_names_the_count_source_target_and_how()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(2);
        var plan = await lab.Engine.PrepareAsync(new MoveRequest(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(2)), lab.Transport);

        Assert.Equal("Move 2 messages?", plan.Title);
        Assert.Equal($"From: dead-letter queue of queue orders in {Lab.Ns}", plan.FromText);
        Assert.Equal($"To: queue orders in {Lab.Ns}", plan.ToText);
        Assert.True(plan.Atomic);
        Assert.Contains("one transaction per message", plan.HowText, StringComparison.Ordinal);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public async Task Endpoints_outside_the_connection_namespace_are_refused()
    {
        var lab = new Lab();
        var refused = await Assert.ThrowsAsync<MoveRefusedException>(() => lab.Engine.PrepareAsync(
            new MoveRequest(MoveEndpoint.Queue("sbns-demo-009", "orders"), lab.Payments, MoveSelection.OldestFirst(1)), lab.Transport));
        Assert.Contains("isn't in the namespace", refused.Message, StringComparison.Ordinal);
    }

    private sealed class InlineProgress(Action<MoveProgress> report) : IProgress<MoveProgress>
    {
        public void Report(MoveProgress value) => report(value);
    }
}
