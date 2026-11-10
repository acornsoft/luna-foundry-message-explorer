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

/// <summary>The Accounts screen with fake sign-in and a fake ARM. Synthetic accounts and namespaces only.</summary>
public sealed class AccountsViewModelTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    private const string ContosoLab = "00000000-0000-0000-0000-00000000c002";
    private const string Fabrikam = "00000000-0000-0000-0000-00000000f001";

    private readonly FakeAuthenticator _auth = new();
    private readonly MemoryAccountStore _store = new();
    private readonly FakeArmReader _arm = new();
    private readonly List<(IEntityInventory Inventory, string Label)> _opened = [];

    public AccountsViewModelTests()
    {
        _arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso"), new TenantRef(ContosoLab, "Contoso Lab")];
        _arm.Tenants["bob"] = [new TenantRef(Fabrikam, "Fabrikam")];
        _arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo-a", "Demo A", Contoso), new SubscriptionRef("sub-empty", "Empty", Contoso)];
        _arm.Subscriptions[ContosoLab] = [new SubscriptionRef("sub-lab", "Lab", ContosoLab)];
        _arm.Subscriptions[Fabrikam] = [new SubscriptionRef("sub-fab", "Fabrikam Dev", Fabrikam)];
        _arm.Namespaces["sub-demo-a"] = [Ns("sbns-demo-001", "sub-demo-a", Contoso), Ns("sbns-demo-002", "sub-demo-a", Contoso)];
        _arm.Namespaces["sub-lab"] = [Ns("sbns-lab-001", "sub-lab", ContosoLab)];
        _arm.Namespaces["sub-fab"] = [Ns("sbns-fab-001", "sub-fab", Fabrikam)];
        _arm.Entities["sbns-fab-001"] = [new EntitySnapshot(EntityKind.Queue, "invoices", null, new EntityCounts(3, 0, 0, 0, 3), "Active", 1024)];
    }

    private static NamespaceRef Ns(string name, string subscription, string tenant) => new(
        name, $"{name}.servicebus.windows.net", $"/subscriptions/{subscription}/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/{name}", "rg-demo", "East US", subscription, tenant);

    private AccountsViewModel Create() => new(_auth, _store, _arm, (inventory, label) =>
    {
        _opened.Add((inventory, label));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Two_accounts_sign_in_side_by_side_and_all_their_namespaces_show_in_one_list()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("bob@fabrikam.example", Fabrikam);

        await accounts.AddAccountAsync();
        await accounts.AddAccountAsync();

        Assert.Equal(["alice@contoso.example", "bob@fabrikam.example"], accounts.Accounts.Select(a => a.Username));
        Assert.All(accounts.Accounts, a => Assert.Equal(AccountState.Ready, a.State));
        Assert.Equal(["sbns-demo-001", "sbns-demo-002", "sbns-lab-001", "sbns-fab-001"], accounts.Namespaces.Select(n => n.Name));
        Assert.Equal("2 accounts, 4 namespaces", accounts.Summary);
        Assert.Equal(2, _store.Saved.Count);
        Assert.Equal($"Tenant {Fabrikam}", accounts.Accounts[1].TenantText);
        Assert.Equal("Signed in. 3 namespaces.", accounts.Accounts[0].StatusText);
    }

    [Fact]
    public async Task The_tree_maps_account_to_subscription_to_namespace_and_hides_empty_subscriptions()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");

        await accounts.AddAccountAsync();

        var root = Assert.Single(accounts.Tree);
        Assert.Equal(DiscoveryNodeKind.Account, root.Kind);
        Assert.Equal("alice@contoso.example", root.Label);
        Assert.Equal(["Demo A", "Lab"], root.Children.Select(c => c.Label));
        Assert.Equal("Subscription in Contoso Lab", root.Children[1].Detail);
        Assert.Equal(["sbns-demo-001", "sbns-demo-002"], root.Children[0].Children.Select(c => c.Label));
        Assert.All(root.Children[0].Children, n => Assert.True(n.IsNamespace));
        Assert.Equal("rg-demo · East US", root.Children[0].Children[0].Detail);
        Assert.True(root.IsExpanded);
    }

    [Fact]
    public async Task On_launch_accounts_come_back_silently_and_one_failing_token_does_not_stop_the_others()
    {
        _store.Save(FakeAuthenticator.Account("alice@contoso.example"));
        _store.Save(FakeAuthenticator.Account("bob@fabrikam.example", Fabrikam));
        _auth.SilentFailures.Add("bob@fabrikam.example");
        var accounts = Create();

        await accounts.RestoreAsync();

        var alice = accounts.Accounts.Single(a => a.Username.StartsWith("alice", StringComparison.Ordinal));
        var bob = accounts.Accounts.Single(a => a.Username.StartsWith("bob", StringComparison.Ordinal));
        Assert.Equal(AccountState.Ready, alice.State);
        Assert.Equal(3, alice.Namespaces.Count);
        Assert.Equal(AccountState.NeedsSignIn, bob.State);
        Assert.Null(bob.Credential);
        Assert.Equal("The saved sign-in has expired or needs approval again.", bob.ErrorMessage);
        Assert.True(bob.SignInCommand.CanExecute(null));
        var bobNode = accounts.Tree.Single(n => n.Account == bob);
        Assert.Equal(DiscoveryNodeKind.Error, Assert.Single(bobNode.Children).Kind);
        Assert.Equal(["sbns-demo-001", "sbns-demo-002", "sbns-lab-001"], accounts.Namespaces.Select(n => n.Name));
    }

    [Fact]
    public async Task A_tenant_that_fails_shows_an_error_row_and_the_account_keeps_its_other_namespaces()
    {
        _arm.FailingTenants.Add(ContosoLab);
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");

        await accounts.AddAccountAsync();

        var alice = Assert.Single(accounts.Accounts);
        Assert.Equal(AccountState.Ready, alice.State);
        Assert.Equal("Signed in. 2 namespaces. 1 could not be read.", alice.StatusText);
        var error = Assert.Single(accounts.Tree[0].Children, c => c.IsError);
        Assert.Equal("Tenant Contoso Lab", error.Label);
    }

    [Fact]
    public async Task A_token_failure_during_discovery_marks_only_that_account()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("bob@fabrikam.example", Fabrikam);
        _arm.Tenants.Remove("bob");
        await accounts.AddAccountAsync();
        _auth.Credentials["bob"] = new FakeCredential("bob", new Azure.RequestFailedException(401, "token rejected"));

        await accounts.AddAccountAsync();

        Assert.Equal(AccountState.Ready, accounts.Accounts[0].State);
        Assert.Equal(AccountState.Error, accounts.Accounts[1].State);
        Assert.NotNull(accounts.Accounts[1].ErrorMessage);
        Assert.Equal(3, accounts.Namespaces.Count);
    }

    [Fact]
    public async Task Remove_forgets_the_account_and_its_namespaces()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("bob@fabrikam.example", Fabrikam);
        await accounts.AddAccountAsync();
        await accounts.AddAccountAsync();

        accounts.Accounts[0].RemoveCommand.Execute(null);

        Assert.Equal("bob@fabrikam.example", Assert.Single(accounts.Accounts).Username);
        Assert.Equal(["bob@fabrikam.example-home"], _store.Saved.Keys);
        Assert.Equal(["sbns-fab-001"], accounts.Namespaces.Select(n => n.Name));
        Assert.Single(accounts.Tree);
    }

    [Fact]
    public async Task Sign_out_keeps_the_account_but_drops_its_credential_and_it_stays_signed_out_next_launch()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        await accounts.AddAccountAsync();

        accounts.Accounts[0].SignOutCommand.Execute(null);

        Assert.Equal(AccountState.SignedOut, accounts.Accounts[0].State);
        Assert.Null(accounts.Accounts[0].Credential);
        Assert.Empty(accounts.Namespaces);
        Assert.True(_store.Saved.Values.Single().SignedOut);

        var nextLaunch = Create();
        await nextLaunch.RestoreAsync();
        Assert.Empty(_auth.SilentCalls);
        Assert.Equal(AccountState.SignedOut, nextLaunch.Accounts.Single().State);

        _auth.WillSignIn("alice@contoso.example");
        await nextLaunch.Accounts[0].SignInCommand.ExecuteAsync();
        Assert.Equal(AccountState.Ready, nextLaunch.Accounts[0].State);
        Assert.Equal("alice@contoso.example", _auth.InteractiveCalls[^1].Hint);
        Assert.False(_store.Saved.Values.Single().SignedOut);
    }

    [Fact]
    public async Task Adding_the_same_account_again_updates_it_instead_of_adding_a_second_row()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("alice@contoso.example");

        await accounts.AddAccountAsync();
        await accounts.AddAccountAsync();

        Assert.Single(accounts.Accounts);
        Assert.Single(_store.Saved);
    }

    [Fact]
    public async Task A_closed_browser_shows_a_plain_message_and_adds_nothing()
    {
        var accounts = Create();

        await accounts.AddAccountAsync();

        Assert.Empty(accounts.Accounts);
        Assert.True(accounts.HasMessage);
        Assert.StartsWith("Sign-in did not finish.", accounts.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_typed_tenant_is_passed_to_sign_in()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        accounts.TenantText = "  contoso.example ";

        await accounts.AddAccountAsync();

        Assert.Equal("contoso.example", _auth.InteractiveCalls.Single().Tenant);
        Assert.Equal("contoso.example", _store.Saved.Values.Single().RequestedTenant);
    }

    [Fact]
    public async Task Opening_a_namespace_uses_the_credential_of_the_account_it_was_found_under()
    {
        var accounts = Create();
        _auth.WillSignIn("alice@contoso.example");
        _auth.WillSignIn("bob@fabrikam.example", Fabrikam);
        await accounts.AddAccountAsync();
        await accounts.AddAccountAsync();
        var fab = accounts.Tree[1].Children[0].Children[0];

        accounts.SelectedNode = fab;
        Assert.True(accounts.OpenCommand.CanExecute(null));
        await accounts.OpenCommand.ExecuteAsync();

        var (inventory, label) = Assert.Single(_opened);
        Assert.Equal("sbns-fab-001 as bob@fabrikam.example", label);
        var queues = await inventory.ListQueuesAsync();
        Assert.Equal("invoices", Assert.Single(queues.Entities).Name);
        Assert.Contains(Fabrikam, _auth.Credentials["bob"].TenantsAsked);
        Assert.DoesNotContain(Fabrikam, _auth.Credentials["alice"].TenantsAsked);
    }

    [Fact]
    public async Task Only_namespace_rows_can_be_opened_and_the_first_signed_in_account_is_the_default()
    {
        var accounts = Create();
        Assert.Null(accounts.DefaultCredential);
        _auth.WillSignIn("alice@contoso.example");
        await accounts.AddAccountAsync();

        accounts.SelectedNode = accounts.Tree[0];
        Assert.False(accounts.OpenCommand.CanExecute(null));
        Assert.Same(_auth.Credentials["alice"], accounts.DefaultCredential);
        Assert.Equal("alice@contoso.example", accounts.DefaultUsername);
    }

    [Fact]
    public async Task An_account_with_nothing_readable_says_so_and_how_to_fix_it()
    {
        _arm.Tenants["carol"] = [];
        var accounts = Create();
        _auth.WillSignIn("carol@contoso.example");

        await accounts.AddAccountAsync();

        var message = Assert.Single(accounts.Tree[0].Children);
        Assert.Equal(DiscoveryNodeKind.Message, message.Kind);
        Assert.Contains("Reader role", message.Detail, StringComparison.Ordinal);
    }
}
