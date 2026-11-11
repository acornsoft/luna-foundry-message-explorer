// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Projektanker.Icons.Avalonia;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>
/// Every Font Awesome icon sits on the vertical middle of its label: the icon's center and the center of the
/// label's first text line are within 1px. The text line, not the TextBlock's box, because a TextBlock stretched to
/// a taller row draws its text at the top of the box.
/// </summary>
public sealed class HeadlessIconAlignmentTests
{
    private const double Tolerance = 1.0;

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static double IconCenter(Icon icon, Visual root) => icon.TranslatePoint(new Point(0, icon.Bounds.Height / 2), root)!.Value.Y;

    private static double TextLineCenter(TextBlock text, Visual root)
    {
        var lines = text.TextLayout.TextLines;
        var line = lines.Count > 0 ? lines[0].Height : text.Bounds.Height;
        var top = text.TranslatePoint(new Point(0, text.Padding.Top), root)!.Value.Y;
        // A box no taller than its text (give or take layout rounding) is centered on its text; a taller,
        // stretched box draws its first line at the top.
        var box = text.Bounds.Height - text.Padding.Top - text.Padding.Bottom;
        return top + ((box <= line + 2 ? box : line) / 2);
    }

    private static bool Visible(Visual v) => v.IsEffectivelyVisible && v.Bounds.Height > 0;

    /// <summary>
    /// The label of an icon: the nearest visible TextBlock beside it in the same panel (or inside a sibling of the
    /// icon) whose box overlaps the icon vertically. Icon-only buttons have none and are skipped.
    /// </summary>
    private static TextBlock? LabelOf(Icon icon, Visual root)
    {
        if (icon.GetVisualParent() is not Visual parent)
            return null;
        var top = icon.TranslatePoint(default, root)!.Value;
        var bottom = top.Y + icon.Bounds.Height;
        return parent.GetVisualDescendants().OfType<TextBlock>()
            .Where(Visible)
            .Select(t => (Text: t, At: t.TranslatePoint(default, root)!.Value))
            .Where(t => t.At.Y < bottom && t.At.Y + t.Text.Bounds.Height > top.Y)
            .OrderBy(t => Math.Abs(t.At.X - top.X))
            .Select(t => t.Text)
            .FirstOrDefault();
    }

    /// <summary>Checks every icon+label pair under <paramref name="scope"/>; returns how many pairs it checked.</summary>
    private static int AssertAligned(Visual scope, Visual root, string where)
    {
        var checkedPairs = 0;
        var problems = new List<string>();
        foreach (var icon in scope.GetVisualDescendants().OfType<Icon>().Where(Visible))
        {
            if (LabelOf(icon, root) is not { } label)
                continue;
            checkedPairs++;
            var offset = IconCenter(icon, root) - TextLineCenter(label, root);
            if (Math.Abs(offset) > Tolerance)
                problems.Add($"{where}: {icon.Value} vs \"{label.Text}\" is {offset:+0.0;-0.0}px off center");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        return checkedPairs;
    }

    private static async Task<(MainWindow Window, MainViewModel Main)> QuickViewWithEverythingOpenAsync()
    {
        var provider = new FakeBrokerProvider(QuickViewViewModelTests.Entities());
        var browser = QuickViewViewModelTests.Browser();
        var main = new MainViewModel(new ProviderCatalog([provider], []), new DispatcherUiScheduler(), quickView: new QuickViewServices((_, _) => () => browser));
        var window = new MainWindow { DataContext = main, Width = 1500, Height = 1000 };
        window.Show();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        main.SelectedTab = MainViewModel.QuickViewTab;
        await main.QuickView.EnsureLoadedAsync();
        Pump(window);
        main.QuickView.PageSize = 5;
        main.QuickView.SelectedNode = main.QuickView.Find("orders");
        Pump(window);
        var dead = main.QuickView.Entity!.LaneOf(MessageLane.DeadLetter)!;
        dead.JumpCommand.Execute(null);
        for (var i = 0; i < 100 && !dead.HasRows; i++)
        {
            Pump(window);
            await Task.Delay(5);
        }

        dead.SelectedRow = dead.Rows[0];
        Pump(window);
        return (window, main);
    }

    [AvaloniaFact]
    public async Task Lane_card_icons_sit_on_the_middle_of_the_card_title()
    {
        var (window, _) = await QuickViewWithEverythingOpenAsync();

        var cards = Named<ItemsControl>(window, "QuickLaneCards");

        Assert.True(AssertAligned(cards, window, "lane cards") >= 8, "each card's lane icon and chevron should be checked against its title");
    }

    [AvaloniaFact]
    public async Task Section_headers_rows_and_load_more_keep_icons_on_the_text_middle()
    {
        var (window, main) = await QuickViewWithEverythingOpenAsync();
        Assert.True(main.QuickView.Entity!.LaneOf(MessageLane.DeadLetter)!.HasMore);

        var sections = Named<ItemsControl>(window, "QuickSections");

        // 4 section headers + at least one dead-letter row reason + the load-more button.
        Assert.True(AssertAligned(sections, window, "sections") >= 6);
        var loadMore = sections.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Peek next", StringComparison.Ordinal) == true);
        Assert.True(Visible(loadMore));
    }

    [AvaloniaFact]
    public async Task Tree_nodes_drawer_and_top_bar_keep_icons_on_the_text_middle()
    {
        var (window, _) = await QuickViewWithEverythingOpenAsync();

        Assert.True(AssertAligned(Named<TreeView>(window, "QuickTree"), window, "tree") >= 3);
        Assert.True(AssertAligned(Named<Border>(window, "QuickDrawer"), window, "drawer") >= 4);
        Assert.True(AssertAligned(Named<Border>(window, "QuickRoot"), window, "quick view") >= 20);
    }

    [AvaloniaFact]
    public void The_push_to_talk_button_centers_its_microphone_with_its_label()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(false)) { SelectedTab = MainViewModel.VoiceTab };
        var window = new MainWindow { DataContext = main };
        window.Show();
        Pump(window);

        var talk = Named<Button>(window, "VoiceTalk");

        Assert.Equal(1, AssertAligned(talk, window, "push-to-talk"));
        Assert.Contains(talk.GetVisualDescendants().OfType<Icon>(), i => i.Value == "fa-solid fa-microphone");
    }
}
