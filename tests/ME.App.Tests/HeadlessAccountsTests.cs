// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The Accounts tab and the paste box in a real (headless) window. Fake accounts and keys only.</summary>
public class HeadlessAccountsTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    private const string KeyName = "Shared" + "AccessKeyName";
    private const string Key = "Shared" + "AccessKey";
    private const string FakeKey = "bm90LWEtcmVhbC1rZXk=";

    private static (MainWindow Window, MainViewModel Main, FakeAuthenticator Auth, TestVault Vault) Open()
    {
        var auth = new FakeAuthenticator();
        var arm = new FakeArmReader();
        arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso")];
        arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo-a", "Demo A", Contoso)];
        arm.Namespaces["sub-demo-a"] =
        [
            new NamespaceRef("sbns-demo-001", "sbns-demo-001.servicebus.windows.net", "/subscriptions/sub-demo-a/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001", "rg-demo", "East US", "sub-demo-a", Contoso),
        ];
        arm.Entities["sbns-demo-001"] = [new EntitySnapshot(EntityKind.Queue, "orders", null, new EntityCounts(2, 1, 0, 0, 3), "Active", 100)];
        var vault = new TestVault();
        var catalog = new ProviderCatalog([new ServiceBusBrokerProvider()], ProviderCatalog.PlannedProviders);
        var main = new MainViewModel(catalog, accounts: new AccountServices(auth, new MemoryAccountStore(), arm, vault.Vault));
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, main, auth, vault);
    }

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

    [AvaloniaFact]
    public void The_accounts_tab_opens_first_and_invites_an_account()
    {
        var (window, _, _, _) = Open();

        Assert.True(Find<TextBlock>(window, "NoAccountsText").IsVisible);
        Assert.True(Find<Button>(window, "AddAccountButton").IsEffectivelyEnabled);
        Assert.True(Find<TextBox>(window, "TenantBox").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Adding_an_account_lists_it_and_its_namespaces_in_the_tree()
    {
        var (window, main, auth, _) = Open();
        auth.WillSignIn("alice@contoso.example");

        await main.Accounts.AddAccountAsync();
        Settle(window);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("alice@contoso.example", texts);
        Assert.Contains($"Tenant {Contoso}", texts);
        Assert.Contains("sbns-demo-001", texts);
        Assert.False(Find<TextBlock>(window, "NoAccountsText").IsVisible);
        Assert.Contains("alice@contoso.example", Find<TextBlock>(window, "EntraHintText").Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Opening_a_namespace_from_the_tree_fills_the_queues_grid()
    {
        var (window, main, auth, _) = Open();
        auth.WillSignIn("alice@contoso.example");
        await main.Accounts.AddAccountAsync();

        main.Accounts.SelectedNode = main.Accounts.Tree[0].Children[0].Children[0];
        await main.Accounts.OpenCommand.ExecuteAsync();
        Settle(window);

        Assert.Equal(MainViewModel.QueuesTab, main.SelectedTab);
        Assert.Equal("orders", Assert.Single(main.Queues.Rows).Name);
        Assert.Equal("Azure Service Bus: sbns-demo-001 as alice@contoso.example", main.Connect.ConnectedTo);
    }

    [AvaloniaFact]
    public void The_paste_box_names_the_flavor_and_never_shows_the_key()
    {
        var (window, main, _, _) = Open();
        main.Connect.UseConnectionString = true;
        main.Connect.ConnectionString = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=listen;{Key}={FakeKey};EntityPath=orders";
        Settle(window);

        var detected = Find<TextBlock>(window, "DetectedText");
        Assert.True(detected.IsVisible);
        Assert.StartsWith("Key (listen) for orders only", detected.Text, StringComparison.Ordinal);
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty);
        Assert.DoesNotContain(shown, t => t.Contains(FakeKey, StringComparison.Ordinal));
        Assert.Equal('•', Find<TextBox>(window, "ConnectionStringBox").PasswordChar);
    }

    [AvaloniaFact]
    public async Task An_entity_key_connects_to_that_entity_and_says_why_counts_are_missing()
    {
        var (window, main, _, _) = Open();
        main.Connect.UseConnectionString = true;
        main.Connect.ConnectionString = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=listen;{Key}={FakeKey};EntityPath=orders";

        await main.Connect.ConnectCommand.ExecuteAsync();
        main.SelectedTab = MainViewModel.QueuesTab;
        Settle(window);

        Assert.True(Find<TextBlock>(window, "QueuesNoticeText").IsVisible);
        Assert.Equal("orders", Assert.Single(main.Queues.Rows).Name);
        Assert.Equal("Azure Service Bus: Key (listen) for orders only, on sbns-demo-001.servicebus.windows.net", main.Connect.ConnectedTo);
        Assert.Empty(main.Connect.ConnectionString);
    }

    [AvaloniaFact]
    public async Task Saving_puts_the_string_only_in_the_secure_store_and_it_can_connect_again()
    {
        var (window, main, _, vault) = Open();
        var text = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={FakeKey}";
        main.Connect.UseConnectionString = true;
        main.Connect.ConnectionString = text;
        main.Connect.SaveConnection = true;
        main.Connect.SaveName = "demo reader";

        await main.Connect.ConnectCommand.ExecuteAsync();
        Settle(window);

        var login = Assert.Single(vault.Vault.List());
        Assert.Equal(("demo reader", LoginKind.ServiceBusConnectionString), (login.DisplayName, login.Kind));
        Assert.Equal("sbns-demo-001.servicebus.windows.net", login.Field("endpoint"));
        Assert.Equal(text, (await vault.Vault.ReadSecretAsync(login.Id))!.Reveal());
        Assert.DoesNotContain(FakeKey, vault.Metadata.Json, StringComparison.Ordinal);
        Assert.Equal(login.Id, Assert.Single(main.Connect.SavedConnections).Id);
        Assert.Empty(main.Connect.ConnectionString);
        Assert.DoesNotContain(FakeKey, main.Connect.ConnectedTo, StringComparison.Ordinal);
        Assert.True(Find<ComboBox>(window, "SavedConnectionsBox").IsEffectivelyVisible);
        Assert.Contains(main.Logins.Items, i => i.Name == "demo reader");

        main.Connect.DisconnectCommand.Execute(null);
        main.Connect.SelectedSaved = main.Connect.SavedConnections[0];
        await main.Connect.ConnectSavedCommand.ExecuteAsync();
        Assert.True(main.Connect.IsConnected);
        Assert.Empty(main.Connect.ConnectionString);
        Assert.NotNull(vault.Vault.Find(login.Id)!.LastUsed);
    }

    [AvaloniaFact]
    public void Saving_a_string_is_off_with_a_plain_reason_when_the_secure_store_is_missing()
    {
        var catalog = new ProviderCatalog([new ServiceBusBrokerProvider()], ProviderCatalog.PlannedProviders);
        var vault = new TestVault("No secure store here.");
        var main = new MainViewModel(catalog, accounts: new AccountServices(new FakeAuthenticator(), new MemoryAccountStore(), new FakeArmReader(), vault.Vault));

        main.Connect.UseConnectionString = true;
        main.Connect.SaveConnection = true;

        Assert.False(main.Connect.CanSave);
        Assert.False(main.Connect.SaveConnection);
        Assert.Equal("No secure store here.", main.Connect.SaveNote);
        Assert.Contains("No secure store here.", main.Logins.StoreText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_saved_logins_tab_lists_rows_and_its_secret_box_is_masked_with_copy_blocked()
    {
        var (window, main, _, vault) = Open();
        vault.Vault.AddAsync(new LoginDraft("Kafka dev (demo)", LoginKind.KafkaSasl, new Dictionary<string, string> { ["bootstrapServers"] = "kafka-demo.contoso.example:9093", ["mechanism"] = "PLAIN", ["username"] = "demo" }), new RuntimeSecret("fake-kafka-demo")).GetAwaiter().GetResult();
        main.SelectedTab = MainViewModel.LoginsTab;
        main.Logins.Selected = main.Logins.Items[0];
        main.Logins.EditCommand.Execute(null);
        Settle(window);

        Assert.True(Find<ListBox>(window, "LoginsList").IsEffectivelyVisible);
        var secretBox = Find<TextBox>(window, "SecretBox");
        Assert.Equal('•', secretBox.PasswordChar);
        Assert.False(secretBox.RevealPassword);
        main.Logins.Editor!.Secret = "typed-but-never-copied";
        var copy = new Avalonia.Interactivity.RoutedEventArgs(TextBox.CopyingToClipboardEvent);
        secretBox.RaiseEvent(copy);
        Assert.True(copy.Handled);
        var cut = new Avalonia.Interactivity.RoutedEventArgs(TextBox.CuttingToClipboardEvent);
        secretBox.RaiseEvent(cut);
        Assert.True(cut.Handled);
        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? string.Empty);
        Assert.DoesNotContain(buttons, b => b.Contains("Reveal", StringComparison.OrdinalIgnoreCase) || b.Contains("Copy", StringComparison.OrdinalIgnoreCase) || b.Contains("Show", StringComparison.OrdinalIgnoreCase));
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty);
        Assert.DoesNotContain(shown, t => t.Contains("fake-kafka-demo", StringComparison.Ordinal) || t.Contains("typed-but-never-copied", StringComparison.Ordinal));
    }
}
