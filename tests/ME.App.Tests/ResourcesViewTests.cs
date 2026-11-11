// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>
/// One Resources list: queues (one implicit receiver) and topics (N subscription receivers), a kind badge on
/// every row, one kind filter (All, Queues, Topics) that is remembered, and one search over resources and receivers.
/// </summary>
public sealed class ResourcesViewTests
{
    private static (QuickViewViewModel Quick, MemoryUiSettingsStore Store, FakeInventory Inventory) Open(UiSettingsData? settings = null)
    {
        var store = new MemoryUiSettingsStore(settings);
        var provider = new FakeBrokerProvider(QuickViewViewModelTests.Entities());
        var quick = new QuickViewViewModel(redaction: QuickPeekSessionTests.Redaction(), settings: store);
        var browser = QuickViewViewModelTests.Browser();
        quick.Attach(provider.Inventory, "sbns-synthetic-001", () => browser);
        return (quick, store, provider.Inventory);
    }

    private static IEnumerable<QuickTreeNode> Walk(IEnumerable<QuickTreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));

    private static List<string> Visible(QuickViewViewModel quick) =>
        Walk(quick.Tree).Where(n => n.IsEntity && n.IsVisible && (n.Receiver is null || quick.Tree.Any(r => r.Children.Contains(n) && r.IsVisible))).Select(n => n.Name).ToList();

    private static List<string> VisibleResources(QuickViewViewModel quick) => quick.Tree.Where(n => n.IsVisible).Select(n => n.Name).ToList();

    [Fact]
    public void Each_kind_has_its_own_icon_tag_and_colour_family()
    {
        Assert.Equal(("fa-solid fa-inbox", "QUEUE"), (EntityKindStyle.Icon(ResourceKind.Queue), EntityKindStyle.Tag(ResourceKind.Queue)));
        Assert.Equal(("fa-solid fa-tower-broadcast", "TOPIC"), (EntityKindStyle.Icon(ResourceKind.Topic), EntityKindStyle.Tag(ResourceKind.Topic)));
        Assert.Equal(("fa-solid fa-code-branch", "SUB"), (EntityKindStyle.Icon(ReceiverKind.Subscription), EntityKindStyle.Tag(ReceiverKind.Subscription)));
        Assert.Equal(EntityKindStyle.QueueIcon, EntityKindStyle.Icon(EntityKind.Queue));
        Assert.True(EntityKindStyle.IsQueueFamily(ResourceKind.Queue));
        Assert.False(EntityKindStyle.IsQueueFamily(ResourceKind.Topic));
    }

    [Fact]
    public async Task The_tree_is_one_resources_list_sorted_by_name_with_a_kind_badge_on_every_row()
    {
        var (quick, _, inventory) = Open();
        await quick.EnsureLoadedAsync();

        Assert.Equal(["order-events", "orders", "payments"], quick.Tree.Select(n => n.Name));
        Assert.All(quick.Tree, n => Assert.Equal(QuickNodeKind.Resource, n.Kind));
        Assert.Equal(["TOPIC", "QUEUE", "QUEUE"], quick.Tree.Select(n => n.KindTag));
        Assert.Equal((3, 2, 1), (quick.AllCount, quick.QueueCount, quick.TopicCount));
        Assert.Equal((1, 1), (inventory.ListCalls, inventory.TopicCalls));
        Assert.Empty(inventory.SubscriptionCalls);
    }

    [Fact]
    public async Task A_queue_opens_straight_to_its_messages_and_has_nothing_to_expand()
    {
        var (quick, _, inventory) = Open();
        await quick.EnsureLoadedAsync();
        var orders = quick.Find("orders")!;

        Assert.False(orders.IsExpandable);
        Assert.Empty(orders.Children);
        Assert.True(orders.Resource!.HasImplicitReceiver);

        quick.SelectedNode = orders;

        var entity = quick.Entity!;
        Assert.Equal(("orders", "QUEUE"), (entity.Name, entity.KindTag));
        Assert.Equal([MessageLane.Active, MessageLane.DeadLetter, MessageLane.Scheduled, MessageLane.TransferDeadLetter], entity.Lanes.Select(l => l.Lane));
        Assert.Empty(inventory.SubscriptionCalls);
    }

    [Fact]
    public async Task A_topic_expands_to_its_receivers_keeping_scheduled_on_the_topic_and_dead_letters_on_receivers()
    {
        var (quick, _, inventory) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        Assert.True(topic.IsExpandable);

        topic.IsExpanded = true;
        await topic.ExpandTask;

        Assert.Equal(["billing", "shipping"], topic.Children.Select(c => c.Name));
        Assert.All(topic.Children, c => Assert.Equal((QuickNodeKind.Receiver, "SUB", ReceiverKind.Subscription), (c.Kind, c.KindTag, c.Receiver!.Kind)));
        Assert.Equal(["order-events"], inventory.SubscriptionCalls);

        quick.SelectedNode = topic;
        Assert.Equal(MessageLane.Scheduled, Assert.Single(quick.Entity!.Lanes).Lane);

        var billing = topic.Children[0];
        Assert.Equal("9", billing.DeadLetterBadge);
        quick.SelectedNode = billing;
        Assert.Equal(("billing", "SUB"), (quick.Entity!.Name, quick.Entity.KindTag));
        Assert.Equal("9", quick.Entity.LaneOf(MessageLane.DeadLetter)!.CountText);
    }

    [Fact]
    public async Task The_kind_filter_shows_all_queues_or_topics_is_remembered_and_clears_a_hidden_selection()
    {
        var (quick, store, _) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        topic.IsExpanded = true;
        await topic.ExpandTask;
        Assert.Equal(ResourceKindFilter.All, quick.KindFilter);
        Assert.True(quick.IsAllKinds);

        quick.SelectedNode = topic.Children[0];
        quick.IsQueuesOnly = true;

        Assert.Equal(ResourceKindFilter.Queues, quick.KindFilter);
        Assert.Equal(["orders", "payments"], VisibleResources(quick));
        Assert.Null(quick.SelectedNode);
        Assert.Equal(ResourceKindFilter.Queues, store.Current.KindFilter);

        quick.SelectedNode = quick.Find("orders");
        quick.IsAllKinds = true;
        Assert.Same(quick.Find("orders"), quick.SelectedNode);

        quick.IsTopicsOnly = true;
        Assert.Equal(["order-events"], VisibleResources(quick));
        Assert.Null(quick.SelectedNode);

        // Clearing a segment never picks a filter by itself.
        quick.IsTopicsOnly = false;
        Assert.Equal(ResourceKindFilter.Topics, quick.KindFilter);

        var (next, _, _) = Open(store.Current);
        Assert.Equal(ResourceKindFilter.Topics, next.KindFilter);
    }

    [Fact]
    public async Task Search_finds_resources_and_receivers_and_opens_the_topic_that_holds_a_match()
    {
        var (quick, _, inventory) = Open();
        await quick.EnsureLoadedAsync();

        quick.Filter = "ship";
        await quick.SearchLoad;

        Assert.Equal(["order-events"], VisibleResources(quick));
        var topic = quick.Find("order-events")!;
        Assert.True(topic.IsExpanded);
        Assert.Equal(["shipping"], topic.Children.Where(c => c.IsVisible).Select(c => c.Name));
        Assert.Equal(["order-events"], inventory.SubscriptionCalls);

        quick.Filter = "pay";
        Assert.Equal(["payments"], VisibleResources(quick));

        quick.Filter = string.Empty;
        Assert.Equal(3, VisibleResources(quick).Count);
        Assert.Single(inventory.SubscriptionCalls); // read once, kept
    }

    [Fact]
    public async Task There_is_no_summary_line_until_the_list_is_narrowed()
    {
        var (quick, _, _) = Open();
        await quick.EnsureLoadedAsync();

        Assert.False(quick.IsNarrowed);
        Assert.Equal(string.Empty, quick.MatchText);
        quick.KindFilter = ResourceKindFilter.Queues;
        Assert.Equal("2 of 3 match queues", quick.MatchText);
    }

    [Fact]
    public void The_kind_filter_round_trips_through_the_demo_store()
    {
        var inner = new MemoryUiSettingsStore();
        var demo = new ThemeOnlyUiSettingsStore(inner);
        _ = new QuickViewViewModel(settings: demo) { IsQueuesOnly = true };
        Assert.Equal(ResourceKindFilter.Queues, inner.Current.KindFilter);
        Assert.True(new QuickViewViewModel(settings: demo).IsQueuesOnly);
    }
}
