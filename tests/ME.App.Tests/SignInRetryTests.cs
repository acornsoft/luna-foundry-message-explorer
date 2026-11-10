// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>
/// A failed, abandoned or cancelled browser sign-in must never leave Add account stuck, must show the real
/// error, and must log it locally without secrets. Fake sign-in only; no browser and no network.
/// </summary>
public sealed class SignInRetryTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    // A made-up, unsigned token shape ({"alg":"none"}.{"sub":"not-real"}), assembled at run time.
    private static readonly string FakeJwt = string.Join('.', "eyJ" + "hbGciOiJub25lIn0", "eyJ" + "zdWIiOiJub3QtcmVhbCJ9", "c2lnbm90cmVhbA");
    private static readonly string FakeKey = Convert.ToBase64String("not-a-real-key"u8.ToArray());
    private readonly ScriptedAuthenticator _auth = new();
    private readonly MemoryAccountStore _store = new();
    private readonly FakeArmReader _arm = new();
    private readonly MemorySignInLog _log = new();

    public SignInRetryTests()
    {
        _arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso")];
        _arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo", "Demo", Contoso)];
        _arm.Namespaces["sub-demo"] = [new NamespaceRef("sbns-demo-001", "sbns-demo-001.servicebus.windows.net", "/subscriptions/sub-demo/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001", "rg-demo", "East US", "sub-demo", Contoso)];
    }

    private AccountsViewModel Create(TimeSpan? timeout = null, IAccountStore? store = null) =>
        new(_auth, store ?? _store, _arm, (_, _) => Task.CompletedTask, _log, timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_browser_that_never_returns_times_out_and_add_account_works_again()
    {
        var accounts = Create(TimeSpan.FromMilliseconds(100));
        _auth.Next.Enqueue(ScriptedAuthenticator.Hang);
        _auth.Next.Enqueue(ScriptedAuthenticator.Succeed("alice@contoso.example"));

        await accounts.AddAccountCommand.ExecuteAsync();

        Assert.Contains("timed out", accounts.Message, StringComparison.Ordinal);
        Assert.False(accounts.IsSigningIn);
        Assert.True(accounts.AddAccountCommand.CanExecute(null));
        Assert.Empty(_store.Saved);
        Assert.True(_auth.LastToken.IsCancellationRequested);

        await accounts.AddAccountCommand.ExecuteAsync();

        Assert.Null(accounts.Message);
        Assert.Equal(AccountState.Ready, Assert.Single(accounts.Accounts).State);
        Assert.Single(_store.Saved);
    }

    [Fact]
    public async Task Cancel_sign_in_stops_the_wait_at_once_and_the_next_try_succeeds()
    {
        var accounts = Create();
        _auth.Next.Enqueue(ScriptedAuthenticator.Hang);
        _auth.Next.Enqueue(ScriptedAuthenticator.Succeed("alice@contoso.example"));

        var waiting = accounts.AddAccountCommand.ExecuteAsync();
        Assert.True(accounts.IsSigningIn);
        Assert.False(accounts.AddAccountCommand.CanExecute(null));
        Assert.True(accounts.CancelSignInCommand.CanExecute(null));

        accounts.CancelSignInCommand.Execute(null);
        await waiting;

        Assert.Equal("Sign-in was cancelled.", accounts.Message);
        Assert.False(accounts.IsSigningIn);
        Assert.False(accounts.CancelSignInCommand.CanExecute(null));
        Assert.True(accounts.AddAccountCommand.CanExecute(null));

        await accounts.AddAccountCommand.ExecuteAsync();
        Assert.Single(accounts.Accounts);
    }

    [Fact]
    public async Task A_failure_shows_the_real_error_and_its_cause_then_retry_succeeds()
    {
        var accounts = Create();
        _auth.Next.Enqueue(_ => throw new AuthenticationFailedException(
            "InteractiveBrowserCredential authentication failed: loopback listener failed",
            new InvalidOperationException("Address already in use (port 49152)")));
        _auth.Next.Enqueue(ScriptedAuthenticator.Succeed("alice@contoso.example"));

        await accounts.AddAccountCommand.ExecuteAsync();

        Assert.StartsWith("Sign-in did not finish. InteractiveBrowserCredential authentication failed", accounts.Message, StringComparison.Ordinal);
        Assert.Contains("Address already in use", accounts.Message, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Contains("Azure.Identity.AuthenticationFailedException", StringComparison.Ordinal) && l.Contains("System.InvalidOperationException: Address already in use", StringComparison.Ordinal));
        Assert.True(accounts.AddAccountCommand.CanExecute(null));

        await accounts.AddAccountCommand.ExecuteAsync();
        Assert.Null(accounts.Message);
        Assert.Single(accounts.Accounts);
    }

    [Fact]
    public async Task An_unexpected_exception_such_as_a_keychain_cache_error_is_shown_not_swallowed()
    {
        var accounts = Create();
        _auth.Next.Enqueue(_ => throw new CredentialUnavailableException("Persistence check failed. Keychain item could not be read (errSecInteractionNotAllowed)."));

        await accounts.AddAccountCommand.ExecuteAsync();

        Assert.Contains("Persistence check failed", accounts.Message, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Contains("CredentialUnavailableException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Any_other_exception_type_reaches_the_ui_with_its_type_name()
    {
        var accounts = Create();
        _auth.Next.Enqueue(_ => throw new PlatformNotSupportedException("Browser could not be opened."));

        await accounts.AddAccountCommand.ExecuteAsync();

        Assert.Contains("PlatformNotSupportedException: Browser could not be opened.", accounts.Message, StringComparison.Ordinal);
        Assert.True(accounts.AddAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task Signing_in_again_on_a_saved_account_is_bounded_too()
    {
        _store.Save(FakeAuthenticator.Account("alice@contoso.example") with { SignedOut = true });
        var accounts = Create(TimeSpan.FromMilliseconds(100));
        await accounts.RestoreAsync();
        _auth.Next.Enqueue(ScriptedAuthenticator.Hang);

        await accounts.Accounts[0].SignInCommand.ExecuteAsync();

        Assert.Contains("timed out", accounts.Accounts[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(AccountState.SignedOut, accounts.Accounts[0].State);
        Assert.True(accounts.Accounts[0].SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_save_failure_after_sign_in_is_reported_and_does_not_crash()
    {
        var accounts = Create(store: new ThrowingStore());
        _auth.Next.Enqueue(ScriptedAuthenticator.Succeed("alice@contoso.example"));

        await accounts.AddAccountCommand.ExecuteAsync();

        var account = Assert.Single(accounts.Accounts);
        Assert.Contains("could not be saved", account.ErrorMessage, StringComparison.Ordinal);
        Assert.True(accounts.AddAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_log_never_holds_a_token_code_or_key()
    {
        var accounts = Create();
        _auth.Next.Enqueue(_ => throw new AuthenticationFailedException($"Redirect http://localhost:49152/?code=not-a-real-code&state=xyz failed. Bearer {FakeJwt} token={FakeJwt}"));

        await accounts.AddAccountCommand.ExecuteAsync();

        var all = string.Join("\n", _log.Lines) + accounts.Message;
        Assert.DoesNotContain(FakeJwt, all, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-code", all, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_log_goes_to_logs_signin_log_in_the_config_folder_and_is_scrubbed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lfme-signin-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileSignInLog(folder, () => new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.FromHours(-5)));
            log.Write("add account: failed: x " + "Shared" + "AccessKey" + "=" + FakeKey + " and " + FakeJwt);

            Assert.Equal(Path.Combine(folder, "logs", "signin.log"), log.Location);
            var text = File.ReadAllText(log.Location);
            Assert.StartsWith("2026-01-05T09:00:00.000-05:00 add account: failed", text, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeJwt, text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_ui_error_names_the_log_file_when_there_is_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lfme-signin-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileSignInLog(folder);
            var accounts = new AccountsViewModel(_auth, _store, _arm, (_, _) => Task.CompletedTask, log);
            _auth.Next.Enqueue(_ => throw new AuthenticationFailedException("AADSTS50020: User account from identity provider does not exist in tenant."));

            await accounts.AddAccountCommand.ExecuteAsync();

            Assert.Contains("AADSTS50020", accounts.Message, StringComparison.Ordinal);
            Assert.EndsWith("Details: " + log.Location, accounts.Message, StringComparison.Ordinal);
            var lines = File.ReadAllLines(log.Location);
            Assert.Contains(lines, l => l.Contains("add account: started", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("AADSTS50020", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Each interactive sign-in runs the next scripted step with the token it was given.</summary>
    private sealed class ScriptedAuthenticator : IAccountAuthenticator
    {
        public Queue<Func<CancellationToken, Task<SignedInAccount>>> Next { get; } = new();

        public CancellationToken LastToken { get; private set; }

        public string ClientId => "00000000-0000-0000-0000-0000000000aa";

        /// <summary>The browser was closed or never opened: MSAL waits on the loopback until cancelled.</summary>
        public static async Task<SignedInAccount> Hang(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }

        public static Func<CancellationToken, Task<SignedInAccount>> Succeed(string username) =>
            _ => Task.FromResult(new SignedInAccount(FakeAuthenticator.Account(username), new FakeCredential(username.Split('@')[0])));

        public SignInPrompt? LastPrompt { get; private set; }

        public Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, SignInPrompt prompt, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            LastPrompt = prompt;
            prompt.LinkReady?.Invoke(new Uri("http://localhost:49152/start?x=1"));
            return Next.Dequeue()(cancellationToken);
        }

        public Task<SignedInAccount> SignInWithDeviceCodeAsync(string? tenant, SignInPrompt prompt, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            LastPrompt = prompt;
            prompt.DeviceCode?.Invoke("Open https://login.example/device and enter DEMO-1234 (fake).");
            return Next.Dequeue()(cancellationToken);
        }

        public Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken) =>
            throw new AuthenticationRequiredException("Interactive sign-in is required.", new Azure.Core.TokenRequestContext(["https://management.azure.com/.default"]));
    }

    private sealed class ThrowingStore : IAccountStore
    {
        public IReadOnlyList<StoredAccount> Load() => [];

        public void Save(StoredAccount account) => throw new Credentials.LoginRejectedException("'example' is not a field of a Microsoft Entra account login.");

        public void Delete(string id)
        {
        }
    }
}

/// <summary>A link posted to the UI thread after the attempt failed must not reappear.</summary>
public sealed class LateLinkTests
{
    [Fact]
    public async Task A_link_that_arrives_after_a_failure_is_dropped()
    {
        var context = new QueueContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var auth = new BrowserChoiceTests.ProgrammableAuthenticator
            {
                Interactive = (prompt, _) =>
                {
                    prompt.LinkReady?.Invoke(new Uri("https://login.example/fake"));
                    throw new Azure.Identity.AuthenticationFailedException("AADSTS50011: redirect mismatch (fake).");
                },
            };
            var accounts = new AccountsViewModel(auth, new MemoryAccountStore(), new Adapters.ServiceBus.Tests.FakeArmReader(), (_, _) => Task.CompletedTask);

            await accounts.AddAccountAsync();
            context.RunAll();

            Assert.False(accounts.HasSignInLink);
            Assert.Contains("AADSTS50011", accounts.Message, StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _work = new();

        public override void Post(SendOrPostCallback d, object? state) => _work.Enqueue((d, state));

        public void RunAll()
        {
            while (_work.TryDequeue(out var item))
                item.Item1(item.Item2);
        }
    }
}
