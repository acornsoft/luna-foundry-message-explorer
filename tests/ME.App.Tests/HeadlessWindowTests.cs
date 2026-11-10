// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell;
using Acornsoft.MessageExplorer.Shell.Tests;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace Acornsoft.MessageExplorer.Shell.Tests;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HeadlessWindowTests
{
    private static (MainWindow Window, MainViewModel Main) Open()
    {
        var catalog = new ProviderCatalog([new FakeBrokerProvider()], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog);
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, main);
    }

    [AvaloniaFact]
    public void Window_starts_in_the_not_connected_state()
    {
        var (window, _) = Open();

        Assert.True(window.FindControl<TextBlock>("NotConnectedText")!.IsVisible);
        Assert.False(window.FindControl<DataGrid>("QueuesGrid")!.IsVisible);
        Assert.False(window.FindControl<Button>("RefreshButton")!.IsEffectivelyEnabled);
        Assert.Equal("Acornsoft Message Explorer", window.Title);
    }

    [AvaloniaFact]
    public void Connection_string_field_is_masked()
    {
        var (window, main) = Open();
        main.Connect.UseConnectionString = true;
        Dispatcher.UIThread.RunJobs();

        var box = window.FindControl<TextBox>("ConnectionStringBox")!;
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal('•', box.PasswordChar);
        Assert.False(box.RevealPassword);
        Assert.False(window.FindControl<TextBox>("NamespaceBox")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Planned_providers_render_as_disabled_radio_buttons()
    {
        var (window, _) = Open();

        var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "provider").ToList();

        Assert.Equal(3, radios.Count);
        Assert.True(radios[0].IsEnabled);
        Assert.True(radios[0].IsChecked);
        Assert.Equal("Apache Kafka (planned)", radios[1].Content);
        Assert.False(radios[1].IsEnabled);
        Assert.False(radios[2].IsEnabled);
    }

    [AvaloniaFact]
    public async Task Connect_fills_the_grid_and_highlights_dead_letters()
    {
        var (window, main) = Open();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var grid = window.FindControl<DataGrid>("QueuesGrid")!;
        Assert.True(grid.IsVisible);
        Assert.Same(main.Queues.Rows, grid.ItemsSource);
        Assert.Equal(7, grid.Columns.Count);
        Assert.Equal("4 queues, 2 with dead letters. Read-only.", window.FindControl<TextBlock>("SummaryText")!.Text);

        var highlighted = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Classes.Contains("deadletter"))
            .Select(t => t.Text ?? string.Empty)
            .ToList();
        // Dead-letter 3 on one queue, transfer dead-letter 1 on another. Zero counts stay plain.
        Assert.Equal(["1", "3"], highlighted.Order(StringComparer.Ordinal).ToArray());
        Assert.True(window.FindControl<Button>("RefreshButton")!.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Error_state_shows_the_message_and_hides_the_grid()
    {
        var provider = new FakeBrokerProvider();
        provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, BrokerException.Explain(BrokerErrorKind.Unreachable));
        var main = new MainViewModel(new ProviderCatalog([provider], []));
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        var error = window.FindControl<TextBlock>("ErrorText")!;
        Assert.True(error.IsVisible);
        Assert.Equal(BrokerException.Explain(BrokerErrorKind.Unreachable), error.Text);
        Assert.False(window.FindControl<DataGrid>("QueuesGrid")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Empty_state_is_shown_for_a_namespace_with_no_queues()
    {
        var provider = new FakeBrokerProvider([]);
        var main = new MainViewModel(new ProviderCatalog([provider], []));
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.Connect.NamespaceText = "contoso-synthetic";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<TextBlock>("EmptyText")!.IsVisible);
    }
}
