// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>A confirm dialog that records what it was shown and answers as told.</summary>
internal sealed class FakeConfirmer(bool answer) : IMoveConfirmer
{
    public List<MovePlan> Shown { get; } = [];

    public bool Answer { get; set; } = answer;

    public Task<bool> ConfirmAsync(MovePlan plan)
    {
        Shown.Add(plan);
        return Task.FromResult(Answer);
    }
}

internal static class MoveDemo
{
    /// <summary>The synthetic demo on its own fresh broker, connected, with a fake confirm dialog.</summary>
    public static async Task<(MainViewModel Main, InMemoryMoveBroker Broker, MemoryMoveAuditLog Audit, FakeConfirmer Confirmer)> OpenAsync(bool turnOn = true, bool answer = true)
    {
        var broker = SyntheticDemoProvider.CreateBroker();
        var audit = new MemoryMoveAuditLog();
        var catalog = new ProviderCatalog([new SyntheticDemoProvider(broker)], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, move: new MoveServices(audit, "kept in memory for this test", MoveServices.Resolver(broker)));
        var confirmer = new FakeConfirmer(answer);
        main.Move.Confirmer = confirmer;
        main.Connect.NamespaceText = "synthetic-demo";
        await main.Connect.ConnectCommand.ExecuteAsync();
        if (turnOn)
        {
            main.MoveModeOn = true;
            await main.Move.TreeLoad;
        }

        return (main, broker, audit, confirmer);
    }

    public static MoveTreeNode Node(MainViewModel main, string path) =>
        main.Move.Tree.SelectMany(n => n.SelfAndDescendants()).Single(n => n.Endpoint?.EntityPath == path);

    public static async Task SelectAsync(MainViewModel main, string path)
    {
        main.Move.SelectedSource = Node(main, path);
        await main.Move.MessagesLoad;
    }
}

public class MoveModeTests
{
    private const string OrdersDlq = "orders/$deadletterqueue";

    [Fact]
    public async Task Move_mode_is_off_at_launch_and_no_startup_option_turns_it_on()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync(turnOn: false);
        StartupOptions.Parse(["--tab", "move", "--move-mode", "on", "--synthetic-demo"], name => name == "LFME_MOVE_MODE" ? "1" : null).Apply(main);

        Assert.False(main.MoveModeOn);
        Assert.False(main.Move.IsOn);
        Assert.False(main.Move.CanWork);
        Assert.Equal(MainViewModel.MoveTab, main.SelectedTab);
        Assert.Empty(main.Move.Tree);
        Assert.False(main.Move.MoveToCommand.CanExecute(null));
        Assert.Contains("turns off again every time the app starts", main.Move.OffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_it_on_builds_the_tree_with_dead_letter_children_and_counts()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();

        Assert.True(main.Move.CanWork);
        Assert.Equal(["Queues", "Topics"], main.Move.Tree.Select(n => n.Label));
        var orders = MoveDemo.Node(main, "orders");
        Assert.Equal(12, orders.Count);
        Assert.Equal(4, MoveDemo.Node(main, OrdersDlq).Count);
        Assert.True(MoveDemo.Node(main, OrdersDlq).IsDeadLetter);
        Assert.False(MoveDemo.Node(main, "order-events").CanBeSource);
        Assert.True(MoveDemo.Node(main, "order-events").CanBeTarget);
        Assert.Equal(2, MoveDemo.Node(main, "order-events/subscriptions/billing/$deadletterqueue").Count);
    }

    [Fact]
    public async Task Picking_a_dead_letter_queue_lists_ids_and_reasons_and_offers_only_its_parent()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, OrdersDlq);

        Assert.Equal("Messages in dead-letter queue of queue orders", main.Move.MessagesTitle);
        Assert.Equal(["ord-dl-001", "ord-dl-002", "ord-dl-003", "ord-dl-004"], main.Move.Messages.Select(m => m.MessageId));
        Assert.Equal("MaxDeliveryCountExceeded", main.Move.Messages[0].ReasonText);
        Assert.Equal(["queue orders"], main.Move.TargetChoices.Select(t => t.ToString()));
        Assert.Equal("queue orders", main.Move.SelectedTarget?.ToString());
    }

    [Fact]
    public async Task Dropping_picked_messages_confirms_moves_audits_and_refreshes_the_grids()
    {
        var (main, broker, audit, confirmer) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, OrdersDlq);
        main.Move.SetSelectedMessages([main.Move.Messages[1], main.Move.Messages[3]]);

        Assert.True(main.Move.BeginDrag(main.Move.SelectedSource, main.Move.SelectedMessages));
        var orders = MoveDemo.Node(main, "orders");
        Assert.True(main.Move.CanDropOn(orders));
        Assert.False(main.Move.CanDropOn(MoveDemo.Node(main, "payments")));
        var result = await main.Move.DropAsync(orders);

        var plan = Assert.Single(confirmer.Shown);
        Assert.Equal("Move 2 messages?", plan.Title);
        Assert.Equal("From: dead-letter queue of queue orders in sbns-demo-001", plan.FromText);
        Assert.True(plan.Atomic);
        Assert.Equal(MoveOutcome.Completed, result!.Outcome);
        Assert.Equal("Moved 2 of 2.", main.Move.Status);
        Assert.False(main.Move.StatusIsError);
        Assert.Equal(["ord-dl-001", "ord-dl-003"], broker.Messages(MoveEndpoint.Queue("sbns-demo-001", "orders", true)).Select(m => m.Message.MessageId));
        Assert.Equal(["ord-dl-001", "ord-dl-003"], main.Move.Messages.Select(m => m.MessageId));
        Assert.Equal(2, MoveDemo.Node(main, OrdersDlq).Count);
        Assert.Equal(14, MoveDemo.Node(main, "orders").Count);
        Assert.Equal(["move-started", "message-moved", "message-moved", "move-finished"], audit.Entries.Select(e => e.Event));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (main.Queues.Rows.FirstOrDefault(r => r.Name == "orders")?.DeadLetter != 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, main.Queues.Rows.Single(r => r.Name == "orders").DeadLetter);
    }

    [Fact]
    public async Task After_a_move_the_tree_keeps_its_nodes_and_selection_and_shows_new_counts()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, OrdersDlq);
        var dlq = main.Move.SelectedSource;
        var orders = MoveDemo.Node(main, "orders");

        var result = await main.Move.MoveSelectedAsync();

        Assert.Equal(MoveOutcome.Completed, result!.Outcome);
        Assert.Same(dlq, main.Move.SelectedSource);
        Assert.Same(orders, MoveDemo.Node(main, "orders"));
        Assert.Equal(0, dlq!.Count);
        Assert.Equal(16, orders.Count);
        Assert.Equal(result.Message, main.Move.Status);
        Assert.Equal("Messages in dead-letter queue of queue orders", main.Move.MessagesTitle);
    }

    [Fact]
    public async Task Saying_no_in_the_dialog_moves_nothing()
    {
        var (main, broker, audit, confirmer) = await MoveDemo.OpenAsync(answer: false);
        await MoveDemo.SelectAsync(main, OrdersDlq);

        var result = await main.Move.MoveSelectedAsync();

        Assert.Null(result);
        Assert.Single(confirmer.Shown);
        Assert.Equal("Not moved. Nothing changed.", main.Move.Status);
        Assert.Equal(4, broker.Count(MoveEndpoint.Queue("sbns-demo-001", "orders", true)));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Without_a_dialog_nothing_can_be_confirmed()
    {
        var (main, broker, _, _) = await MoveDemo.OpenAsync();
        main.Move.Confirmer = null;
        await MoveDemo.SelectAsync(main, OrdersDlq);

        await main.Move.MoveSelectedAsync();

        Assert.Equal(4, broker.Count(MoveEndpoint.Queue("sbns-demo-001", "orders", true)));
    }

    [Fact]
    public async Task Dragging_a_source_node_moves_the_oldest_n()
    {
        var (main, broker, _, _) = await MoveDemo.OpenAsync();
        main.Move.CountText = "3";
        var source = MoveDemo.Node(main, "orders");

        Assert.True(main.Move.BeginDrag(source));
        var result = await main.Move.DropAsync(MoveDemo.Node(main, "invoices"));

        Assert.Equal(3, result!.Moved);
        Assert.Equal(["inv-001", "inv-002", "inv-003", "inv-004", "inv-005", "ord-001", "ord-002", "ord-003"],
            broker.Messages(MoveEndpoint.Queue("sbns-demo-001", "invoices")).Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task Move_to_from_the_keyboard_uses_the_chosen_target()
    {
        var (main, broker, _, confirmer) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, "order-events/subscriptions/billing/$deadletterqueue");

        Assert.Equal(["topic order-events"], main.Move.TargetChoices.Select(t => t.ToString()));
        Assert.True(main.Move.MoveToCommand.CanExecute(null));
        await main.Move.MoveToCommand.ExecuteAsync();

        Assert.Contains(confirmer.Shown[0].Warnings, w => w.Contains("Every subscription of topic order-events gets a copy", StringComparison.Ordinal));
        Assert.Equal(0, broker.Count(MoveEndpoint.Subscription("sbns-demo-001", "order-events", "billing", true)));
        Assert.Equal(2, broker.Count(MoveEndpoint.Subscription("sbns-demo-001", "order-events", "shipping")));
    }

    [Fact]
    public async Task Sessions_keep_order_when_moved_from_the_app()
    {
        var (main, broker, _, confirmer) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, "shipments-by-customer");
        main.Move.CountText = "8";
        main.Move.SelectedTarget = main.Move.TargetChoices.Single(t => t.Node.Endpoint!.Name == "orders");

        await main.Move.MoveToCommand.ExecuteAsync();

        Assert.True(confirmer.Shown[0].Sessions);
        var moved = broker.Messages(MoveEndpoint.Queue("sbns-demo-001", "orders")).Where(m => m.Message.SessionId is not null).ToList();
        Assert.Equal(["cust-a", "cust-a", "cust-a", "cust-b", "cust-b", "cust-b", "cust-c", "cust-c"], moved.Select(m => m.Message.SessionId));
        Assert.Equal(["shp-001", "shp-003", "shp-006", "shp-002", "shp-005", "shp-008", "shp-004", "shp-007"], moved.Select(m => m.Message.MessageId));
    }

    [Fact]
    public async Task A_bad_count_or_too_many_is_refused_in_plain_words()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, "orders");
        main.Move.SelectedTarget = main.Move.TargetChoices.First();

        main.Move.CountText = "lots";
        await main.Move.MoveSelectedAsync();
        Assert.Equal("Enter how many messages to move (1 to 100).", main.Move.Status);

        main.Move.CountText = "500";
        await main.Move.MoveSelectedAsync();
        Assert.Equal("One drop moves at most 100 messages. Pick fewer and drop again.", main.Move.Status);
        Assert.True(main.Move.StatusIsError);
    }

    [Fact]
    public async Task Turning_it_off_clears_the_list_and_disables_moves()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        await MoveDemo.SelectAsync(main, OrdersDlq);
        Assert.NotEmpty(main.Move.Messages);

        main.MoveModeOn = false;

        Assert.Empty(main.Move.Messages);
        Assert.False(main.Move.CanWork);
        Assert.False(main.Move.MoveToCommand.CanExecute(null));
        Assert.False(main.Move.BeginDrag(main.Move.SelectedSource));
    }

    [Fact]
    public async Task A_provider_without_move_support_is_refused_plainly()
    {
        var catalog = new ProviderCatalog([new FakeBrokerProvider()], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog);
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        main.MoveModeOn = true;

        Assert.True(main.Move.IsRefused);
        Assert.Equal("Move mode isn't available for this connection.", main.Move.RefusalText);
        Assert.False(main.Move.CanWork);
    }

    [Fact]
    public async Task Disconnecting_clears_move_mode_state()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        main.Connect.DisconnectCommand.Execute(null);
        await Task.Yield();

        Assert.Empty(main.Move.Tree);
        Assert.False(main.Move.HasNamespace);
    }
}
