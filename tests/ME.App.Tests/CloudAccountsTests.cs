// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Each account picks Azure public, US Government or China. Fake sign-in and fake ARM; synthetic names only.</summary>
public sealed class CloudAccountsTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    private readonly FakeAuthenticator _auth = new();
    private readonly MemoryAccountStore _store = new();
    private readonly FakeArmReader _arm = new();

    public CloudAccountsTests()
    {
        _arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso")];
        _arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo", "Demo", Contoso)];
        _arm.Namespaces["sub-demo"] = [new NamespaceRef("sbns-demo-001", "sbns-demo-001.servicebus.usgovcloudapi.net", "/subscriptions/sub-demo/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001", "rg-demo", "US Gov Virginia", "sub-demo", Contoso, AzureClouds.UsGovernmentId)];
        _arm.Entities["sbns-demo-001"] = [new EntitySnapshot(EntityKind.Queue, "orders", null, new EntityCounts(1, 0, 0, 0, 1), "Active", 10)];
    }

    private AccountsViewModel Create(List<IEntityInventory>? opened = null) => new(_auth, _store, _arm, (inventory, _) =>
    {
        opened?.Add(inventory);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_new_account_signs_in_to_the_public_cloud_unless_another_is_picked()
    {
        var accounts = Create();

        Assert.Same(AzureClouds.Public, accounts.SelectedCloud);
        Assert.Equal(["public", "usgov", "china"], accounts.Clouds.Select(c => c.Id));
    }

    [Fact]
    public async Task Picking_us_government_signs_in_there_and_discovers_through_that_clouds_resource_manager()
    {
        var accounts = Create();
        accounts.SelectedCloud = AzureClouds.UsGovernment;
        _auth.WillSignIn("alice@contoso.example");

        await accounts.AddAccountAsync();

        Assert.Equal(["usgov"], _auth.InteractiveClouds);
        Assert.Equal(["usgov"], _arm.CloudsAsked);
        var account = Assert.Single(accounts.Accounts);
        Assert.Equal("Azure US Government", account.CloudText);
        Assert.Equal("usgov", _store.Saved.Values.Single().Cloud);
        Assert.Equal(["sbns-demo-001"], accounts.Namespaces.Select(n => n.Name));
    }

    [Fact]
    public async Task The_tree_names_the_cloud_for_accounts_outside_the_public_cloud()
    {
        var accounts = Create();
        accounts.SelectedCloud = AzureClouds.UsGovernment;
        _auth.WillSignIn("alice@contoso.example");
        await accounts.AddAccountAsync();

        var node = Assert.Single(accounts.Tree);
        Assert.StartsWith("Azure US Government · Signed in", node.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tree_reads_as_before_for_public_cloud_accounts()
    {
        var accounts = Create();
        _arm.Namespaces["sub-demo"] = [_arm.Namespaces["sub-demo"][0] with { Cloud = AzureClouds.PublicId }];
        _auth.WillSignIn("alice@contoso.example");
        await accounts.AddAccountAsync();

        Assert.StartsWith("Signed in", Assert.Single(accounts.Tree).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_namespace_reads_it_through_the_namespaces_cloud()
    {
        var opened = new List<IEntityInventory>();
        var accounts = Create(opened);
        accounts.SelectedCloud = AzureClouds.UsGovernment;
        _auth.WillSignIn("alice@contoso.example");
        await accounts.AddAccountAsync();
        _arm.CloudsAsked.Clear();

        var node = accounts.Tree.SelectMany(Flatten).First(n => n.IsNamespace);
        await accounts.OpenAsync(node);

        Assert.Single(opened);
        Assert.Equal(["usgov"], _arm.CloudsAsked);
    }

    [Fact]
    public async Task Two_accounts_in_different_clouds_each_keep_their_own()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("bob@fabrikam.example", "00000000-0000-0000-0000-00000000f001");

        accounts.SelectedCloud = AzureClouds.China;
        await accounts.AddAccountAsync();
        accounts.SelectedCloud = AzureClouds.Public;
        await accounts.AddAccountAsync();

        Assert.Equal(["china", "public"], _auth.InteractiveClouds);
        Assert.Equal(["china", "public"], _arm.CloudsAsked);
        Assert.Equal(["Azure China (21Vianet)", "Azure (public)"], accounts.Accounts.Select(a => a.CloudText));
    }

    [Fact]
    public async Task Restoring_signs_each_account_back_in_against_its_saved_cloud()
    {
        _store.Save(FakeAuthenticator.Account("alice@contoso.example") with { Cloud = AzureClouds.ChinaId });
        var accounts = Create();

        await accounts.RestoreAsync();

        Assert.Equal(["china"], _arm.CloudsAsked);
    }

    [Fact]
    public async Task Signing_in_again_uses_the_accounts_cloud_not_the_picker()
    {
        _store.Save(FakeAuthenticator.Account("alice@contoso.example") with { Cloud = AzureClouds.UsGovernmentId, SignedOut = true });
        var accounts = Create();
        await accounts.RestoreAsync();
        accounts.SelectedCloud = AzureClouds.China;
        _auth.WillSignIn("alice@contoso.example");

        await accounts.Accounts[0].SignInCommand.ExecuteAsync();

        Assert.Equal(["usgov"], _auth.InteractiveClouds);
    }

    [Fact]
    public void The_vault_keeps_the_cloud_and_accounts_saved_without_one_load_as_public()
    {
        var vault = new TestVault().Vault;
        var store = new VaultAccountStore(vault);

        store.Save(FakeAuthenticator.Account("alice@contoso.example") with { Cloud = AzureClouds.UsGovernmentId });
        vault.Put("entra-legacy", new Credentials.LoginDraft("bob@fabrikam.example", Credentials.LoginKind.EntraAccount, new Dictionary<string, string>
        {
            ["username"] = "bob@fabrikam.example",
            ["tenantId"] = "00000000-0000-0000-0000-00000000f001",
            ["homeAccountId"] = "bob-home",
            ["authenticationRecord"] = "{}",
            ["signedOut"] = "false",
        }));

        var loaded = store.Load().ToDictionary(a => a.Username, a => a.Cloud);
        Assert.Equal("usgov", loaded["alice@contoso.example"]);
        Assert.Equal("public", loaded["bob@fabrikam.example"]);
    }

    [Theory]
    [InlineData("public", "https://login.microsoftonline.com/organizations")]
    [InlineData("usgov", "https://login.microsoftonline.us/organizations")]
    [InlineData("china", "https://login.chinacloudapi.cn/organizations")]
    public void Entra_sign_in_uses_the_clouds_authority_host(string cloud, string authority)
    {
        Assert.Equal(authority, EntraAuthenticator.Authority(AzureClouds.Find(cloud), null));
        Assert.Equal(authority.Replace("organizations", "contoso.example", StringComparison.Ordinal), EntraAuthenticator.Authority(AzureClouds.Find(cloud), " contoso.example "));
    }

    [Theory]
    [InlineData("public", "login.microsoftonline.com", "https://management.azure.com/.default")]
    [InlineData("usgov", "login.microsoftonline.us", "https://management.usgovcloudapi.net/.default")]
    [InlineData("china", "login.chinacloudapi.cn", "https://management.chinacloudapi.cn/.default")]
    public void The_msal_client_for_a_cloud_points_at_that_clouds_host_and_asks_for_its_resource_manager(string cloud, string host, string scope)
    {
        var app = EntraAuthenticator.BuildApp(new EntraOptions(EntraOptions.AzureCliClientId, "lfme-test"), AzureClouds.Find(cloud), null);

        Assert.Equal(host, new Uri(app.Authority).Host);
        Assert.Equal([scope], EntraAuthenticator.ScopesFor(AzureClouds.Find(cloud)));
    }

    [Fact]
    public void A_prompt_without_a_cloud_targets_the_public_cloud()
    {
        Assert.Same(AzureClouds.Public, SignInPrompt.Default.TargetCloud);
        Assert.Same(AzureClouds.China, (SignInPrompt.Default with { Cloud = AzureClouds.China }).TargetCloud);
    }

    [Fact]
    public async Task Sign_in_with_a_code_goes_to_the_picked_cloud_and_the_account_keeps_it()
    {
        var accounts = Create();
        accounts.SelectedCloud = AzureClouds.UsGovernment;
        _auth.WillSignIn("alice@contoso.example");

        await accounts.SignInWithCodeAsync();

        Assert.Equal(["usgov"], _auth.InteractiveClouds);
        Assert.Equal("usgov", _store.Saved.Values.Single().Cloud);
        Assert.Equal(["usgov"], _arm.CloudsAsked);
    }

    [Fact]
    public async Task Changing_the_picker_while_sign_in_waits_does_not_move_the_account_to_another_cloud()
    {
        var accounts = Create();
        accounts.SelectedCloud = AzureClouds.China;
        var gate = new TaskCompletionSource();
        _auth.BeforeInteractive = () => gate.Task;
        _auth.WillSignIn("alice@contoso.example");

        var adding = accounts.AddAccountAsync();
        accounts.SelectedCloud = AzureClouds.Public;
        gate.SetResult();
        await adding;

        Assert.Equal("china", _store.Saved.Values.Single().Cloud);
    }

    private static IEnumerable<DiscoveryNode> Flatten(DiscoveryNode node) => node.Children.SelectMany(Flatten).Prepend(node);
}
