// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

/// <summary>The stdio JSON-RPC host against small python3 plugins written inline, and the real sample plugins.</summary>
public sealed class StdioPluginHostTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly PluginHostOptions Fast = new()
    {
        StartTimeout = TimeSpan.FromSeconds(15),
        CallTimeout = TimeSpan.FromSeconds(2),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    };

    // A tiny plugin: answers initialize with the given handshake, then runs the given handler per request.
    private static string[] Script(string handshake, string onRequest = "reply(rid, {'messages': []})") =>
    [
        "python3", "-c",
        "import json, sys\n" +
        "def send(m):\n    sys.stdout.write(json.dumps(m) + '\\n'); sys.stdout.flush()\n" +
        "def reply(rid, r):\n    send({'jsonrpc': '2.0', 'id': rid, 'result': r})\n" +
        "for line in sys.stdin:\n" +
        "    req = json.loads(line); rid = req.get('id'); m = req.get('method')\n" +
        "    if m == 'initialize':\n        reply(rid, " + handshake + ")\n        continue\n" +
        "    if m == 'shutdown':\n        reply(rid, {}); break\n" +
        "    " + onRequest.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n",
    ];

    private const string GoodHandshake = "{'protocol': 'lfme-plugin/1.0', 'name': 'probe', 'version': '1.0.0', 'roles': ['generator']}";

    private static Task<StdioJsonRpcPluginHost> Launch(string[] command, string version = "1.0.0", params PluginRole[] roles) =>
        StdioJsonRpcPluginHost.StartAsync(
            "probe", version, command, Directory.GetCurrentDirectory(),
            (roles.Length == 0 ? [PluginRole.Generator] : roles).ToHashSet(),
            new PluginInitialize("test-run", 1, Start, new JsonObject(), new Dictionary<string, string>()),
            Fast);

    [PythonFact]
    public async Task Handshake_succeeds_and_calls_round_trip()
    {
        var onTick = "reply(rid, {'messages': [{'correlationId': 'c-1', 'body': {'n': 1}, 'delayMs': 1500}]})";
        await using var host = await Launch(Script(GoodHandshake, onTick));

        Assert.Equal("probe", host.Info.Name);
        Assert.Equal(PluginProtocol.Version, host.Info.Protocol);
        var messages = await host.TickAsync(Start, CancellationToken.None);
        var message = Assert.Single(messages);
        Assert.Equal("c-1", message.CorrelationId);
        Assert.Equal(1500, message.DelayMs);
        Assert.Null(message.To);
    }

    [PythonFact]
    public async Task A_different_protocol_major_is_refused()
    {
        var error = await Assert.ThrowsAsync<PluginHandshakeException>(() =>
            Launch(Script("{'protocol': 'lfme-plugin/2.0', 'name': 'probe', 'version': '1.0.0', 'roles': ['generator']}")));
        Assert.Contains("lfme-plugin/2.0", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task A_version_other_than_the_pin_is_refused()
    {
        var error = await Assert.ThrowsAsync<PluginHandshakeException>(() => Launch(Script(GoodHandshake), version: "1.0.1"));
        Assert.Contains("1.0.1", error.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task A_missing_role_is_refused()
    {
        var error = await Assert.ThrowsAsync<PluginHandshakeException>(() => Launch(Script(GoodHandshake), "1.0.0", PluginRole.Simulator));
        Assert.Contains("simulator", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task A_plugin_that_does_not_answer_times_out_and_is_stopped()
    {
        var host = await Launch(Script(GoodHandshake, "import time\ntime.sleep(60)"));
        await using (host)
        {
            await Assert.ThrowsAsync<PluginTimeoutException>(() => host.TickAsync(Start, CancellationToken.None));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!host.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(host.HasExited);
        }
    }

    [PythonFact]
    public async Task A_crash_reports_the_exit_code_and_stderr()
    {
        await using var host = await Launch(Script(GoodHandshake, "sys.stderr.write('boom: out of widgets\\n'); sys.stderr.flush()\nsys.exit(3)"));

        var error = await Assert.ThrowsAsync<PluginCrashedException>(() => host.TickAsync(Start, CancellationToken.None));
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
        Assert.Contains("out of widgets", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task Text_on_stdout_that_is_not_json_is_a_protocol_fault()
    {
        await using var host = await Launch(Script(GoodHandshake, "print('hello there', flush=True)"));

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => host.TickAsync(Start, CancellationToken.None));
        Assert.True(error is PluginProtocolException or PluginCrashedException, error.GetType().Name);
    }

    [PythonFact]
    public async Task An_error_answer_names_the_method()
    {
        await using var host = await Launch(Script(GoodHandshake, "send({'jsonrpc': '2.0', 'id': rid, 'error': {'code': -32000, 'message': 'no stock'}})"));

        var error = await Assert.ThrowsAsync<PluginCallException>(() => host.TickAsync(Start, CancellationToken.None));
        Assert.Contains("tick", error.Message, StringComparison.Ordinal);
        Assert.Contains("no stock", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task Event_notifications_are_collected()
    {
        var onTick = "send({'jsonrpc': '2.0', 'method': 'event', 'params': {'kind': 'throttled'}})\nreply(rid, {'messages': []})";
        await using var host = await Launch(Script(GoodHandshake, onTick));

        await host.TickAsync(Start, CancellationToken.None);
        var notice = Assert.Single(host.Events);
        Assert.Equal("throttled", notice["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_command_that_does_not_exist_fails_to_start()
    {
        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Launch(["lfme-no-such-plugin-binary"]));
        Assert.Contains("lfme-no-such-plugin-binary", error.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task The_python_sample_plugin_runs_through_the_host_and_is_seeded()
    {
        async Task<string[]> Batch(long seed)
        {
            await using var host = await StdioJsonRpcPluginHost.StartAsync(
                "hello-generator", "0.1.0", ["python3", Repo.PythonPlugin], Repo.Root, new HashSet<PluginRole> { PluginRole.Generator },
                new PluginInitialize("py-run", seed, Start, new JsonObject { ["count"] = 3 }, new Dictionary<string, string>()), Fast);
            Assert.Equal("hello-generator", host.Info.Name);
            var batch = await host.NextBatchAsync(10, CancellationToken.None);
            return batch.Select(m => m.CorrelationId + " " + m.Body.ToJsonString()).ToArray();
        }

        var first = await Batch(7);
        Assert.Equal(3, first.Length);
        Assert.Equal(first, await Batch(7));
    }

    [Fact]
    public async Task The_erp_standin_confirms_ships_later_and_flags_repeats()
    {
        Assert.True(File.Exists(Repo.ErpDll), "Build the sample plugins first: " + Repo.ErpDll);
        await using var host = await StdioJsonRpcPluginHost.StartAsync(
            "erp-standin", "0.2.0", ["dotnet", Repo.ErpDll], Repo.Root, new HashSet<PluginRole> { PluginRole.Simulator },
            new PluginInitialize("erp-run", 1, Start, new JsonObject { ["failureRate"] = 0.0, ["shipDelay"] = "PT3H" }, new Dictionary<string, string>()), Fast);

        Assert.Contains("orders.created", host.Info.Subscriptions);
        var order = new BusMessage("orders.created", 1, "ord-1", Start, new JsonObject { ["orderRef"] = "ord-1", ["lines"] = 2 });

        var replies = await host.DeliverAsync(order, Start, CancellationToken.None);
        Assert.Equal(["orders.confirmed", "orders.shipped"], replies.Select(r => r.To));
        Assert.Equal((long)TimeSpan.FromHours(3).TotalMilliseconds, replies[1].DelayMs);

        var again = Assert.Single(await host.DeliverAsync(order, Start, CancellationToken.None));
        Assert.Equal("orders.rejected", again.To);
        Assert.Equal("DUPLICATE", again.Body["code"]!.GetValue<string>());
        Assert.Contains(host.Events, e => e["kind"]!.GetValue<string>() == "state");
    }

    [Fact]
    public async Task The_erp_standin_retries_a_poison_order_then_dead_letters_it()
    {
        await using var host = await StdioJsonRpcPluginHost.StartAsync(
            "erp-standin", "0.2.0", ["dotnet", Repo.ErpDll], Repo.Root, new HashSet<PluginRole> { PluginRole.Simulator },
            new PluginInitialize("erp-run", 1, Start, new JsonObject { ["faults"] = new JsonObject { ["maxDeliveries"] = 3, ["backoff"] = "PT10S" } }, new Dictionary<string, string>()), Fast);

        var body = new JsonObject { ["orderRef"] = "ord-bad", ["lines"] = 0 };
        var delays = new List<long>();
        OutboundMessage reply;
        var now = Start;
        while (true)
        {
            reply = Assert.Single(await host.DeliverAsync(new BusMessage("orders.created.retry", 1, "ord-bad", now, body), now, CancellationToken.None));
            if (reply.To != "orders.created.retry")
                break;
            delays.Add(reply.DelayMs);
            body = reply.Body;
            now = now.AddMilliseconds(reply.DelayMs);
        }

        Assert.Equal("orders.created/$deadletterqueue", reply.To);
        Assert.Equal(3, reply.Body["deliveryCount"]!.GetValue<int>());
        Assert.Equal("MaxDeliveryCountExceeded", reply.Body["deadLetterReason"]!.GetValue<string>());
        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] > delays[0], "backoff should grow");
    }
}
