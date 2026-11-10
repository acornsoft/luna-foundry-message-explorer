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
        Assert.Equal(QueueListState.Loaded, main.Queues.State);
        Assert.Equal(4, main.Queues.Rows.Count);
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
        Assert.Equal(QueueListState.NotConnected, main.Queues.State);
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
        Assert.Equal(QueueListState.NotConnected, main.Queues.State);
        Assert.Empty(main.Queues.Rows);
        Assert.True(provider.Inventory.Disposed);
        Assert.False(main.Queues.RefreshCommand.CanExecute(null));
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

public class QueuesViewModelTests
{
    private static FakeInventory Inventory(IReadOnlyList<EntitySnapshot>? queues = null) =>
        new(new FakeBrokerProvider().Descriptor, queues ?? SyntheticQueues.Standard());

    [Fact]
    public void Starts_not_connected_with_refresh_disabled()
    {
        var vm = new QueuesViewModel();
        Assert.True(vm.IsNotConnected);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.Summary);
    }

    [Fact]
    public async Task Loading_state_is_visible_while_the_read_is_in_flight()
    {
        var inventory = Inventory();
        inventory.Gate = new TaskCompletionSource();
        var vm = new QueuesViewModel();

        var load = vm.LoadAsync(inventory);
        Assert.True(vm.IsLoading);
        Assert.False(vm.HasRows);

        inventory.Gate.SetResult();
        await load;
        Assert.True(vm.HasRows);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Empty_namespace_shows_the_empty_state()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory([]));
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
        Assert.Equal("This namespace has no queues.", vm.EmptyText);
    }

    [Fact]
    public async Task Loaded_state_summarises_dead_letter_queues()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());
        Assert.Equal(QueueListState.Loaded, vm.State);
        Assert.Equal(2, vm.DeadLetterQueueCount);
        Assert.Equal("4 queues, 2 with dead letters. Read-only.", vm.Summary);
        Assert.Equal(1, vm.PagesRead);
    }

    [Fact]
    public async Task Broker_error_shows_its_plain_message_and_clears_rows()
    {
        var inventory = Inventory();
        var vm = new QueuesViewModel();
        await vm.LoadAsync(inventory);
        inventory.FailWith = new BrokerException(BrokerErrorKind.Forbidden, BrokerException.Explain(BrokerErrorKind.Forbidden));

        await vm.RefreshCommand.ExecuteAsync();

        Assert.True(vm.IsError);
        Assert.Empty(vm.Rows);
        Assert.Equal(BrokerException.Explain(BrokerErrorKind.Forbidden), vm.ErrorMessage);
        Assert.False(vm.ErrorIsRetryable);
    }

    [Fact]
    public async Task Unexpected_error_never_leaks_its_text()
    {
        var inventory = Inventory();
        inventory.FailWith = new InvalidOperationException("internal detail with Key=not-real");
        var vm = new QueuesViewModel();

        await vm.LoadAsync(inventory);

        Assert.True(vm.IsError);
        Assert.DoesNotContain("not-real", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_reads_again_and_recovers_from_an_error()
    {
        var inventory = Inventory();
        inventory.FailWith = new BrokerException(BrokerErrorKind.Throttled, "busy");
        var vm = new QueuesViewModel();
        await vm.LoadAsync(inventory);
        Assert.True(vm.IsError);
        Assert.True(vm.ErrorIsRetryable);

        inventory.FailWith = null;
        await vm.RefreshCommand.ExecuteAsync();

        Assert.True(vm.HasRows);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, inventory.ListCalls);
    }

    [Fact]
    public async Task Default_sort_is_by_name_ascending()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());
        Assert.Equal(
            ["gsl-case-sync", "gsl-disputes-intake", "gsl-disputes-provisional-credit", "gsl-notices-outbound"],
            vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal("Sorted by Name, ascending", vm.SortText);
    }

    [Fact]
    public async Task New_count_column_sorts_descending_and_the_same_column_flips()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());

        vm.Sort(QueueSortColumn.DeadLetter);
        Assert.Equal("gsl-disputes-provisional-credit", vm.Rows[0].Name);
        Assert.True(vm.SortDescending);

        vm.Sort(QueueSortColumn.DeadLetter);
        Assert.False(vm.SortDescending);
        Assert.Equal("gsl-disputes-provisional-credit", vm.Rows[^1].Name);
    }

    [Theory]
    [InlineData(QueueSortColumn.Active, "gsl-case-sync")]
    [InlineData(QueueSortColumn.Scheduled, "gsl-notices-outbound")]
    [InlineData(QueueSortColumn.TransferDeadLetter, "gsl-case-sync")]
    [InlineData(QueueSortColumn.Size, "gsl-case-sync")]
    public async Task Each_count_column_puts_the_largest_first(QueueSortColumn column, string first)
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());
        vm.Sort(column);
        Assert.Equal(first, vm.Rows[0].Name);
    }

    [Fact]
    public async Task Status_sorts_ascending_with_name_as_tie_break()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());
        vm.Sort(QueueSortColumn.Status);
        Assert.False(vm.SortDescending);
        Assert.Equal("gsl-notices-outbound", vm.Rows[^1].Name);
        Assert.Equal("gsl-case-sync", vm.Rows[0].Name);
    }

    [Fact]
    public async Task Sort_survives_a_refresh()
    {
        var vm = new QueuesViewModel();
        await vm.LoadAsync(Inventory());
        vm.Sort(QueueSortColumn.Active);
        await vm.RefreshCommand.ExecuteAsync();
        Assert.Equal("gsl-case-sync", vm.Rows[0].Name);
        Assert.Equal(QueueSortColumn.Active, vm.SortColumn);
    }

    [Fact]
    public async Task Many_queues_from_many_pages_all_appear()
    {
        var vm = new QueuesViewModel();
        var inventory = new FakeInventory(new FakeBrokerProvider().Descriptor, SyntheticQueues.Many(250), pageSize: 100);
        await vm.LoadAsync(inventory);
        Assert.Equal(250, vm.Rows.Count);
        Assert.Equal(3, vm.PagesRead);
        Assert.Equal(35, vm.DeadLetterQueueCount);
    }
}

public class QueueRowViewModelTests
{
    [Fact]
    public void Dead_letters_above_zero_are_flagged()
    {
        Assert.True(new QueueRowViewModel(SyntheticQueues.Queue("a", 0, 1)).HasDeadLetters);
        Assert.True(new QueueRowViewModel(SyntheticQueues.Queue("b", 0, 0, transferDeadLetter: 1)).HasDeadLetters);
        Assert.False(new QueueRowViewModel(SyntheticQueues.Queue("c", 9, 0)).HasDeadLetters);

        var transferOnly = new QueueRowViewModel(SyntheticQueues.Queue("d", 0, 0, transferDeadLetter: 2));
        Assert.False(transferOnly.DeadLetterAboveZero);
        Assert.True(transferOnly.TransferDeadLetterAboveZero);
    }

    [Fact]
    public void Text_columns_format_counts_size_and_unknowns()
    {
        var row = new QueueRowViewModel(SyntheticQueues.Queue("a", 12345, 3, 2, 1, size: 5 * 1024 * 1024));
        Assert.Equal("12,345", row.ActiveText);
        Assert.Equal("3", row.DeadLetterText);
        Assert.Equal("2", row.ScheduledText);
        Assert.Equal("1", row.TransferDeadLetterText);
        Assert.Equal("5.0 MB", row.SizeText);
        Assert.Equal("Active", row.Status);

        var unknown = new QueueRowViewModel(new EntitySnapshot(EntityKind.Queue, "u", null, EntityCounts.Unknown, null, null));
        Assert.Equal("-", unknown.ActiveText);
        Assert.Equal("-", unknown.SizeText);
        Assert.Equal("Unknown", unknown.Status);
        Assert.False(unknown.HasDeadLetters);

        Assert.Equal("512 B", new QueueRowViewModel(SyntheticQueues.Queue("s", 0, 0, size: 512)).SizeText);
        Assert.Equal("2.0 KB", new QueueRowViewModel(SyntheticQueues.Queue("k", 0, 0, size: 2048)).SizeText);
    }
}
