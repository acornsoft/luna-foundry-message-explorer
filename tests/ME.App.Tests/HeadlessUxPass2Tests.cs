// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Icon = Projektanker.Icons.Avalonia.Icon;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The full-window review of #19, checked in a real (headless) window.</summary>
public sealed class HeadlessUxPass2Tests
{
    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static async Task<(MainWindow Window, MainViewModel Main)> ConnectedAsync(double width = 1440, string? savedEndpoint = null)
    {
        var vault = new TestVault();
        if (savedEndpoint is not null)
        {
            await vault.Vault.AddAsync(
                new LoginDraft("Reader (demo)", LoginKind.ServiceBusConnectionString, new Dictionary<string, string> { ["endpoint"] = savedEndpoint }),
                new RuntimeSecret("fake-not-a-real-key"));
        }

        var catalog = new ProviderCatalog([new FakeBrokerProvider(SyntheticQueues.WithTopics())], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, new DispatcherUiScheduler(),
            accounts: new AccountServices(new FakeAuthenticator(), new MemoryAccountStore(), ThemeAndSetupTests.Arm(), vault.Vault),
            settings: new MemoryUiSettingsStore());
        var window = new MainWindow { DataContext = main, Width = width, Height = 900 };
        window.Show();
        Pump(window);
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        Pump(window);
        return (window, main);
    }

    private static List<Border> VisibleBadges(Visual row) =>
        row.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("badge") && !b.Classes.Contains("kind") && b.IsEffectivelyVisible).ToList();

    // ---- MUST 1 ----

    [AvaloniaFact]
    public async Task Each_row_shows_at_most_two_named_badges_and_their_text_meets_four_point_five_in_both_themes()
    {
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var (window, main) = await ConnectedAsync();
                var events = main.QuickView.Find("gsl-dispute-events")!;
                events.IsExpanded = true;
                await events.ExpandTask;
                Pump(window);

                var tree = Find<TreeView>(window, "QuickTree");
                var rows = tree.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("row") && g.IsEffectivelyVisible).ToList();
                Assert.True(rows.Count >= 8, $"only {rows.Count} rows");
                var all = new List<Border>();
                foreach (var row in rows)
                {
                    var badges = VisibleBadges(row);
                    Assert.InRange(badges.Count, 0, 2);
                    all.AddRange(badges);
                }

                Assert.Contains(all, b => b.Classes.Contains("dead"));
                foreach (var badge in all)
                {
                    var name = AutomationProperties.GetName(badge);
                    Assert.Matches(@"^\d+ (active|dead-lettered)$", name);
                    Assert.Equal(name, ToolTip.GetTip(badge));
                    var text = (TextBlock)badge.Child!;
                    Assert.Equal(FontWeight.SemiBold, text.FontWeight);
                    var ratio = HeadlessUxPassTests.RenderedContrast(text);
                    Assert.True(ratio >= 4.5, $"{variant}: badge \"{text.Text}\" is {ratio:0.00}:1");
                }

                window.Close();
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    // ---- MUST 2 ----

    [AvaloniaFact]
    public async Task A_zero_card_shows_0_and_never_empty()
    {
        var (window, main) = await ConnectedAsync();
        main.QuickView.SelectedNode = main.QuickView.Find("gsl-notices-outbound"); // active 0
        Pump(window);

        var cards = Find<ItemsControl>(window, "QuickLaneCards");
        var texts = cards.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
        Assert.DoesNotContain("Empty", texts);
        Assert.Contains("0", texts);
        Assert.Contains("Nothing to peek", texts);
    }

    // ---- SHOULD 3, 4 ----

    [AvaloniaFact]
    public async Task A_pinned_selection_keeps_its_place_in_the_tree()
    {
        var (window, main) = await ConnectedAsync();
        var quick = main.QuickView;
        var order = quick.Tree.Select(n => n.Name).ToList();
        var intake = quick.Find("gsl-disputes-intake")!;
        quick.SelectedNode = intake;
        quick.Filter = "ledger";
        await quick.SearchLoad;
        Pump(window);

        Assert.Equal(order, quick.Tree.Select(n => n.Name).ToList());
        var tree = Find<TreeView>(window, "QuickTree");
        var shown = quick.Tree.Where(n => n.IsVisible).ToList();
        var ys = shown.Select(n => ((TreeViewItem)tree.ContainerFromItem(n)!).TranslatePoint(default, tree)!.Value.Y).ToList();
        Assert.Equal(ys.Order().ToList(), ys); // shown rows, the pinned one included, stay in tree order

        var pins = tree.GetVisualDescendants().OfType<Icon>().Where(i => i.Name == "QuickPinIcon" && i.IsEffectivelyVisible).ToList();
        Assert.Single(pins); // only on the selected row, not its parent
    }

    // ---- SHOULD 6 ----

    [AvaloniaFact]
    public async Task The_quick_switch_checks_the_open_namespace_and_rows_have_two_lines()
    {
        var (window, main) = await ConnectedAsync(savedEndpoint: "synthetic.servicebus.windows.net");
        var button = Find<Button>(window, "NamespaceSwitcherButton");
        button.Flyout!.ShowAt(button);
        Pump(window);

        var panel = (Control)((Flyout)button.Flyout).Content!;
        var saved = Find<ItemsControl>(panel, "QuickSwitchSaved");
        var item = saved.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("quickitem"));
        var lines = item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(["synthetic", "connection string · Azure public"], lines);
        var check = item.GetVisualDescendants().OfType<Icon>().Single(i => i.Name == "QuickSwitchCheck");
        Assert.True(check.IsEffectivelyVisible);
        Assert.Contains("open now", AutomationProperties.GetName(item), StringComparison.Ordinal);
        button.Flyout.Hide();
    }

    // ---- SHOULD 7, 8 ----

    [AvaloniaFact]
    public void The_namespace_placeholder_starts_with_eg_and_is_lighter_than_typed_text()
    {
        var main = new MainViewModel(new ProviderCatalog([new FakeBrokerProvider(SyntheticQueues.WithTopics())], ProviderCatalog.PlannedProviders),
            new DispatcherUiScheduler(), settings: new MemoryUiSettingsStore());
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        main.OpenSwitcher(SwitcherPage.Connect);
        main.Connect.NamespaceText = string.Empty;
        Pump(window);

        var box = Find<TextBox>(window, "NamespaceBox");
        Assert.StartsWith("e.g. ", box.Watermark, StringComparison.Ordinal);
        var watermark = box.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Watermark");
        Assert.Equal(FontStyle.Italic, watermark.FontStyle);
        Assert.True(window.TryFindResource("LfmeMutedTextBrush", window.ActualThemeVariant, out var muted));
        Assert.True(window.TryFindResource("LfmeTextBrush", window.ActualThemeVariant, out var typed));
        var color = ((ISolidColorBrush)watermark.Foreground!).Color;
        Assert.Equal(((ISolidColorBrush)muted!).Color, color);
        Assert.NotEqual(((ISolidColorBrush)typed!).Color, color);
        Assert.True(HeadlessUxPassTests.RenderedContrast(watermark) >= 4.5);
        Assert.Equal("Enter a namespace to connect.", Find<TextBlock>(window, "ConnectBlockedText").Text);
    }

    // ---- NICE 9 ----

    [AvaloniaFact]
    public async Task With_voice_open_in_a_narrow_window_the_cards_are_two_by_two()
    {
        var (window, main) = await ConnectedAsync(width: 1100);
        main.QuickView.SelectedNode = main.QuickView.Find("gsl-disputes-intake");
        Pump(window);
        var grid = Find<ItemsControl>(window, "QuickLaneCards").GetVisualDescendants().OfType<UniformGrid>().First();

        main.IsVoiceOpen = true;
        Pump(window);
        Assert.True(Find<Control>(window, "QuickEntityPanel").Bounds.Width < 720);
        Assert.Equal((2, 2), (grid.Rows, grid.Columns));
        var cards = grid.Children.OfType<Control>().Select(c => c.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card"))).ToList();
        var top = cards[0].TranslatePoint(default, grid)!.Value.Y + cards[0].Bounds.Height;
        var below = cards[2].TranslatePoint(default, grid)!.Value.Y;
        Assert.True(below - top >= 6, $"rows {below - top:0} px apart"); // the two rows don't touch

        main.IsVoiceOpen = false;
        window.Width = 1440;
        Pump(window);
        Assert.Equal((1, 4), (grid.Rows, grid.Columns));
    }
}
