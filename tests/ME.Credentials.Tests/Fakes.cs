// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
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
internal sealed class FakeBrowserCrypto(bool available = true) : IBrowserCryptoInterop
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public Dictionary<string, byte[]> Records { get; } = [];

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);

    public Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken)
    {
        if (!_keys.ContainsKey(keyName))
            _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return Task.CompletedTask;
    }

    public Task<byte[]> EncryptAsync(string keyName, byte[] iv, byte[] plaintext, byte[] associatedData, CancellationToken cancellationToken)
    {
        using var aes = new AesGcm(_keys[keyName], 16);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(iv, plaintext, cipher, tag, associatedData);
        return Task.FromResult<byte[]>([.. cipher, .. tag]);
    }

    public Task<byte[]> DecryptAsync(string keyName, byte[] iv, byte[] ciphertext, byte[] associatedData, CancellationToken cancellationToken)
    {
        using var aes = new AesGcm(_keys[keyName], 16);
        var plain = new byte[ciphertext.Length - 16];
        aes.Decrypt(iv, ciphertext[..^16], ciphertext[^16..], plain, associatedData);
        return Task.FromResult(plain);
    }

    public Task PutRecordAsync(string id, byte[] record, CancellationToken cancellationToken)
    {
        Records[id] = record;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetRecordAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(Records.TryGetValue(id, out var r) ? r : null);

    public Task DeleteRecordAsync(string id, CancellationToken cancellationToken)
    {
        Records.Remove(id);
        return Task.CompletedTask;
    }
}
