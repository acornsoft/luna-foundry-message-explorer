// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Tests;

internal sealed class MemoryMetadata : ILoginMetadataStore
{
    public string? Json { get; set; }

    public int Saves { get; private set; }

    public string? Load() => Json;

    public void Save(string json)
    {
        Json = json;
        Saves++;
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeKeychain : IKeychainApi
{
    public Dictionary<(string Service, string Account), (byte[] Data, KeychainItem Item)> Items { get; } = [];

    public int? FailWith { get; set; }

    public List<string> Calls { get; } = [];

    public int Add(KeychainItem item, byte[] data)
    {
        Calls.Add("add");
        if (FailWith is { } code)
            return code;
        if (Items.ContainsKey((item.Service, item.Account)))
            return AppleKeychainStore.DuplicateItem;
        Items[(item.Service, item.Account)] = (data.ToArray(), item);
        return 0;
    }

    public int Update(KeychainItem item, byte[] data)
    {
        Calls.Add("update");
        if (FailWith is { } code)
            return code;
        if (!Items.TryGetValue((item.Service, item.Account), out var existing))
            return AppleKeychainStore.ItemNotFound;
        Items[(item.Service, item.Account)] = (data.ToArray(), existing.Item);
        return 0;
    }

    public int CopyData(KeychainItem item, out byte[]? data)
    {
        data = Items.TryGetValue((item.Service, item.Account), out var existing) ? existing.Data.ToArray() : null;
        return data is null ? AppleKeychainStore.ItemNotFound : 0;
    }

    public int Delete(KeychainItem item) => Items.Remove((item.Service, item.Account)) ? 0 : AppleKeychainStore.ItemNotFound;
}

internal sealed class FakeCredentialManager : ICredentialManagerApi
{
    public Dictionary<string, (string User, byte[] Blob)> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Write(string target, string userName, byte[] blob)
    {
        Items[target] = (userName, blob.ToArray());
        return 0;
    }

    public int Read(string target, out byte[]? blob)
    {
        blob = Items.TryGetValue(target, out var item) ? item.Blob.ToArray() : null;
        return blob is null ? WindowsCredentialStore.NotFound : 0;
    }

    public int Delete(string target) => Items.Remove(target) ? 0 : WindowsCredentialStore.NotFound;
}

internal sealed class FakeSecretService(bool running = true) : ISecretServiceApi
{
    public List<(string Label, Dictionary<string, string> Attributes, string Secret)> Items { get; } = [];

    public void Store(string label, IReadOnlyDictionary<string, string> attributes, string secret)
    {
        Running();
        Items.RemoveAll(i => Same(i.Attributes, attributes));
        Items.Add((label, attributes.ToDictionary(), secret));
    }

    public string? Lookup(IReadOnlyDictionary<string, string> attributes)
    {
        Running();
        return Items.FirstOrDefault(i => Same(i.Attributes, attributes)).Secret;
    }

    public void Clear(IReadOnlyDictionary<string, string> attributes)
    {
        Running();
        Items.RemoveAll(i => Same(i.Attributes, attributes));
    }

    private void Running()
    {
        if (!running)
            throw new SecretStoreException("The Secret Service keyring refused the request: Cannot autolaunch D-Bus without X11 $DISPLAY");
    }

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
}

/// <summary>Stands in for WebCrypto + IndexedDB: a key the test never reads, AES-GCM, records in a dictionary.</summary>
/// <summary>Mirrors lfme-vault.js with .NET AES-GCM: fresh IV per write, login ID as associated data.</summary>
internal sealed class FakeBrowserCrypto(bool available = true) : IBrowserCryptoInterop
{
    private readonly Dictionary<string, byte[]> _keys = [];

    /// <summary>What IndexedDB would hold: 12-byte IV followed by ciphertext and tag.</summary>
    public Dictionary<string, byte[]> Records { get; } = [];

    public List<byte[]> IvsUsed { get; } = [];

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);

    public Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken)
    {
        if (!_keys.ContainsKey(keyName))
            _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return Task.CompletedTask;
    }

    public Task EncryptAsync(string keyName, string id, string plaintext, CancellationToken cancellationToken)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        IvsUsed.Add(iv);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        using var aes = new AesGcm(_keys[keyName], 16);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aes.Encrypt(iv, plain, cipher, tag, Encoding.UTF8.GetBytes(id));
        Records[id] = [.. iv, .. cipher, .. tag];
        return Task.CompletedTask;
    }

    public Task<string?> DecryptAsync(string keyName, string id, CancellationToken cancellationToken)
    {
        if (!Records.TryGetValue(id, out var record))
            return Task.FromResult<string?>(null);
        using var aes = new AesGcm(_keys[keyName], 16);
        var plain = new byte[record.Length - 12 - 16];
        aes.Decrypt(record[..12], record[12..^16], record[^16..], plain, Encoding.UTF8.GetBytes(id));
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        Records.Remove(id);
        return Task.CompletedTask;
    }
}
