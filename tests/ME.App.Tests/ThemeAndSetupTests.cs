// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Shell.Views;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Light, dark and system themes; the palette; and the Azure Data Studio style setup (accounts, recent, browse).</summary>
public sealed class ThemeAndSetupTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";

    // ---- Theme choice and persistence ----

    [Fact]
    public void The_theme_defaults_to_system_and_a_switch_is_applied_and_remembered()
    {
        var store = new MemoryUiSettingsStore();
        var applied = new List<ThemeVariant>();
        var theme = new ThemeViewModel(store, applied.Add);

        Assert.Equal(ThemeChoice.System, theme.Choice);
        Assert.True(theme.IsSystem);
        Assert.Equal([ThemeVariant.Default], applied);

        theme.IsDark = true;
        Assert.Equal(ThemeChoice.Dark, theme.Choice);
        Assert.Equal(ThemeVariant.Dark, applied[^1]);
        Assert.Equal(ThemeChoice.Dark, store.Current.Theme);

        // Clearing a radio button never picks a theme by itself.
        theme.IsDark = false;
        Assert.Equal(ThemeChoice.Dark, theme.Choice);

        var next = new ThemeViewModel(store, applied.Add);
        Assert.Equal(ThemeChoice.Dark, next.Choice);
        Assert.Equal(ThemeVariant.Dark, applied[^1]);

        next.IsLight = true;
        Assert.Equal((ThemeChoice.Light, ThemeVariant.Light), (store.Current.Theme, applied[^1]));
        Assert.Equal(ThemeVariant.Default, ThemeViewModel.VariantFor(ThemeChoice.System));
    }

    [Fact]
    public void The_settings_file_round_trips_and_a_broken_file_falls_back_to_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lfme-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileUiSettingsStore(dir);
            Assert.Equal(new UiSettingsData().Theme, store.Load().Theme);

            store.Save(new UiSettingsData { Theme = ThemeChoice.Light, KindFilter = ResourceKindFilter.Topics, Recent = [new RecentConnection("sbns-demo-001", DateTimeOffset.UnixEpoch, LoginId: "login-1")] });
            var loaded = new FileUiSettingsStore(dir).Load();
            Assert.Equal((ThemeChoice.Light, ResourceKindFilter.Topics), (loaded.Theme, loaded.KindFilter));
            Assert.Equal("login-1", Assert.Single(loaded.Recent).LoginId);
            Assert.Contains("\"theme\": \"light\"", File.ReadAllText(Path.Combine(dir, FileUiSettingsStore.FileName)), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, FileUiSettingsStore.FileName), "{ not json");
            var fallback = new FileUiSettingsStore(dir).Load();
            Assert.Equal((ThemeChoice.System, ResourceKindFilter.All), (fallback.Theme, fallback.KindFilter));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Palette: every text pair passes WCAG AA (4.5:1), icons and accents 3:1, in both themes ----

    private static readonly (string Foreground, string Background)[] TextPairs =
    [
        ("Text", "Surface"), ("Text", "Card"), ("MutedText", "Surface"), ("MutedText", "Card"),
        ("AccentText", "Surface"), ("AccentText", "Card"), ("AccentText", "AccentSoft"), ("Text", "AccentSoft"),
        ("OnAccent", "Accent"), ("OnAccent", "AccentHover"), ("OnAccent", "AccentPressed"),
        ("Error", "ErrorBackground"), ("Error", "Card"), ("Success", "SuccessBackground"), ("Success", "Card"),
        ("WarningText", "WarningBackground"), ("Text", "Badge"), ("Text", "Code"),
        ("Text", "QvRoot"), ("Text", "QvPanel"), ("Text", "QvRaised"), ("Text", "QvHover"),
        ("DeadText", "DeadSoft"), ("ScheduledText", "ScheduledSoft"), ("TransferText", "TransferSoft"),
        ("Queue", "QueueSoft"), ("Topic", "TopicSoft"), ("Queue", "QvPanel"), ("Topic", "QvPanel"),
        ("Queue", "QvRoot"), ("Topic", "QvRoot"), ("Active", "QvPanel"), ("Active", "ActiveSoft"),
        // The slim bar, the switcher sheet and the kind filter.
        ("MutedText", "Code"), ("Text", "Code"), ("AccentText", "Code"),
    ];

    private static readonly (string Foreground, string Background)[] IconPairs =
    [
        ("Accent", "Surface"), ("Accent", "Card"), ("Dead", "QvPanel"), ("Scheduled", "QvPanel"),
        ("Transfer", "QvPanel"), ("Warning", "WarningBackground"), ("CardBorder", "Surface"),
        ("MutedText", "Card"), ("AccentText", "AccentSoft"), ("Queue", "Card"), ("Topic", "Card"), ("Queue", "AccentSoft"), ("Topic", "AccentSoft"),
        ("Success", "SuccessBackground"), ("WarningText", "WarningBackground"),
    ];

    private static Color ColorOf(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource($"Lfme{key}Color", variant, out var value), $"Lfme{key}Color is missing in {variant}");
        return Assert.IsType<Color>(value);
    }

    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [AvaloniaFact]
    public void Every_palette_pair_meets_its_contrast_ratio_in_light_and_dark()
    {
        var failures = new List<string>();
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var (fg, bg) in TextPairs)
            {
                var ratio = Contrast(ColorOf(fg, variant), ColorOf(bg, variant));
                if (ratio < 4.5)
                    failures.Add($"{variant} text {fg} on {bg}: {ratio:0.00}");
            }

            foreach (var (fg, bg) in IconPairs.Where(p => p.Foreground != "CardBorder"))
            {
                var ratio = Contrast(ColorOf(fg, variant), ColorOf(bg, variant));
                if (ratio < 3)
                    failures.Add($"{variant} icon {fg} on {bg}: {ratio:0.00}");
            }
        }

        Assert.Empty(failures);
        // The brand colours: maroon accent in light, gold in dark.
        Assert.Equal(Color.Parse("#4F2001"), ColorOf("Accent", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#FF9900"), ColorOf("Accent", ThemeVariant.Dark));
    }

    [Fact]
    public void The_contrast_formula_matches_known_values()
    {
        Assert.Equal(21, Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1, Contrast(Colors.White, Colors.White), 2);
        Assert.InRange(Contrast(Color.Parse("#FF9900"), Colors.White), 2.0, 2.2); // gold is never text on white
    }

    // ---- Every DynamicResource the views use resolves in both themes ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [AvaloniaFact]
    public void Every_dynamic_resource_in_the_app_resolves_in_light_and_dark()
    {
        var app = Path.Combine(RepoRoot(), "src", "ME.App");
        var keys = Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{DynamicResource\s+([A-Za-z0-9_.]+)\s*\}").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("LfmeQueueBrush", keys);
        Assert.Contains("LfmeTopicBrush", keys);
        var missing = new[] { ThemeVariant.Light, ThemeVariant.Dark }
            .SelectMany(v => keys.Where(k => !Application.Current!.TryGetResource(k, v, out _)).Select(k => $"{v}: {k}"))
            .ToList();
        Assert.Empty(missing);
    }

    [AvaloniaFact]
    public void No_view_hard_codes_a_colour()
    {
        var app = Path.Combine(RepoRoot(), "src", "ME.App");
        var offenders = Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("App.axaml", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"(Foreground|Background|BorderBrush|Fill|Stroke|Color)=""#[0-9A-Fa-f]{3,8}"""))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }

    // ---- Key controls render in both themes ----

    [AvaloniaFact]
    public async Task The_setup_pages_and_explorer_render_in_light_and_dark()
    {
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var store = new MemoryUiSettingsStore(new UiSettingsData { Theme = variant == ThemeVariant.Light ? ThemeChoice.Light : ThemeChoice.Dark });
                var (window, main, auth, _) = OpenWindow(store);
                Assert.Equal(variant, Application.Current!.RequestedThemeVariant);
                Assert.Equal(variant, window.ActualThemeVariant);

                foreach (var name in new[] { "SwitcherThemePicker", "SwitcherRail", "ConnectButton", "ConnectSources", "NamespaceSwitcherButton", "ModeChip" })
                    Assert.True(Find<Control>(window, name).IsEffectivelyVisible, $"{name} in {variant}");

                auth.WillSignIn("alice@contoso.example");
                await main.Accounts.AddAccountAsync();
                main.OpenSwitcher(SwitcherPage.Accounts);
                Settle(window);

                foreach (var name in new[] { "SwitcherThemePicker", "AddAccountButton", "SignInWithCodeButton", "AccountList", "BrowseTree", "BrowseSubscriptionBox" })
                    Assert.True(Find<Control>(window, name).IsEffectivelyVisible, $"{name} in {variant}");
                var primary = Find<Button>(window, "AddAccountButton");
                Assert.Equal(ColorOf("Accent", variant), Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color);
                Assert.Equal(ColorOf("Surface", variant), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);

                main.OpenSwitcher(SwitcherPage.Logins);
                Settle(window);
                Assert.True(Find<Control>(window, "AddLoginButton").IsEffectivelyVisible);

                main.Connect.NamespaceText = "contoso-synthetic";
                await main.Connect.ConnectCommand.ExecuteAsync();
                Settle(window);
                foreach (var name in new[] { "KindFilter", "QuickTree", "QuickSummaryText", "ResourceSearchBox" })
                    Assert.True(Find<Control>(window, name).IsEffectivelyVisible, $"{name} in {variant}");
                var queueIcon = Find<TreeView>(window, "QuickTree").GetVisualDescendants().OfType<Projektanker.Icons.Avalonia.Icon>()
                    .First(i => i.Classes.Contains("queue") && i.IsEffectivelyVisible);
                Assert.Equal(ColorOf("Queue", variant), Assert.IsAssignableFrom<ISolidColorBrush>(queueIcon.Foreground).Color);
                window.Close();
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaFact]
    public void The_theme_pickers_switch_the_whole_app_and_remember_it()
    {
        try
        {
            var store = new MemoryUiSettingsStore();
            var (window, main, _, _) = OpenWindow(store);
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);

            var dark = Find<ThemePicker>(window, "SwitcherThemePicker").GetVisualDescendants().OfType<RadioButton>().First(r => r.Name == "ThemeDark");
            dark.IsChecked = true;
            Settle(window);
            Assert.Equal((ThemeChoice.Dark, ThemeVariant.Dark, ThemeVariant.Dark), (store.Current.Theme, Application.Current.RequestedThemeVariant, window.ActualThemeVariant));
            // The same picker stays with the sheet on every page (one picker per surface: the switcher and Settings).
            main.IsAccountsPage = true;
            Settle(window);
            Assert.True(Find<ThemePicker>(window, "SwitcherThemePicker").GetVisualDescendants().OfType<RadioButton>().First(r => r.Name == "ThemeDark").IsChecked);
            Assert.Single(window.GetVisualDescendants().OfType<ThemePicker>(), p => p.IsEffectivelyVisible);

            main.Theme.IsSystem = true;
            Settle(window);
            Assert.Equal((ThemeChoice.System, ThemeVariant.Default), (store.Current.Theme, Application.Current.RequestedThemeVariant));
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    // ---- Accounts (ADS "Linked accounts"), Recent and Browse ----

    private static (MainWindow Window, MainViewModel Main, FakeAuthenticator Auth, TestVault Vault) OpenWindow(IUiSettingsStore? store = null, FakeArmReader? arm = null)
    {
        var auth = new FakeAuthenticator();
        arm ??= Arm();
        var vault = new TestVault();
        var catalog = new ProviderCatalog([new FakeBrokerProvider(), new ServiceBusBrokerProvider()], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, accounts: new AccountServices(auth, new MemoryAccountStore(), arm, vault.Vault), settings: store ?? new MemoryUiSettingsStore());
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        Settle(window);
        return (window, main, auth, vault);
    }

    private static FakeArmReader Arm()
    {
        var arm = new FakeArmReader();
        arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso")];
        arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo-a", "Demo A", Contoso), new SubscriptionRef("sub-demo-b", "Demo B", Contoso)];
        arm.Namespaces["sub-demo-a"] = [Ns("sbns-demo-001", "sub-demo-a", "rg-orders")];
        arm.Namespaces["sub-demo-b"] = [Ns("sbns-demo-002", "sub-demo-b", "rg-billing")];
        arm.Hubs["sub-demo-a"] = [Ns("evhns-demo-001", "sub-demo-a", "rg-orders") with { Kind = NamespaceKind.EventHubs, FullyQualifiedNamespace = "evhns-demo-001.servicebus.windows.net" }];
        arm.Entities["sbns-demo-001"] = [new EntitySnapshot(EntityKind.Queue, "orders", null, new EntityCounts(2, 1, 0, 0, 3), "Active", 100)];
        arm.Entities["sbns-demo-002"] = [];
        return arm;
    }

    private static NamespaceRef Ns(string name, string subscription, string group) => new(
        name, $"{name}.servicebus.windows.net", $"/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.ServiceBus/namespaces/{name}", group, "East US", subscription, Contoso);

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

    private static IEnumerable<BrowseNode> Walk(IEnumerable<BrowseNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));

    [AvaloniaFact]
    public async Task An_account_expands_to_its_tenants_and_can_be_refreshed()
    {
        var (window, main, auth, _) = OpenWindow();
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();
        Settle(window);

        var account = Assert.Single(main.Accounts.Accounts);
        var tenant = Assert.Single(account.Tenants);
        Assert.Equal(("Contoso", "2 subscriptions", false), (tenant.Name, tenant.Detail, tenant.IsError));
        Assert.Equal("1 tenant", account.TenantsToggleText);
        Assert.Equal("AL", account.Initials);
        Assert.True(account.RefreshCommand.CanExecute(null));
        Assert.True(main.Accounts.RefreshAllCommand.CanExecute(null));

        await account.RefreshCommand.ExecuteAsync();
        Assert.Equal(AccountState.Ready, account.State);
        Assert.Equal(2, main.Accounts.Namespaces.Count);
    }

    [Fact]
    public void The_device_code_is_picked_out_of_the_sign_in_text_for_the_copy_button()
    {
        Assert.Equal("DEMO-1234", AccountsViewModel.ParseDeviceCode("To sign in, use a web browser to open https://microsoft.com/devicelogin and enter the code DEMO-1234 to authenticate."));
        Assert.Equal("ABCD2345X", AccountsViewModel.ParseDeviceCode("enter the code ABCD2345X to authenticate"));
        Assert.Null(AccountsViewModel.ParseDeviceCode("Waiting for the browser."));
        Assert.Null(AccountsViewModel.ParseDeviceCode(null));
    }

    [AvaloniaFact]
    public async Task Browse_lists_subscriptions_groups_and_both_namespace_kinds_with_a_filter()
    {
        var (window, main, auth, vault) = OpenWindow();
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();
        main.OpenSwitcher(SwitcherPage.Accounts);
        Settle(window);
        var browse = main.Sources;

        Assert.Equal(["All subscriptions", "Demo A", "Demo B"], browse.Subscriptions.Select(s => s.Label));
        Assert.Equal("2 Service Bus namespaces, 1 Event Hubs namespace", browse.BrowseSummary);
        var namespaces = Walk(browse.Tree).Where(n => n.IsNamespace).ToList();
        Assert.Equal(["sbns-demo-001", "evhns-demo-001", "sbns-demo-002"], namespaces.Select(n => n.Label).OrderBy(n => n == "sbns-demo-002").ThenBy(n => n.StartsWith("evh", StringComparison.Ordinal)));
        Assert.Equal("fa-solid fa-wave-square", namespaces.Single(n => n.IsEventHubs).Icon);

        browse.Subscription = browse.Subscriptions[2];
        Settle(window);
        Assert.Equal(["sbns-demo-002"], Walk(browse.Tree).Where(n => n.IsNamespace).Select(n => n.Label));

        // A rebuild keeps the picked subscription (it used to blank the picker).
        await main.Accounts.RefreshAllCommand.ExecuteAsync();
        Settle(window);
        Assert.Same(browse.Subscriptions[2], browse.Subscription);
        Assert.Equal("Demo B", Find<ComboBox>(window, "BrowseSubscriptionBox").SelectedItem?.ToString());

        browse.Subscription = SubscriptionFilter.All;
        browse.FilterText = "billing";
        Assert.Equal(["sbns-demo-002"], Walk(browse.Tree).Where(n => n.IsNamespace).Select(n => n.Label));
        browse.ClearFilterCommand.Execute(null);
        Assert.Equal(3, Walk(browse.Tree).Count(n => n.IsNamespace));

        // Event Hubs: browse and save as a login (no secret), never opened in the Service Bus explorer.
        browse.SelectedNode = Walk(browse.Tree).Single(n => n.IsEventHubs);
        Assert.True(browse.IsHubSelected);
        Assert.False(browse.OpenCommand.CanExecute(null));
        await browse.SaveHubCommand.ExecuteAsync();
        var login = Assert.Single(vault.Vault.List());
        Assert.Equal((LoginKind.EventHubsEntra, "evhns-demo-001.servicebus.windows.net"), (login.Kind, login.Field("namespace")));
        Assert.Equal(0, vault.Secrets.Count);
    }

    [AvaloniaFact]
    public async Task Opening_from_browse_lands_in_the_explorer_and_shows_up_in_recent()
    {
        var store = new MemoryUiSettingsStore();
        var (window, main, auth, _) = OpenWindow(store);
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();
        Settle(window);
        var browse = main.Sources;
        Assert.Equal(ConnectSourcesViewModel.SavedTab, browse.SelectedTab);
        Assert.False(browse.HasRecent);

        browse.SelectedNode = Walk(browse.Tree).Single(n => n.Label == "sbns-demo-001");
        await browse.OpenCommand.ExecuteAsync();
        Settle(window);

        Assert.False(main.IsSwitcherOpen);
        Assert.True(main.QuickView.IsConnected);
        var recent = Assert.Single(browse.Recent);
        Assert.Equal("sbns-demo-001 as alice@contoso.example", recent.Title);
        Assert.Equal("sbns-demo-001", Assert.Single(store.Current.Recent).Namespace!.Name);
        Assert.Null(store.Current.Recent[0].LoginId);

        main.Connect.DisconnectCommand.Execute(null);
        browse.SelectedRecent = recent;
        await browse.OpenRecentCommand.ExecuteAsync();
        Assert.True(main.Connect.IsConnected);
        Assert.Single(browse.Recent);

        browse.ClearRecentCommand.Execute(null);
        Assert.Empty(browse.Recent);
        Assert.Empty(store.Current.Recent);
    }
}
