// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// Reads the 0.6.0 "Save to Keychain" item (one secure-store item holding every saved connection string) so
/// it can be moved into the credential vault, then clears it. Never writes.
/// </summary>
public sealed class LegacySavedConnections : ILegacySavedConnections
{
    private readonly Storage? _storage;

    public LegacySavedConnections(string configDirectory)
    {
        try
        {
            var properties = new StorageCreationPropertiesBuilder("saved-connections.bin", configDirectory)
                .WithMacKeyChain("LunaFoundryMessageExplorer", "saved-connections")
                .WithLinuxKeyring(
                    "lfme_saved_connections.keyring",
                    MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    "Luna Foundry Message Explorer saved connections",
                    new KeyValuePair<string, string>("Version", "1"),
                    new KeyValuePair<string, string>("Product", "LunaFoundryMessageExplorer"))
                .Build();
            _storage = Storage.Create(properties);
        }
        catch (Exception exception) when (exception is MsalCachePersistenceException or DllNotFoundException or PlatformNotSupportedException or InvalidOperationException)
        {
            _storage = null;
        }
    }

    public IReadOnlyDictionary<string, string> ReadAll()
    {
        try
        {
            var bytes = _storage?.ReadData();
            if (bytes is not { Length: > 0 })
                return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or MsalCachePersistenceException or InvalidOperationException or DllNotFoundException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void Clear()
    {
        try
        {
            _storage?.Clear(ignoreExceptions: true);
        }
        catch (MsalCachePersistenceException)
        {
            // Nothing left to clear.
        }
    }
}
