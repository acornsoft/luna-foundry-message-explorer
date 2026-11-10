// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public sealed class TopicsViewModelTests
{
    private static FakeInventory Inventory(IReadOnlyList<EntitySnapshot>? entities = null) =>
        new FakeBrokerProvider(entities ?? SyntheticQueues.WithTopics()).Inventory;

    [Fact]
    public void Starts_not_connected()
    {
        var topics = new TopicsViewModel();

        Assert.True(topics.IsNotConnected);
        Assert.True(topics.NoTopicSelected);
        Assert.False(topics.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Lists_topics_with_subscription_counts()
    {
        var topics = new TopicsViewModel();
        await topics.LoadAsync(Inventory());

        Assert.True(topics.HasRows);
        Assert.Equal(["gsl-dispute-events", "gsl-ledger-postings"], topics.Topics.Select(t => t.Name));
        Assert.Equal(["3", "1"], topics.Topics.Select(t => t.SubscriptionCountText));
        Assert.Equal("-", topics.Topics[0].ActiveText);
        Assert.Equal("2 topics, 4 subscriptions. Read-only.", topics.Summary);
        Assert.True(topics.NoTopicSelected);
    }

    [Fact]
    public async Task Picking_a_topic_lists_its_subscriptions_with_per_cell_dead_letter_highlight()
    {
        var inventory = Inventory();
        var topics = new TopicsViewModel();
        await topics.LoadAsync(inventory);

        topics.SelectedTopic = topics.Topics[0];
        await topics.SubscriptionsLoad;

        Assert.Equal(["gsl-dispute-events"], inventory.SubscriptionCalls);
        Assert.True(topics.HasSubscriptions);
        Assert.Equal("Subscriptions of gsl-dispute-events", topics.SubscriptionsTitle);
        Assert.Equal(["audit-copy", "case-tracker", "notice-writer"], topics.Subscriptions.Select(s => s.Name));
        var byName = topics.Subscriptions.ToDictionary(s => s.Name);
        Assert.True(byName["notice-writer"].DeadLetterAboveZero);
        Assert.False(byName["notice-writer"].TransferDeadLetterAboveZero);
        Assert.False(byName["audit-copy"].DeadLetterAboveZero);
        Assert.True(byName["audit-copy"].TransferDeadLetterAboveZero);
        Assert.False(byName["case-tracker"].HasDeadLetters);
        Assert.Equal("3 subscriptions, 2 with dead letters.", topics.SubscriptionsSummary);
    }

    [Fact]
    public async Task A_namespace_without_topics_is_empty()
    {
        var topics = new TopicsViewModel();
        await topics.LoadAsync(Inventory(SyntheticQueues.Standard()));

        Assert.True(topics.IsEmpty);
        Assert.Equal("This namespace has no topics.", topics.EmptyText);
    }

    [Fact]
    public async Task A_topic_without_subscriptions_is_empty()
    {
        var topics = new TopicsViewModel();
        await topics.LoadAsync(Inventory([SyntheticQueues.Topic("lonely", 0)]));

        topics.SelectedTopic = topics.Topics.Single();
        await topics.SubscriptionsLoad;

        Assert.True(topics.SubscriptionsEmpty);
    }

    [Fact]
    public async Task Broker_errors_show_the_plain_message()
    {
        var inventory = Inventory();
        inventory.FailWith = new BrokerException(BrokerErrorKind.Forbidden, "No permission to list topics.");
        var topics = new TopicsViewModel();

        await topics.LoadAsync(inventory);

        Assert.True(topics.IsError);
        Assert.Equal("No permission to list topics.", topics.ErrorMessage);
        Assert.Empty(topics.Topics);
    }

    [Fact]
    public async Task Subscription_errors_stay_in_the_subscription_pane()
    {
        var inventory = Inventory();
        var topics = new TopicsViewModel();
        await topics.LoadAsync(inventory);
        inventory.FailWith = new BrokerException(BrokerErrorKind.Forbidden, "No permission to list subscriptions.");

        topics.SelectedTopic = topics.Topics[0];
        await topics.SubscriptionsLoad;

        Assert.True(topics.SubscriptionsError);
        Assert.Equal("No permission to list subscriptions.", topics.SubscriptionError);
        Assert.True(topics.HasRows);
    }

    [Fact]
    public async Task Shows_loading_while_the_broker_answers()
    {
        var inventory = Inventory();
        inventory.Gate = new TaskCompletionSource();
        var topics = new TopicsViewModel();

        var load = topics.LoadAsync(inventory);
        Assert.True(topics.IsLoading);
        inventory.Gate.SetResult();
        await load;

        Assert.True(topics.HasRows);
    }

    [Fact]
    public async Task Clear_goes_back_to_not_connected()
    {
        var topics = new TopicsViewModel();
        await topics.LoadAsync(Inventory());
        topics.SelectedTopic = topics.Topics[0];
        await topics.SubscriptionsLoad;

        topics.Clear();

        Assert.True(topics.IsNotConnected);
        Assert.Empty(topics.Topics);
        Assert.Empty(topics.Subscriptions);
        Assert.Null(topics.SelectedTopic);
        Assert.True(topics.NoTopicSelected);
    }

    [Fact]
    public async Task Refresh_keeps_the_selected_topic()
    {
        var inventory = Inventory();
        var topics = new TopicsViewModel();
        await topics.LoadAsync(inventory);
        topics.SelectedTopic = topics.Topics[1];
        await topics.SubscriptionsLoad;

        await topics.RefreshCommand.ExecuteAsync();
        await topics.SubscriptionsLoad;

        Assert.Equal(2, inventory.TopicCalls);
        Assert.Equal("gsl-ledger-postings", topics.SelectedTopic?.Name);
        Assert.Equal(["reconciler"], topics.Subscriptions.Select(s => s.Name));
    }

    [Fact]
    public async Task Connecting_loads_queues_and_topics_and_disconnecting_clears_both()
    {
        var provider = new FakeBrokerProvider(SyntheticQueues.WithTopics());
        var main = new MainViewModel(new ProviderCatalog([provider], ProviderCatalog.PlannedProviders));
        main.Connect.NamespaceText = "synthetic.example";

        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.True(main.Queues.HasRows);
        Assert.True(main.Topics.HasRows);
        Assert.Equal(1, provider.Inventory.TopicCalls);

        main.Connect.DisconnectCommand.Execute(null);

        Assert.True(main.Queues.IsNotConnected);
        Assert.True(main.Topics.IsNotConnected);
    }

    [Fact]
    public void Startup_options_pick_the_tab_collection_and_demo()
    {
        var options = StartupOptions.Parse(["--collection", Repo.CrewStock], _ => null);
        Assert.Equal(MainViewModel.JobsTab, options.Tab);
        Assert.False(options.SyntheticDemo);

        Assert.Equal(MainViewModel.TopicsTab, StartupOptions.Parse(["--tab", "topics"], _ => null).Tab);
        Assert.True(StartupOptions.Parse([], name => name == "LFME_SYNTHETIC_DEMO" ? "1" : null).SyntheticDemo);
        var paced = StartupOptions.Parse(["--pace", "600", "--run"], _ => null);
        Assert.Equal(600, paced.Pace);
        Assert.True(paced.Run);

        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));
        (options with { Pace = 600 }).Apply(main);
        Assert.Equal(MainViewModel.JobsTab, main.SelectedTab);
        Assert.Equal(600, main.Jobs.SelectedPace.SimulatedSecondsPerSecond);
        Assert.Equal(["shift-sync"], main.Jobs.Jobs.Select(j => j.Name));
    }

    [Fact]
    public async Task The_synthetic_demo_connects_and_lists_topics_and_subscriptions()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: true));
        StartupOptions.Parse(["--synthetic-demo", "--tab", "topics"], _ => null).Apply(main);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!main.Topics.HasRows && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(main.Connect.IsConnected);
        Assert.True(main.Queues.HasRows);
        Assert.Contains(main.Topics.Topics, t => t.Name == "order-events" && t.SubscriptionCount == 3);
    }
}
