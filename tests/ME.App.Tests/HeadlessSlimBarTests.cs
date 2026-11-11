// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Shell.Views;
using Acornsoft.MessageExplorer.Testing;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class HeadlessSlimBarTests
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

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<(MainWindow Window, MainViewModel Main, FakeBrokerProvider Provider)> ConnectedAsync()
    {
        var (window, main, provider) = Open();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        Pump(window);
        return (window, main, provider);
    }

    private static List<string?> VisibleTags(Window window) =>
        Find<TreeView>(window, "QuickTree").GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kind") && b.IsEffectivelyVisible).Select(b => ((TextBlock)b.Child!).Text).ToList();

    [AvaloniaFact]
    public void One_slim_bar_replaces_the_banner_the_tabs_and_the_quick_view_bar()
    {
        var (window, _, _) = Open();

        foreach (var gone in new[] { "MainTabs", "BannerText", "PolicyText", "ConnectPinToggle", "ConnectCard", "QuickPeekText", "QuickFooterText", "QuickFindBox", "ShowQueuesToggle", "ShowTopicsToggle", "AccountsThemePicker", "ConnectThemePicker" })
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name == gone);

        var bar = Find<Border>(window, "TopBar");
        Assert.True(bar.Bounds.Height <= 48);
        var names = bar.GetVisualDescendants().OfType<Control>().Select(c => c.Name).Where(n => n is not null).ToList();
        Assert.Equal(["NamespaceSwitcherButton", "ResourceSearchBox", "ModeChip", "VoiceButton", "SettingsButton"],
            names.Where(n => n is "NamespaceSwitcherButton" or "ResourceSearchBox" or "ModeChip" or "VoiceButton" or "SettingsButton").Distinct());
        Assert.Equal("Choose a namespace", Find<TextBlock>(window, "NamespaceSwitcherText").Text);
        Assert.Equal("Read-only", Find<TextBlock>(window, "ModeChipText").Text);

        // The switcher sheet is up at launch, with its three pages.
        Assert.True(Find<Panel>(window, "SwitcherSheet").IsVisible);
        Assert.True(Find<Grid>(window, "ConnectPanel").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "CloseSwitcherButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task The_read_only_explanation_is_said_once_as_the_mode_chip_tooltip()
    {
        var (window, main, _) = await ConnectedAsync();

        var tip = ToolTip.GetTip(Find<ToggleButton>(window, "ModeChip")) as string;
        Assert.Equal(main.Shell.ReadOnlyTip, tip);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("never receives", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("Read-only ·", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(main.Shell.Banner, StringComparison.Ordinal));
        Assert.Single(texts, t => t == "Read-only");
    }

    [AvaloniaFact]
    public async Task The_mode_chip_switches_between_the_resources_and_the_move_workspace()
    {
        var (window, main, _) = await ConnectedAsync();
        Assert.True(Find<Panel>(window, "ResourcesWorkspace").IsVisible);
        Assert.False(window.FindControl<DockPanel>("MoveWorkspace")!.IsVisible);

        Find<ToggleButton>(window, "ModeChip").IsChecked = true;
        Pump(window);

        Assert.True(main.MoveModeOn);
        Assert.Equal("Move", Find<TextBlock>(window, "ModeChipText").Text);
        Assert.Contains("move", Find<ToggleButton>(window, "ModeChip").Classes);
        Assert.True(Find<DockPanel>(window, "MoveWorkspace").IsVisible);
        Assert.False(window.FindControl<Panel>("ResourcesWorkspace")!.IsVisible);
        Assert.Contains("Click to go back to Read-only.", ToolTip.GetTip(Find<ToggleButton>(window, "ModeChip")) as string, StringComparison.Ordinal);

        Find<ToggleButton>(window, "ModeChip").IsChecked = false;
        Pump(window);
        Assert.False(main.MoveModeOn);
        Assert.True(Find<Panel>(window, "ResourcesWorkspace").IsVisible);
    }

    [AvaloniaFact]
    public async Task The_switcher_closes_on_connect_and_reopens_from_the_bar_on_any_page()
    {
        var (window, main, _) = await ConnectedAsync();
        Assert.False(window.FindControl<Panel>("SwitcherSheet")!.IsVisible);
        Assert.Equal(main.QuickView.NamespaceName, Find<TextBlock>(window, "NamespaceSwitcherText").Text);

        Find<Button>(window, "NamespaceSwitcherButton").Command!.Execute(null);
        Pump(window);
        Assert.True(Find<Panel>(window, "SwitcherSheet").IsVisible);
        Assert.True(Find<Button>(window, "CloseSwitcherButton").IsVisible);

        Find<RadioButton>(window, "SwitcherAccountsPage").IsChecked = true;
        Pump(window);
        Assert.True(Find<Control>(window, "AccountsPanel").IsEffectivelyVisible);
        Assert.False(window.FindControl<Grid>("ConnectPanel")!.IsEffectivelyVisible);

        Find<RadioButton>(window, "SwitcherLoginsPage").IsChecked = true;
        Pump(window);
        Assert.True(Find<Control>(window, "LoginsPanel").IsEffectivelyVisible);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump(window);
        Assert.False(main.IsSwitcherOpen);
    }

    [AvaloniaFact]
    public async Task Ctrl_f_focuses_the_one_search_box_which_finds_receivers()
    {
        var (window, main, provider) = await ConnectedAsync();

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Pump(window);
        var search = Find<TextBox>(window, "ResourceSearchBox");
        Assert.True(search.IsFocused);

        search.Text = "reconciler";
        Pump(window);
        await main.QuickView.SearchLoad;
        Pump(window);
        Assert.Equal(2, provider.Inventory.SubscriptionCalls.Count);
        var visible = main.QuickView.Tree.Where(n => n.IsVisible).ToList();
        var ledger = Assert.Single(visible);
        Assert.True(ledger.IsExpanded);
        Assert.Contains(ledger.Children, c => c.Name == "reconciler" && c.IsVisible);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump(window);
        Assert.Equal(string.Empty, main.QuickView.Filter);
    }

    [AvaloniaFact]
    public async Task One_resources_list_tells_queues_and_topics_apart_by_icon_and_tag()
    {
        var (window, main, _) = await ConnectedAsync();

        var quick = main.QuickView;
        Assert.Equal(6, quick.Tree.Count);
        var topic = quick.Find("gsl-dispute-events")!;
        Assert.Equal(("fa-solid fa-tower-broadcast", "TOPIC"), (topic.Icon, topic.KindTag));
        var queue = quick.Tree.First(n => n.IsQueueFamily);
        Assert.Equal(("fa-solid fa-inbox", "QUEUE"), (queue.Icon, queue.KindTag));
        Assert.False(queue.IsExpandable);

        topic.IsExpanded = true;
        await topic.ExpandTask;
        Pump(window);
        var subscription = topic.Children[0];
        Assert.Equal(("fa-solid fa-code-branch", "SUB"), (subscription.Icon, subscription.KindTag));

        quick.SelectedNode = subscription;
        Pump(window);
        Assert.Equal(subscription.Name, Find<TextBlock>(window, "QuickEntityTitle").Text);
        var tag = Find<Border>(window, "QuickEntityKind");
        Assert.Contains("topic", tag.Classes);
        Assert.Equal("SUB", ((TextBlock)tag.Child!).Text);

        var tags = VisibleTags(window);
        Assert.Contains("QUEUE", tags);
        Assert.Contains("TOPIC", tags);
        Assert.Contains("SUB", tags);
    }

    [AvaloniaFact]
    public async Task The_kind_filter_shows_all_queues_or_topics_and_clears_a_hidden_selection()
    {
        var (window, main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        Assert.True(Find<RadioButton>(window, "KindAll").IsChecked);
        quick.SelectedNode = quick.Find("gsl-dispute-events");
        Assert.True(quick.HasEntity);

        Find<RadioButton>(window, "KindQueues").IsChecked = true;
        Pump(window);
        Assert.Equal(ResourceKindFilter.Queues, quick.KindFilter);
        Assert.Null(quick.SelectedNode);
        Assert.False(quick.HasEntity);
        Assert.Equal(["QUEUE"], VisibleTags(window).Distinct());

        Find<RadioButton>(window, "KindTopics").IsChecked = true;
        Pump(window);
        Assert.Equal(ResourceKindFilter.Topics, quick.KindFilter);
        Assert.Equal(["TOPIC"], VisibleTags(window).Distinct());

        Find<RadioButton>(window, "KindAll").IsChecked = true;
        Pump(window);
        Assert.Equal(["QUEUE", "TOPIC"], VisibleTags(window).Distinct().Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_keyboard_walks_the_resources_list_and_opens_a_topic_with_right()
    {
        var (window, main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        var tree = Find<TreeView>(window, "QuickTree");
        quick.SelectedNode = quick.Tree[0];
        Pump(window);
        var first = (TreeViewItem)tree.ContainerFromItem(quick.Tree[0])!;
        first.Focus();
        Pump(window);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Pump(window);
        Assert.Same(quick.Tree[1], quick.SelectedNode);

        var topic = quick.Tree.First(n => n.IsExpandable);
        quick.SelectedNode = topic;
        Pump(window);
        ((TreeViewItem)tree.ContainerFromItem(topic)!).Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Pump(window);
        Assert.True(topic.IsExpanded);
        await topic.ExpandTask;
        Pump(window);
        Assert.Contains(topic.Children, c => c.Receiver is not null);
    }

    [AvaloniaFact]
    public void Voice_opens_as_a_side_panel_from_the_mic()
    {
        var (window, main, _) = Open();
        Assert.False(window.FindControl<Border>("VoicePanelHost")!.IsVisible);

        Find<ToggleButton>(window, "VoiceButton").IsChecked = true;
        Pump(window);
        Assert.True(main.IsVoiceOpen);
        Assert.True(Find<Border>(window, "VoicePanelHost").IsVisible);

        Find<Button>(window, "CloseVoiceButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Assert.False(main.IsVoiceOpen);
    }

    [AvaloniaFact]
    public async Task Settings_holds_the_theme_top_n_and_test_jobs()
    {
        var (window, main, _) = await ConnectedAsync();
        var settings = Find<Button>(window, "SettingsButton");
        settings.Flyout!.ShowAt(settings);
        Pump(window);

        var panel = (settings.Flyout as Flyout)!.Content as StackPanel;
        Assert.NotNull(panel);
        var top = panel!.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "QuickPageSizeBox");
        top.Value = 10;
        Pump(window);
        Assert.Equal(10, main.QuickView.PageSize);
        Assert.Contains(panel.GetVisualDescendants().OfType<ThemePicker>(), p => p.Name == "SettingsThemePicker");

        var jobs = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenJobsButton");
        jobs.Command!.Execute(null);
        Pump(window);
        Assert.True(main.IsJobsOpen);
        Assert.True(Find<Panel>(window, "JobsSheet").IsVisible);
        settings.Flyout.Hide();
    }

    [AvaloniaFact]
    public async Task The_explorer_shows_the_error_state()
    {
        var (window, main, provider) = Open();
        provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, BrokerException.Explain(BrokerErrorKind.Unreachable));
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Pump(window);

        Assert.True(Find<TextBlock>(window, "QuickErrorText").IsVisible);
        Assert.Empty(main.QuickView.Tree);
    }

    [AvaloniaFact]
    public void Test_jobs_open_from_settings_and_show_the_job_seed_and_pinned_plugins()
    {
        var (window, main, _) = Open();
        main.Jobs.OpenCollection(Repo.ErpStandin);
        main.OpenJobsCommand.Execute(null);
        Pump(window);

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
        main.OpenJobsCommand.Execute(null);
        Pump(window);

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
