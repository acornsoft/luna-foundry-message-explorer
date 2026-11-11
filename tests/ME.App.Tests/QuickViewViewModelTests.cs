// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class QuickViewViewModelTests
{
    /// <summary>Synthetic entities for any broker: two queues, a topic with two subscriptions.</summary>
    internal static IReadOnlyList<EntitySnapshot> Entities() =>
    [
        SyntheticQueues.Queue("orders", active: 60, deadLetter: 30, scheduled: 6, transferDeadLetter: 3),
        SyntheticQueues.Queue("payments", active: 4, deadLetter: 0),
        SyntheticQueues.Topic("order-events", subscriptions: 2, scheduled: 6),
        SyntheticQueues.Subscription("order-events", "billing", active: 20, deadLetter: 9, transferDeadLetter: 1),
        SyntheticQueues.Subscription("order-events", "shipping", active: 5, deadLetter: 0),
    ];

    internal static SyntheticMessageBrowser Browser() => new SyntheticMessageBrowser()
        .Generate(BrowseEntity.Queue("orders"), active: 60, deadLetter: 30, scheduled: 6, transferDeadLetter: 3, firstSequence: 400)
        .Generate(BrowseEntity.Queue("payments"), active: 4)
        .Generate(BrowseEntity.Subscription("order-events", "billing"), active: 20, deadLetter: 9, transferDeadLetter: 1, firstSequence: 800);

    private static (QuickViewViewModel Quick, FakeInventory Inventory, SyntheticMessageBrowser Browser, List<string> Clipboard) Open(bool canPeek = true)
    {
        var provider = new FakeBrokerProvider(Entities());
        var browser = Browser();
        var clipboard = new List<string>();
        var quick = new QuickViewViewModel(redaction: QuickPeekSessionTests.Redaction())
        {
            CopyText = text =>
            {
                clipboard.Add(text);
                return Task.CompletedTask;
            },
        };
        quick.Attach(provider.Inventory, "sbns-synthetic-001", canPeek ? () => browser : null);
        return (quick, provider.Inventory, browser, clipboard);
    }

    [Fact]
    public async Task Nothing_is_read_until_asked_then_one_resources_list_holds_queues_and_topics()
    {
        var (quick, inventory, browser, _) = Open();
        Assert.Equal(0, inventory.ListCalls);
        Assert.Empty(quick.Tree);

        await quick.EnsureLoadedAsync();
        await quick.EnsureLoadedAsync();

        Assert.Equal(1, inventory.ListCalls);
        Assert.Equal(1, inventory.TopicCalls);
        Assert.Equal("sbns-synthetic-001", quick.NamespaceName);
        Assert.All(quick.Tree, n => Assert.Equal(QuickNodeKind.Resource, n.Kind));
        Assert.Equal(["orders", "payments"], quick.Tree.Where(n => n.IsQueueFamily).Select(c => c.Name));
        Assert.Contains(quick.Tree, n => n.IsTopicFamily && n.IsExpandable);
        Assert.Empty(inventory.SubscriptionCalls);
        Assert.Empty(browser.Calls);
    }

    [Fact]
    public async Task A_topic_reads_its_subscriptions_the_first_time_it_is_expanded()
    {
        var (quick, inventory, _, _) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        Assert.True(Assert.Single(topic.Children).IsPlaceholder);

        topic.IsExpanded = true;
        await topic.ExpandTask;
        topic.IsExpanded = false;
        topic.IsExpanded = true;

        Assert.Equal(["order-events"], inventory.SubscriptionCalls);
        Assert.Equal(["billing", "shipping"], topic.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Selecting_a_queue_shows_four_lanes_with_runtime_counts_and_peeks_nothing()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();

        quick.SelectedNode = quick.Find("orders");

        var entity = quick.Entity!;
        Assert.Equal([MessageLane.Active, MessageLane.DeadLetter, MessageLane.Scheduled, MessageLane.TransferDeadLetter], entity.Lanes.Select(l => l.Lane));
        Assert.Equal(["60", "30", "6", "3"], entity.Lanes.Select(l => l.CountText));
        Assert.Equal(["orders", "orders/$DeadLetterQueue", "orders", "orders/$Transfer/$DeadLetterQueue"], entity.Lanes.Select(l => l.Path));
        Assert.All(entity.Lanes, l => Assert.Equal("not loaded", l.Status));
        Assert.True(entity.LaneOf(MessageLane.DeadLetter)!.Alarm);
        Assert.Empty(browser.Calls);
    }

    [Fact]
    public async Task Opening_a_section_peeks_top_n_and_load_more_continues()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");
        var dead = quick.Entity!.LaneOf(MessageLane.DeadLetter)!;

        dead.IsExpanded = true;
        await WaitFor(() => dead.HasRows);

        Assert.Equal(25, dead.Rows.Count);
        Assert.True(dead.HasMore);
        Assert.Equal("top 25 · more waiting", dead.Status);
        Assert.Equal((PeekSource.DeadLetter, 0L, 25), (browser.Calls[0].Source, browser.Calls[0].From, browser.Calls[0].Max));
        Assert.StartsWith("Peek next 25 from #", dead.LoadMoreText, StringComparison.Ordinal);

        await dead.LoadMoreCommand.ExecuteAsync();

        Assert.Equal(30, dead.Rows.Count);
        Assert.False(dead.HasMore);
        Assert.Equal("all 30 peeked", dead.Status);
        Assert.False(dead.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task Top_n_is_configurable_and_capped_at_one_hundred()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        quick.PageSize = 10;
        quick.SelectedNode = quick.Find("orders");
        var active = quick.Entity!.LaneOf(MessageLane.Active)!;

        active.IsExpanded = true;
        await WaitFor(() => active.HasRows);

        Assert.Equal(10, active.Rows.Count);
        Assert.Equal(10, browser.Calls[0].Max);
        quick.PageSize = 0;
        Assert.Equal(1, quick.PageSize);
        quick.PageSize = 500;
        Assert.Equal(100, quick.PageSize);
        Assert.Equal(QuickViewViewModel.DefaultPageSize, new QuickViewViewModel().PageSize);
    }

    [Fact]
    public async Task A_subscription_shows_the_scheduled_count_from_its_topic_and_says_so()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        var topic = quick.Find("order-events")!;
        topic.IsExpanded = true;
        await topic.ExpandTask;

        quick.SelectedNode = quick.Find("billing", "order-events");

        var entity = quick.Entity!;
        Assert.Equal([MessageLane.Active, MessageLane.DeadLetter, MessageLane.Scheduled, MessageLane.TransferDeadLetter], entity.Lanes.Select(l => l.Lane));
        var scheduled = entity.LaneOf(MessageLane.Scheduled)!;
        Assert.Equal("6", scheduled.CountText);
        Assert.False(scheduled.IsPeekable);
        Assert.Contains("On topic order-events", scheduled.Note, StringComparison.Ordinal);
        Assert.Contains("subscription peek doesn't return scheduled messages", scheduled.Note, StringComparison.Ordinal);
        scheduled.IsExpanded = true;
        Assert.False(scheduled.IsExpanded);
        Assert.Equal("order-events/Subscriptions/billing/$DeadLetterQueue", entity.LaneOf(MessageLane.DeadLetter)!.Path);
        Assert.Empty(browser.Calls);
    }

    [Fact]
    public async Task A_topic_shows_only_its_scheduled_count()
    {
        var (quick, _, _, _) = Open();
        await quick.EnsureLoadedAsync();

        quick.SelectedNode = quick.Find("order-events");

        var lane = Assert.Single(quick.Entity!.Lanes);
        Assert.Equal(MessageLane.Scheduled, lane.Lane);
        Assert.False(lane.IsPeekable);
        Assert.Equal("6", lane.CountText);
    }

    [Fact]
    public async Task A_card_click_opens_its_section_and_focuses_it()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");
        var transfer = quick.Entity!.LaneOf(MessageLane.TransferDeadLetter)!;

        transfer.JumpCommand.Execute(null);
        await WaitFor(() => transfer.HasRows);

        Assert.True(transfer.IsExpanded);
        Assert.Same(transfer, quick.Entity.Focused);
        Assert.Equal(3, transfer.Rows.Count);
        Assert.All(browser.Calls, c => Assert.Equal(PeekSource.TransferDeadLetter, c.Source));
    }

    [Fact]
    public async Task Picking_a_row_opens_the_drawer_and_copy_puts_only_redacted_text_on_the_clipboard()
    {
        var (quick, _, browser, clipboard) = Open();
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");
        var dead = quick.Entity!.LaneOf(MessageLane.DeadLetter)!;
        dead.IsExpanded = true;
        await WaitFor(() => dead.HasRows);

        dead.SelectedRow = dead.Rows[2];
        Assert.Same(dead.Rows[2], quick.Entity.Selected);
        Assert.True(quick.Entity.HasSelection);

        await dead.Rows[2].CopyCommand.ExecuteAsync();
        var copied = Assert.Single(clipboard);
        var frame = (await browser.PeekAsync(BrowseEntity.Queue("orders"), PeekSource.DeadLetter, dead.Rows[2].Message.SequenceNumber, 1)).Single();
        foreach (var value in frame.Body.Values.Concat(frame.ApplicationProperties.Values).Where(v => v.Length >= 8))
            Assert.DoesNotContain(value, copied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Body (masked)", copied, StringComparison.Ordinal);

        quick.Entity.CloseDrawerCommand.Execute(null);
        Assert.False(quick.Entity.HasSelection);
        Assert.Null(dead.SelectedRow);
    }

    [Fact]
    public async Task Refreshing_active_starts_the_shared_main_cursor_again_and_forgets_scheduled_rows()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");
        var active = quick.Entity!.LaneOf(MessageLane.Active)!;
        var scheduled = quick.Entity.LaneOf(MessageLane.Scheduled)!;
        active.IsExpanded = true;
        await WaitFor(() => active.HasRows);
        scheduled.IsExpanded = true;
        await WaitFor(() => scheduled.HasRows);

        var before = browser.Calls.Count;
        await active.RefreshCommand.ExecuteAsync();

        Assert.Equal((PeekSource.Main, 0L), (browser.Calls[before].Source, browser.Calls[before].From));
        Assert.Equal(25, active.Rows.Count);
        Assert.False(scheduled.HasRows);
        Assert.False(scheduled.IsExpanded);
    }

    [Fact]
    public async Task Without_a_browser_every_lane_is_count_only()
    {
        var (quick, _, browser, _) = Open(canPeek: false);
        await quick.EnsureLoadedAsync();

        quick.SelectedNode = quick.Find("orders");

        Assert.False(quick.CanPeek);
        Assert.Equal("Read-only · counts only", quick.PeekText);
        Assert.All(quick.Entity!.Lanes, l =>
        {
            Assert.False(l.IsPeekable);
            Assert.Equal("count only", l.Status);
            Assert.Contains("Peek isn't available for Fake broker", l.Note, StringComparison.Ordinal);
        });
        Assert.Empty(browser.Calls);
    }

    [Fact]
    public async Task A_failed_peek_is_shown_in_the_section()
    {
        var (quick, _, browser, _) = Open();
        browser.Fault = _ => new BrokerException(BrokerErrorKind.Forbidden, "This login can't peek here.");
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");
        var active = quick.Entity!.LaneOf(MessageLane.Active)!;

        active.IsExpanded = true;
        await WaitFor(() => active.HasError);

        Assert.Equal("Couldn't peek: This login can't peek here.", active.Error);
        Assert.False(active.IsLoading);
        Assert.Empty(active.Rows);
    }

    [Fact]
    public async Task The_find_box_hides_entities_that_do_not_match()
    {
        var (quick, _, _, _) = Open();
        await quick.EnsureLoadedAsync();

        quick.Filter = "pay";

        Assert.True(quick.Find("payments")!.IsVisible);
        Assert.False(quick.Find("orders")!.IsVisible);
        Assert.False(quick.Find("order-events")!.IsVisible);
        quick.Filter = string.Empty;
        Assert.True(quick.Find("orders")!.IsVisible);
    }

    [Fact]
    public async Task Disconnect_disposes_the_browser_and_clears_the_view()
    {
        var (quick, _, browser, _) = Open();
        await quick.EnsureLoadedAsync();
        quick.SelectedNode = quick.Find("orders");

        await quick.ClearAsync();

        Assert.True(browser.Disposed);
        Assert.Empty(quick.Tree);
        Assert.Null(quick.Entity);
        Assert.True(quick.IsNotConnected);
    }

    [Fact]
    public async Task Connecting_reads_the_resources_once_and_fills_the_list()
    {
        var provider = new FakeBrokerProvider(Entities());
        var browser = Browser();
        var main = new MainViewModel(new ProviderCatalog([provider], []), quickView: new QuickViewServices((_, _) => () => browser));
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();

        Assert.Equal(1, provider.Inventory.ListCalls);
        Assert.Equal(1, provider.Inventory.TopicCalls);
        Assert.NotEmpty(main.QuickView.Tree);
        Assert.Equal(AppSection.Resources, StartupOptions.Parse(["--tab", "quick"], _ => null).Section);
    }

    [Fact]
    public void The_demo_and_service_bus_resolve_a_browser_and_other_providers_do_not()
    {
        var resolve = QuickViewServices.Resolver(null);
        var demo = new SyntheticDemoProvider().OpenInventory(BrokerConnection.WithEntra(SyntheticDemoProvider.Id, "synthetic-demo"));
        var fake = new FakeBrokerProvider().Inventory;

        var opened = resolve(demo, null)!();
        Assert.IsType<SyntheticMessageBrowser>(opened);
        Assert.Null(resolve(fake, BrokerConnection.WithEntra(FakeBrokerProvider.Id, "anything")));
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the view model.");
    }
}
