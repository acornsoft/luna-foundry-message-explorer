// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Projektanker.Icons.Avalonia;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class HeadlessQuickViewTests
{
    private static async Task<(MainWindow Window, MainViewModel Main, SyntheticMessageBrowser Browser)> OpenAsync(SyntheticMessageBrowser? browser = null)
    {
        var provider = new FakeBrokerProvider(QuickViewViewModelTests.Entities());
        browser ??= QuickViewViewModelTests.Browser();
        var quick = new QuickViewServices((_, _) => () => browser);
        var main = new MainViewModel(new ProviderCatalog([provider], []), new DispatcherUiScheduler(), quickView: quick);
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        main.SelectedTab = MainViewModel.QuickViewTab;
        await main.QuickView.EnsureLoadedAsync();
        Pump(window);
        return (window, main, browser);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named {name}.");

    [AvaloniaFact]
    public async Task Quick_view_shows_the_tree_cards_and_sections_with_font_awesome_icons()
    {
        var (window, main, browser) = await OpenAsync();
        var tree = Find<TreeView>(window, "QuickTree");
        Assert.Same(main.QuickView.Tree, tree.ItemsSource);

        main.QuickView.SelectedNode = main.QuickView.Find("orders");
        Pump(window);

        Assert.Equal("orders", Find<TextBlock>(window, "QuickEntityTitle").Text);
        var cards = Find<ItemsControl>(window, "QuickLaneCards");
        Assert.Equal(4, cards.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("card")));
        var icons = window.GetVisualDescendants().OfType<Icon>().Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("fa-solid fa-skull-crossbones", icons);
        Assert.Contains("fa-solid fa-inbox", icons);
        Assert.Empty(browser.Calls);
        Assert.False(Find<Border>(window, "QuickDrawer").IsVisible);
    }

    [AvaloniaFact]
    public async Task A_lane_card_click_opens_the_section_and_the_rows_are_virtualized()
    {
        var browser = new SyntheticMessageBrowser().Generate(BrowseEntity.Queue("orders"), active: 60, deadLetter: 100, firstSequence: 400);
        var (window, main, _) = await OpenAsync(browser);
        main.QuickView.PageSize = 100;
        main.QuickView.SelectedNode = main.QuickView.Find("orders");
        Pump(window);

        var card = Find<ItemsControl>(window, "QuickLaneCards").GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("card")).ElementAt(1);
        card.Command!.Execute(card.CommandParameter);
        var dead = main.QuickView.Entity!.LaneOf(MessageLane.DeadLetter)!;
        await QuickViewViewModelTests.WaitFor(() => dead.HasRows);
        Pump(window);

        Assert.Equal(100, dead.Rows.Count);
        var list = Find<ItemsControl>(window, "QuickSections").GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, dead.Rows));
        Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        var realized = list.GetRealizedContainers().Count();
        Assert.InRange(realized, 1, 40);

        dead.SelectedRow = dead.Rows[0];
        Pump(window);
        Assert.True(Find<Border>(window, "QuickDrawer").IsVisible);
        Assert.Equal("#" + dead.Rows[0].Message.SequenceText, Find<TextBlock>(window, "QuickDrawerTitle").Text);
        Assert.Contains("••••••", Find<SelectableTextBlock>(window, "QuickBodyText").Text, StringComparison.Ordinal);
    }
}
