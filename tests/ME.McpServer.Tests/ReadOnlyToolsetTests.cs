// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.ReadTools;

namespace Acornsoft.MessageExplorer.McpServer.Tests;

/// <summary>The read-only tools shared by the MCP server and the voice panel. Synthetic data only.</summary>
public sealed class ReadOnlyToolsetTests
{
    private static ReadOnlyToolset Synthetic() => ReadOnlyToolset.Over(new OpenedSource(new SyntheticNamespace(), "synthetic"));

    [Fact]
    public void The_toolset_is_exactly_four_reading_tools()
    {
        Assert.Equal(["list_entities", "get_entity", "check_limits", "summarize_dead_letters"], ReadOnlyToolset.Tools.Select(t => t.Name));
        foreach (var tool in ReadOnlyToolset.Tools)
        {
            Assert.Contains("Read-only", tool.Description, StringComparison.Ordinal);
            Assert.Equal("object", tool.InputSchema["type"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("send_message")]
    [InlineData("move_dead_letter")]
    [InlineData("purge")]
    [InlineData("request_resubmit")]
    [InlineData("peek_body")]
    public async Task A_tool_that_would_change_or_read_bodies_does_not_exist(string name)
    {
        var result = await Synthetic().CallAsync(name, null);

        Assert.True(result.IsError);
        Assert.Contains("only read", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_entities_gives_every_entity_with_counts()
    {
        var result = await Synthetic().CallAsync("list_entities", null);

        Assert.False(result.IsError);
        var paths = result.Structured["entities"]!.AsArray().Select(e => e!["path"]!.GetValue<string>());
        Assert.Equal(["orders", "payments", "orders-events", "orders-events/subscriptions/billing", "orders-events/subscriptions/shipping"], paths);
        Assert.True(result.Structured["readOnly"]!.GetValue<bool>());
        Assert.Contains("payments: active 4, dead-letter 3", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_entities_filters_by_topic_and_by_kind()
    {
        var topic = await Synthetic().CallAsync("list_entities", new JsonObject { ["entity"] = "orders-events", ["kind"] = "subscription" });

        Assert.Equal(2, topic.Structured["entities"]!.AsArray().Count);
        var bad = await Synthetic().CallAsync("list_entities", new JsonObject { ["kind"] = "namespace" });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Get_entity_finds_a_subscription_by_path_and_says_when_one_is_missing()
    {
        var found = await Synthetic().CallAsync("get_entity", new JsonObject { ["path"] = "orders-events/subscriptions/billing" });
        Assert.False(found.IsError);
        Assert.Equal(1, found.Structured["entity"]!["deadLetter"]!.GetValue<long>());

        var missing = await Synthetic().CallAsync("get_entity", new JsonObject { ["path"] = "invoices" });
        Assert.True(missing.IsError);
        var noPath = await Synthetic().CallAsync("get_entity", null);
        Assert.True(noPath.IsError);
    }

    [Fact]
    public async Task Check_limits_lists_breaches_and_needs_a_limit()
    {
        var result = await Synthetic().CallAsync("check_limits", new JsonObject { ["maxDeadLetter"] = 0 });

        Assert.False(result.IsError);
        Assert.False(result.Structured["passed"]!.GetValue<bool>());
        Assert.Equal(["payments", "orders-events/subscriptions/billing"], result.Structured["breaches"]!.AsArray().Select(b => b!["path"]!.GetValue<string>()));
        Assert.StartsWith("2 checks failed", result.Text, StringComparison.Ordinal);

        var scoped = await Synthetic().CallAsync("check_limits", new JsonObject { ["maxDeadLetter"] = 0, ["entities"] = new JsonArray("orders") });
        Assert.True(scoped.Structured["passed"]!.GetValue<bool>());

        Assert.True((await Synthetic().CallAsync("check_limits", null)).IsError);
        Assert.True((await Synthetic().CallAsync("check_limits", new JsonObject { ["maxActive"] = -1 })).IsError);
        Assert.True((await Synthetic().CallAsync("check_limits", new JsonObject { ["maxActive"] = "ten" })).IsError);
    }

    [Fact]
    public async Task Summarize_dead_letters_puts_the_most_first()
    {
        var result = await Synthetic().CallAsync("summarize_dead_letters", null);

        Assert.Equal(4, result.Structured["total"]!.GetValue<long>());
        Assert.Equal("payments", result.Structured["entities"]![0]!["path"]!.GetValue<string>());
        Assert.StartsWith("4 dead-lettered messages in 2 entities", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_error_is_a_tool_error_not_a_crash()
    {
        var tools = new ReadOnlyToolset(_ => throw new BrokerException(BrokerErrorKind.Unauthenticated, "sign-in needed\nmore detail"), "sbns-demo-001.servicebus.windows.net");

        var result = await tools.CallAsync("list_entities", null);

        Assert.True(result.IsError);
        Assert.Equal("Unauthenticated: sign-in needed", result.Text);
    }

    [Fact]
    public void The_shared_read_library_reaches_no_send_receive_settle_or_management_write()
    {
        var violations = MutationScanner.Scan(typeof(ReadOnlyToolset).Assembly.Location, _ => true);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }
}
