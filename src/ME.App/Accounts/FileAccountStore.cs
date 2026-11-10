// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>One small JSON file per account under accounts/ in the config folder. No tokens, no secrets.</summary>
public sealed class FileAccountStore(string directory) : IAccountStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Directory { get; } = Path.Combine(directory, "accounts");

    public IReadOnlyList<StoredAccount> Load()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var accounts = new List<StoredAccount>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(file), Json) is { Id.Length: > 0 } account)
                    accounts.Add(account);
            }
            catch (JsonException)
            {
                // A damaged file is skipped; the account can be added again.
            }
        }

        return accounts;
    }

    public void Save(StoredAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(account.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(account, Json));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(string id) =>
        Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant() + ".json");
}
