// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class ConnectViewModelTests
{
    private static (MainViewModel Main, FakeBrokerProvider Provider) Create(IReadOnlyList<EntitySnapshot>? queues = null)
    {
        var provider = new FakeBrokerProvider(queues);
        var catalog = new ProviderCatalog([provider], ProviderCatalog.PlannedProviders);
        return (new MainViewModel(catalog), provider);
    }

    [Fact]
    public void Enabled_provider_is_preselected_and_planned_ones_are_disabled()
    {
        var (main, _) = Create();
        var providers = main.Connect.Providers;

        Assert.Equal(3, providers.Count);
        Assert.True(providers[0].IsSelected);
        Assert.Equal("Apache Kafka (planned)", providers[1].Label);
        Assert.Equal("AWS SQS / SNS (planned)", providers[2].Label);
        Assert.False(providers[1].IsEnabled);
        Assert.False(providers[2].IsEnabled);
    }

    [Fact]
    public void A_planned_provider_cannot_be_selected()
    {
        var (main, _) = Create();
        main.Connect.Providers[1].IsSelected = true;

        Assert.False(main.Connect.Providers[1].IsSelected);
        Assert.Equal(FakeBrokerProvider.Id, main.Connect.SelectedProvider?.Id);
    }

    [Fact]
    public void Entra_is_the_default_and_needs_a_namespace()
    {
        var (main, _) = Create();
        Assert.True(main.Connect.UseEntra);
        Assert.Equal(AuthMode.Entra, main.Connect.Mode);
        Assert.False(main.Connect.CanConnect);
        Assert.False(main.Connect.ConnectCommand.CanExecute(null));

        main.Connect.NamespaceText = "contoso-synthetic";
        Assert.True(main.Connect.CanConnect);
        Assert.True(main.Connect.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void Switching_to_connection_string_needs_the_secret_not_the_namespace()
    {
        var (main, _) = Create();
        main.Connect.NamespaceText = "contoso-synthetic";
        main.Connect.UseConnectionString = true;

        Assert.False(main.Connect.UseEntra);
        Assert.Equal(AuthMode.ConnectionString, main.Connect.Mode);
        Assert.False(main.Connect.CanConnect);
        main.Connect.ConnectionString = "Endpoint=sb://synthetic/;Key=not-real";
        Assert.True(main.Connect.CanConnect);
    }

    [Fact]
    public async Task Entra_connect_sends_an_entra_connection_and_loads_queues()
    {
        var (main, provider) = Create();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.Equal(AuthMode.Entra, provider.LastConnection?.AuthMode);
        Assert.Equal("contoso-synthetic", provider.LastConnection?.Endpoint);
        Assert.Null(provider.LastConnection?.Secret);
        Assert.True(main.Connect.IsConnected);
        Assert.True(main.QuickView.IsConnected);
        Assert.Equal(4, main.QuickView.QueueCount);
        Assert.Equal(4, main.QuickView.Tree.Count);
    }

    [Fact]
    public async Task Connection_string_is_passed_once_then_cleared_from_memory()
    {
        var (main, provider) = Create();
        main.Connect.UseConnectionString = true;
        main.Connect.ConnectionString = "Endpoint=sb://synthetic/;Key=not-real";

        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.Equal(AuthMode.ConnectionString, provider.LastConnection?.AuthMode);
        Assert.Equal("Endpoint=sb://synthetic/;Key=not-real", provider.LastConnection?.Secret?.Reveal());
        Assert.Equal(string.Empty, main.Connect.ConnectionString);
        Assert.DoesNotContain("not-real", main.Connect.ConnectedTo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_validation_message_is_shown_and_nothing_opens()
    {
        var (main, provider) = Create();
        main.Connect.NamespaceText = "bad-endpoint";

        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.True(main.Connect.HasValidationMessage);
        Assert.Equal("That endpoint is not valid.", main.Connect.ValidationMessage);
        Assert.Equal(0, provider.OpenCalls);
        Assert.False(main.Connect.IsConnected);
        Assert.True(main.QuickView.IsNotConnected);
    }

    [Fact]
    public async Task Disconnect_clears_the_grid_and_disposes_the_inventory()
    {
        var (main, provider) = Create();
        main.Connect.NamespaceText = "contoso-synthetic";
        await main.Connect.ConnectCommand.ExecuteAsync();
        Assert.True(main.Connect.DisconnectCommand.CanExecute(null));

        main.Connect.DisconnectCommand.Execute(null);

        Assert.False(main.Connect.IsConnected);
        Assert.True(main.QuickView.IsNotConnected);
        Assert.Empty(main.QuickView.Tree);
        Assert.True(provider.Inventory.Disposed);
        Assert.Equal("Choose a namespace", main.NamespaceLabel);
    }

    [Fact]
    public async Task Real_service_bus_provider_rejects_a_bad_namespace_before_any_network_call()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog());
        Assert.Equal(ServiceBusBrokerProvider.ProviderId, main.Connect.SelectedProvider?.Id);
        main.Connect.NamespaceText = "abc";

        await main.Connect.ConnectCommand.ExecuteAsync();

        Assert.False(main.Connect.IsConnected);
        Assert.Contains("6 to 50", main.Connect.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_catalog_has_service_bus_enabled_and_two_planned()
    {
        var catalog = MainViewModel.DefaultCatalog();
        Assert.Equal(
            [ServiceBusBrokerProvider.ProviderId, ProviderCatalog.KafkaId, ProviderCatalog.AwsSqsSnsId],
            catalog.Descriptors.Select(d => d.Id).ToArray());
        Assert.Equal(1, catalog.Descriptors.Count(d => d.IsEnabled));
    }
}
