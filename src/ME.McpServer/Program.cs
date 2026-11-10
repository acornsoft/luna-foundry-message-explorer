// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.ReadTools;
using Azure.Core;

namespace Acornsoft.MessageExplorer.McpServer;

/// <summary><c>lfme-mcp &lt;source&gt;</c>: starts the read-only MCP server on stdio. Logs go to stderr only.</summary>
public static class Program
{
    public const int Usage = 64;

    public const string HelpText = """
        lfme-mcp: a read-only MCP server (stdio) for Service Bus inventory and health checks.

          lfme-mcp <source>

        Source (exactly one), the same as lfme:
          --synthetic                     a made-up namespace in memory (orders, payments, orders-events)
          --resource-id <ARM id>          counts through Azure Resource Manager; the Reader role is enough
          --namespace <name-or-host>      the namespace's management API (needs a management read permission)
            [--cloud public|usgov|china]  the Azure cloud, for --resource-id or --namespace
          --connection-string-env <VAR>   a connection string read from environment variable VAR (Listen-only)

        Tools: list_entities, get_entity, check_limits, summarize_dead_letters. All read counts only:
        no message bodies, and nothing is sent, received, settled, moved or deleted.
        Protocol: MCP 2026-07-28 (stateless, server/discover), and the initialize handshake of 2025-11-25.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        return await RunAsync(args, Console.In, Console.Out, Console.Error, Environment.GetEnvironmentVariable).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        Func<BrokerConnection, IEntityInventory>? open = null,
        Func<NamespaceRef, TokenCredential, IEntityInventory>? openArm = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            // Help goes to stderr: stdout carries MCP messages only.
            await stderr.WriteLineAsync(HelpText).ConfigureAwait(false);
            return args.Length == 0 ? Usage : 0;
        }

        OpenedSource opened;
        try
        {
            opened = InventorySources.Open(Parse(args), environment, open, openArm);
        }
        catch (InventorySourceException exception)
        {
            await stderr.WriteLineAsync("lfme-mcp: " + exception.Message).ConfigureAwait(false);
            return Usage;
        }

        await using (opened.Inventory.ConfigureAwait(false))
        {
            await stderr.WriteLineAsync($"lfme-mcp {McpStdioServer.Version}: read-only, serving {opened.Label} on stdio.").ConfigureAwait(false);
            await new McpStdioServer(ReadOnlyToolset.Over(opened)).RunAsync(stdin, stdout).ConfigureAwait(false);
        }

        return 0;
    }

    private static InventorySourceSpec Parse(string[] args)
    {
        var synthetic = false;
        string? ns = null, resourceId = null, cloud = null, variable = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new InventorySourceException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--synthetic": synthetic = true; break;
                case "--namespace": ns = Next(); break;
                case "--resource-id": resourceId = Next(); break;
                case "--cloud": cloud = Next(); break;
                case "--connection-string-env": variable = Next(); break;
                case "--connection-string" or "--sas" or "--key":
                    throw new InventorySourceException($"{args[i]} is not accepted: secrets are never taken on the command line. Put it in an environment variable and pass --connection-string-env <VAR>.");
                default: throw new InventorySourceException($"Unknown option '{args[i]}'.");
            }
        }

        return new InventorySourceSpec(synthetic, ns, resourceId, cloud, variable);
    }
}
