// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Picking the browser for sign-in, copying the link, and signing in with a code. Fakes only; nothing is launched.</summary>
public sealed class BrowserChoiceTests
{
    private static readonly Uri Link = new("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=x&redirect_uri=http%3A%2F%2Flocalhost%3A49152");

    [Fact]
    public void On_macos_installed_apps_in_applications_and_home_applications_are_listed_between_default_and_copy_link()
    {
        var present = new HashSet<string> { "/Applications/Safari.app", "/Applications/Google Chrome.app", "/Users/demo/Applications/Arc.app" };

        var choices = BrowserChoices.Detect(BrowserPlatform.MacOS, present.Contains, name => name == "HOME" ? "/Users/demo" : null);

        Assert.Equal(["default", "safari", "chrome", "arc", "copy-link"], choices.Select(c => c.Id));
        Assert.Equal("/Users/demo/Applications/Arc.app", choices.Single(c => c.Id == "arc").Executable);
    }

    [Fact]
    public void On_windows_program_files_and_local_app_data_are_searched()
    {
        var present = new HashSet<string> { @"C:\Program Files\Microsoft\Edge\Application\msedge.exe", @"C:\Users\demo\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe" };
        var env = new Dictionary<string, string> { ["ProgramFiles"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["LOCALAPPDATA"] = @"C:\Users\demo\AppData\Local" };

        var choices = BrowserChoices.Detect(BrowserPlatform.Windows, present.Contains, env.GetValueOrDefault);

        Assert.Equal(["default", "edge", "firefox", "brave", "copy-link"], choices.Select(c => c.Id));
    }

    [Fact]
    public void On_linux_browsers_are_found_on_path()
    {
        var present = new HashSet<string> { "/usr/bin/firefox", "/opt/bin/google-chrome-stable" };

        var choices = BrowserChoices.Detect(BrowserPlatform.Linux, present.Contains, name => name == "PATH" ? "/usr/local/bin:/usr/bin:/opt/bin" : null);

        Assert.Equal(["default", "chrome", "firefox", "copy-link"], choices.Select(c => c.Id));
        Assert.Equal("/opt/bin/google-chrome-stable", choices.Single(c => c.Id == "chrome").Executable);
    }

    [Fact]
    public void A_saved_browser_that_is_no_longer_installed_falls_back_to_the_system_default()
    {
        var choices = BrowserChoices.Detect(BrowserPlatform.MacOS, _ => false, _ => null);

        Assert.Same(BrowserChoices.SystemDefault, BrowserChoices.Find(choices, "brave"));
        Assert.Same(BrowserChoices.CopyLinkOnly, BrowserChoices.Find(choices, "copy-link"));
    }

    [Fact]
    public void A_picked_mac_browser_opens_with_open_dash_a_and_the_url_as_one_argument()
    {
        var info = ProcessBrowserLauncher.Command(BrowserPlatform.MacOS, Link, new BrowserChoice("chrome", "Google Chrome", "/Applications/Google Chrome.app"))!;

        Assert.Equal("/usr/bin/open", info.FileName);
        Assert.Equal(["-a", "/Applications/Google Chrome.app", Link.AbsoluteUri], info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [Theory]
    [InlineData(BrowserPlatform.MacOS, "/usr/bin/open")]
    [InlineData(BrowserPlatform.Linux, "xdg-open")]
    public void The_system_default_uses_the_os_opener(BrowserPlatform platform, string opener)
    {
        var info = ProcessBrowserLauncher.Command(platform, Link, BrowserChoices.SystemDefault)!;

        Assert.Equal(opener, info.FileName);
        Assert.Equal([Link.AbsoluteUri], info.ArgumentList);
    }

    [Fact]
    public void A_picked_windows_or_linux_browser_starts_its_program_with_the_url()
    {
        var info = ProcessBrowserLauncher.Command(BrowserPlatform.Windows, Link, new BrowserChoice("edge", "Microsoft Edge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"))!;

        Assert.Equal(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", info.FileName);
        Assert.Equal([Link.AbsoluteUri], info.ArgumentList);
    }

    [Fact]
    public void Copy_link_only_starts_nothing_and_non_web_links_are_refused()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new ProcessBrowserLauncher(BrowserPlatform.MacOS, info => { started.Add(info); return true; });

        Assert.False(launcher.Open(Link, BrowserChoices.CopyLinkOnly));
        Assert.Empty(started);
        Assert.Throws<ArgumentException>(() => ProcessBrowserLauncher.Command(BrowserPlatform.MacOS, new Uri("file:///etc/hosts"), BrowserChoices.SystemDefault));
    }

    [Fact]
    public void A_browser_that_fails_to_start_reports_false_instead_of_throwing()
    {
        var launcher = new ProcessBrowserLauncher(BrowserPlatform.Linux, _ => throw new System.ComponentModel.Win32Exception(2, "No such file"));

        Assert.False(launcher.Open(Link, new BrowserChoice("firefox", "Firefox", "/usr/bin/firefox")));
    }

    [Fact]
    public async Task The_msal_browser_hook_publishes_the_link_first_then_opens_the_chosen_browser()
    {
        var order = new List<string>();
        var launcher = new RecordingLauncher(order, opens: true);
        var log = new MemorySignInLog();
        var chrome = new BrowserChoice("chrome", "Google Chrome", "/Applications/Google Chrome.app");
        var options = EntraAuthenticator.WebView(new SignInPrompt(chrome, link => order.Add("link " + link.Host)), launcher, log);

        await options.OpenBrowserAsync(Link);

        Assert.Equal(["link login.microsoftonline.com", "open chrome"], order);
        Assert.Contains(log.Lines, l => l == "sign-in page opened in Google Chrome");
        Assert.DoesNotContain(log.Lines, l => l.Contains("client_id=x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_browser_will_not_open_the_hook_keeps_waiting_so_the_copied_link_can_finish()
    {
        var order = new List<string>();
        var log = new MemorySignInLog();
        var options = EntraAuthenticator.WebView(new SignInPrompt(BrowserChoices.CopyLinkOnly, _ => order.Add("link")), new RecordingLauncher(order, opens: false), log);

        await options.OpenBrowserAsync(Link);

        Assert.Equal(["link", "open copy-link"], order);
        Assert.Contains(log.Lines, l => l.Contains("not opened", StringComparison.Ordinal));
    }

    [Fact]
    public void Sign_in_uses_the_any_work_account_authority_or_the_typed_tenant_with_a_localhost_redirect()
    {
        Assert.Equal("https://login.microsoftonline.com/organizations", EntraAuthenticator.Authority(null));
        Assert.Equal("https://login.microsoftonline.com/contoso.example", EntraAuthenticator.Authority(" contoso.example "));
        Assert.Equal("http://localhost", EntraAuthenticator.RedirectUri);
    }

    [Fact]
    public void The_kept_record_has_the_authentication_record_fields_and_no_token()
    {
        var json = EntraAuthenticator.RecordJson("alice@contoso.example", "login.microsoftonline.com", "alice-home", "00000000-0000-0000-0000-00000000c001", "00000000-0000-0000-0000-0000000000aa");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(["username", "authority", "homeAccountId", "tenantId", "clientId"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Add_account_uses_the_picked_browser_and_the_account_remembers_it()
    {
        var (accounts, auth, store) = Create();
        accounts.SelectedBrowser = accounts.Browsers.Single(b => b.Id == "firefox");
        auth.WillSignIn("alice@contoso.example");

        await accounts.AddAccountAsync();

        Assert.Equal(["firefox"], auth.Browsers);
        Assert.Equal("firefox", store.Saved.Values.Single().Browser);
        Assert.Equal("firefox", accounts.Accounts[0].Browser.Id);
    }

    [Fact]
    public async Task Sign_in_again_opens_the_accounts_own_browser_and_changing_it_on_the_card_saves()
    {
        var (accounts, auth, store) = Create();
        store.Save(FakeAuthenticator.Account("alice@contoso.example") with { SignedOut = true, Browser = "firefox" });
        await accounts.RestoreAsync();
        accounts.SelectedBrowser = BrowserChoices.CopyLinkOnly;

        auth.WillSignIn("alice@contoso.example");
        await accounts.Accounts[0].SignInCommand.ExecuteAsync();
        Assert.Equal(["firefox"], auth.Browsers);

        accounts.Accounts[0].Browser = accounts.Browsers.Single(b => b.Id == "chrome");
        Assert.Equal("chrome", store.Saved.Values.Single().Browser);
    }

    [Fact]
    public async Task Copy_sign_in_link_puts_the_waiting_link_on_the_clipboard_and_clears_after()
    {
        var (accounts, _, _) = Create(out var auth2);
        var copied = new List<string>();
        accounts.Clipboard = text => { copied.Add(text); return Task.CompletedTask; };
        var gate = new TaskCompletionSource<SignedInAccount>();
        auth2.Interactive = (prompt, _) =>
        {
            prompt.LinkReady?.Invoke(Link);
            return gate.Task;
        };

        var waiting = accounts.AddAccountCommand.ExecuteAsync();
        Assert.True(accounts.HasSignInLink);
        await accounts.CopySignInLinkCommand.ExecuteAsync();
        accounts.CancelSignIn();
        gate.SetCanceled();
        await waiting;

        Assert.Equal([Link.AbsoluteUri], copied);
        Assert.False(accounts.HasSignInLink);
        Assert.True(accounts.AddAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task Sign_in_with_a_code_shows_the_code_message_then_adds_the_account()
    {
        var (accounts, auth, _) = Create();
        auth.WillSignIn("alice@contoso.example");
        string? seen = null;
        accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.DeviceCodeText) && accounts.DeviceCodeText is { } text)
                seen = text;
        };

        await accounts.SignInWithCodeCommand.ExecuteAsync();

        Assert.Contains("DEMO-1234", seen, StringComparison.Ordinal);
        Assert.False(accounts.HasDeviceCode);
        Assert.Single(accounts.Accounts);
    }

    [Fact]
    public async Task Add_account_and_sign_in_with_a_code_are_disabled_only_while_a_sign_in_waits()
    {
        var (accounts, _, _) = Create(out var auth2);
        var gate = new TaskCompletionSource<SignedInAccount>();
        auth2.Interactive = (_, token) => { token.Register(() => gate.TrySetCanceled()); return gate.Task; };

        var waiting = accounts.AddAccountCommand.ExecuteAsync();
        Assert.False(accounts.SignInWithCodeCommand.CanExecute(null));
        accounts.CancelSignInCommand.Execute(null);
        await waiting;

        Assert.True(accounts.SignInWithCodeCommand.CanExecute(null));
        Assert.True(accounts.AddAccountCommand.CanExecute(null));
        Assert.Equal("Sign-in was cancelled.", accounts.Message);
    }

    [Fact]
    public void The_vault_keeps_each_accounts_browser()
    {
        var vault = new TestVault().Vault;
        var store = new VaultAccountStore(vault);

        store.Save(FakeAuthenticator.Account("alice@contoso.example") with { Browser = "edge" });

        Assert.Equal("edge", store.Load().Single().Browser);
    }

    private static readonly IReadOnlyList<BrowserChoice> TestBrowsers =
    [
        BrowserChoices.SystemDefault,
        new("chrome", "Google Chrome", "/Applications/Google Chrome.app"),
        new("firefox", "Firefox", "/Applications/Firefox.app"),
        BrowserChoices.CopyLinkOnly,
    ];

    private static (AccountsViewModel, FakeAuthenticator, MemoryAccountStore) Create()
    {
        var auth = new FakeAuthenticator();
        var store = new MemoryAccountStore();
        return (new AccountsViewModel(auth, store, new FakeArmReader(), (_, _) => Task.CompletedTask, browsers: TestBrowsers), auth, store);
    }

    private static (AccountsViewModel, ProgrammableAuthenticator, MemoryAccountStore) Create(out ProgrammableAuthenticator auth)
    {
        auth = new ProgrammableAuthenticator();
        var store = new MemoryAccountStore();
        return (new AccountsViewModel(auth, store, new FakeArmReader(), (_, _) => Task.CompletedTask, browsers: TestBrowsers), auth, store);
    }

    private sealed class RecordingLauncher(List<string> order, bool opens) : IBrowserLauncher
    {
        public bool Open(Uri url, BrowserChoice browser)
        {
            order.Add("open " + browser.Id);
            return opens;
        }
    }

    internal sealed class ProgrammableAuthenticator : IAccountAuthenticator
    {
        public Func<SignInPrompt, CancellationToken, Task<SignedInAccount>> Interactive { get; set; } = (_, _) => throw new InvalidOperationException("not set");

        public string ClientId => "00000000-0000-0000-0000-0000000000aa";

        public Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, SignInPrompt prompt, CancellationToken cancellationToken) => Interactive(prompt, cancellationToken);

        public Task<SignedInAccount> SignInWithDeviceCodeAsync(string? tenant, SignInPrompt prompt, CancellationToken cancellationToken) => Interactive(prompt, cancellationToken);

        public Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
