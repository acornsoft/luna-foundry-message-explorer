// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Operations.ServiceBus;

namespace Acornsoft.MessageExplorer.Operations.Tests;

/// <summary>
/// Acornsoft.MessageExplorer.Operations is the only assembly that may send, receive, lock or settle. The same
/// Mono.Cecil scanner as the adapter guard reads every other assembly built from src/ and must find nothing.
/// </summary>
public partial class OnlyOperationsMutatesTests
{
    private const string Operations = "Acornsoft.MessageExplorer.Operations";

    [Fact]
    public void No_assembly_but_operations_reaches_a_mutating_service_bus_api()
    {
        var assemblies = BuiltAssemblies().Where(a => a.Name != Operations).ToList();
        Assert.True(assemblies.Count >= 12, "Expected every src assembly to be built; found " + string.Join(", ", assemblies.Select(a => a.Name)));
        Assert.Contains(assemblies, a => a.Name == "Acornsoft.MessageExplorer.Shell");
        Assert.Contains(assemblies, a => a.Name == "Acornsoft.MessageExplorer.Desktop");
        Assert.Contains(assemblies, a => a.Name == "Acornsoft.MessageExplorer.Adapters.ServiceBus");

        var violations = assemblies.SelectMany(a => MutationScanner.Scan(a.Path, _ => true).Select(v => $"{a.Name}: {v}")).ToList();
        Assert.True(violations.Count == 0, "Mutating API reachable outside Operations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_scanner_does_see_the_mutations_in_operations()
    {
        var path = typeof(ServiceBusMoveTransport).Assembly.Location;
        var violations = MutationScanner.Scan(path, _ => true);
        Assert.Contains(violations, v => v.Contains("SendMessageAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("CompleteMessageAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("ReceiveMessagesAsync", StringComparison.Ordinal));
    }

    /// <summary>Each src project's own assembly, from the same configuration as this test.</summary>
    private static IEnumerable<(string Name, string Path)> BuiltAssemblies()
    {
        var root = RepoRoot();
        var configuration = typeof(OnlyOperationsMutatesTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var match = AssemblyName().Match(File.ReadAllText(project));
            if (!match.Success)
                continue;
            var name = match.Groups[1].Value;
            var bin = Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration);
            if (!Directory.Exists(bin))
                continue;
            var dll = Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (dll is not null)
                yield return (name, dll);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MessageExplorer.sln not found.");
    }

    [GeneratedRegex("<AssemblyName>([^<]+)</AssemblyName>")]
    private static partial Regex AssemblyName();
}
