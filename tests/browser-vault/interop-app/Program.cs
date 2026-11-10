// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Browser;
using Acornsoft.MessageExplorer.Credentials.Stores;

[assembly: SupportedOSPlatform("browser")]

namespace Acornsoft.MessageExplorer.Credentials.Browser.InteropApp;

public static partial class Program
{
    public static void Main()
    {
    }

    /// <summary>Runs the C# store and the vault against the real module; returns one line per check.</summary>
    [JSExport]
    public static async Task<string> RunAsync(string secret)
    {
        var lines = new List<string>();
        var store = await BrowserVault.CreateAsync("/lfme-vault.js");
        lines.Add($"ready={store.UnavailableReason is null}");
        await store.WriteAsync("login-cs", new RuntimeSecret(secret));
        lines.Add($"roundtrip={(await store.ReadAsync("login-cs"))?.Reveal() == secret}");
        lines.Add($"missing-null={await store.ReadAsync("login-none") is null}");

        var vault = new CredentialVault(store, new MemoryLoginMetadataStore());
        var login = await vault.AddAsync(
            new LoginDraft("RabbitMQ (demo)", LoginKind.RabbitMqPassword, new Dictionary<string, string> { ["host"] = "rabbit-demo.fabrikam.example", ["username"] = "demo" }),
            new RuntimeSecret(secret));
        lines.Add($"vault-roundtrip={(await vault.ReadSecretAsync(login.Id))?.Reveal() == secret}");
        lines.Add($"login-id={login.Id}");
        await vault.DeleteAsync(login.Id);
        lines.Add($"vault-deleted={await store.ReadAsync(login.Id) is null}");
        return string.Join("\n", lines);
    }

    /// <summary>Reads one login through the C# store: "null", "match", "other", or "refused" (and whether the message leaked the secret).</summary>
    [JSExport]
    public static async Task<string> ReadOutcomeAsync(string id, string secret)
    {
        var store = await BrowserVault.CreateAsync("/lfme-vault.js");
        try
        {
            var value = await store.ReadAsync(id);
            return value is null ? "null" : value.Reveal() == secret ? "match" : "other";
        }
        catch (SecretStoreException exception)
        {
            return $"refused leak={exception.Message.Contains(secret, StringComparison.Ordinal)}";
        }
    }
}
