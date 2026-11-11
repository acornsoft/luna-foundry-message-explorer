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

/// <summary>The full-window review of #19: view-model side.</summary>
public sealed class UxPass2Tests
{
    private static (MainViewModel Main, FakeAuthenticator Auth) Build(string? savedEndpoint = null, int savedCount = 1)
    {
        var auth = new FakeAuthenticator();
        var vault = new TestVault();
        for (var i = 0; savedEndpoint is not null && i < savedCount; i++)
        {
            vault.Vault.AddAsync(
                new LoginDraft($"Reader {i + 1} (demo)", LoginKind.ServiceBusConnectionString, new Dictionary<string, string> { ["endpoint"] = savedEndpoint }),
                new RuntimeSecret("fake-not-a-real-key")).GetAwaiter().GetResult();
        }

        var catalog = new ProviderCatalog([new FakeBrokerProvider(SyntheticQueues.WithTopics())], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, accounts: new AccountServices(auth, new MemoryAccountStore(), ThemeAndSetupTests.Arm(), vault.Vault),
            settings: new MemoryUiSettingsStore());
        return (main, auth);
    }

    private static async Task<MainViewModel> ConnectedAsync(string? savedEndpoint = null)
    {
        var (main, _) = Build(savedEndpoint);
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        return main;
    }

    // ---- 1 Row badges: at most active and dead-lettered, each named ----

    [Fact]
    public async Task Rows_show_at_most_an_active_and_a_dead_lettered_badge_each_with_a_name()
    {
        var quick = (await ConnectedAsync()).QuickView;

        var credit = quick.Find("gsl-disputes-provisional-credit")!; // 4 active, 3 dead-lettered
        Assert.Equal(("4", "4 active"), (credit.PrimaryBadge, credit.PrimaryBadgeName));
        Assert.Equal(("3", "3 dead-lettered"), (credit.DeadLetterBadge, credit.DeadLetterName));

        var intake = quick.Find("gsl-disputes-intake")!; // 12 active, 2 scheduled: no scheduled badge on the row
        Assert.Equal(1, intake.BadgeCount);
        var sync = quick.Find("gsl-case-sync")!; // 40 active, transfer dead-letter 1: no transfer badge on the row
        Assert.Equal(1, sync.BadgeCount);

        // A topic has no active count of its own: its receivers carry the badges.
        var events = quick.Find("gsl-dispute-events")!;
        Assert.False(events.HasPrimaryBadge);
        events.IsExpanded = true;
        await events.ExpandTask;
        var writer = events.Children.Single(c => c.Name == "notice-writer");
        Assert.Equal(("1 active", "2 dead-lettered", 2), (writer.PrimaryBadgeName, writer.DeadLetterName, writer.BadgeCount));
        Assert.All(quick.Tree.Concat(events.Children), n => Assert.InRange(n.BadgeCount, 0, 2));
    }

    // ---- 2 Zero cards show 0 ----

    [Fact]
    public async Task A_zero_card_shows_zero_and_nothing_to_peek()
    {
        var quick = (await ConnectedAsync()).QuickView;
        quick.SelectedNode = quick.Find("gsl-disputes-intake"); // dead-letter 0
        var dead = quick.Entity!.LaneOf(MessageLane.DeadLetter)!;
        Assert.Equal("0", dead.CountText);
        Assert.Equal("Nothing to peek", dead.CardFooter);
        Assert.DoesNotContain(quick.Entity.Lanes, l => l.CountText == "Empty" || l.CardFooter.Contains("Empty", StringComparison.Ordinal));
    }

    // ---- 5 Match line wording ----

    [Fact]
    public async Task The_match_line_names_filters_after_a_colon_and_quotes_the_search()
    {
        var quick = (await ConnectedAsync()).QuickView;

        quick.KindFilter = ResourceKindFilter.Topics;
        Assert.Equal("2 of 6: Topics", quick.MatchText);

        quick.Filter = "reconciler";
        await quick.SearchLoad;
        Assert.Equal("1 of 6 match 'reconciler': Topics", quick.MatchText);

        quick.ClearFiltersCommand.Execute(null);
        quick.Filter = "audit";
        await quick.SearchLoad;
        Assert.Equal("1 of 6 match 'audit'", quick.MatchText);
    }

    // ---- 6 Quick switch rows ----

    [Fact]
    public async Task Quick_switch_rows_have_the_namespace_on_top_account_and_cloud_below_and_a_check_on_the_open_one()
    {
        var main = await ConnectedAsync(savedEndpoint: "synthetic.servicebus.windows.net");

        var saved = Assert.Single(main.QuickSaved);
        Assert.Equal("synthetic", saved.Title);
        Assert.Equal("connection string · Azure public", saved.Detail);
        Assert.True(saved.IsCurrent);
        Assert.Same(main.Connect.SavedConnections[0], saved.Login);
        Assert.Contains("open now", saved.Name, StringComparison.Ordinal);

        main.Connect.DisconnectCommand.Execute(null);
        Assert.False(Assert.Single(main.QuickSaved).IsCurrent);
    }

    [Fact]
    public void A_recent_namespace_shows_its_name_over_account_and_cloud()
    {
        var item = MainViewModel.QuickSwitchFor(
            new RecentItemViewModel(new RecentConnection("sbns-demo-001 as alice@contoso.example", DateTimeOffset.UtcNow, AccountName: "alice@contoso.example",
                Namespace: new Adapters.ServiceBus.Arm.NamespaceRef("sbns-demo-001", "sbns-demo-001.servicebus.usgovcloudapi.net", "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001", "rg", "usgovvirginia", "s", "t", AzureClouds.UsGovernmentId)), "1h ago"),
            [], openNamespace: null);
        Assert.Equal(("sbns-demo-001", "alice@contoso.example · Azure US Government", false), (item.Title, item.Detail, item.IsCurrent));
    }

    // ---- 7, 8 Connect reasons; 10 one saved login ----

    [Fact]
    public async Task With_accounts_and_no_account_chosen_connect_says_to_choose_one()
    {
        var (main, auth) = Build();
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();
        main.Connect.NamespaceText = "sbns-demo-001.servicebus.windows.net";
        Assert.True(main.Connect.CanConnect);

        main.Connect.SelectedAccount = null;
        Assert.False(main.Connect.CanConnect);
        Assert.Equal("Choose an Entra account to connect.", main.Connect.ConnectBlockedReason);
        Assert.Equal("Choose which signed-in account to use.", main.Connect.EntraHint);
    }

    [Fact]
    public void Exactly_one_saved_login_is_selected_for_you()
    {
        var (one, _) = Build("sbns-demo-001.servicebus.windows.net", savedCount: 1);
        Assert.NotNull(one.Connect.SelectedSaved);
        Assert.Null(one.Connect.ConnectSavedBlockedReason);

        var (two, _) = Build("sbns-demo-001.servicebus.windows.net", savedCount: 2);
        Assert.Null(two.Connect.SelectedSaved);
    }

    // ---- 9 Cards wrap to 2 by 2 in a narrow pane ----

    [Theory]
    [InlineData(1000, 4, 1, 4)]
    [InlineData(719, 4, 2, 2)]
    [InlineData(600, 3, 2, 2)]
    [InlineData(600, 2, 1, 2)]
    [InlineData(600, 1, 1, 1)]
    public void Lane_cards_switch_to_two_by_two_below_720px(double width, int cards, int rows, int columns) =>
        Assert.Equal((rows, columns), Views.QuickView.CardGrid(width, cards));
}
