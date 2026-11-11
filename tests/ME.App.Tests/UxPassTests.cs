// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Settings;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The UX review of the slim bar (PR #18): behaviour changes, view-model side.</summary>
public sealed class UxPassTests
{
    private static (MainViewModel Main, FakeAuthenticator Auth, FakeBrokerProvider Provider) Build(UiSettingsData? data = null, bool savedLogin = false)
    {
        var auth = new FakeAuthenticator();
        var vault = new TestVault();
        if (savedLogin)
        {
            vault.Vault.AddAsync(
                new LoginDraft("Reader (demo)", LoginKind.ServiceBusConnectionString, new Dictionary<string, string> { ["endpoint"] = "sbns-demo-001.servicebus.windows.net" }),
                new RuntimeSecret("fake-not-a-real-key")).GetAwaiter().GetResult();
        }

        var provider = new FakeBrokerProvider(SyntheticQueues.WithTopics());
        var catalog = new ProviderCatalog([provider], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, accounts: new AccountServices(auth, new MemoryAccountStore(), ThemeAndSetupTests.Arm(), vault.Vault),
            settings: new MemoryUiSettingsStore(data ?? new UiSettingsData()));
        return (main, auth, provider);
    }

    private static async Task<(MainViewModel Main, FakeBrokerProvider Provider)> ConnectedAsync()
    {
        var (main, _, provider) = Build();
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        return (main, provider);
    }

    // ---- (a) The switcher opens on the pane with the next action ----

    [Fact]
    public void First_run_lands_on_accounts_with_focus_on_add_an_account()
    {
        var (main, _, _) = Build();

        Assert.True(main.IsSwitcherOpen);
        Assert.Equal(SwitcherPage.Accounts, main.SwitcherPage);
        Assert.Equal(SwitcherFocus.AddAccount, main.SwitcherFocus);
    }

    [Fact]
    public async Task Accounts_but_nothing_saved_lands_on_accounts_with_browse_in_view()
    {
        var (main, auth, _) = Build();
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();
        main.OpenSwitcher();

        Assert.Equal(SwitcherPage.Accounts, main.SwitcherPage);
        Assert.Equal(SwitcherFocus.Browse, main.SwitcherFocus);
    }

    [Fact]
    public void Saved_logins_but_no_recents_land_on_connect_saved()
    {
        var (main, _, _) = Build(savedLogin: true);

        Assert.Equal(SwitcherPage.Connect, main.SwitcherPage);
        Assert.Equal(ConnectSourcesViewModel.SavedTab, main.Sources.SelectedTab);
        Assert.Equal(SwitcherFocus.None, main.SwitcherFocus);
    }

    [Fact]
    public void Recents_land_on_connect_recent()
    {
        var data = new UiSettingsData { Recent = [new RecentConnection("sbns-demo-001 as alice@contoso.example", DateTimeOffset.UtcNow.AddHours(-1))] };
        var (main, _, _) = Build(data, savedLogin: true);

        Assert.Equal(SwitcherPage.Connect, main.SwitcherPage);
        Assert.Equal(ConnectSourcesViewModel.RecentTab, main.Sources.SelectedTab);
    }

    [Fact]
    public async Task A_page_the_person_picked_is_kept_when_accounts_arrive_later()
    {
        var (main, auth, _) = Build();
        main.IsLoginsPage = true;
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();

        Assert.Equal(SwitcherPage.Logins, main.SwitcherPage);
    }

    // ---- (b) No summary line; a match line only while the list is narrowed; a dead-letters filter ----

    [Fact]
    public async Task The_match_line_shows_only_while_a_filter_or_search_narrows_the_list()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        Assert.False(quick.IsNarrowed);
        Assert.Equal(string.Empty, quick.MatchText);

        quick.Filter = "reconciler";
        await quick.SearchLoad;
        Assert.True(quick.IsNarrowed);
        Assert.Equal("1 of 6 match 'reconciler'", quick.MatchText);

        quick.Filter = string.Empty;
        quick.KindFilter = ResourceKindFilter.Queues;
        Assert.Equal("4 of 6: Queues", quick.MatchText);

        quick.ClearFiltersCommand.Execute(null);
        Assert.False(quick.IsNarrowed);
        Assert.Equal(ResourceKindFilter.All, quick.KindFilter);
        Assert.All(quick.Tree, n => Assert.True(n.IsVisible));
    }

    [Fact]
    public async Task Has_dead_letters_keeps_queues_with_dead_letters_and_topics_whose_receivers_have_them()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;

        quick.OnlyDeadLetters = true;
        await quick.SearchLoad;

        Assert.Equal(["gsl-case-sync", "gsl-dispute-events", "gsl-disputes-provisional-credit"],
            quick.Tree.Where(n => n.IsVisible).Select(n => n.Name).Order(StringComparer.Ordinal));
        var events = quick.Find("gsl-dispute-events")!;
        Assert.Equal(["audit-copy", "notice-writer"], events.Children.Where(c => c.IsVisible).Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal("3 of 6: Has dead letters", quick.MatchText);

        quick.KindFilter = ResourceKindFilter.Queues;
        Assert.Equal("2 of 6: Queues, Has dead letters", quick.MatchText);
    }

    // ---- Mode chip (findings 1 and 2 held pending the Move redesign: chip unchanged, one-phrase tooltip) ----

    [Fact]
    public async Task The_read_only_chip_tooltip_is_one_phrase_and_the_chip_is_otherwise_unchanged()
    {
        var (main, _) = await ConnectedAsync();

        Assert.Equal("Read-only", main.ModeText);
        Assert.Equal("Read-only: peeks, never changes messages", main.ModeTip);

        main.MoveModeOn = true;
        Assert.Equal("Move", main.ModeText);
    }

    // ---- 3 Namespace identity ----

    [Fact]
    public async Task The_switcher_and_the_move_banner_name_the_namespace_account_and_cloud()
    {
        var (main, _) = await ConnectedAsync();

        Assert.Equal("synthetic", main.NamespaceLabel);
        Assert.False(string.IsNullOrEmpty(main.NamespaceIdentity));
        Assert.Contains(main.NamespaceIdentity!, main.NamespaceDetail, StringComparison.Ordinal);
        Assert.StartsWith($"Move mode is on for synthetic ({main.NamespaceAccount}, {main.NamespaceCloud})", main.MoveBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_namespace_from_an_azure_endpoint_names_its_cloud_and_account()
    {
        Assert.Equal(("alice@contoso.example", "Azure public"), MainViewModel.IdentityOf("sbns-demo-001.servicebus.windows.net", "alice@contoso.example", "Azure Service Bus"));
        Assert.Equal(("Azure sign-in", "Azure US Government"), MainViewModel.IdentityOf("sbns-gov.servicebus.usgovcloudapi.net", null, "Azure Service Bus"));
        Assert.Equal(("connection string", "Fake broker"), MainViewModel.IdentityOf(null, null, "Fake broker", connectionString: true));
    }

    // ---- 7, 9, 10 Counts ----

    [Fact]
    public async Task Zero_counts_are_hidden_and_the_dead_letter_badge_has_a_spoken_name()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;

        var notices = quick.Find("gsl-notices-outbound")!; // active 0, scheduled 9
        Assert.False(notices.HasPrimaryBadge);
        var credit = quick.Find("gsl-disputes-provisional-credit")!;
        Assert.Equal("3 dead-lettered", credit.DeadLetterName);

        quick.SelectedNode = notices;
        // A zero card shows 0 like the others (never "Empty"), captioned "Nothing to peek".
        var active = quick.Entity!.LaneOf(MessageLane.Active)!;
        Assert.Equal("0", active.CountText);
        Assert.Equal("Nothing to peek", active.CardFooter);
        Assert.Equal("9", quick.Entity.LaneOf(MessageLane.Scheduled)!.CountText);
    }

    [Fact]
    public async Task Every_entity_shows_cards_and_sections_in_one_fixed_order()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        var events = quick.Find("gsl-dispute-events")!;
        events.IsExpanded = true;
        await events.ExpandTask;

        var order = new[] { MessageLane.Active, MessageLane.DeadLetter, MessageLane.Scheduled, MessageLane.TransferDeadLetter };
        foreach (var node in new[] { quick.Find("gsl-disputes-intake")!, quick.Find("notice-writer", "gsl-dispute-events")!, events })
        {
            quick.SelectedNode = node;
            var lanes = quick.Entity!.Lanes.Select(l => l.Lane).ToList();
            Assert.Equal(order.Where(lanes.Contains), lanes);
        }
    }

    // ---- 6 Kind badges ----

    [Fact]
    public async Task Kind_badges_show_only_on_topics_and_receivers_under_all_and_never_under_a_kind_filter()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        var events = quick.Find("gsl-dispute-events")!;
        events.IsExpanded = true;
        await events.ExpandTask;
        var queue = quick.Find("gsl-disputes-intake")!;

        Assert.False(queue.ShowKindTag);
        Assert.Equal("Queue", queue.KindName);
        Assert.True(events.ShowKindTag);
        Assert.True(events.Children[0].ShowKindTag);

        quick.KindFilter = ResourceKindFilter.Topics;
        Assert.False(events.ShowKindTag);
        Assert.False(events.Children[0].ShowKindTag);
    }

    // ---- 8 Search keeps the selection ----

    [Fact]
    public async Task Search_that_hides_the_selection_keeps_it_pinned_with_a_pin_tip()
    {
        var (main, _) = await ConnectedAsync();
        var quick = main.QuickView;
        var intake = quick.Find("gsl-disputes-intake")!;
        quick.SelectedNode = intake;

        quick.Filter = "ledger";
        await quick.SearchLoad;

        Assert.Same(intake, quick.SelectedNode);
        Assert.True(intake.IsVisible);
        Assert.True(intake.IsPinned);
        Assert.Equal("Selected, doesn't match", QuickTreeNode.PinTip);
        Assert.Equal("1 of 6 match 'ledger'", quick.MatchText);

        quick.Filter = string.Empty;
        Assert.False(intake.IsPinned);
    }

    // ---- 12 Shortcuts, 13 reasons ----

    [Fact]
    public void Shortcut_hints_follow_the_platform()
    {
        Assert.Equal("⌘F", Shortcuts.Format("F", mac: true));
        Assert.Equal("Ctrl+F", Shortcuts.Format("F", mac: false));
        var main = new MainViewModel(MainViewModel.DefaultCatalog(false));
        Assert.EndsWith(Shortcuts.Format("F"), main.SearchHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_connect_says_why()
    {
        var (main, _, _) = Build();
        main.Connect.NamespaceText = string.Empty;
        Assert.False(main.Connect.CanConnect);
        Assert.Equal("Enter a namespace to connect.", main.Connect.ConnectBlockedReason);

        main.Connect.UseConnectionString = true;
        Assert.Equal("Paste a connection string to connect.", main.Connect.ConnectBlockedReason);

        main.Connect.ConnectionString = "Endpoint=sb://synthetic/;Key=not-real";
        Assert.True(main.Connect.CanConnect);
        Assert.Null(main.Connect.ConnectBlockedReason);
        Assert.Equal("Pick a saved login first.", main.Connect.ConnectSavedBlockedReason);
    }

    // ---- 15 Quick switch ----

    [Fact]
    public async Task The_quick_switch_opens_a_recent_or_a_saved_login_and_manage_opens_the_sheet()
    {
        var (main, _, provider) = Build(savedLogin: true);
        main.IsSwitcherOpen = false;

        Assert.True(main.HasQuickSwitchItems);
        var saved = Assert.Single(main.Connect.SavedConnections);
        Assert.True(main.QuickConnectSavedCommand.CanExecute(saved));

        main.ManageAccountsCommand.Execute(null);
        Assert.True(main.IsSwitcherOpen);
        Assert.Equal(SwitcherPage.Connect, main.SwitcherPage);
        await Task.CompletedTask;
        Assert.Equal(0, provider.OpenCalls);
    }

    // ---- 16 Voice ----

    [Fact]
    public void With_no_audio_device_the_question_box_moves_up_next_to_start()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(false));
        Assert.NotNull(main.Voice.AudioNote);
        Assert.True(main.Voice.AskNearStart);
    }
}
