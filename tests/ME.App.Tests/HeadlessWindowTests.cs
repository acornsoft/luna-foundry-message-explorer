// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell;
using Acornsoft.MessageExplorer.Shell.Tests;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace Acornsoft.MessageExplorer.Shell.Tests;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HeadlessWindowTests
{
    private static (MainWindow Window, MainViewModel Main) Open()
    {
        var catalog = new ProviderCatalog([new FakeBrokerProvider()], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog) { SelectedTab = MainViewModel.QueuesTab };
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, main);
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named {name}.");

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Window_starts_in_the_not_connected_state()
    {
        var (window, _) = Open();

        Assert.True(Find<TextBlock>(window, "QuickNotConnectedText").IsVisible);
        Assert.False(Find<TextBlock>(window, "QuickSummaryText").IsVisible);
        Assert.False(Find<Button>(window, "QuickRefreshButton").IsEffectivelyEnabled);
        Assert.Equal("Acornsoft Message Explorer", window.Title);
    }

    [AvaloniaFact]
    public void Connection_string_field_is_masked()
    {
        var (window, main) = Open();
        main.Connect.UseConnectionString = true;
        Dispatcher.UIThread.RunJobs();

        var box = window.FindControl<TextBox>("ConnectionStringBox")!;
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal('•', box.PasswordChar);
        Assert.False(box.RevealPassword);
        Assert.False(window.FindControl<TextBox>("NamespaceBox")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Planned_providers_render_as_disabled_radio_buttons()
    {
        var (window, _) = Open();

        var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "provider").ToList();

        Assert.Equal(3, radios.Count);
        Assert.True(radios[0].IsEnabled);
        Assert.True(radios[0].IsChecked);
        Assert.Equal("Apache Kafka (planned)", radios[1].Content);
        Assert.False(radios[1].IsEnabled);
        Assert.False(radios[2].IsEnabled);
    }

    [AvaloniaFact]
    public async Task Connect_fills_the_tree_and_highlights_dead_letters()
    {
        var (window, main) = Open();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Settle(window);

        Assert.Equal(MainViewModel.ExplorerTab, main.SelectedTab);
        Assert.False(Find<TextBlock>(window, "QuickNotConnectedText").IsVisible);
        Assert.StartsWith("4 queues (2 with dead letters), ", Find<TextBlock>(window, "QuickSummaryText").Text, StringComparison.Ordinal);
        var tree = Find<TreeView>(window, "QuickTree");
        var queueTags = tree.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kind") && b.Classes.Contains("queue") && b.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(4, queueTags.Count);
        Assert.All(queueTags, t => Assert.Equal("QUEUE", ((TextBlock)t.Child!).Text));
        // Dead-letter 3 on one queue, transfer dead-letter 1 on another; zero counts get no badge.
        int Badges(string kind) => tree.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains(kind) && b.IsEffectivelyVisible);
        Assert.Equal((1, 1), (Badges("dead"), Badges("transfer")));
        Assert.True(Find<Button>(window, "QuickRefreshButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Error_state_shows_the_message()
    {
        var provider = new FakeBrokerProvider();
        provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, BrokerException.Explain(BrokerErrorKind.Unreachable));
        var main = new MainViewModel(new ProviderCatalog([provider], [])) { SelectedTab = MainViewModel.QueuesTab };
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Settle(window);

        var error = Find<TextBlock>(window, "QuickErrorText");
        Assert.True(error.IsVisible);
        Assert.Equal(BrokerException.Explain(BrokerErrorKind.Unreachable), error.Text);
        Assert.False(Find<TextBlock>(window, "QuickSummaryText").IsVisible);
        Assert.False(Find<TextBlock>(window, "QuickEmptyText").IsVisible);
    }

    [AvaloniaFact]
    public async Task Empty_state_is_shown_for_a_namespace_with_no_queues_or_topics()
    {
        var provider = new FakeBrokerProvider([]);
        var main = new MainViewModel(new ProviderCatalog([provider], [])) { SelectedTab = MainViewModel.QueuesTab };
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Settle(window);

        Assert.True(Find<TextBlock>(window, "QuickEmptyText").IsVisible);
        Assert.Equal("This namespace has no queues or topics.", Find<TextBlock>(window, "QuickEmptyText").Text);
    }
}
