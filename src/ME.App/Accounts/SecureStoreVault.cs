// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// Saved connection strings in one OS secure-store item: a Keychain item on macOS, a Secret Service
/// (libsecret) item on Linux, a DPAPI-encrypted file on Windows. When the store does not work, saving is
/// turned off with a plain reason; nothing falls back to a plain file.
/// </summary>
public sealed class SecureStoreVault : ISecretVault
{
    private const string Service = "LunaFoundryMessageExplorer";
    private const string Item = "saved-connections";
    private readonly Storage? _storage;
    private readonly object _gate = new();

    public SecureStoreVault(string configDirectory)
    {
        try
        {
            var properties = new StorageCreationPropertiesBuilder("saved-connections.bin", configDirectory)
                .WithMacKeyChain(Service, Item)
                .WithLinuxKeyring(
                    "lfme_saved_connections.keyring",
                    MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    "Luna Foundry Message Explorer saved connections",
                    new KeyValuePair<string, string>("Version", "1"),
                    new KeyValuePair<string, string>("Product", Service))
                .Build();
            var storage = Storage.Create(properties);
            storage.VerifyPersistence();
            _storage = storage;
        }
        catch (Exception exception) when (exception is MsalCachePersistenceException or DllNotFoundException or PlatformNotSupportedException or InvalidOperationException)
        {
            UnavailableReason = "The system secure store is not available, so connection strings can't be saved. They still work for this session.";
        }
    }

    public string? UnavailableReason { get; }

    public IReadOnlyList<string> Names()
    {
        lock (_gate)
            return ReadAll().Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? Read(string name)
    {
        lock (_gate)
            return ReadAll().GetValueOrDefault(name);
    }

    public void Write(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (_gate)
        {
            var all = ReadAll();
            all[name.Trim()] = value.Trim();
            WriteAll(all);
        }
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (all.Remove(name))
                WriteAll(all);
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        var storage = _storage ?? throw new InvalidOperationException(UnavailableReason);
        var bytes = storage.ReadData();
        if (bytes is not { Length: > 0 })
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteAll(Dictionary<string, string> all)
    {
        var storage = _storage ?? throw new InvalidOperationException(UnavailableReason);
        storage.WriteData(JsonSerializer.SerializeToUtf8Bytes(all));
    }
}
