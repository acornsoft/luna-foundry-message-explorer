// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The shell around the one slim bar: namespace switcher, mode chip, voice, settings and startup.</summary>
public sealed class MainShellTests
{
    private static async Task<(MainViewModel Main, FakeBrokerProvider Provider)> ConnectedAsync()
    {
        var provider = new FakeBrokerProvider(SyntheticQueues.WithTopics());
        var main = new MainViewModel(new ProviderCatalog([provider], ProviderCatalog.PlannedProviders));
        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();
        await main.QuickView.EnsureLoadedAsync();
        return (main, provider);
    }

    [Fact]
    public async Task Connecting_reads_queues_and_topics_once_and_disconnecting_clears_the_resources()
    {
        var (main, provider) = await ConnectedAsync();

        Assert.Equal(1, provider.Inventory.ListCalls);
        Assert.Equal(1, provider.Inventory.TopicCalls);
        Assert.Empty(provider.Inventory.SubscriptionCalls);
        Assert.Equal(6, main.QuickView.AllCount);
        Assert.Equal(4, main.QuickView.QueueCount);
        Assert.Equal(2, main.QuickView.TopicCount);

        main.Connect.DisconnectCommand.Execute(null);

        Assert.True(main.QuickView.IsNotConnected);
        Assert.Empty(main.QuickView.Tree);
        Assert.Equal("Choose a namespace", main.NamespaceLabel);
    }

    [Fact]
    public async Task The_switcher_is_open_until_a_namespace_connects_then_names_it_in_the_bar()
    {
        var provider = new FakeBrokerProvider(SyntheticQueues.WithTopics());
        var main = new MainViewModel(new ProviderCatalog([provider], ProviderCatalog.PlannedProviders));

        Assert.True(main.IsSwitcherOpen);
        Assert.True(main.IsAccountsPage); // first run: the next step is adding an account
        Assert.Equal("Choose a namespace", main.NamespaceLabel);
        Assert.False(main.CloseTopmost()); // Esc can't dismiss it before there is anything behind it.
        Assert.True(main.IsSwitcherOpen);

        main.Connect.NamespaceText = "synthetic.example";
        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.False(main.IsSwitcherOpen);
        Assert.Equal(main.QuickView.NamespaceName, main.NamespaceLabel);
        Assert.NotEqual("Choose a namespace", main.NamespaceLabel);

        main.OpenSwitcherCommand.Execute(null);
        Assert.True(main.IsSwitcherOpen);
        main.IsLoginsPage = true;
        Assert.Equal(SwitcherPage.Logins, main.SwitcherPage);
        Assert.False(main.IsConnectPage);
        Assert.True(main.CloseTopmost());
        Assert.False(main.IsSwitcherOpen);
    }

    [Fact]
    public void The_mode_chip_reads_read_only_with_the_one_explanation_and_switches_to_move()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));

        Assert.False(main.MoveModeOn);
        Assert.True(main.IsReadOnly);
        Assert.Equal("Read-only", main.ModeText);
        Assert.Equal(main.Shell.ReadOnlyTip, main.ModeTip);
        Assert.Equal("Read-only: peeks, never changes messages", main.ModeTip);

        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        main.MoveModeOn = true;

        Assert.True(main.Move.IsOn);
        Assert.False(main.IsReadOnly);
        Assert.Equal("Move", main.ModeText);
        Assert.Contains("Click to go back to Read-only.", main.ModeTip, StringComparison.Ordinal);
        Assert.Contains(nameof(MainViewModel.ModeText), changed);
        Assert.Contains(nameof(MainViewModel.IsReadOnly), changed);

        main.MoveModeOn = false;
        Assert.False(main.Move.IsOn);
    }

    [Fact]
    public void Voice_and_test_jobs_open_as_panels_and_esc_closes_the_topmost()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));
        main.IsSwitcherOpen = false;

        main.IsVoiceOpen = true;
        main.OpenJobsCommand.Execute(null);
        Assert.True(main.IsJobsOpen);

        Assert.True(main.CloseTopmost());
        Assert.False(main.IsJobsOpen);
        Assert.True(main.IsVoiceOpen);
        Assert.True(main.CloseTopmost());
        Assert.False(main.IsVoiceOpen);
        Assert.False(main.CloseTopmost());
    }

    [Theory]
    [InlineData("accounts", AppSection.Accounts)]
    [InlineData("logins", AppSection.Logins)]
    [InlineData("resources", AppSection.Resources)]
    [InlineData("queues", AppSection.Resources)]
    [InlineData("topics", AppSection.Resources)]
    [InlineData("quick", AppSection.Resources)]
    [InlineData("jobs", AppSection.Jobs)]
    [InlineData("move", AppSection.Move)]
    [InlineData("voice", AppSection.Voice)]
    public void The_old_tab_names_still_open_the_matching_place(string tab, AppSection section) =>
        Assert.Equal(section, StartupOptions.Parse(["--tab", tab], _ => null).Section);

    [Fact]
    public void Startup_options_pick_the_section_collection_and_demo()
    {
        var options = StartupOptions.Parse(["--collection", Repo.CrewStock], _ => null);
        Assert.Equal(AppSection.Jobs, options.Section);
        Assert.False(options.SyntheticDemo);

        Assert.True(StartupOptions.Parse([], name => name == "LFME_SYNTHETIC_DEMO" ? "1" : null).SyntheticDemo);
        var paced = StartupOptions.Parse(["--pace", "600", "--run"], _ => null);
        Assert.Equal(600, paced.Pace);
        Assert.True(paced.Run);

        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));
        (options with { Pace = 600 }).Apply(main);
        Assert.True(main.IsJobsOpen);
        Assert.Equal(600, main.Jobs.SelectedPace.SimulatedSecondsPerSecond);
        Assert.Equal(["shift-sync"], main.Jobs.Jobs.Select(j => j.Name));

        var voice = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));
        StartupOptions.Parse(["--tab", "voice"], _ => null).Apply(voice);
        Assert.True(voice.IsVoiceOpen);

        var logins = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: false));
        StartupOptions.Parse(["--tab", "logins"], _ => null).Apply(logins);
        Assert.True(logins.IsSwitcherOpen);
        Assert.True(logins.IsLoginsPage);
    }

    [Fact]
    public async Task The_synthetic_demo_connects_and_lists_resources_with_topic_receivers()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(syntheticDemo: true));
        StartupOptions.Parse(["--synthetic-demo", "--tab", "topics"], _ => null).Apply(main);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (main.QuickView.TopicCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(main.Connect.IsConnected);
        Assert.False(main.IsSwitcherOpen);
        Assert.True(main.QuickView.QueueCount > 0);
        var orderEvents = Assert.Single(main.QuickView.Tree, n => n.Name == "order-events");
        Assert.Equal(ResourceKind.Topic, orderEvents.Resource!.Kind);
        Assert.Equal(3, orderEvents.Resource.ReceiverCount);
    }
}
