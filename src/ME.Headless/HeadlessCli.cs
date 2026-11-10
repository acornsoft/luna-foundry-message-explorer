// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Headless;

/// <summary>
/// <c>lfme</c>: Message Explorer without a window, for containers, pipelines and schedulers. Read-only: it
/// lists queues, topics and subscriptions with their message counts and checks them against limits. It never
/// reads a message body, and it cannot send, receive, settle, move, create or delete anything (the compiled
/// tool is scanned for that in the tests). Works for any queue or topic.
/// </summary>
public static partial class HeadlessCli
{
    public const int Ok = 0;
    public const int BrokerError = 2;
    public const int ThresholdBreached = 3;
    public const int Usage = 64;

    public const string HelpText = """
        lfme: read-only Service Bus inventory and health checks, headless.

          lfme inventory <source> [--format table|json] [--out <file>] [--summary <file>]
          lfme check     <source> [--max-dead-letter <n>] [--max-active <n>] [--max-scheduled <n>]
                                  [--entity <name>]... [--format table|json] [--out <file>] [--summary <file>]

        Source (exactly one):
          --synthetic                     a made-up namespace in memory (orders, payments, orders-events)
          --resource-id <ARM id>          read counts through Azure Resource Manager with Microsoft Entra ID.
                                          The Reader role on the namespace is enough (least privilege).
          --namespace <name-or-host>      read the namespace's own management API with Microsoft Entra ID
                                          (needs a management read permission on the namespace)
            [--cloud public|usgov|china]  the Azure cloud (default public), for --resource-id or --namespace
                                          Entra sign-in: workload identity, managed identity, environment
                                          (AZURE_CLIENT_ID ...) or Azure CLI, in that order
          --connection-string-env <VAR>   a connection string or SAS token read from environment variable VAR.
                                          Secrets are never taken on the command line. Use a Listen-only key.

        --entity limits a check to a queue, a topic (and its subscriptions) or topic/subscriptions/name.
        --summary appends a Markdown table to a file, for example $GITHUB_STEP_SUMMARY.

        Read-only: no message bodies are read, and nothing is sent, received, settled, moved or deleted.
        Exit codes: 0 ok, 2 broker or sign-in error, 3 a check failed, 64 bad arguments.
        """;

    public static Task<int> Main(string[] args) =>
        RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, connection => new ServiceBusBrokerProvider().OpenInventory(connection));

    /// <param name="open">Opens the read-only inventory for a namespace's management API. Tests pass a fake.</param>
    /// <param name="openArm">Opens the read-only inventory through Azure Resource Manager. Null: the real ARM reader.</param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        Func<BrokerConnection, IEntityInventory> open,
        Func<NamespaceRef, TokenCredential, IEntityInventory>? openArm = null)
    {
        openArm ??= (ns, credential) => new ArmEntityInventory(new ArmReader(AzureClouds.Find(ns.Cloud)), credential, ns);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(open);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await stdout.WriteLineAsync(HelpText).ConfigureAwait(false);
            return args.Length == 0 ? Usage : Ok;
        }

        if (args[0] is not ("inventory" or "check"))
            return await Fail(stderr, $"Unknown command '{args[0]}'. lfme only reads: use inventory or check.").ConfigureAwait(false);

        Options options;
        try
        {
            options = Options.Parse(args[0], args[1..]);
        }
        catch (UsageException exception)
        {
            return await Fail(stderr, exception.Message).ConfigureAwait(false);
        }

        IEntityInventory inventory;
        string source;
        try
        {
            (inventory, source) = Open(options, environment, open, openArm);
        }
        catch (UsageException exception)
        {
            return await Fail(stderr, exception.Message).ConfigureAwait(false);
        }
        catch (BrokerException exception)
        {
            return await BrokerFailure(stderr, exception).ConfigureAwait(false);
        }

        List<EntitySnapshot> entities;
        try
        {
            await using (inventory.ConfigureAwait(false))
                entities = await ReadAllAsync(inventory).ConfigureAwait(false);
        }
        catch (BrokerException exception)
        {
            return await BrokerFailure(stderr, exception).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException exception)
        {
            return await BrokerFailure(stderr, new BrokerException(BrokerErrorKind.Unauthenticated, FirstLine(exception.Message))).ConfigureAwait(false);
        }

        var report = Report.Build(options, source, entities);
        await stdout.WriteAsync(options.Json ? report.ToJson() : report.ToTable()).ConfigureAwait(false);
        if (options.OutPath is { } outPath)
            await File.WriteAllTextAsync(outPath, report.ToJson()).ConfigureAwait(false);
        if (options.SummaryPath is { } summaryPath)
            await File.AppendAllTextAsync(summaryPath, report.ToMarkdown()).ConfigureAwait(false);
        return report.Breaches.Count == 0 ? Ok : ThresholdBreached;
    }

    private static (IEntityInventory Inventory, string Source) Open(
        Options options,
        Func<string, string?> environment,
        Func<BrokerConnection, IEntityInventory> open,
        Func<NamespaceRef, TokenCredential, IEntityInventory> openArm)
    {
        if (options.Synthetic)
            return (new SyntheticNamespace(), "synthetic");

        if (options.ConnectionStringVariable is { } variable)
        {
            var value = environment(variable);
            if (string.IsNullOrWhiteSpace(value))
                throw new UsageException($"Environment variable {variable} is not set or is empty.");
            var connection = BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret(value));
            var plan = ServiceBusBrokerProvider.Plan(connection);
            return (open(connection), plan.FullyQualifiedNamespace);
        }

        var cloud = AzureClouds.Find(options.Cloud);
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            AuthorityHost = cloud.AuthorityHost,
            ExcludeInteractiveBrowserCredential = true,
        });

        if (options.ResourceId is { } resourceId)
        {
            var match = NamespaceResourceId().Match(resourceId.Trim());
            if (!match.Success)
                throw new UsageException("--resource-id must be a Service Bus namespace: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.ServiceBus/namespaces/<name>.");
            var name = match.Groups["name"].Value;
            var ns = new NamespaceRef(
                name,
                cloud.NamespaceHost(name),
                resourceId.Trim(),
                match.Groups["group"].Value,
                string.Empty,
                match.Groups["subscription"].Value,
                environment("AZURE_TENANT_ID") is { Length: > 0 } tenant ? tenant : null!,
                cloud.Id);
            return (openArm(ns, credential), ns.FullyQualifiedNamespace);
        }

        var host = cloud.NamespaceHost(options.Namespace!);
        return (open(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, host, credential)), host);
    }

    [GeneratedRegex(@"^/subscriptions/(?<subscription>[^/]+)/resourceGroups/(?<group>[^/]+)/providers/Microsoft\.ServiceBus/namespaces/(?<name>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceResourceId();

    private static async Task<List<EntitySnapshot>> ReadAllAsync(IEntityInventory inventory)
    {
        var all = new List<EntitySnapshot>();
        all.AddRange((await inventory.ListQueuesAsync().ConfigureAwait(false)).Entities);
        var topics = (await inventory.ListTopicsAsync().ConfigureAwait(false)).Entities;
        foreach (var topic in topics)
        {
            all.Add(topic);
            all.AddRange((await inventory.ListSubscriptionsAsync(topic.Name).ConfigureAwait(false)).Entities);
        }

        return all;
    }

    private static async Task<int> Fail(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync("lfme: " + message).ConfigureAwait(false);
        await stderr.WriteLineAsync("Run 'lfme --help' for usage.").ConfigureAwait(false);
        return Usage;
    }

    private static async Task<int> BrokerFailure(TextWriter stderr, BrokerException exception)
    {
        await stderr.WriteLineAsync($"lfme: {exception.Kind}: {FirstLine(exception.Message)}").ConfigureAwait(false);
        return BrokerError;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Options(
        string Command,
        bool Synthetic,
        string? Namespace,
        string? ResourceId,
        string? Cloud,
        string? ConnectionStringVariable,
        bool Json,
        string? OutPath,
        string? SummaryPath,
        long? MaxDeadLetter,
        long? MaxActive,
        long? MaxScheduled,
        IReadOnlyList<string> Entities)
    {
        public static Options Parse(string command, string[] args)
        {
            var synthetic = false;
            string? ns = null, resourceId = null, cloud = null, variable = null, outPath = null, summary = null;
            var json = false;
            long? maxDead = null, maxActive = null, maxScheduled = null;
            var entities = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
                long Number()
                {
                    var name = args[i];
                    var text = Next();
                    return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        ? value
                        : throw new UsageException($"{name} needs a whole number, not '{text}'.");
                }

                switch (args[i])
                {
                    case "--synthetic": synthetic = true; break;
                    case "--namespace": ns = Next(); break;
                    case "--resource-id": resourceId = Next(); break;
                    case "--cloud":
                        cloud = Next();
                        if (!AzureClouds.All.Any(c => string.Equals(c.Id, cloud, StringComparison.OrdinalIgnoreCase)))
                            throw new UsageException($"--cloud is public, usgov or china, not '{cloud}'.");
                        break;
                    case "--connection-string-env": variable = Next(); break;
                    case "--connection-string" or "--sas" or "--key":
                        // The value is skipped, never echoed: a secret on the command line ends up in shell history and process lists.
                        throw new UsageException($"{args[i]} is not accepted: secrets are never taken on the command line. Put it in an environment variable and pass --connection-string-env <VAR>.");
                    case "--format":
                        var format = Next();
                        json = format switch
                        {
                            "json" => true,
                            "table" => false,
                            _ => throw new UsageException($"--format is table or json, not '{format}'."),
                        };
                        break;
                    case "--out": outPath = Next(); break;
                    case "--summary": summary = Next(); break;
                    case "--max-dead-letter" when command == "check": maxDead = Number(); break;
                    case "--max-active" when command == "check": maxActive = Number(); break;
                    case "--max-scheduled" when command == "check": maxScheduled = Number(); break;
                    case "--entity": entities.Add(Next()); break;
                    default: throw new UsageException($"Unknown option '{args[i]}' for {command}.");
                }
            }

            var sources = (synthetic ? 1 : 0) + (ns is null ? 0 : 1) + (resourceId is null ? 0 : 1) + (variable is null ? 0 : 1);
            if (sources != 1)
                throw new UsageException("Give exactly one source: --synthetic, --resource-id, --namespace or --connection-string-env.");
            if (cloud is not null && ns is null && resourceId is null)
                throw new UsageException("--cloud goes with --resource-id or --namespace.");
            return new Options(command, synthetic, ns, resourceId, cloud, variable, json, outPath, summary, maxDead, maxActive, maxScheduled, entities);
        }
    }

    private sealed record Breach(string Path, string Reason);

    private sealed record Report(string Source, IReadOnlyList<EntitySnapshot> Entities, IReadOnlyList<Breach> Breaches, bool IsCheck)
    {
        public static Report Build(Options options, string source, IReadOnlyList<EntitySnapshot> all)
        {
            var entities = options.Entities.Count == 0 ? all : all.Where(e => options.Entities.Any(f => Matches(e, f))).ToList();
            var breaches = new List<Breach>();
            if (options.Command == "check")
            {
                foreach (var e in entities)
                {
                    Limit(e, "dead-letter", e.Counts.DeadLetter, options.MaxDeadLetter);
                    Limit(e, "active", e.Counts.Active, options.MaxActive);
                    Limit(e, "scheduled", e.Counts.Scheduled, options.MaxScheduled);
                }
            }

            return new Report(source, entities, breaches, options.Command == "check");

            void Limit(EntitySnapshot e, string what, long? count, long? max)
            {
                if (max is { } limit && count is { } value && value > limit)
                    breaches.Add(new Breach(e.Path, $"{what} {value} > {limit}"));
            }
        }

        private static bool Matches(EntitySnapshot e, string filter) =>
            string.Equals(e.Path, filter, StringComparison.OrdinalIgnoreCase)
            || (e.Parent is null && string.Equals(e.Name, filter, StringComparison.OrdinalIgnoreCase))
            || (e.Parent is not null && string.Equals(e.Parent, filter, StringComparison.OrdinalIgnoreCase));

        public string ToJson()
        {
            var payload = new
            {
                tool = "lfme",
                version = typeof(HeadlessCli).Assembly.GetName().Version?.ToString(3),
                readOnly = true,
                source = Source,
                entities = Entities.Select(e => new
                {
                    kind = e.Kind.ToString().ToLowerInvariant(),
                    path = e.Path,
                    active = e.Counts.Active,
                    deadLetter = e.Counts.DeadLetter,
                    scheduled = e.Counts.Scheduled,
                    transferDeadLetter = e.Counts.TransferDeadLetter,
                    status = e.Status,
                }),
                breaches = Breaches.Select(b => new { path = b.Path, reason = b.Reason }),
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }

        public string ToTable()
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"Source: {Source} (read-only)");
            var width = Math.Max(6, Entities.Select(e => e.Path.Length).DefaultIfEmpty(0).Max());
            text.AppendLine(CultureInfo.InvariantCulture, $"{"KIND",-13} {"ENTITY".PadRight(width)} {"ACTIVE",8} {"DEAD-LETTER",12} {"SCHEDULED",10}");
            foreach (var e in Entities)
                text.AppendLine(CultureInfo.InvariantCulture, $"{e.Kind.ToString().ToLowerInvariant(),-13} {e.Path.PadRight(width)} {N(e.Counts.Active),8} {N(e.Counts.DeadLetter),12} {N(e.Counts.Scheduled),10}");
            if (IsCheck)
            {
                text.AppendLine();
                if (Breaches.Count == 0)
                    text.AppendLine("All checks passed.");
                foreach (var b in Breaches)
                    text.AppendLine(CultureInfo.InvariantCulture, $"FAIL {b.Path}: {b.Reason}");
            }

            return text.ToString();
        }

        public string ToMarkdown()
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"### Message Explorer: {Source} (read-only)");
            text.AppendLine();
            text.AppendLine("| Entity | Kind | Active | Dead-letter | Scheduled |");
            text.AppendLine("| --- | --- | ---: | ---: | ---: |");
            foreach (var e in Entities)
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{e.Path}` | {e.Kind.ToString().ToLowerInvariant()} | {N(e.Counts.Active)} | {N(e.Counts.DeadLetter)} | {N(e.Counts.Scheduled)} |");
            if (IsCheck)
            {
                text.AppendLine();
                text.AppendLine(Breaches.Count switch
                {
                    0 => "All checks passed.",
                    1 => "**1 check failed:**",
                    var n => $"**{n} checks failed:**",
                });
                foreach (var b in Breaches)
                    text.AppendLine(CultureInfo.InvariantCulture, $"- `{b.Path}`: {b.Reason}");
            }

            text.AppendLine();
            return text.ToString();
        }

        private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
    }
}
