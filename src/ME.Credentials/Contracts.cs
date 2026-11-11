// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials;

/// <summary>How well a secret store protects what it holds.</summary>
public enum SecretProtection
{
    /// <summary>Nothing can be saved. Every write is refused (fail closed).</summary>
    None,

    /// <summary>The OS secure store: Keychain, Windows DPAPI / Credential Manager, Secret Service.</summary>
    OperatingSystem,

    /// <summary>Encrypted in the browser with a non-extractable WebCrypto key. Weaker than an OS store.</summary>
    BrowserProtected,

    /// <summary>In memory only, gone when the app closes. Demos and tests.</summary>
    Memory,
}

/// <summary>
/// Secrets in a platform secure store, by key. Never a plain file: when there is no secure store,
/// <see cref="UnavailableReason"/> says why and every write is refused. Async because the browser store is.
/// </summary>
public interface ISecretStore
{
    /// <summary>"Keychain", "Windows Credential Manager", "Secret Service keyring", "browser storage".</summary>
    string Name { get; }

    SecretProtection Protection { get; }

    /// <summary>Null when the store works.</summary>
    string? UnavailableReason { get; }

    Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default);

    Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Where the non-secret login metadata (config JSON) lives: a file on desktop, local storage on the web.</summary>
public interface ILoginMetadataStore
{
    string? Load();

    void Save(string json);
}

/// <summary>
/// Saved logins for every system the app connects to. One contract on every platform: metadata in config
/// JSON (<see cref="ILoginMetadataStore"/>), secrets only in the platform <see cref="ISecretStore"/>.
/// </summary>
public interface ICredentialVault
{
    /// <summary>The secret store's name, for messages ("Keychain").</summary>
    string SecureStoreName { get; }

    SecretProtection Protection { get; }

    /// <summary>Null when secrets can be saved; otherwise why not. Logins without a secret still save.</summary>
    string? SecretsUnavailableReason { get; }

    event EventHandler? Changed;

    IReadOnlyList<LoginEntry> List();

    LoginEntry? Find(string id);

    /// <summary>Adds a login. Throws <see cref="LoginRejectedException"/> before anything is written when it can't be saved safely.</summary>
    Task<LoginEntry> AddAsync(LoginDraft draft, RuntimeSecret? secret, CancellationToken cancellationToken = default);

    /// <summary>Changes a login. A null <paramref name="secret"/> keeps the saved one.</summary>
    Task<LoginEntry> UpdateAsync(string id, LoginDraft draft, RuntimeSecret? secret, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces, under a caller-chosen ID, a login whose kind has no secret (an Entra account record).</summary>
    LoginEntry Put(string id, LoginDraft draft);

    LoginEntry Rename(string id, string displayName);

    /// <summary>Deletes the login and its secret.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    LoginEntry MarkUsed(string id);

    /// <summary>The secret, for connecting. Null when the login has none.</summary>
    Task<RuntimeSecret?> ReadSecretAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>A login was not saved. The message never contains a secret or a field value.</summary>
public sealed class LoginRejectedException(string message) : Exception(message);

/// <summary>A secret store call failed. The message is plain and never contains the secret.</summary>
public sealed class SecretStoreException(string message) : Exception(message);
