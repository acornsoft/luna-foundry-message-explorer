// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.Tests;

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);
}

/// <summary>A synthetic namespace: orders (with dead letters), payments, a session queue, and a topic with two subscriptions.</summary>
internal sealed class Lab
{
    public const string Ns = "sbns-demo-001.servicebus.windows.net";

    public Lab(string ns = Ns)
    {
        Broker = new InMemoryMoveBroker(ns)
            .AddQueue("orders")
            .AddQueue("payments")
            .AddQueue("orders-by-customer", sessions: true)
            .AddQueue("orders-replay", sessions: true)
            .AddTopic("order-events")
            .AddSubscription("order-events", "billing")
            .AddSubscription("order-events", "audit");
        Transport = new InMemoryMoveTransport(Broker);
        Switch.TurnOn();
        Engine = new MoveEngine(Switch, Audit, Clock) { ReceiveWait = TimeSpan.Zero };
    }

    public InMemoryMoveBroker Broker { get; }

    public InMemoryMoveTransport Transport { get; }

    public MoveModeSwitch Switch { get; } = new();

    public MemoryMoveAuditLog Audit { get; } = new();

    public FixedClock Clock { get; } = new();

    public MoveEngine Engine { get; }

    public string NsName => Broker.Namespace;

    public MoveEndpoint Orders => MoveEndpoint.Queue(NsName, "orders");

    public MoveEndpoint OrdersDlq => MoveEndpoint.Queue(NsName, "orders", deadLetter: true);

    public MoveEndpoint Payments => MoveEndpoint.Queue(NsName, "payments");

    public MoveEndpoint SessionQueue => MoveEndpoint.Queue(NsName, "orders-by-customer");

    public MoveEndpoint SessionTarget => MoveEndpoint.Queue(NsName, "orders-replay");

    public MoveEndpoint Topic => MoveEndpoint.TopicOf(NsName, "order-events");

    public MoveEndpoint Billing => MoveEndpoint.Subscription(NsName, "order-events", "billing");

    public MoveEndpoint BillingDlq => MoveEndpoint.Subscription(NsName, "order-events", "billing", deadLetter: true);

    public MoveEndpoint Audit_ => MoveEndpoint.Subscription(NsName, "order-events", "audit");

    public List<long> SeedDeadLetters(int count, string prefix = "order")
    {
        var sequences = new List<long>();
        for (var i = 1; i <= count; i++)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenant"] = "contoso-synthetic",
                ["attempt"] = i,
                ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
                ["DeadLetterErrorDescription"] = "Synthetic failure",
            };
            sequences.Add(Broker.Seed(OrdersDlq, InMemoryMoveBroker.Message($"{prefix}-{i:000}", correlation: $"corr-{i:000}", body: $"{{\"secretBody\":\"BODY-{i}\"}}", properties: properties), "MaxDeliveryCountExceeded"));
        }

        return sequences;
    }

    public async Task<MoveResult> MoveAsync(MoveEndpoint source, MoveEndpoint target, MoveSelection selection, IMoveTransport? targetTransport = null, CancellationToken cancellationToken = default)
    {
        var plan = await Engine.PrepareAsync(new MoveRequest(source, target, selection, "alice@contoso.example"), Transport, targetTransport, cancellationToken);
        plan.Confirm();
        return await Engine.ExecuteAsync(plan, cancellationToken: cancellationToken);
    }
}

/// <summary>Wraps a transport and lies about one thing, to prove the engine copes.</summary>
internal sealed class LyingTransport(IMoveTransport inner, bool? requiresSessions = null, MoveCapabilities? capabilities = null, bool? duplicateDetection = null, bool canReceive = true, bool canSend = true) : IMoveTransport
{
    public string ProviderName => inner.ProviderName;

    public string Namespace => inner.Namespace;

    public MoveCapabilities Capabilities => capabilities ?? inner.Capabilities;

    public Task<RightCheck> CheckReceiveAsync(MoveEndpoint source, CancellationToken cancellationToken) =>
        canReceive ? inner.CheckReceiveAsync(source, cancellationToken) : Task.FromResult(RightCheck.No("This login can't receive from the source."));

    public Task<RightCheck> CheckSendAsync(MoveEndpoint target, CancellationToken cancellationToken) =>
        canSend ? inner.CheckSendAsync(target, cancellationToken) : Task.FromResult(RightCheck.No("This login can't send to the target."));

    public async Task<bool> RequiresSessionsAsync(MoveEndpoint source, CancellationToken cancellationToken) =>
        requiresSessions ?? await inner.RequiresSessionsAsync(source, cancellationToken);

    public async Task<bool?> DuplicateDetectionAsync(MoveEndpoint target, CancellationToken cancellationToken) =>
        duplicateDetection ?? await inner.DuplicateDetectionAsync(target, cancellationToken);

    public Task<IReadOnlyList<MoveMessageInfo>> PeekAsync(MoveEndpoint source, int max, CancellationToken cancellationToken) => inner.PeekAsync(source, max, cancellationToken);

    public Task<IMoveReceiver> OpenReceiverAsync(MoveEndpoint source, CancellationToken cancellationToken) => inner.OpenReceiverAsync(source, cancellationToken);

    public Task<IMoveReceiver?> AcceptNextSessionAsync(MoveEndpoint source, CancellationToken cancellationToken) => inner.AcceptNextSessionAsync(source, cancellationToken);

    public IMoveSender OpenSender(MoveEndpoint target) => inner.OpenSender(target);

    public Task RunInTransactionAsync(Func<Task> work) => inner.RunInTransactionAsync(work);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
