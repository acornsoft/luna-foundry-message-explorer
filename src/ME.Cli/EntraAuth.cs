// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using Azure.Core;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Cli;

/// <summary>
/// Microsoft Entra is the only supported sign-in for lfme. Connection strings, SAS keys, shared access
/// policies, anonymous access, and emulator fallbacks are refused. The managed-identity probe stays off
/// unless LFME_USE_MANAGED_IDENTITY=1, because it can stall for minutes off Azure.
/// </summary>
internal static class EntraAuth
{
    public const string SignInMessage = "Sign in with Microsoft Entra first (az login). Keys, SAS, and connection strings are not supported.";
    public const string SkipAuthMessage = "lfme does not allow skipping Microsoft Entra sign-in.";
    public const string CredentialMessage = "Sign in with Microsoft Entra (az login). Keys, SAS, and connection strings are refused.";
    public const string ManagementScope = "https://management.azure.com/.default";

    private static readonly string AccessKeyToken = "Shared" + "AccessKey";
    private static readonly string AccessSignatureToken = "Shared" + "AccessSignature";

    public static DefaultAzureCredential CreateCredential()
    {
        var useManagedIdentity = Environment.GetEnvironmentVariable("LFME_USE_MANAGED_IDENTITY") == "1";
        return new DefaultAzureCredential(CredentialOptions(useManagedIdentity));
    }

    public static DefaultAzureCredentialOptions CredentialOptions(bool useManagedIdentity) =>
        new()
        {
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = !useManagedIdentity,
        };

    public static async Task EnsureSignedInAsync(TokenCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.Token))
                throw new EntraSignInException(SignInMessage);
        }
        catch (EntraSignInException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CredentialUnavailableException or AuthenticationFailedException)
        {
            throw new EntraSignInException(SignInMessage, ex);
        }
    }

    public static void RefuseNonEntraInput(CliArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var name in args.Options.Keys.Concat(args.Flags))
        {
            if (IsForbiddenName(name))
                throw new CliUsageException("'" + name + "' is not supported. " + CredentialMessage);
        }

        foreach (var value in args.Options.Values)
        {
            if (LooksLikeConnectionString(value))
                throw new CliUsageException("A connection string or SAS value was passed. " + CredentialMessage);
        }

        RefuseForbiddenEnvironment();
    }

    public static void RefuseForbiddenEnvironment()
    {
        foreach (var entry in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            if (entry.Key is not string name)
                continue;
            var value = entry.Value as string;
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (IsForbiddenEnvironmentName(name))
                throw new CliUsageException("Environment variable " + name + " is not supported. " + (IsSkipAuthName(name) ? SkipAuthMessage : CredentialMessage));
            if (name.StartsWith("LFME_", StringComparison.OrdinalIgnoreCase) && LooksLikeConnectionString(value))
                throw new CliUsageException("Environment variable " + name + " looks like a connection string or SAS value. " + CredentialMessage);
        }
    }

    public static bool IsForbiddenName(string name)
    {
        var normalized = Normalize(name);
        return normalized is "key" or "sas" or "secret" or "password" or "token" or "anonymous"
            || ContainsForbiddenToken(normalized);
    }

    public static bool LooksLikeConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Contains(AccessKeyToken, StringComparison.OrdinalIgnoreCase)
            || value.Contains(AccessSignatureToken, StringComparison.OrdinalIgnoreCase)
            || value.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase))
            return true;
        return value.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase)
            && (value.Contains("sb://", StringComparison.OrdinalIgnoreCase) || value.Contains(';', StringComparison.Ordinal));
    }

    internal static bool IsForbiddenEnvironmentName(string name)
    {
        if (!name.StartsWith("LFME_", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = Normalize(name[5..]);
        return rest is "key" or "sas" or "secret" or "password" or "token" or "anonymous"
            || IsSkipAuthName(name)
            || ContainsForbiddenToken(rest);
    }

    internal static bool IsSkipAuthName(string name)
    {
        var normalized = Normalize(name);
        return normalized is "lfme-skip-auth" or "lfme-no-auth" or "lfme-anonymous" or "lfme-use-sas" or "lfme-use-connection-string";
    }

    private static bool ContainsForbiddenToken(string normalized) =>
        normalized.Contains("connection-string", StringComparison.Ordinal)
        || normalized.Contains("shared-access", StringComparison.Ordinal)
        || normalized.Contains("sas-key", StringComparison.Ordinal)
        || normalized.Contains("sas-token", StringComparison.Ordinal)
        || normalized.Contains("shared-key", StringComparison.Ordinal)
        || normalized.Contains("account-key", StringComparison.Ordinal)
        || normalized.Contains("access-key", StringComparison.Ordinal)
        || normalized.Contains("skip-auth", StringComparison.Ordinal)
        || normalized.Contains("no-auth", StringComparison.Ordinal);

    private static string Normalize(string name) => name.Replace('_', '-').ToLowerInvariant();
}

internal sealed class EntraSignInException : Exception
{
    public EntraSignInException(string message)
        : base(message)
    {
    }

    public EntraSignInException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
