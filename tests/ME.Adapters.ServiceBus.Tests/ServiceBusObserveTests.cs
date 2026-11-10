// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

public class ServiceBusObserveTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Peek_with_a_sequence_cursor_does_not_settle_or_change_counts()
    {
        var broker = SeedStates();
        var before = broker.Snapshot();
        var client = Client(broker);

        var main = await client.PeekAsync("disputes/case-sync", SubQueue.Main, fromSequenceNumber: 1, max: 100);
        var dead = await client.PeekAsync("disputes/case-sync", SubQueue.DeadLetter, fromSequenceNumber: 1, max: 100);
        var again = await client.PeekAsync("disputes/case-sync", SubQueue.Main, fromSequenceNumber: 1, max: 100);

        Assert.Equal([1L, 2L, 3L], main.Select(message => message.EnqueuedSequenceNumber).ToArray());
        Assert.Equal([1L, 2L, 3L], again.Select(message => message.EnqueuedSequenceNumber).ToArray());
        Assert.Equal(MessageState.Active, main[0].State);
        Assert.Equal(MessageState.Deferred, main[1].State);
        Assert.Equal(MessageState.Scheduled, main[2].State);
        Assert.True(main[0].IsExpired);
        Assert.Equal(4, main[0].DeliveryCount);
        Assert.NotNull(main[0].LockedUntil);
        Assert.Single(dead);
        Assert.False(dead[0].IsExpired);
        Assert.Equal(before, broker.Snapshot());
        Assert.Equal(0, broker.WriteCalls);
    }

    [Fact]
    public async Task Paging_is_idempotent_and_stops_at_one_hundred()
    {
        var broker = new SyntheticServiceBus();
        for (var sequence = 1; sequence <= 150; sequence++)
            broker.Add(Active(sequence, deliveryCount: 1));
        var before = broker.Snapshot();
        var client = Client(broker);

        var first = await client.PeekAsync("disputes", SubQueue.Main, 1, 100);
        var second = await client.PeekAsync("disputes", SubQueue.Main, 101, 100);
        var repeated = await client.PeekAsync("disputes", SubQueue.Main, 1, 100);

        Assert.Equal(100, first.Count);
        Assert.Equal(1, first[0].EnqueuedSequenceNumber);
        Assert.Equal(100, first[^1].EnqueuedSequenceNumber);
        Assert.Equal(50, second.Count);
        Assert.Equal(101, second[0].EnqueuedSequenceNumber);
        Assert.Equal(first.Select(message => message.EnqueuedSequenceNumber), repeated.Select(message => message.EnqueuedSequenceNumber));
        Assert.Equal(before, broker.Snapshot());
        Assert.Equal(0, broker.WriteCalls);
    }

    [Fact]
    public async Task Over_cap_and_negative_cursor_throw_without_a_write()
    {
        var broker = SeedStates();
        var before = broker.Snapshot();
        var client = Client(broker);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.PeekAsync("disputes/case-sync", SubQueue.Main, 1, 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.PeekAsync("disputes/case-sync", SubQueue.Main, -1, 10));
        Assert.Equal(before, broker.Snapshot());
        Assert.Equal(0, broker.WriteCalls);
    }

    [Fact]
    public async Task Peeked_body_is_redacted_and_the_stored_body_stays()
    {
        var broker = new SyntheticServiceBus();
        broker.Add(Active(7, deliveryCount: 1, body: new Dictionary<string, string>
        {
            ["ssn"] = "219-09-9999",
            ["status"] = "open",
            ["subject"] = "ssn 219-09-9999",
        }));
        var client = Client(broker);

        var peeked = await client.PeekAsync("disputes", SubQueue.Main, 1, 10);
        var surface = string.Join("\n", peeked[0].Envelope.Data.Values) + peeked[0].Envelope.Subject;

        Assert.DoesNotContain("219-09-9999", surface, StringComparison.Ordinal);
        Assert.Equal("219-09-9999", broker.SnapshotBody(7, "ssn"));
        Assert.Equal(0, broker.WriteCalls);
    }

    [Fact]
    public void Probe_detects_settlement_renew_and_defer_when_called_directly()
    {
        var broker = SeedStates();
        broker.RenewLock(1);
        broker.Defer(1);
        broker.Complete(1);
        broker.Abandon(2);
        broker.DeadLetter(3);
        broker.ReceiveDeferred(4);
        broker.Schedule(5);
        broker.Cancel(6);

        Assert.Equal(8, broker.WriteCalls);
        Assert.Equal(0, broker.Messages.Count(message => message.Sequence == 1));
    }

    [Fact]
    public void Observe_client_exposes_no_settlement_methods()
    {
        var names = typeof(ServiceBusObserveClient).GetMethods().Select(method => method.Name).ToArray();
        foreach (var forbidden in new[] { "Receive", "Complete", "Abandon", "DeadLetter", "Defer", "Renew", "Schedule", "Cancel" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void Adapter_source_does_not_name_settlement_apis()
    {
        var forbidden = new[]
        {
            "ReceiveMessage",
            "CompleteMessage",
            "AbandonMessage",
            "DeadLetterMessage",
            "DeferMessage",
            "RenewMessageLock",
            "RenewLock",
            "ReceiveDeferred",
            "ScheduleMessage",
            "CancelScheduled",
        };
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "ME.Adapters.ServiceBus"), "*.cs");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Client_reports_the_service_bus_matrix_row()
    {
        var client = Client(new SyntheticServiceBus());
        Assert.True(client.Capabilities.PeekSideEffectFree);
        Assert.Equal("Azure Service Bus Data Receiver", client.Capabilities.RequiredRole);
    }

    private static ServiceBusObserveClient Client(SyntheticServiceBus broker) =>
        new(broker, Pipeline(), ObservedAt);

    private static RedactionPipeline Pipeline()
    {
        var material = Encoding.UTF8.GetBytes("synthetic-key-servicebus-0001");
        return DisputeRedactionProfile.Create(new ProjectTokenKey("servicebus", material), "synthetic-salt-dispute");
    }

    private static SyntheticMessage Active(long sequence, int deliveryCount, IReadOnlyDictionary<string, string>? body = null) =>
        new("disputes", SubQueue.Main, sequence, MessageState.Active, deliveryCount, null, null, body ?? new Dictionary<string, string> { ["status"] = "open" });

    private static SyntheticServiceBus SeedStates()
    {
        var broker = new SyntheticServiceBus();
        broker.Add(new SyntheticMessage(
            "disputes/case-sync",
            SubQueue.Main,
            1,
            MessageState.Active,
            4,
            ObservedAt.AddHours(-1),
            ObservedAt.AddMinutes(1),
            new Dictionary<string, string> { ["status"] = "open" }));
        broker.Add(new SyntheticMessage("disputes/case-sync", SubQueue.Main, 2, MessageState.Deferred, 2, null, null, new Dictionary<string, string> { ["status"] = "open" }));
        broker.Add(new SyntheticMessage("disputes/case-sync", SubQueue.Main, 3, MessageState.Scheduled, 0, null, null, new Dictionary<string, string> { ["status"] = "open" }));
        broker.Add(new SyntheticMessage(
            "disputes/case-sync",
            SubQueue.DeadLetter,
            9,
            MessageState.Active,
            5,
            ObservedAt.AddHours(-2),
            null,
            new Dictionary<string, string> { ["status"] = "open" },
            "CaseNotFound"));
        return broker;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("MessageExplorer.sln was not found above the test output.");
    }
}

internal sealed record SyntheticMessage(
    string Entity,
    SubQueue SubQueue,
    long Sequence,
    MessageState State,
    int DeliveryCount,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LockedUntil,
    IReadOnlyDictionary<string, string> Body,
    string? DeadLetterReason = null);

internal sealed record BrokerSnapshot(string Rows);

internal sealed class SyntheticServiceBus : IServiceBusPeekTransport
{
    private readonly List<SyntheticMessage> _messages = [];

    public IReadOnlyList<SyntheticMessage> Messages => _messages;
    public int ReceiveCalls { get; private set; }
    public int CompleteCalls { get; private set; }
    public int AbandonCalls { get; private set; }
    public int DeadLetterCalls { get; private set; }
    public int DeferCalls { get; private set; }
    public int RenewLockCalls { get; private set; }
    public int ReceiveDeferredCalls { get; private set; }
    public int ScheduleCalls { get; private set; }
    public int CancelCalls { get; private set; }

    public int WriteCalls =>
        ReceiveCalls + CompleteCalls + AbandonCalls + DeadLetterCalls + DeferCalls
        + RenewLockCalls + ReceiveDeferredCalls + ScheduleCalls + CancelCalls;

    public void Add(SyntheticMessage message) => _messages.Add(message);

    public BrokerSnapshot Snapshot()
    {
        var rows = _messages
            .OrderBy(message => message.Sequence)
            .Select(message => string.Join("|", message.Entity, message.SubQueue, message.Sequence, message.State, message.DeliveryCount, message.ExpiresAt, message.LockedUntil, message.Body["status"]));
        return new BrokerSnapshot(string.Join("\n", rows));
    }

    public string SnapshotBody(long sequence, string field) =>
        _messages.Single(message => message.Sequence == sequence).Body[field];

    public Task<IReadOnlyList<ServiceBusPeekFrame>> PeekAsync(
        string entity,
        SubQueue subQueue,
        long fromSequenceNumber,
        int max,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceBusPeekFrame> frames = _messages
            .Where(message => message.Entity == entity && message.SubQueue == subQueue && message.Sequence >= fromSequenceNumber)
            .OrderBy(message => message.Sequence)
            .Take(max)
            .Select(message => new ServiceBusPeekFrame(
                message.Sequence,
                message.State,
                message.DeliveryCount,
                message.ExpiresAt,
                message.LockedUntil,
                Subject: null,
                message.DeadLetterReason,
                TraceParent: null,
                message.Body))
            .ToList();
        return Task.FromResult(frames);
    }

    public void Receive(long sequence)
    {
        ReceiveCalls++;
        Replace(sequence, message => message with { DeliveryCount = message.DeliveryCount + 1 });
    }

    public void Complete(long sequence)
    {
        CompleteCalls++;
        _messages.RemoveAll(message => message.Sequence == sequence);
    }

    public void Abandon(long sequence)
    {
        AbandonCalls++;
        Replace(sequence, message => message with { DeliveryCount = message.DeliveryCount + 1, LockedUntil = null });
    }

    public void DeadLetter(long sequence)
    {
        DeadLetterCalls++;
        Replace(sequence, message => message with { SubQueue = SubQueue.DeadLetter, DeliveryCount = message.DeliveryCount + 1 });
    }

    public void Defer(long sequence)
    {
        DeferCalls++;
        Replace(sequence, message => message with { State = MessageState.Deferred });
    }

    public void RenewLock(long sequence)
    {
        RenewLockCalls++;
        Replace(sequence, message => message with { LockedUntil = message.LockedUntil?.AddMinutes(1) ?? DateTimeOffset.UtcNow.AddMinutes(1) });
    }

    public void ReceiveDeferred(long sequence)
    {
        ReceiveDeferredCalls++;
        Replace(sequence, message => message with { State = MessageState.Active, DeliveryCount = message.DeliveryCount + 1 });
    }

    public void Schedule(long sequence)
    {
        ScheduleCalls++;
        Add(new SyntheticMessage("scheduled", SubQueue.Main, sequence, MessageState.Scheduled, 0, null, null, new Dictionary<string, string> { ["status"] = "new" }));
    }

    public void Cancel(long sequence)
    {
        CancelCalls++;
        _messages.RemoveAll(message => message.Sequence == sequence);
    }

    private void Replace(long sequence, Func<SyntheticMessage, SyntheticMessage> update)
    {
        var index = _messages.FindIndex(message => message.Sequence == sequence);
        if (index < 0)
            return;
        _messages[index] = update(_messages[index]);
    }
}
