// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

/// <summary>
/// A packaged app is self-contained, so the machine may have no <c>dotnet</c> on the PATH.
/// Such an app runs a <c>dotnet plugin.dll</c> command itself: <c>&lt;app&gt; --run-plugin plugin.dll</c>.
/// </summary>
public static class DotnetPluginCommand
{
    public const string RunPluginSwitch = "--run-plugin";

    public static IReadOnlyList<string> RunWithSelf(IReadOnlyList<string> command, string selfPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrEmpty(selfPath);
        if (command.Count < 2
            || !string.Equals(command[0], "dotnet", StringComparison.Ordinal)
            || !command[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return command;
        return [selfPath, RunPluginSwitch, .. command.Skip(1)];
    }

    /// <summary>The app's own executable, or null when it runs under the <c>dotnet</c> muxer (then <c>dotnet</c> exists).</summary>
    public static string? SelfHostPath(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
            return null;
        var name = processPath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : processPath;
    }
}
