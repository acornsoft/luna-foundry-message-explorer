// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>Refuses every write: the fail-closed store for a computer with no secure store.</summary>
public sealed class UnavailableSecretStore(string name, string reason) : ISecretStore
{
    public string Name => name;

    public SecretProtection Protection => SecretProtection.None;

    public string? UnavailableReason => reason;

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default) => throw new SecretStoreException(reason);

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new SecretStoreException(reason);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Secrets held in this process only and gone at exit. For the synthetic demo and tests; never a file.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);

    public string Name => "memory (this session only)";

    public SecretProtection Protection => SecretProtection.Memory;

    public string? UnavailableReason => null;

    public int Count => _items.Count;

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        _items[key] = secret.Reveal();
        return Task.CompletedTask;
    }

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.TryGetValue(key, out var value) ? new RuntimeSecret(value) : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _items.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Picks the secret store for the platform the app runs on.</summary>
public static class SecretStores
{
    public const string DefaultService = "LunaFoundryMessageExplorer.logins";

    public static ISecretStore ForCurrentPlatform(string service = DefaultService)
    {
        if (OperatingSystem.IsMacOS())
            return AppleKeychainStore.Create(service, ApplePlatform.MacOS);
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            return AppleKeychainStore.Create(service, ApplePlatform.IOS);
        if (OperatingSystem.IsWindows())
            return WindowsCredentialStore.Create(service);
        if (OperatingSystem.IsLinux())
            return LibSecretStore.Create(service);
        if (OperatingSystem.IsAndroid())
            return new UnavailableSecretStore("Android Keystore", "On Android the app head creates the store (SealedSecretStore over the Android Keystore). Until then secrets can't be saved here.");
        if (OperatingSystem.IsBrowser())
            return new UnavailableSecretStore("browser storage", "In the browser, create the store with BrowserVault.CreateAsync (ME.Credentials.Browser), which loads lfme-vault.js. Until then secrets can't be saved here; sign in with Entra instead.");
        return new UnavailableSecretStore("secure store", "This platform has no supported secure store, so secrets can't be saved.");
    }
}

/// <summary>Login metadata held in memory (the synthetic demo and tests). Nothing is written to disk.</summary>
public sealed class MemoryLoginMetadataStore : ILoginMetadataStore
{
    public string? Json { get; private set; }

    public string? Load() => Json;

    public void Save(string json) => Json = json;
}
