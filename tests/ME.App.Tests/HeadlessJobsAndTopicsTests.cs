// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    [AvaloniaFact]
    public void Main_window_opens_on_accounts_with_one_queues_and_topics_tab()
    {
        var (window, _, _) = Open();

        var tabs = window.FindControl<TabControl>("MainTabs")!;
        Assert.Equal(["Accounts", "Saved logins", "Queues & topics", "Test jobs", "Move", "Voice"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
        Assert.Equal(MainViewModel.AccountsTab, tabs.SelectedIndex);
        Assert.Equal(MainViewModel.QueuesTab, MainViewModel.TopicsTab);
        Assert.Equal(MainViewModel.QuickViewTab, MainViewModel.ExplorerTab);
    }

    [AvaloniaFact]
    public async Task One_tree_holds_queues_and_topics_told_apart_by_icon_and_tag()
    {
        var (window, main, _) = Open();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.TopicsTab);

        var quick = main.QuickView;
        Assert.Equal(2, quick.TopicCount);
        var topic = quick.Find("gsl-dispute-events")!;
        Assert.Equal(("fa-solid fa-tower-broadcast", "TOPIC"), (topic.Icon, topic.KindTag));
        var queue = quick.Tree[0].Children[0].Children[0];
        Assert.Equal(("fa-solid fa-inbox", "QUEUE"), (queue.Icon, queue.KindTag));

        topic.IsExpanded = true;
        await topic.ExpandTask;
        Show(window, main, MainViewModel.TopicsTab);
        var subscription = topic.Children[0];
        Assert.Equal(("fa-solid fa-code-branch", "SUB"), (subscription.Icon, subscription.KindTag));

        quick.SelectedNode = subscription;
        Show(window, main, MainViewModel.TopicsTab);
        Assert.Equal(subscription.Name, Find<TextBlock>(window, "QuickEntityTitle").Text);
        var tag = Find<Border>(window, "QuickEntityKind");
        Assert.Contains("topic", tag.Classes);
        Assert.Equal("SUB", ((TextBlock)tag.Child!).Text);

        var tags = Find<TreeView>(window, "QuickTree").GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kind") && b.IsEffectivelyVisible).Select(b => ((TextBlock)b.Child!).Text).ToList();
        Assert.Contains("QUEUE", tags);
        Assert.Contains("TOPIC", tags);
        Assert.Contains("SUB", tags);
    }

    [AvaloniaFact]
    public async Task The_queues_and_topics_toggles_hide_a_kind_and_clear_a_hidden_selection()
    {
        var (window, main, _) = Open();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.ExplorerTab);
        var quick = main.QuickView;
        quick.SelectedNode = quick.Find("gsl-dispute-events");
        Assert.True(quick.HasEntity);

        Find<ToggleButton>(window, "ShowTopicsToggle").IsChecked = false;
        Show(window, main, MainViewModel.ExplorerTab);

        Assert.False(quick.ShowTopics);
        Assert.Null(quick.SelectedNode);
        Assert.False(quick.HasEntity);
        var tags = Find<TreeView>(window, "QuickTree").GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kind") && b.IsEffectivelyVisible).Select(b => ((TextBlock)b.Child!).Text).Distinct().ToList();
        Assert.Equal(["QUEUE"], tags);

        Find<ToggleButton>(window, "ShowQueuesToggle").IsChecked = false;
        Show(window, main, MainViewModel.ExplorerTab);
        Assert.True(quick.NothingShown);
        Assert.DoesNotContain(Find<TreeView>(window, "QuickTree").GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("kind") && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Once_connected_the_connect_panel_steps_aside_on_the_explorer_unless_pinned()
    {
        var (window, main, _) = Open();
        Assert.True(Find<Border>(window, "ConnectCard").IsVisible);

        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.ExplorerTab);
        Assert.False(main.ShowConnectPanel);
        Assert.False(window.FindControl<Border>("ConnectCard")!.IsVisible);

        Find<ToggleButton>(window, "ConnectPinToggle").IsChecked = true;
        Show(window, main, MainViewModel.ExplorerTab);
        Assert.True(Find<Border>(window, "ConnectCard").IsVisible);

        main.ConnectPinned = false;
        Show(window, main, MainViewModel.AccountsTab);
        Assert.True(Find<Border>(window, "ConnectCard").IsVisible);

        main.Connect.DisconnectCommand.Execute(null);
        Show(window, main, MainViewModel.ExplorerTab);
        Assert.True(Find<Border>(window, "ConnectCard").IsVisible);
    }

    [AvaloniaFact]
    public async Task The_explorer_shows_the_error_state()
    {
        var (window, main, provider) = Open();
        provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, BrokerException.Explain(BrokerErrorKind.Unreachable));
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Show(window, main, MainViewModel.TopicsTab);

        Assert.True(Find<TextBlock>(window, "QuickErrorText").IsVisible);
        Assert.Empty(main.QuickView.Tree);
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
