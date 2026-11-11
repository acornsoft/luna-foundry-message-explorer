// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.ReadTools;

namespace Acornsoft.MessageExplorer.McpServer.Tests;

/// <summary>The read-only MCP server over stdio: MCP 2026-07-28 per-request metadata, server/discover, and the older initialize handshake.</summary>
public sealed class McpStdioServerTests
{
    private const string Modern = "2026-07-28";

    private readonly McpStdioServer _server = new(ReadOnlyToolset.Over(new OpenedSource(new SyntheticNamespace(), "synthetic")));

    private static JsonObject Meta(string version = Modern) => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = version,
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
    };

    private async Task<JsonObject> Send(string method, JsonObject? parameters = null, int id = 1)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            request["params"] = parameters;
        var line = await _server.HandleLineAsync(request.ToJsonString());
        Assert.NotNull(line);
        Assert.DoesNotContain('\n', line);
        return (JsonObject)JsonNode.Parse(line)!;
    }

    private Task<JsonObject> SendModern(string method, JsonObject? parameters = null)
    {
        parameters ??= [];
        parameters["_meta"] = Meta();
        return Send(method, parameters);
    }

    [Fact]
    public async Task Server_discover_lists_the_stateless_version_tools_and_instructions()
    {
        var response = await SendModern("server/discover");

        var result = response["result"]!;
        Assert.Equal([Modern], result["supportedVersions"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Contains("never read message bodies", result["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("public", result["cacheScope"]!.GetValue<string>());
        Assert.True(result["ttlMs"]!.GetValue<int>() >= 0);
        Assert.Equal("lfme-mcp", result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_discover_answers_even_without_metadata_so_a_dual_era_client_can_probe()
    {
        var response = await Send("server/discover");

        Assert.NotNull(response["result"]);
    }

    [Fact]
    public async Task An_unsupported_version_is_refused_with_the_supported_list()
    {
        var response = await Send("tools/list", new JsonObject { ["_meta"] = Meta("2099-01-01") });

        var error = response["error"]!;
        Assert.Equal(McpStdioServer.UnsupportedProtocolVersion, error["code"]!.GetValue<int>());
        Assert.Equal(-32022, McpStdioServer.UnsupportedProtocolVersion);
        Assert.Equal([Modern], error["data"]!["supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("2099-01-01", error["data"]!["requested"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_modern_request_must_carry_client_capabilities()
    {
        var response = await Send("tools/list", new JsonObject
        {
            ["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = Modern },
        });

        Assert.Equal(McpStdioServer.InvalidParams, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_with_no_version_and_no_handshake_names_the_supported_versions()
    {
        var response = await Send("tools/list");

        Assert.Equal(McpStdioServer.InvalidRequest, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("2025-11-25", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_listed_tool_is_annotated_read_only_and_never_destructive()
    {
        var response = await SendModern("tools/list");

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(4, tools.Count);
        foreach (var tool in tools)
        {
            var annotations = tool!["annotations"]!;
            Assert.True(annotations["readOnlyHint"]!.GetValue<bool>());
            Assert.False(annotations["destructiveHint"]!.GetValue<bool>());
            Assert.False(annotations["openWorldHint"]!.GetValue<bool>());
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
        }

        Assert.Equal("public", response["result"]!["cacheScope"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_call_returns_text_and_structured_content()
    {
        var response = await SendModern("tools/call", new JsonObject
        {
            ["name"] = "check_limits",
            ["arguments"] = new JsonObject { ["maxDeadLetter"] = 0 },
        });

        var result = response["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("text", result["content"]![0]!["type"]!.GetValue<string>());
        Assert.StartsWith("2 checks failed", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, result["structuredContent"]!["breaches"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_bad_argument_is_a_tool_error_the_model_can_read()
    {
        var response = await SendModern("tools/call", new JsonObject { ["name"] = "get_entity", ["arguments"] = new JsonObject { ["path"] = "invoices" } });

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("send_message")]
    [InlineData("request_move_dead_letter")]
    [InlineData("purge_queue")]
    public async Task A_tool_that_would_change_anything_is_refused(string name)
    {
        var response = await SendModern("tools/call", new JsonObject { ["name"] = name });

        Assert.Equal(McpStdioServer.InvalidParams, response["error"]!["code"]!.GetValue<int>());
        Assert.Null(response["result"]);
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("prompts/list")]
    [InlineData("sampling/createMessage")]
    public async Task Other_methods_are_not_found(string method)
    {
        var response = await SendModern(method);

        Assert.Equal(McpStdioServer.MethodNotFound, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_legacy_client_can_initialize_then_list_and_call_without_metadata()
    {
        var init = await Send("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1.0.0" },
        });
        Assert.Equal("2025-11-25", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("lfme-mcp", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Null(await _server.HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var list = await Send("tools/list", id: 2);
        Assert.Equal(4, list["result"]!["tools"]!.AsArray().Count);
        Assert.Null(list["result"]!["cacheScope"]);

        var call = await Send("tools/call", new JsonObject { ["name"] = "summarize_dead_letters" }, id: 3);
        Assert.False(call["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_legacy_client_asking_for_an_unknown_version_is_offered_the_newest_legacy_one()
    {
        var init = await Send("initialize", new JsonObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject() });

        Assert.Equal("2025-11-25", init["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_input_is_a_parse_error_and_notifications_get_no_reply()
    {
        var parse = JsonNode.Parse((await _server.HandleLineAsync("{not json"))!)!;
        Assert.Equal(McpStdioServer.ParseError, parse["error"]!["code"]!.GetValue<int>());
        Assert.Null(await _server.HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}"""));
    }

    [Fact]
    public async Task Run_writes_one_message_per_line_and_nothing_else_to_stdout()
    {
        var discover = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "server/discover", ["params"] = new JsonObject { ["_meta"] = Meta() } };
        var call = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "list_entities", ["_meta"] = Meta() } };
        var input = new StringReader(discover.ToJsonString() + "\n\n" + call.ToJsonString() + "\n");
        var output = new StringWriter();

        await _server.RunAsync(input, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("2.0", JsonNode.Parse(l)!["jsonrpc"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_program_serves_synthetic_data_and_logs_only_to_stderr()
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["params"] = new JsonObject { ["_meta"] = Meta() } };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await Program.RunAsync(["--synthetic"], new StringReader(request.ToJsonString() + "\n"), stdout, stderr, _ => null);

        Assert.Equal(0, code);
        Assert.Contains("read-only, serving synthetic", stderr.ToString(), StringComparison.Ordinal);
        Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task The_program_refuses_a_secret_on_the_command_line_without_echoing_it()
    {
        var secret = "Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKey=" + Convert.ToBase64String("not-a-real-key"u8.ToArray());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await Program.RunAsync(["--connection-string", secret], new StringReader(string.Empty), stdout, stderr, _ => null);

        Assert.Equal(Program.Usage, code);
        Assert.DoesNotContain(secret, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task The_program_reads_a_connection_string_only_from_the_environment()
    {
        var secret = "Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKeyName=listen;" + "Shared" + "AccessKey=" + Convert.ToBase64String("not-a-real-key"u8.ToArray());
        BrokerConnection? opened = null;
        var stderr = new StringWriter();

        var code = await Program.RunAsync(["--connection-string-env", "SB_CONNECTION"], new StringReader(string.Empty), new StringWriter(), stderr,
            name => name == "SB_CONNECTION" ? secret : null,
            open: connection =>
            {
                opened = connection;
                return new SyntheticNamespace();
            });

        Assert.Equal(0, code);
        Assert.NotNull(opened);
        Assert.Contains("sbns-demo-001.servicebus.windows.net", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AccessKey", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_compiled_server_reaches_no_send_receive_settle_or_management_write()
    {
        var violations = MutationScanner.Scan(typeof(McpStdioServer).Assembly.Location, _ => true);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_server_references_neither_the_operations_nor_the_admin_assemblies()
    {
        var references = typeof(McpStdioServer).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        references.AddRange(typeof(ReadOnlyToolset).Assembly.GetReferencedAssemblies().Select(a => a.Name));

        Assert.DoesNotContain("Acornsoft.MessageExplorer.Operations", references);
        Assert.DoesNotContain("Acornsoft.MessageExplorer.Admin", references);
        Assert.DoesNotContain("Acornsoft.MessageExplorer.Mcp", references);
    }
}
