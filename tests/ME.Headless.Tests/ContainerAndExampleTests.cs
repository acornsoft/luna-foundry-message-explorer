// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using YamlDotNet.RepresentationModel;

namespace Acornsoft.MessageExplorer.Headless.Tests;

/// <summary>The container recipe and the example GitHub Actions workflow stay read-only and secret-free.</summary>
public sealed class ContainerAndExampleTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void The_container_runs_lfme_as_a_non_root_user_on_a_chiseled_runtime()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "eng", "container", "Dockerfile"));
        var final = lines.SkipWhile(l => !l.StartsWith("FROM mcr.microsoft.com/dotnet/runtime:", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(final);
        Assert.Contains("chiseled", final[0], StringComparison.Ordinal);
        Assert.Contains(final, l => l.StartsWith("USER ", StringComparison.Ordinal));
        Assert.Contains(final, l => l.Contains("ENTRYPOINT [\"dotnet\", \"/app/lfme.dll\"]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("ENV ", StringComparison.Ordinal) || l.StartsWith("ARG ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_example_workflow_can_only_read_the_repository_and_request_an_oidc_token()
    {
        var workflow = Load("docs/examples/github-actions/servicebus-health.yml");
        var permissions = (YamlMappingNode)workflow.Children[new YamlScalarNode("permissions")];

        var granted = permissions.Children.ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => ((YamlScalarNode)p.Value).Value!);
        Assert.Equal(new Dictionary<string, string> { ["contents"] = "read", ["id-token"] = "write" }, granted);
    }

    [Fact]
    public void The_example_workflow_only_runs_lfme_check_read_only_with_no_secret_on_the_command_line()
    {
        var text = File.ReadAllText(Path.Combine(Root, "docs/examples/github-actions/servicebus-health.yml"));
        var active = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        Assert.Contains("lfme:local check", active, StringComparison.Ordinal);
        Assert.Contains("--read-only", active, StringComparison.Ordinal);
        Assert.Contains("--resource-id", active, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", active, StringComparison.Ordinal);
        Assert.DoesNotContain("--connection-string ", active, StringComparison.Ordinal);
        foreach (var verb in new[] { "move", "purge", "resubmit", "send", "delete" })
            Assert.DoesNotContain("lfme:local " + verb, active, StringComparison.Ordinal);
    }

    private static YamlMappingNode Load(string relative)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Root, relative));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
