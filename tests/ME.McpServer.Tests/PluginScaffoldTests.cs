// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;

namespace Acornsoft.MessageExplorer.McpServer.Tests;

/// <summary>The Cursor plugin scaffold: valid manifests, a read-only server, synthetic by default, and no secrets.</summary>
public sealed class PluginScaffoldTests
{
    private static readonly string Plugin = Path.Combine(RepoRoot(), "plugins", "cursor", "lfme-readonly");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void The_manifest_names_the_plugin_its_license_and_relative_component_paths()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Plugin, ".cursor-plugin", "plugin.json")))!;

        Assert.Equal("lfme-readonly", manifest["name"]!.GetValue<string>());
        Assert.Equal("MPL-2.0", manifest["license"]!.GetValue<string>());
        foreach (var key in new[] { "mcpServers", "skills" })
        {
            var path = manifest[key]!.GetValue<string>();
            Assert.StartsWith("./", path, StringComparison.Ordinal);
            Assert.DoesNotContain("..", path, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(Plugin, path)) || Directory.Exists(Path.Combine(Plugin, path)), path);
        }
    }

    [Fact]
    public void The_mcp_config_runs_lfme_mcp_on_synthetic_data_with_no_secrets()
    {
        var text = File.ReadAllText(Path.Combine(Plugin, "mcp.json"));
        var server = JsonNode.Parse(text)!["mcpServers"]!["lfme"]!;

        Assert.Equal("lfme-mcp", server["command"]!.GetValue<string>());
        Assert.Equal(["--synthetic"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(server["env"]);
        Assert.DoesNotContain("AccessKey", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_skill_has_front_matter_and_names_only_tools_the_server_has()
    {
        var skill = File.ReadAllText(Path.Combine(Plugin, "skills", "service-bus-health", "SKILL.md"));

        Assert.StartsWith("---\nname: service-bus-health\ndescription: ", skill.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        foreach (var tool in ReadTools.ReadOnlyToolset.Tools)
            Assert.Contains($"`{tool.Name}`", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scaffold_is_marked_unpublished_and_has_no_marketplace_entry()
    {
        Assert.Contains("not published", File.ReadAllText(Path.Combine(Plugin, "README.md")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Plugin, ".cursor-plugin", "marketplace.json")));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), ".cursor-plugin", "marketplace.json")));
        Assert.Contains("Mozilla Public License Version 2.0", File.ReadAllText(Path.Combine(Plugin, "LICENSE")), StringComparison.Ordinal);
    }
}
