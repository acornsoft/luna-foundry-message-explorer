// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Tests;

/// <summary>
/// The store the Android head uses over the Android Keystore, checked here with a fake cipher (real AES-GCM
/// with an in-test key) and an in-memory blob store. The secret values are made up.
/// </summary>
public sealed class SealedSecretStoreTests
{
    private const string Planted = "planted-secret-value-not-real-0001";

    [Fact]
    public async Task A_secret_round_trips_and_only_a_sealed_blob_is_stored()
    {
        var blobs = new MemoryBlobs();
        var store = new SealedSecretStore("Android Keystore", new FakeGcmCipher(), blobs);

        await store.WriteAsync("login-orders", new RuntimeSecret(Planted));

        Assert.Equal(Planted, (await store.ReadAsync("login-orders"))!.Reveal());
        var stored = Assert.Single(blobs.Items);
        Assert.DoesNotContain(Planted, stored.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(Planted, Encoding.UTF8.GetString(Convert.FromBase64String(stored.Value)), StringComparison.Ordinal);
        Assert.Equal(SecretProtection.OperatingSystem, store.Protection);
    }

    [Fact]
    public async Task A_blob_copied_under_another_key_does_not_open()
    {
        var blobs = new MemoryBlobs();
        var store = new SealedSecretStore("Android Keystore", new FakeGcmCipher(), blobs);
        await store.WriteAsync("login-orders", new RuntimeSecret(Planted));

        blobs.Items["lfme.v1.login-payments"] = blobs.Items["lfme.v1.login-orders"];

        var error = await Assert.ThrowsAsync<SecretStoreException>(() => store.ReadAsync("login-payments"));
        Assert.DoesNotContain(Planted, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_blob_is_refused_with_a_plain_message()
    {
        var blobs = new MemoryBlobs();
        var store = new SealedSecretStore("Android Keystore", new FakeGcmCipher(), blobs);
        await store.WriteAsync("login-orders", new RuntimeSecret(Planted));
        var bytes = Convert.FromBase64String(blobs.Items["lfme.v1.login-orders"]);
        bytes[^1] ^= 0x01;
        blobs.Items["lfme.v1.login-orders"] = Convert.ToBase64String(bytes);

        var error = await Assert.ThrowsAsync<SecretStoreException>(() => store.ReadAsync("login-orders"));
        Assert.Contains("Save the login again", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_secrets_read_as_null_and_delete_removes_the_blob()
    {
        var blobs = new MemoryBlobs();
        var store = new SealedSecretStore("Android Keystore", new FakeGcmCipher(), blobs);

        Assert.Null(await store.ReadAsync("login-none"));
        await store.WriteAsync("login-orders", new RuntimeSecret(Planted));
        await store.DeleteAsync("login-orders");

        Assert.Empty(blobs.Items);
        Assert.Null(await store.ReadAsync("login-orders"));
    }

    [Fact]
    public async Task Without_a_working_key_every_write_is_refused_and_nothing_is_stored()
    {
        var blobs = new MemoryBlobs();
        var store = new SealedSecretStore("Android Keystore", new FakeGcmCipher("The Keystore key was invalidated."), blobs);

        await Assert.ThrowsAsync<SecretStoreException>(() => store.WriteAsync("login-orders", new RuntimeSecret(Planted)));

        Assert.Empty(blobs.Items);
        Assert.Equal(SecretProtection.None, store.Protection);
        Assert.Equal("The Keystore key was invalidated.", store.UnavailableReason);
    }

    [Fact]
    public async Task The_vault_over_the_sealed_store_saves_a_sas_login_without_the_secret_in_metadata()
    {
        var blobs = new MemoryBlobs();
        var metadata = new MemoryLoginMetadataStore();
        var vault = new CredentialVault(new SealedSecretStore("Android Keystore", new FakeGcmCipher(), blobs), metadata);

        var saved = await vault.AddAsync(
            new LoginDraft("orders reader (demo)", LoginKind.ServiceBusConnectionString, new Dictionary<string, string> { ["endpoint"] = "sbns-demo-001.servicebus.windows.net" }),
            new RuntimeSecret(Planted));

        Assert.Equal(Planted, (await vault.ReadSecretAsync(saved.Id))!.Reveal());
        Assert.DoesNotContain(Planted, metadata.Json!, StringComparison.Ordinal);
        Assert.Equal(SecretProtection.OperatingSystem, vault.Protection);
    }

    private sealed class MemoryBlobs : ISealedBlobStore
    {
        public Dictionary<string, string> Items { get; } = [];

        public string? Read(string key) => Items.GetValueOrDefault(key);

        public void Write(string key, string base64Blob) => Items[key] = base64Blob;

        public void Delete(string key) => Items.Remove(key);
    }

    /// <summary>Same shape as the Keystore cipher: AES-256-GCM, 12-byte IV, 16-byte tag, IV stored in front.</summary>
    private sealed class FakeGcmCipher(string? unavailable = null) : ISecretCipher
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public string? UnavailableReason => unavailable;

        public byte[] Seal(byte[] plaintext, byte[] associatedData)
        {
            var iv = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];
            using var gcm = new AesGcm(_key, 16);
            gcm.Encrypt(iv, plaintext, cipher, tag, associatedData);
            return [.. iv, .. cipher, .. tag];
        }

        public byte[] Open(byte[] sealedBlob, byte[] associatedData)
        {
            var iv = sealedBlob[..12];
            var tag = sealedBlob[^16..];
            var cipher = sealedBlob[12..^16];
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(_key, 16);
            gcm.Decrypt(iv, cipher, tag, plain, associatedData);
            return plain;
        }
    }
}
