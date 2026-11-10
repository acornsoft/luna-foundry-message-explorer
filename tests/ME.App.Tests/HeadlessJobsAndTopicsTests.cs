// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class HeadlessJobsAndTopicsTests
{
    private static (MainWindow Window, MainViewModel Main, FakeBrokerProvider Provider) Open()
    {
        var provider = new FakeBrokerProvider(SyntheticQueues.WithTopics());
        var main = new MainViewModel(new ProviderCatalog([provider], ProviderCatalog.PlannedProviders), new DispatcherUiScheduler());
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, main, provider);
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named {name}.");

    private static void Show(Window window, MainViewModel main, int tab)
    {
        main.SelectedTab = tab;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Highlighted(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("deadletter"))
            .Select(t => t.Text ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToList();

    [AvaloniaFact]
    public void Main_window_opens_on_accounts_then_queues_topics_and_test_jobs_tabs()
    {
        var (window, _, _) = Open();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        Assert.Equal(["Accounts", "Saved logins", "Queues", "Topics", "Test jobs"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
        Assert.Equal(MainViewModel.AccountsTab, tabs.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task Topics_tab_lists_topics_then_subscriptions_with_dead_letter_cells_highlighted()
    {
        var (window, main, _) = Open();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.TopicsTab);

        var topicsGrid = Find<DataGrid>(window, "TopicsGrid");
        Assert.True(topicsGrid.IsVisible);
        Assert.Same(main.Topics.Topics, topicsGrid.ItemsSource);
        Assert.Equal("2 topics, 4 subscriptions. Read-only.", Find<TextBlock>(window, "TopicsSummaryText").Text);
        Assert.True(Find<TextBlock>(window, "NoTopicText").IsVisible);

        topicsGrid.SelectedItem = main.Topics.Topics[0];
        await main.Topics.SubscriptionsLoad;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var subscriptions = Find<DataGrid>(window, "SubscriptionsGrid");
        Assert.True(subscriptions.IsVisible);
        Assert.Equal(5, subscriptions.Columns.Count);
        Assert.Equal("Subscriptions of gsl-dispute-events", Find<TextBlock>(window, "SubscriptionsTitleText").Text);
        // Dead-letter 2 on one subscription, transfer dead-letter 4 on another. Zero counts stay plain.
        Assert.Equal(["2", "4"], Highlighted(subscriptions));

        // Counts sort as numbers, not as text.
        Assert.Equal(
            ["SubscriptionCount", "Scheduled", "SizeInBytes"],
            topicsGrid.Columns.Skip(1).Where(c => c.Header is "Subscriptions" or "Scheduled" or "Size").Select(c => c.SortMemberPath));
        Assert.Equal(
            ["Name", "Active", "DeadLetter", "TransferDeadLetter", "Status"],
            subscriptions.Columns.Select(c => c.SortMemberPath));
    }

    [AvaloniaFact]
    public async Task Topics_tab_shows_the_error_state()
    {
        var (window, main, provider) = Open();
        provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, BrokerException.Explain(BrokerErrorKind.Unreachable));
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.TopicsTab);

        Assert.True(Find<TextBlock>(window, "TopicsErrorText").IsVisible);
        Assert.False(Find<DataGrid>(window, "TopicsGrid").IsVisible);
    }

    [AvaloniaFact]
    public void Jobs_tab_shows_the_job_seed_and_pinned_plugins()
    {
        var (window, main, _) = Open();
        main.Jobs.OpenCollection(Repo.ErpStandin);
        Show(window, main, MainViewModel.JobsTab);

        Assert.Equal("424242", Find<TextBlock>(window, "SeedText").Text);
        var plugins = Find<ItemsControl>(window, "PluginList").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(["order-generator@0.2.0 · generator, assertion", "erp-standin@0.2.0 · simulator", "bus-observer@1.0.0 · observer · built in"], plugins);
        Assert.True(Find<Button>(window, "RunButton").IsEffectivelyEnabled);
        Assert.False(Find<Button>(window, "CancelButton").IsEffectivelyEnabled);
        Assert.False(Find<Button>(window, "ExportButton").IsEffectivelyEnabled);
        Assert.Contains("in-memory broker only", Find<TextBlock>(window, "SafetyText").Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Run_button_streams_the_erp_sample_into_the_live_grid()
    {
        var (window, main, _) = Open();
        main.Jobs.OpenCollection(Repo.ErpStandin);
        Show(window, main, MainViewModel.JobsTab);

        Find<Button>(window, "RunButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(JobPanelState.Running, main.Jobs.State);
        Assert.False(Find<Button>(window, "RunButton").IsEffectivelyEnabled);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (main.Jobs.IsBusy && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(JobPanelState.Passed, main.Jobs.State);
        Assert.True(main.Jobs.Live.FlushCount <= main.Jobs.Live.StepCount);

        var grid = Find<DataGrid>(window, "LiveGrid");
        Assert.Same(main.Jobs.Live.Rows, grid.ItemsSource);
        Assert.Equal(55, main.Jobs.Live.Rows.Count);
        Assert.StartsWith("Passed: erp-standin.erp-day.seed-424242. 55 of 55 passed", Find<TextBlock>(window, "LiveSummaryText").Text, StringComparison.Ordinal);
        Assert.StartsWith("55 sent · 0 pending · 52 passed · 0 failed · 3 dead-lettered · 0 late", Find<TextBlock>(window, "CountersText").Text, StringComparison.Ordinal);
        Assert.True(Find<Button>(window, "ExportButton").IsEffectivelyEnabled);

        main.Jobs.Live.Filter = LiveFilter.DeadLettered;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(3, main.Jobs.Live.Rows.Count);
        var statuses = grid.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("deadletter")).Select(t => t.Text).ToList();
        Assert.Equal(["dead-lettered", "dead-lettered", "dead-lettered"], statuses);
        var picker = Find<ComboBox>(window, "FilterPicker");
        Assert.Contains("Dead-lettered", picker.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    }
}
