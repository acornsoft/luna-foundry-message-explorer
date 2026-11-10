// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>Where the app keeps its settings and account records. <c>LFME_CONFIG_DIR</c> overrides it.</summary>
public static class AppPaths
{
    public const string FolderName = "LunaFoundryMessageExplorer";

    /// <summary>~/Library/Application Support/LunaFoundryMessageExplorer on macOS, ~/.local/share on Linux, %LOCALAPPDATA% on Windows.</summary>
    public static string ConfigDirectory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment("LFME_CONFIG_DIR") is { Length: > 0 } overridden)
            return overridden;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), FolderName);
    }
}

/// <summary>
/// Entra sign-in settings. The client ID defaults to the public Azure CLI application, which most tenants
/// already allow, so no app registration is needed. <c>LFME_ENTRA_CLIENT_ID</c> or <c>entraClientId</c> in
/// settings.json picks another public client application.
/// </summary>
public sealed record EntraOptions(string ClientId, string CacheName)
{
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public const string DefaultCacheName = "lfme";
    public const string AnyWorkAccountTenant = "organizations";

    public static EntraOptions Resolve(Func<string, string?> environment, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var fromEnvironment = environment("LFME_ENTRA_CLIENT_ID");
        var clientId = !string.IsNullOrWhiteSpace(fromEnvironment) ? fromEnvironment.Trim() : ReadSetting(configDirectory) ?? AzureCliClientId;
        return new EntraOptions(clientId, DefaultCacheName);
    }

    private static string? ReadSetting(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "settings.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("entraClientId", out var value) && value.GetString() is { Length: > 0 } id ? id.Trim() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
