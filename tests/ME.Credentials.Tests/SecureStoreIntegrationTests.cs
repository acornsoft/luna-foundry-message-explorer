// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Tests;

/// <summary>Runs only where CI sets LFME_SECURE_STORE_TESTS=1: the macOS, Windows and Linux (keyring) jobs.</summary>
public sealed class SecureStoreFactAttribute : FactAttribute
{
    public SecureStoreFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LFME_SECURE_STORE_TESTS") != "1")
            Skip = "Set LFME_SECURE_STORE_TESTS=1 to use this computer's real secure store.";
    }
}

/// <summary>The real OS secure store, under a test service name. Adds, reads, replaces and deletes a fake secret.</summary>
public sealed class SecureStoreIntegrationTests
{
    private const string Service = "LunaFoundryMessageExplorer.tests";

    [SecureStoreFact]
    public async Task The_real_store_adds_reads_replaces_and_deletes_a_secret()
    {
        var store = SecretStores.ForCurrentPlatform(Service);
        Assert.Null(store.UnavailableReason);
        Assert.Equal(SecretProtection.OperatingSystem, store.Protection);
        var key = "it-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await store.WriteAsync(key, new RuntimeSecret("fake-ci-secret-1"));
            Assert.Equal("fake-ci-secret-1", (await store.ReadAsync(key))!.Reveal());

            await store.WriteAsync(key, new RuntimeSecret("fake-ci-secret-2"));
            Assert.Equal("fake-ci-secret-2", (await store.ReadAsync(key))!.Reveal());
        }
        finally
        {
            await store.DeleteAsync(key);
        }

        Assert.Null(await store.ReadAsync(key));
    }

    [SecureStoreFact]
    public async Task The_vault_on_the_real_store_keeps_the_secret_out_of_every_file()
    {
        var folder = Directory.CreateTempSubdirectory("lfme-vault-it-").FullName;
        var vault = new CredentialVault(SecretStores.ForCurrentPlatform(Service), new FileLoginMetadataStore(Path.Combine(folder, "logins.json")));
        var entry = await vault.AddAsync(
            new LoginDraft("Kafka CI (demo)", LoginKind.KafkaSasl, new Dictionary<string, string> { ["bootstrapServers"] = "kafka-demo.example:9093", ["mechanism"] = "PLAIN", ["username"] = "demo-user" }),
            new RuntimeSecret("fake-ci-kafka-password"));
        try
        {
            Assert.Equal("fake-ci-kafka-password", (await vault.ReadSecretAsync(entry.Id))!.Reveal());
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("fake-ci-kafka-password", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            await vault.DeleteAsync(entry.Id);
            Directory.Delete(folder, recursive: true);
        }
    }
}
