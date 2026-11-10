// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// Real Entra sign-in through MSAL directly, so the app decides where the sign-in page opens: the system
/// default, a browser the user picked, or no browser at all (copy the link, or sign in with a code). The
/// localhost redirect lands on MSAL's loopback listener whichever browser completes it; cancelling the token
/// stops that listener. Tokens stay in MSAL's cache in the OS secure store (Keychain on macOS); the app keeps
/// only an AuthenticationRecord-shaped JSON (who and where, no token).
/// </summary>
public sealed class EntraAuthenticator : IAccountAuthenticator
{
    /// <summary>Azure Resource Manager, for listing tenants, subscriptions and namespaces.</summary>
    public static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    public const string AuthorityHost = "https://login.microsoftonline.com/";
    public const string RedirectUri = "http://localhost";

    private readonly EntraOptions _options;
    private readonly IBrowserLauncher _launcher;
    private readonly ISignInLog _log;
    private readonly string? _cacheDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IPublicClientApplication> _apps = new(StringComparer.OrdinalIgnoreCase);
    private MsalCacheHelper? _cache;
    private bool _cacheTried;

    public EntraAuthenticator(EntraOptions options, IBrowserLauncher launcher, ISignInLog? log = null, string? cacheDirectory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _log = log ?? new MemorySignInLog();
        _cacheDirectory = cacheDirectory;
    }

    public string ClientId => _options.ClientId;

    public static string Authority(string? tenant) => AuthorityHost + (string.IsNullOrWhiteSpace(tenant) ? EntraOptions.AnyWorkAccountTenant : tenant.Trim());

    public async Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, SignInPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var requested = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();
        var app = await AppAsync(requested).ConfigureAwait(false);
        var builder = app.AcquireTokenInteractive(ArmScopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount)
            .WithSystemWebViewOptions(WebView(prompt, _launcher, _log));
        if (!string.IsNullOrWhiteSpace(loginHint))
            builder = builder.WithLoginHint(loginHint);
        var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(ToStored(result, requested, prompt.Browser.Id), new MsalAccountCredential(app, result.Account));
    }

    public async Task<SignedInAccount> SignInWithDeviceCodeAsync(string? tenant, SignInPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var requested = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();
        var app = await AppAsync(requested).ConfigureAwait(false);
        var result = await app.AcquireTokenWithDeviceCode(ArmScopes, code =>
        {
            _log.Write("device code shown (expires " + code.ExpiresOn.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ")");
            prompt.DeviceCode?.Invoke(code.Message);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(ToStored(result, requested, prompt.Browser.Id), new MsalAccountCredential(app, result.Account));
    }

    public async Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var app = await AppAsync(account.RequestedTenant).ConfigureAwait(false);
        var msalAccount = await app.GetAccountAsync(account.Id).ConfigureAwait(false)
            ?? (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault(a => string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase))
            ?? throw new AuthenticationRequiredException("No saved sign-in for this account. Sign in again.", new TokenRequestContext(ArmScopes));
        var credential = new MsalAccountCredential(app, msalAccount);

        // Proves the cached sign-in still works. Throws AuthenticationRequiredException when it does not.
        await credential.GetTokenAsync(new TokenRequestContext(ArmScopes), cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(account with { SignedOut = false }, credential);
    }

    /// <summary>
    /// The hook MSAL calls with the sign-in URL: hand the link to the screen (Copy sign-in link) first, then
    /// open the chosen browser. A browser that won't start is logged, not thrown, so the user can still paste
    /// the link into any browser and the localhost redirect still completes.
    /// </summary>
    public static SystemWebViewOptions WebView(SignInPrompt prompt, IBrowserLauncher launcher, ISignInLog log)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(log);
        return new SystemWebViewOptions
        {
            OpenBrowserAsync = url =>
            {
                prompt.LinkReady?.Invoke(url);
                var opened = launcher.Open(url, prompt.Browser);
                log.Write(opened
                    ? $"sign-in page opened in {prompt.Browser.Name}"
                    : $"sign-in page not opened ({prompt.Browser.Name}); waiting for the link to be used in a browser");
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>What the app keeps: the same fields as Azure.Identity's AuthenticationRecord, so older records still read.</summary>
    public static string RecordJson(string username, string authority, string homeAccountId, string tenantId, string clientId) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["username"] = username,
            ["authority"] = authority,
            ["homeAccountId"] = homeAccountId,
            ["tenantId"] = tenantId,
            ["clientId"] = clientId,
        });

    private StoredAccount ToStored(AuthenticationResult result, string? requestedTenant, string browserId)
    {
        var home = result.Account.HomeAccountId?.Identifier ?? result.UniqueId;
        var authority = new Uri(AuthorityHost).Host;
        var json = RecordJson(result.Account.Username, authority, home, result.TenantId, _options.ClientId);
        return new StoredAccount(home, result.Account.Username, result.TenantId, requestedTenant, json, Browser: browserId);
    }

    private async Task<IPublicClientApplication> AppAsync(string? tenant)
    {
        var authority = Authority(tenant);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_apps.TryGetValue(authority, out var existing))
                return existing;
            var app = PublicClientApplicationBuilder.Create(_options.ClientId)
                .WithAuthority(authority)
                .WithRedirectUri(RedirectUri)
                .Build();
            if (await CacheAsync().ConfigureAwait(false) is { } cache)
                cache.RegisterCache(app.UserTokenCache);
            _apps[authority] = app;
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The persistent token cache (Keychain, DPAPI file, Secret Service). When the OS store can't be used the
    /// reason is logged and sign-in still works for this session; it is never swallowed silently.
    /// </summary>
    private async Task<MsalCacheHelper?> CacheAsync()
    {
        if (_cacheTried)
            return _cache;
        _cacheTried = true;
        try
        {
            var directory = _cacheDirectory ?? MsalCacheHelper.UserRootDirectory;
            var properties = new StorageCreationPropertiesBuilder(_options.CacheName + ".msal.cache", directory)
                .WithMacKeyChain("LunaFoundryMessageExplorer.msal", _options.CacheName)
                .WithLinuxKeyring("com.acornsoft.lfme.msal", MsalCacheHelper.LinuxKeyRingDefaultCollection, "Message Explorer sign-in cache",
                    new KeyValuePair<string, string>("app", "lfme"), new KeyValuePair<string, string>("cache", _options.CacheName))
                .Build();
            var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
            helper.VerifyPersistence();
            _cache = helper;
        }
        catch (Exception exception) when (exception is MsalCachePersistenceException or IOException or UnauthorizedAccessException or DllNotFoundException or InvalidOperationException)
        {
            _log.Write($"token cache unavailable, sign-in will not be remembered: {exception.GetType().FullName}: {exception.Message.Split('\n')[0]}");
            _cache = null;
        }

        return _cache;
    }
}

/// <summary>Tokens for one signed-in account, silently from MSAL's cache, in any tenant the account belongs to.</summary>
public sealed class MsalAccountCredential(IPublicClientApplication app, IAccount account) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var builder = app.AcquireTokenSilent(requestContext.Scopes, account);
        if (!string.IsNullOrEmpty(requestContext.TenantId))
            builder = builder.WithTenantId(requestContext.TenantId);
        try
        {
            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException exception)
        {
            throw new AuthenticationRequiredException("The saved sign-in has expired or needs approval again. " + exception.ErrorCode, requestContext, exception);
        }
    }
}
