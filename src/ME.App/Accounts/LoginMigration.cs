// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>Moves what 0.6.0 saved into the credential vault. Safe to run on every launch.</summary>
public static class LoginMigration
{
    /// <summary>Imports each 0.6.0 saved connection string as a login, then clears the old item. Returns how many moved.</summary>
    public static async Task<int> ImportSavedConnectionsAsync(ILegacySavedConnections legacy, ICredentialVault vault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(vault);
        if (vault.SecretsUnavailableReason is not null)
            return 0; // Leave them where they are until a secure store is available.

        var saved = legacy.ReadAll();
        if (saved.Count == 0)
            return 0;

        var moved = 0;
        var existing = vault.List().Select(e => e.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in saved)
        {
            if (existing.Contains(name))
                continue;
            var input = ServiceBusConnectionInput.Parse(value);
            if (input.Error is not null)
                continue;
            await vault.AddAsync(DraftFor(name, input), new RuntimeSecret(value), cancellationToken).ConfigureAwait(false);
            moved++;
        }

        legacy.Clear();
        return moved;
    }

    /// <summary>Moves 0.6.0 account files (accounts/*.json) into the vault and deletes them.</summary>
    public static int ImportAccountFiles(FileAccountStore files, IAccountStore target)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);
        var accounts = files.Load();
        foreach (var account in accounts)
        {
            target.Save(account);
            files.Delete(account.Id);
        }

        return accounts.Count;
    }

    /// <summary>The login draft for a pasted Service Bus string: the non-secret parts only.</summary>
    public static LoginDraft DraftFor(string name, ServiceBusConnectionInput input, string? accountId = null, string? accountName = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Flavor == ConnectionFlavor.Emulator)
            return new LoginDraft(name, LoginKind.EmulatorConnectionString, new Dictionary<string, string> { ["endpoint"] = input.FullyQualifiedNamespace ?? string.Empty });

        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint"] = input.FullyQualifiedNamespace ?? string.Empty };
        if (input.EntityPath is { } path)
            fields["entityPath"] = path;
        if (input.KeyName is { } keyName)
            fields["keyName"] = keyName;
        if (input.Flavor == ConnectionFlavor.EndpointOnly && accountName is not null)
            fields["accountName"] = accountName;
        return new LoginDraft(name, LoginKind.ServiceBusConnectionString, fields, input.Flavor == ConnectionFlavor.EndpointOnly ? accountId : null);
    }
}
