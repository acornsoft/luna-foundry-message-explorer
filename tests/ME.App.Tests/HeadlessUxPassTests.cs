// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Shell.Views;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The UX review of the slim bar (PR #18), checked in a real (headless) window.</summary>
public sealed class HeadlessUxPassTests
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

    private static (MainWindow Window, MainViewModel Main) Open(bool syntheticDemo = false, IUiSettingsStore? store = null)
    {
        var catalog = syntheticDemo ? MainViewModel.DefaultCatalog(true) : new ProviderCatalog([new FakeBrokerProvider(SyntheticQueues.WithTopics())], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, new DispatcherUiScheduler(), settings: store ?? new MemoryUiSettingsStore());
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        Pump(window);
        return (window, main);
    }

    private static async Task<(MainWindow Window, MainViewModel Main)> ConnectedAsync(IUiSettingsStore? store = null)
    {
        var (window, main) = Open(store: store);
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        Pump(window);
        return (window, main);
    }

    // ---- 3 (1 and 2 held pending the Move redesign) ----

    [AvaloniaFact]
    public async Task The_mode_chip_keeps_its_toggle_with_a_one_phrase_tip_and_the_banner_names_the_namespace()
    {
        var (window, main) = await ConnectedAsync();
        var chip = Find<ToggleButton>(window, "ModeChip");
        Assert.Equal("Read-only: peeks, never changes messages", ToolTip.GetTip(chip) as string);
        Assert.Null(chip.Flyout);

        chip.IsChecked = true;
        Pump(window);
        Assert.True(main.MoveModeOn);
        Assert.Equal("Move", Find<TextBlock>(window, "ModeChipText").Text);
        Assert.Equal(main.MoveBannerText, Find<TextBlock>(window, "MoveModeBannerText").Text);
        Assert.StartsWith("Move mode is on for synthetic (", main.MoveBannerText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_switcher_shows_the_namespace_identity_under_its_name()
    {
        var (window, main) = await ConnectedAsync();
        var identity = Find<TextBlock>(window, "NamespaceIdentityText");
        Assert.True(identity.IsEffectivelyVisible);
        Assert.Equal(main.NamespaceIdentity, identity.Text);
        Assert.Contains(main.NamespaceIdentity!, ToolTip.GetTip(Find<Button>(window, "NamespaceSwitcherButton")) as string, StringComparison.Ordinal);
    }

    // ---- MUST 4: small text, measured on the rendered window ----

    [AvaloniaFact]
    public async Task Small_text_is_big_enough_and_meets_four_point_five_to_one_in_both_themes()
    {
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                var (window, main) = await ConnectedAsync();
                var quick = main.QuickView;
                var events = quick.Find("gsl-dispute-events")!;
                events.IsExpanded = true;
                await events.ExpandTask;
                quick.SelectedNode = quick.Find("gsl-disputes-provisional-credit");
                Pump(window);

                var failures = new List<string>();
                var checkedTexts = 0;
                foreach (var scope in new[] { "QuickRoot", "TopBar" })
                {
                    foreach (var text in Find<Control>(window, scope).GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
                    {
                        var small = text.FontSize < 14 || text.Classes.Contains("dim") || text.Classes.Contains("mono");
                        if (!small)
                            continue;
                        checkedTexts++;
                        var inKindBadge = text.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("kind"));
                        if (inKindBadge && text.FontSize < 10)
                            failures.Add($"{variant}: kind badge \"{text.Text}\" is {text.FontSize}px");
                        if (text.FontSize < 10)
                            failures.Add($"{variant}: \"{text.Text}\" is {text.FontSize}px");
                        var ratio = RenderedContrast(text);
                        if (ratio < 4.5)
                            failures.Add($"{variant}: \"{text.Text}\" ({string.Join(' ', text.Classes)}) is {ratio:0.00}:1");
                    }
                }

                Assert.True(checkedTexts > 20, $"only {checkedTexts} small texts were checked");
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct()));
                window.Close();
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    /// <summary>
    /// Contrast of a text as the window draws it: its resolved foreground, with every ancestor's opacity, over the
    /// stack of resolved backgrounds behind it (blended down to the first opaque one).
    /// </summary>
    internal static double RenderedContrast(TextBlock text)
    {
        var fg = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground);
        var opacity = text.Opacity * fg.Opacity * text.GetVisualAncestors().OfType<Visual>().Aggregate(1.0, (o, v) => o * v.Opacity);
        var layers = new List<Color>();
        foreach (var visual in text.GetVisualAncestors())
        {
            var brush = visual switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                ContentPresenter c => c.Background,
                TemplatedControl t => t.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush solid && solid.Color.A > 0)
            {
                var c = solid.Color;
                layers.Add(Color.FromArgb((byte)(c.A * solid.Opacity), c.R, c.G, c.B));
                if (layers[^1].A == 255)
                    break;
            }
        }

        var bg = Colors.White;
        for (var i = layers.Count - 1; i >= 0; i--)
            bg = Blend(layers[i], bg, layers[i].A / 255.0);
        var shown = Blend(fg.Color, bg, fg.Color.A / 255.0 * opacity);
        return ThemeAndSetupTests.Contrast(shown, bg);
    }

    private static Color Blend(Color top, Color under, double alpha) => Color.FromRgb(
        (byte)Math.Round((top.R * alpha) + (under.R * (1 - alpha))),
        (byte)Math.Round((top.G * alpha) + (under.G * (1 - alpha))),
        (byte)Math.Round((top.B * alpha) + (under.B * (1 - alpha))));

    // ---- MUST 5: icon-only buttons ----

    [AvaloniaFact]
    public async Task Every_icon_only_button_has_a_name_a_tooltip_and_a_24px_hit_area()
    {
        var (window, main) = await HeadlessIconAlignmentTests.QuickViewWithEverythingOpenAsync();
        main.IsVoiceOpen = true;
        Pump(window);

        var iconOnly = window.GetVisualDescendants().OfType<Button>()
            .Concat<ContentControl>(window.GetVisualDescendants().OfType<ToggleButton>())
            .Where(b => b.IsEffectivelyVisible && b is not RadioButton && b.GetVisualDescendants().OfType<Projektanker.Icons.Avalonia.Icon>().Any()
                        && !b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.GetVisualAncestors().OfType<Projektanker.Icons.Avalonia.Icon>().FirstOrDefault() is null))
            .ToList();
        Assert.True(iconOnly.Count >= 8, $"found {iconOnly.Count}");
        var problems = new List<string>();
        foreach (var button in iconOnly)
        {
            var label = $"{button.Name ?? button.GetType().Name} ({ToolTip.GetTip(button)})";
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                problems.Add(label + ": no accessible name");
            if (ToolTip.GetTip(button) is not string { Length: > 0 })
                problems.Add(label + ": no tooltip");
            if (button.Bounds.Width < 24 || button.Bounds.Height < 24)
                problems.Add($"{label}: {button.Bounds.Width}x{button.Bounds.Height}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        foreach (var name in new[] { "VoiceButton", "SettingsButton", "QuickRefreshButton" })
            Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => c.Name == name);
        Assert.Contains("resources list", ToolTip.GetTip(Find<Button>(window, "QuickRefreshButton")) as string, StringComparison.Ordinal);
    }

    // ---- (b), 6, 7, 8, 10, 14 ----

    [AvaloniaFact]
    public async Task The_match_line_and_clear_show_only_while_narrowed()
    {
        var (window, main) = await ConnectedAsync();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name == "QuickSummaryText");
        Assert.False(window.FindControl<Control>("QuickMatchPanel")?.IsEffectivelyVisible ?? false);

        Find<TextBox>(window, "ResourceSearchBox").Text = "reconciler";
        await main.QuickView.SearchLoad;
        Pump(window);
        Assert.True(Find<Control>(window, "QuickMatchPanel").IsEffectivelyVisible);
        Assert.Equal("1 of 6 match 'reconciler'", Find<TextBlock>(window, "QuickMatchText").Text);

        Find<Button>(window, "QuickClearFilters").Command!.Execute(null);
        Pump(window);
        Assert.False(Find<Control>(window, "QuickMatchPanel").IsVisible);
        Assert.Equal(string.Empty, main.QuickView.Filter);
    }

    [AvaloniaFact]
    public async Task A_pinned_selection_is_dimmed_with_a_note_and_sections_have_no_count_chips()
    {
        var (window, main) = await ConnectedAsync();
        var quick = main.QuickView;
        quick.SelectedNode = quick.Find("gsl-disputes-intake");
        main.QuickView.Filter = "ledger";
        await quick.SearchLoad;
        Pump(window);

        Assert.True(Find<TextBlock>(window, "QuickPinnedNote").IsEffectivelyVisible);
        var tree = Find<TreeView>(window, "QuickTree");
        var row = (TreeViewItem)tree.ContainerFromItem(quick.SelectedNode!)!;
        Assert.True(row.IsVisible);
        Assert.Contains(row.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("pinned"));

        var sections = Find<ItemsControl>(window, "QuickSections");
        Assert.DoesNotContain(sections.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge") && b.IsEffectivelyVisible);
        Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(tree));
    }

    [AvaloniaFact]
    public async Task The_dead_letter_badge_is_spoken_as_n_dead_lettered()
    {
        var (window, main) = await ConnectedAsync();
        var tree = Find<TreeView>(window, "QuickTree");
        var dead = tree.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("dead") && b.IsEffectivelyVisible);
        Assert.Matches(@"^\d+ dead-lettered$", AutomationProperties.GetName(dead));
        Assert.DoesNotContain(tree.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("badge") && b.IsEffectivelyVisible),
            b => ((TextBlock)b.Child!).Text == "0");
    }

    // ---- 11, 13, 15, (a) focus ----

    [AvaloniaFact]
    public void Appearance_lives_only_in_settings_and_first_run_focuses_add_an_account()
    {
        var (window, main) = Open();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ThemePicker>(), p => p.IsEffectivelyVisible);
        Assert.True(main.IsAccountsPage);
        Assert.True(Find<Button>(window, "AddAccountButton").IsFocused);
    }

    [AvaloniaFact]
    public void A_disabled_connect_shows_its_reason_line()
    {
        var (window, main) = Open();
        main.OpenSwitcher(SwitcherPage.Connect);
        main.Connect.NamespaceText = string.Empty;
        Pump(window);

        var reason = Find<TextBlock>(window, "ConnectBlockedText");
        Assert.True(reason.IsEffectivelyVisible);
        Assert.Equal("Enter a namespace to connect.", reason.Text);
        main.Connect.NamespaceText = "synthetic.example";
        Pump(window);
        Assert.False(reason.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_switcher_button_opens_a_quick_switch_with_recent_saved_and_manage_accounts()
    {
        var (window, main) = await ConnectedAsync();
        var button = Find<Button>(window, "NamespaceSwitcherButton");
        button.Flyout!.ShowAt(button);
        Pump(window);

        var panel = (Control)((Flyout)button.Flyout).Content!;
        Assert.True(Find<ItemsControl>(panel, "QuickSwitchRecent").IsVisible || Find<TextBlock>(panel, "QuickSwitchEmpty").IsVisible);
        var manage = Find<Button>(panel, "ManageAccountsButton");
        manage.Command!.Execute(null);
        button.Flyout.Hide();
        Pump(window);
        Assert.True(main.IsSwitcherOpen);
    }

    [AvaloniaFact]
    public void With_no_audio_device_the_question_box_sits_next_to_start_session()
    {
        var (window, main) = Open();
        main.IsSwitcherOpen = false;
        main.IsVoiceOpen = true;
        Pump(window);

        var ask = Find<TextBox>(window, "VoiceAskText");
        var start = Find<Button>(window, "VoiceStart");
        var askTop = ask.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var startTop = start.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(Math.Abs(askTop - startTop) < 60, $"ask at {askTop}, start at {startTop}");
    }
}
