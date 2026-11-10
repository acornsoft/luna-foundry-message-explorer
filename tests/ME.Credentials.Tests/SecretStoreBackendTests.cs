// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Tests;

/// <summary>Each platform backend's logic against a fake of its native API.</summary>
public sealed class SecretStoreBackendTests
{
    private static readonly RuntimeSecret Secret = new("fake-password-not-real");

    [Fact]
    public async Task Keychain_adds_once_then_updates_reads_and_deletes()
    {
        var keychain = new FakeKeychain();
        var store = new AppleKeychainStore(keychain, "LunaFoundryMessageExplorer.tests", ApplePlatform.MacOS);

        await store.WriteAsync("login-1", Secret);
        await store.WriteAsync("login-1", new RuntimeSecret("second-fake"));

        Assert.Equal(["update", "add", "update"], keychain.Calls);
        Assert.Equal("second-fake", (await store.ReadAsync("login-1"))!.Reveal());
        var item = keychain.Items[("LunaFoundryMessageExplorer.tests", "login-1")].Item;
        Assert.Equal(KeychainAccessibility.Default, item.Accessibility);
        await store.DeleteAsync("login-1");
        await store.DeleteAsync("login-1");
        Assert.Null(await store.ReadAsync("login-1"));
    }

    [Fact]
    public async Task Ios_keychain_items_are_this_device_only_when_unlocked()
    {
        var keychain = new FakeKeychain();
        var store = new AppleKeychainStore(keychain, "svc", ApplePlatform.IOS);

        await store.WriteAsync("login-1", Secret);

        Assert.Equal(KeychainAccessibility.WhenUnlockedThisDeviceOnly, keychain.Items[("svc", "login-1")].Item.Accessibility);
    }

    [Fact]
    public async Task A_keychain_error_is_plain_and_has_no_secret()
    {
        var store = new AppleKeychainStore(new FakeKeychain { FailWith = -25308 }, "svc", ApplePlatform.MacOS);

        var error = await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("login-1", Secret));

        Assert.Equal("The Keychain could not save the secret (status -25308).", error.Message);
    }

    [Fact]
    public async Task Credential_manager_uses_one_target_per_login_and_refuses_oversized_secrets()
    {
        var api = new FakeCredentialManager();
        var store = new WindowsCredentialStore(api, "LunaFoundryMessageExplorer.tests");

        await store.WriteAsync("login-1", Secret);

        Assert.Equal("LunaFoundryMessageExplorer.tests/login-1", Assert.Single(api.Items.Keys));
        Assert.Equal("fake-password-not-real", Encoding.UTF8.GetString(api.Items.Values.Single().Blob));
        Assert.Equal("fake-password-not-real", (await store.ReadAsync("login-1"))!.Reveal());
        await store.DeleteAsync("login-1");
        Assert.Null(await store.ReadAsync("login-1"));
        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("big", new RuntimeSecret(new string('x', 3000))));
        Assert.Empty(api.Items);
    }

    [Fact]
    public async Task Secret_service_items_are_found_by_application_service_and_login()
    {
        var api = new FakeSecretService();
        var store = new LibSecretStore(api, "LunaFoundryMessageExplorer.tests");

        await store.WriteAsync("login-1", Secret);

        var item = Assert.Single(api.Items);
        Assert.Equal("ai.acornsoft.lfme", item.Attributes["application"]);
        Assert.Equal("login-1", item.Attributes["login"]);
        Assert.Equal("fake-password-not-real", (await store.ReadAsync("login-1"))!.Reveal());
        await store.DeleteAsync("login-1");
        Assert.Empty(api.Items);
    }

    [Fact]
    public async Task With_no_secret_service_the_linux_store_fails_closed()
    {
        var store = new LibSecretStore(new FakeSecretService(running: false), "svc");

        Assert.NotNull(store.UnavailableReason);
        Assert.Contains("Nothing is written to a file", store.UnavailableReason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("login-1", Secret));
    }

    [Fact]
    public async Task The_browser_store_keeps_only_ciphertext_and_reads_it_back()
    {
        var browser = new FakeBrowserCrypto();
        var store = new BrowserSecretStore(browser);
        Assert.True(await store.InitializeAsync());

        await store.WriteAsync("login-1", Secret);

        var record = browser.Records["login-1"];
        Assert.DoesNotContain("fake-password", Encoding.UTF8.GetString(record), StringComparison.Ordinal);
        Assert.Equal("fake-password-not-real", (await store.ReadAsync("login-1"))!.Reveal());
        Assert.Equal(SecretProtection.BrowserProtected, store.Protection);
        Assert.Contains("weaker than the OS store", store.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_browser_store_rejects_a_changed_or_moved_record()
    {
        var browser = new FakeBrowserCrypto();
        var store = new BrowserSecretStore(browser);
        await store.InitializeAsync();
        await store.WriteAsync("login-1", Secret);

        browser.Records["login-2"] = browser.Records["login-1"];
        await Assert.ThrowsAsync<SecretStoreException>(() => store.ReadAsync("login-2"));

        browser.Records["login-1"][^1] ^= 0xFF;
        await Assert.ThrowsAsync<SecretStoreException>(() => store.ReadAsync("login-1"));
    }

    [Fact]
    public async Task The_browser_store_fails_closed_until_it_is_ready()
    {
        var store = new BrowserSecretStore(new FakeBrowserCrypto(available: false));
        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("login-1", Secret));

        Assert.False(await store.InitializeAsync());
        Assert.NotNull(store.UnavailableReason);
        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("login-1", Secret));
    }

    [Fact]
    public void The_platform_store_matches_the_operating_system()
    {
        var store = SecretStores.ForCurrentPlatform("LunaFoundryMessageExplorer.tests");

        var expected = OperatingSystem.IsMacOS() ? "Keychain"
            : OperatingSystem.IsWindows() ? "Windows Credential Manager"
            : OperatingSystem.IsLinux() ? "Secret Service keyring"
            : store.Name;
        Assert.Equal(expected, store.Name);
        Assert.True(store.Protection is SecretProtection.OperatingSystem or SecretProtection.None);
    }
}
