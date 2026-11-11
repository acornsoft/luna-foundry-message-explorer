// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>One view for queues, topics and subscriptions: told apart at a glance, and each kind can be hidden.</summary>
public sealed class QuickViewKindTests
{
    private static (QuickViewViewModel Quick, MemoryUiSettingsStore Store) Open(UiSettingsData? settings = null)
    {
        var store = new MemoryUiSettingsStore(settings);
        var provider = new FakeBrokerProvider(QuickViewViewModelTests.Entities());
        var quick = new QuickViewViewModel(redaction: QuickPeekSessionTests.Redaction(), settings: store);
        var browser = QuickViewViewModelTests.Browser();
        quick.Attach(provider.Inventory, "sbns-synthetic-001", () => browser);
        return (quick, store);
    }

    private static IEnumerable<QuickTreeNode> Walk(IEnumerable<QuickTreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));

    private static List<string> VisibleEntities(QuickViewViewModel quick) =>
        Walk(quick.Tree).Where(n => n.IsEntity && n.IsVisible).Select(n => n.Name).ToList();

    [Fact]
    public void Queues_topics_and_subscriptions_each_have_their_own_icon_and_tag()
    {
        Assert.Equal(("fa-solid fa-inbox", "QUEUE"), (EntityKindStyle.Icon(EntityKind.Queue), EntityKindStyle.Tag(EntityKind.Queue)));
        Assert.Equal(("fa-solid fa-tower-broadcast", "TOPIC"), (EntityKindStyle.Icon(EntityKind.Topic), EntityKindStyle.Tag(EntityKind.Topic)));
        Assert.Equal(("fa-solid fa-code-branch", "SUB"), (EntityKindStyle.Icon(EntityKind.Subscription), EntityKindStyle.Tag(EntityKind.Subscription)));
        Assert.True(EntityKindStyle.IsQueueFamily(EntityKind.Queue));
        Assert.False(EntityKindStyle.IsQueueFamily(EntityKind.Topic));
        Assert.False(EntityKindStyle.IsQueueFamily(EntityKind.Subscription));
        Assert.Equal(3, new[] { EntityKind.Queue, EntityKind.Topic, EntityKind.Subscription }.Select(EntityKindStyle.Icon).Distinct().Count());
    }

    [Fact]
    public async Task The_tree_counts_each_kind_and_tags_every_node()
    {
        var (quick, _) = Open();
        await quick.EnsureLoadedAsync();

        Assert.Equal((2, 1), (quick.QueueCount, quick.TopicCount));
        var orders = quick.Find("orders")!;
        Assert.Equal(("QUEUE", true, false), (orders.KindTag, orders.IsQueueFamily, orders.IsTopicFamily));
        var topic = quick.Find("order-events")!;
        Assert.Equal(("TOPIC", false, true), (topic.KindTag, topic.IsQueueFamily, topic.IsTopicFamily));
        topic.IsExpanded = true;
        await topic.ExpandTask;
        var billing = quick.Find("billing", "order-events")!;
        Assert.Equal(("SUB", "fa-solid fa-code-branch", true), (billing.KindTag, billing.Icon, billing.IsTopicFamily));
        Assert.Equal("2 queues (1 with dead letters), 1 topic. Read-only.", quick.Summary);
    }

    [Fact]
    public async Task One_view_shows_a_queue_a_topic_and_a_subscription()
    {
        var (quick, _) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        topic.IsExpanded = true;
        await topic.ExpandTask;

        quick.SelectedNode = quick.Find("orders");
        Assert.Equal(("orders", "QUEUE", true), (quick.Entity!.Name, quick.Entity.KindTag, quick.Entity.IsQueueFamily));
        Assert.Equal(4, quick.Entity.Lanes.Count);

        quick.SelectedNode = topic;
        Assert.Equal(("order-events", "TOPIC", true), (quick.Entity!.Name, quick.Entity.KindTag, quick.Entity.IsTopicFamily));
        Assert.Single(quick.Entity.Lanes);

        quick.SelectedNode = quick.Find("billing", "order-events");
        Assert.Equal(("billing", "SUB", true), (quick.Entity!.Name, quick.Entity.KindTag, quick.Entity.IsTopicFamily));
        Assert.Equal(4, quick.Entity.Lanes.Count);
    }

    [Fact]
    public async Task Hiding_topics_hides_their_subscriptions_clears_a_hidden_selection_and_is_remembered()
    {
        var (quick, store) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        topic.IsExpanded = true;
        await topic.ExpandTask;
        quick.SelectedNode = quick.Find("billing", "order-events");
        Assert.True(quick.HasEntity);

        quick.ShowTopics = false;

        Assert.Equal(["orders", "payments"], VisibleEntities(quick));
        Assert.Null(quick.SelectedNode);
        Assert.False(quick.HasEntity);
        Assert.False(store.Current.ShowTopics);
        Assert.True(store.Current.ShowQueues);

        // A selection that stays visible is kept.
        quick.SelectedNode = quick.Find("orders");
        quick.ShowTopics = true;
        Assert.Same(quick.Find("orders"), quick.SelectedNode);
        Assert.Contains("order-events", VisibleEntities(quick));

        quick.ShowQueues = false;
        Assert.Null(quick.SelectedNode);
        Assert.DoesNotContain("orders", VisibleEntities(quick));
        Assert.Contains("billing", VisibleEntities(quick));
        Assert.False(store.Current.ShowQueues);
    }

    [Fact]
    public async Task The_toggles_start_from_the_saved_settings_and_both_off_says_so()
    {
        var (quick, store) = Open(new UiSettingsData { ShowQueues = false, ShowTopics = true });
        Assert.False(quick.ShowQueues);
        await quick.EnsureLoadedAsync();
        Assert.Equal(["order-events"], VisibleEntities(quick));

        quick.ShowTopics = false;
        Assert.True(quick.NothingShown);
        Assert.Empty(VisibleEntities(quick));
        Assert.Contains("Queues", quick.NoEntityText, StringComparison.Ordinal);
        Assert.Equal((false, false), (store.Current.ShowQueues, store.Current.ShowTopics));

        // The text filter and the kind toggles work together.
        quick.ShowQueues = true;
        quick.ShowTopics = true;
        quick.Filter = "pay";
        Assert.Equal(["payments"], VisibleEntities(quick));
    }

    [Fact]
    public void The_toggle_settings_round_trip_with_the_theme_only_store()
    {
        var inner = new MemoryUiSettingsStore();
        var demo = new ThemeOnlyUiSettingsStore(inner);
        var quick = new QuickViewViewModel(settings: demo) { ShowQueues = false };
        Assert.False(inner.Current.ShowQueues);
        Assert.False(new QuickViewViewModel(settings: demo).ShowQueues);
    }
}
