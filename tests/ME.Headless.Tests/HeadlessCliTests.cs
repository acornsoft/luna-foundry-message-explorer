// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Headless.Tests;

/// <summary>The headless <c>lfme</c> command: read-only inventory and health checks for any queue or topic. Synthetic data only.</summary>
public sealed class HeadlessCliTests
{
    // Built at run time so secret scanners never see a key-shaped literal.
    private static readonly string FakeKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-real-key"));
    private static readonly string FakeConnection = "Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKeyName=listen;" + "Shared" + "AccessKey=" + FakeKey;

    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _env = [];
    private const string ResourceId = "/subscriptions/10000000-0000-0000-0000-000000000001/resourceGroups/rg-orders-demo/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001";

    private readonly List<BrokerConnection> _opened = [];
    private readonly List<NamespaceRef> _openedArm = [];

    private Task<int> Run(params string[] args) => HeadlessCli.RunAsync(args, _out, _err, name => _env.GetValueOrDefault(name), connection =>
    {
        _opened.Add(connection);
        return new SyntheticNamespace();
    }, (ns, _) =>
    {
        _openedArm.Add(ns);
        return new SyntheticNamespace();
    });

    [Fact]
    public async Task No_arguments_prints_help_and_is_a_usage_error()
    {
        Assert.Equal(HeadlessCli.Usage, await Run());
        Assert.Contains("lfme inventory", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(HeadlessCli.Ok, await Run("--help"));
    }

    [Fact]
    public async Task Inventory_of_the_synthetic_namespace_lists_queues_topics_and_subscriptions_with_counts()
    {
        Assert.Equal(HeadlessCli.Ok, await Run("inventory", "--synthetic"));

        var text = _out.ToString();
        Assert.Contains("queue", text, StringComparison.Ordinal);
        Assert.Contains("orders", text, StringComparison.Ordinal);
        Assert.Contains("payments", text, StringComparison.Ordinal);
        Assert.Contains("orders-events/subscriptions/billing", text, StringComparison.Ordinal);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Json_output_is_machine_readable_says_read_only_and_holds_no_message_bodies()
    {
        Assert.Equal(HeadlessCli.Ok, await Run("inventory", "--synthetic", "--format", "json"));

        using var doc = JsonDocument.Parse(_out.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.Equal("synthetic", root.GetProperty("source").GetString());
        var entities = root.GetProperty("entities").EnumerateArray().ToList();
        Assert.Contains(entities, e => e.GetProperty("path").GetString() == "payments" && e.GetProperty("deadLetter").GetInt64() == 3);
        Assert.DoesNotContain("body", _out.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_fails_with_exit_3_when_dead_letters_pass_the_limit_and_names_the_entity()
    {
        Assert.Equal(HeadlessCli.ThresholdBreached, await Run("check", "--synthetic", "--max-dead-letter", "0"));

        Assert.Contains("payments", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("dead-letter 3 > 0", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_passes_when_every_entity_is_within_the_limits()
    {
        Assert.Equal(HeadlessCli.Ok, await Run("check", "--synthetic", "--max-dead-letter", "10", "--max-active", "1000"));
        Assert.Contains("All checks passed", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entity_filters_limit_the_check_to_the_named_queues_topics_or_subscriptions()
    {
        Assert.Equal(HeadlessCli.Ok, await Run("check", "--synthetic", "--max-dead-letter", "0", "--entity", "orders"));
        Assert.Equal(HeadlessCli.ThresholdBreached, await Run("check", "--synthetic", "--max-dead-letter", "0", "--entity", "orders-events/subscriptions/billing"));
    }

    [Fact]
    public async Task A_connection_string_on_the_command_line_is_refused_and_never_echoed()
    {
        Assert.Equal(HeadlessCli.Usage, await Run("inventory", "--connection-string", FakeConnection));

        Assert.Contains("--connection-string-env", _err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, _err.ToString() + _out.ToString(), StringComparison.Ordinal);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_connection_string_is_read_from_the_named_environment_variable_and_kept_out_of_every_output()
    {
        _env["SB_READER"] = FakeConnection;
        var report = Path.Combine(Path.GetTempPath(), "lfme-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal(HeadlessCli.Ok, await Run("inventory", "--connection-string-env", "SB_READER", "--out", report));

            var opened = Assert.Single(_opened);
            Assert.Equal(AuthMode.ConnectionString, opened.AuthMode);
            Assert.Equal(FakeConnection, opened.Secret!.Reveal());
            Assert.DoesNotContain(FakeKey, _out.ToString() + _err.ToString() + File.ReadAllText(report), StringComparison.Ordinal);
            Assert.Contains("sbns-demo-001.servicebus.windows.net", File.ReadAllText(report), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public async Task A_missing_environment_variable_is_a_usage_error()
    {
        Assert.Equal(HeadlessCli.Usage, await Run("inventory", "--connection-string-env", "NOT_SET"));
        Assert.Contains("NOT_SET", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public", "sbns-demo-001.servicebus.windows.net")]
    [InlineData("usgov", "sbns-demo-001.servicebus.usgovcloudapi.net")]
    [InlineData("china", "sbns-demo-001.servicebus.chinacloudapi.cn")]
    public async Task A_namespace_signs_in_with_entra_in_the_picked_cloud(string cloud, string host)
    {
        Assert.Equal(HeadlessCli.Ok, await Run("inventory", "--namespace", "sbns-demo-001", "--cloud", cloud));

        var opened = Assert.Single(_opened);
        Assert.Equal(AuthMode.Entra, opened.AuthMode);
        Assert.Equal(host, opened.Endpoint);
        Assert.NotNull(opened.Credential);
    }

    [Theory]
    [InlineData("public", "sbns-demo-001.servicebus.windows.net")]
    [InlineData("usgov", "sbns-demo-001.servicebus.usgovcloudapi.net")]
    public async Task A_resource_id_reads_counts_through_resource_manager_so_the_reader_role_is_enough(string cloud, string host)
    {
        _env["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-00000000c001";

        Assert.Equal(HeadlessCli.Ok, await Run("check", "--resource-id", ResourceId, "--cloud", cloud, "--max-dead-letter", "10"));

        var ns = Assert.Single(_openedArm);
        Assert.Equal("sbns-demo-001", ns.Name);
        Assert.Equal("rg-orders-demo", ns.ResourceGroup);
        Assert.Equal("10000000-0000-0000-0000-000000000001", ns.SubscriptionId);
        Assert.Equal("00000000-0000-0000-0000-00000000c001", ns.TenantId);
        Assert.Equal(cloud, ns.Cloud);
        Assert.Equal(host, ns.FullyQualifiedNamespace);
        Assert.Empty(_opened);
    }

    [Theory]
    [InlineData("/subscriptions/x/resourceGroups/rg/providers/Microsoft.EventHub/namespaces/n")]
    [InlineData("sbns-demo-001")]
    [InlineData("/subscriptions/x/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/n/queues/orders")]
    public async Task A_resource_id_that_is_not_a_service_bus_namespace_is_a_usage_error(string id)
    {
        Assert.Equal(HeadlessCli.Usage, await Run("inventory", "--resource-id", id));
        Assert.Empty(_openedArm);
    }

    [Fact]
    public async Task Exactly_one_source_is_required()
    {
        Assert.Equal(HeadlessCli.Usage, await Run("inventory"));
        Assert.Equal(HeadlessCli.Usage, await Run("inventory", "--synthetic", "--namespace", "sbns-demo-001"));
    }

    [Fact]
    public async Task A_broker_error_exits_2_with_a_plain_reason()
    {
        var code = await HeadlessCli.RunAsync(["inventory", "--namespace", "sbns-demo-001"], _out, _err, _ => null,
            _ => throw new BrokerException(BrokerErrorKind.Forbidden, "The identity has no Reader role on this namespace."));

        Assert.Equal(HeadlessCli.BrokerError, code);
        Assert.Contains("Forbidden", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("Reader role", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_markdown_summary_can_be_appended_for_a_ci_job_page()
    {
        var summary = Path.Combine(Path.GetTempPath(), "lfme-summary-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(summary, "earlier step\n");
        try
        {
            await Run("check", "--synthetic", "--max-dead-letter", "0", "--summary", summary);

            var text = File.ReadAllText(summary);
            Assert.StartsWith("earlier step\n", text, StringComparison.Ordinal);
            Assert.Contains("| Entity |", text, StringComparison.Ordinal);
            Assert.Contains("payments", text, StringComparison.Ordinal);
            Assert.Contains("2 checks failed", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(summary);
        }
    }

    [Fact]
    public async Task Unknown_options_and_bad_numbers_are_usage_errors()
    {
        Assert.Equal(HeadlessCli.Usage, await Run("inventory", "--synthetic", "--purge"));
        Assert.Equal(HeadlessCli.Usage, await Run("check", "--synthetic", "--max-dead-letter", "lots"));
        Assert.Equal(HeadlessCli.Usage, await Run("move", "--synthetic"));
    }

    [Fact]
    public void The_compiled_headless_tool_reaches_no_send_receive_settle_or_management_write()
    {
        var violations = MutationScanner.Scan(typeof(HeadlessCli).Assembly.Location, _ => true);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_headless_tool_does_not_reference_the_operations_assembly_that_can_move_messages()
    {
        var references = typeof(HeadlessCli).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("Acornsoft.MessageExplorer.Operations", references);
    }
}
