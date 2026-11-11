// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Account records on disk, the client ID choice, and the config folder. Temp folders only.</summary>
public sealed class AccountsInfrastructureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("lfme-accounts-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_file_store_keeps_one_record_per_account_and_round_trips_it()
    {
        var store = new FileAccountStore(_folder);
        var alice = new StoredAccount("alice-home", "alice@contoso.example", "00000000-0000-0000-0000-00000000c001", null, "{\"username\":\"alice@contoso.example\"}");
        var bob = new StoredAccount("bob-home", "bob@fabrikam.example", "00000000-0000-0000-0000-00000000f001", "fabrikam.example", "{}");

        store.Save(alice);
        store.Save(bob);
        store.Save(bob with { SignedOut = true });

        var loaded = new FileAccountStore(_folder).Load().OrderBy(a => a.Username, StringComparer.Ordinal).ToList();
        Assert.Equal([alice, bob with { SignedOut = true }], loaded);
        Assert.Equal(2, Directory.GetFiles(store.Directory).Length);

        store.Delete("alice-home");
        Assert.Equal(["bob@fabrikam.example"], store.Load().Select(a => a.Username));
    }

    [Fact]
    public void The_file_store_writes_no_token_or_secret_field()
    {
        var store = new FileAccountStore(_folder);
        store.Save(new StoredAccount("alice-home", "alice@contoso.example", "00000000-0000-0000-0000-00000000c001", null, "{}"));

        var text = File.ReadAllText(Directory.GetFiles(store.Directory).Single());
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_record_is_skipped()
    {
        var store = new FileAccountStore(_folder);
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(Path.Combine(store.Directory, "broken.json"), "{ not json");

        Assert.Empty(store.Load());
    }

    [Fact]
    public void The_client_id_defaults_to_the_public_azure_cli_app()
    {
        var options = EntraOptions.Resolve(_ => null, _folder);

        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", options.ClientId);
        Assert.Equal("lfme", options.CacheName);
    }

    [Fact]
    public void The_environment_beats_the_settings_file_which_beats_the_default()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "{\"entraClientId\": \"11111111-1111-1111-1111-111111111111\"}");

        Assert.Equal("11111111-1111-1111-1111-111111111111", EntraOptions.Resolve(_ => null, _folder).ClientId);
        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            EntraOptions.Resolve(name => name == "LFME_ENTRA_CLIENT_ID" ? "22222222-2222-2222-2222-222222222222" : null, _folder).ClientId);
    }

    [Fact]
    public void The_config_folder_can_be_overridden()
    {
        Assert.Equal(_folder, AppPaths.ConfigDirectory(name => name == "LFME_CONFIG_DIR" ? _folder : null));
        Assert.EndsWith(AppPaths.FolderName, AppPaths.ConfigDirectory(_ => null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_accounts_page_can_be_picked_on_the_command_line()
    {
        Assert.Equal(AppSection.Accounts, StartupOptions.Parse(["--tab", "accounts"], _ => null).Section);
        Assert.Equal(AppSection.Resources, StartupOptions.Parse(["--tab", "queues"], _ => null).Section);
    }

    [Fact]
    public async Task The_synthetic_demo_has_four_accounts_and_one_tenant_error()
    {
        var services = AccountServices.SyntheticDemo();
        var accounts = new AccountsViewModel(services.Authenticator, services.Store, services.Arm, (_, _) => Task.CompletedTask);

        await accounts.RestoreAsync();

        Assert.Equal(4, accounts.Accounts.Count);
        Assert.Contains(accounts.Namespaces, n => n.Name == "sbns-demo-001");
        Assert.Contains(accounts.Tree.SelectMany(n => n.Children), n => n.IsError);
        Assert.All(accounts.Accounts, a => Assert.EndsWith(".example", a.Username, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_synthetic_demo_shows_an_account_in_each_cloud_with_that_clouds_namespace_host()
    {
        var services = AccountServices.SyntheticDemo();
        var accounts = new AccountsViewModel(services.Authenticator, services.Store, services.Arm, (_, _) => Task.CompletedTask);

        await accounts.RestoreAsync();

        Assert.Equal(["china", "public", "usgov"], accounts.Accounts.Select(a => a.Account.Cloud).Distinct().Order());
        Assert.Contains(accounts.Namespaces, n => n.FullyQualifiedNamespace == "sbns-gov-demo-001.servicebus.usgovcloudapi.net" && n.Cloud == "usgov");
        Assert.Contains(accounts.Namespaces, n => n.FullyQualifiedNamespace == "sbns-cn-demo-001.servicebus.chinacloudapi.cn" && n.Cloud == "china");
    }

    [Fact]
    public void The_phone_view_can_be_picked_on_the_command_line_and_is_off_by_default()
    {
        Assert.True(StartupOptions.Parse(["--phone-view"], _ => null).PhoneView);
        Assert.False(StartupOptions.Parse([], _ => null).PhoneView);
    }
}
