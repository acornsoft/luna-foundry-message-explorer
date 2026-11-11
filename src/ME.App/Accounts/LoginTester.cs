// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

public enum LoginTestOutcome
{
    Passed,
    Failed,
    NotAvailable,
}

/// <summary>What a test found, in one sentence with no secret in it.</summary>
public sealed record LoginTestResult(LoginTestOutcome Outcome, string Message);

/// <summary>Checks that a saved login still works. Read-only: it lists queues and changes nothing.</summary>
public interface ILoginTester
{
    Task<LoginTestResult> TestAsync(LoginEntry login, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tests Service Bus and emulator logins by opening a read-only inventory and listing queues. Other systems
/// say plainly that testing arrives with their provider. Messages are passed through the redactor.
/// </summary>
public sealed class LoginTester(ICredentialVault vault, Func<IBrokerProvider?> serviceBus, Func<IAccountCredentialSource?> accounts) : ILoginTester
{
    public LoginTester(ICredentialVault vault, IBrokerProvider serviceBus, Func<IAccountCredentialSource?> accounts)
        : this(vault, () => serviceBus, accounts)
    {
    }

    public async Task<LoginTestResult> TestAsync(LoginEntry login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        var account = login.AccountId is { } id ? accounts()?.SignedInAccounts.FirstOrDefault(a => a.Id == id) : null;
        if (login.AccountId is not null && account is null)
            return new(LoginTestOutcome.Failed, $"Not signed in as {AccountName(login)}. Sign in on the Accounts page, then test again.");

        switch (login.Kind)
        {
            case LoginKind.EntraAccount:
                var signedIn = accounts()?.SignedInAccounts.Any(a => a.Id == login.Field("homeAccountId")) == true;
                return signedIn
                    ? new(LoginTestOutcome.Passed, "Signed in.")
                    : new(LoginTestOutcome.Failed, "Signed out. Sign in again on the Accounts page.");
            case LoginKind.ServiceBusEntra:
                return await ListQueuesAsync(BrokerConnection.WithEntra(ServiceBusId, login.Field("namespace"), account!.Credential), null, cancellationToken).ConfigureAwait(false);
            case LoginKind.ServiceBusConnectionString or LoginKind.EmulatorConnectionString:
                RuntimeSecret? secret;
                try
                {
                    secret = await vault.ReadSecretAsync(login.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (SecretStoreException exception)
                {
                    return new(LoginTestOutcome.Failed, exception.Message);
                }

                if (secret is null)
                    return new(LoginTestOutcome.Failed, $"The secret is missing from your {vault.SecureStoreName}. Edit the login and enter it again.");
                return await ListQueuesAsync(BrokerConnection.WithConnectionString(ServiceBusId, secret, account?.Credential), secret, cancellationToken).ConfigureAwait(false);
            default:
                return new(LoginTestOutcome.NotAvailable, $"Not tested: the {LoginKinds.SystemLabel(login.System)} provider isn't built yet. The login is saved and ready for it.");
        }
    }

    private string ServiceBusId => serviceBus()?.Descriptor.Id ?? string.Empty;

    private async Task<LoginTestResult> ListQueuesAsync(BrokerConnection connection, RuntimeSecret? secret, CancellationToken cancellationToken)
    {
        if (serviceBus() is not { } provider)
            return new(LoginTestOutcome.NotAvailable, "Not tested: Service Bus isn't available in this window.");
        try
        {
            if (provider.Validate(connection) is { } problem)
                return new(LoginTestOutcome.Failed, SecretRedactor.Redact(problem, secret));
            await using var inventory = provider.OpenInventory(connection);
            var result = await inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(false);
            var queues = result.Entities.Count == 1 ? "1 queue" : $"{result.Entities.Count} queues";
            var message = result.Notice is { } notice ? $"Connected. {notice}" : $"Connected. {queues} can be read.";
            return new(LoginTestOutcome.Passed, SecretRedactor.Redact(message, secret));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Broker and SDK messages can echo what they were given; never show the secret.
            return new(LoginTestOutcome.Failed, SecretRedactor.Redact($"The test failed: {exception.Message}", secret));
        }
    }

    private static string AccountName(LoginEntry login) =>
        login.Field("accountName") is { Length: > 0 } name ? name : "its Entra account";
}
