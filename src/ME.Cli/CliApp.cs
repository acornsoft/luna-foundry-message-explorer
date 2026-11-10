// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Scenarios;
using Acornsoft.MessageExplorer.Scenarios.GslRegE;

namespace Acornsoft.MessageExplorer.Cli;

/// <summary>Console runner for live scenarios. No UI. Exit codes: 0 ok, 1 blocked or mismatch, 2 usage.</summary>
internal sealed class CliApp
{
    public const string Usage = """
        lfme: Luna Foundry Message Explorer console runner (synthetic data only)

          lfme scenario list
          lfme scenario run <scenario-id> --subscription <id> --resource-group <rg> [--run-id <id>] [--settle-seconds 90] [--send]
          lfme dlq peek <scenario-id> --subscription <id> --resource-group <rg> --run <run-id>
          lfme dlq resubmit <scenario-id> --subscription <id> --resource-group <rg> (--run <run-id> | --sequence 3,7) [--yes] [--send]

        Auth is Microsoft Entra through DefaultAzureCredential (az login works). Every command except --help
        requires a signed-in Entra account. Keys, SAS, connection strings, and skip-auth variables are refused.
        lfme has no HTTP listener and no local emulator mode.
        scenario run is a dry run by default (gate, plan, and counts; nothing is sent). Pass --send to write to the queue.
        Resubmit needs both --yes and --send. dlq peek needs --run so it only shows that run's dead-letters.
        --subscription and --resource-group can come from LFME_SUBSCRIPTION and LFME_RESOURCE_GROUP.
        The sandbox gate also needs the subscription in LFME_ALLOWED_SUBSCRIPTIONS (comma-separated).
        Add --tcp to use AMQP on port 5671 instead of WebSockets on 443.
        """;

    public static readonly IReadOnlyList<string> LiveScenarios = ["gsl-reg-e-dispute"];

    private readonly Func<AzureQueueAddress, bool, IScenarioBroker> _brokerFactory;
    private readonly TextWriter _out;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Task> _ensureSignedIn;

    public CliApp(
        Func<AzureQueueAddress, bool, IScenarioBroker> brokerFactory,
        TextWriter output,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<CancellationToken, Task> ensureSignedIn)
    {
        _brokerFactory = brokerFactory;
        _out = output;
        _clock = clock;
        _delay = delay;
        _ensureSignedIn = ensureSignedIn ?? throw new ArgumentNullException(nameof(ensureSignedIn));
    }

    public async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken = default)
    {
        CliArgs args;
        try
        {
            args = CliArgs.Parse(argv);
            if (args.Flags.Contains("help") || args.Command.Length == 0)
            {
                _out.WriteLine(Usage);
                return args.Command.Length == 0 && !args.Flags.Contains("help") ? 2 : 0;
            }

            EntraAuth.RefuseNonEntraInput(args);
            await _ensureSignedIn(cancellationToken).ConfigureAwait(false);

            return args.Command switch
            {
                "scenario list" => List(),
                "scenario run" => await RunScenarioAsync(args, cancellationToken).ConfigureAwait(false),
                "dlq peek" => await PeekAsync(args, cancellationToken).ConfigureAwait(false),
                "dlq resubmit" => await ResubmitAsync(args, cancellationToken).ConfigureAwait(false),
                _ => throw new CliUsageException("Unknown command '" + args.Command + "'."),
            };
        }
        catch (EntraSignInException error)
        {
            _out.WriteLine(error.Message);
            return 1;
        }
        catch (CliUsageException error)
        {
            _out.WriteLine("Usage error: " + error.Message);
            _out.WriteLine(Usage);
            return 2;
        }
        catch (ScenarioParseException error)
        {
            _out.WriteLine("Scenario error: " + error.Message);
            return 2;
        }
    }

    private int List()
    {
        foreach (var id in LiveScenarios)
        {
            var scenario = Load(id);
            var target = RegETarget.From(scenario);
            _out.WriteLine($"{scenario.Id}  seed {scenario.Seed}  {scenario.Steps.Sum(s => s.Count)} messages  -> {target.FullyQualifiedNamespace}/{target.Queue}");
        }

        return 0;
    }

    private async Task<int> RunScenarioAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var (scenario, target, runner, broker) = Prepare(args);
        await using var _ = broker as IAsyncDisposable;
        var runId = args.Options.TryGetValue("run-id", out var given)
            ? given
            : "r" + _clock.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (args.Flags.Contains("send") && args.Flags.Contains("dry-run"))
            throw new CliUsageException("Use --send to send, or omit it for a dry run. Do not pass both.");
        var options = new LiveRunOptions(runId, TimeSpan.FromSeconds(args.Int("settle-seconds", 90)), TimeSpan.FromSeconds(5), DryRun: !args.Flags.Contains("send"));
        var report = await runner.RunAsync(scenario, options, cancellationToken).ConfigureAwait(false);
        return report.Allowed && report.Mismatches.Count == 0 ? 0 : 1;
    }

    private async Task<int> PeekAsync(CliArgs args, CancellationToken cancellationToken)
    {
        if (!args.Options.TryGetValue("run", out var run) || string.IsNullOrWhiteSpace(run))
            throw new CliUsageException("lfme dlq peek needs --run so it only shows dead-letters from that run.");

        var (_, target, _, broker) = Prepare(args);
        await using var _ = broker as IAsyncDisposable;
        var counts = await broker.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        _out.WriteLine($"{target.FullyQualifiedNamespace}/{target.Queue}: {counts}");
        var letters = (await broker.PeekDeadLettersAsync(LiveScenarioRunner.PeekCap, cancellationToken).ConfigureAwait(false))
            .Where(m => m.RunId == run)
            .ToList();
        _out.WriteLine($"Dead-letters for run {run} (peek, nothing locked): {letters.Count}");
        foreach (var group in letters.GroupBy(m => m.DeadLetterReason ?? "(none)"))
        {
            _out.WriteLine($"  {group.Key}: {group.Count()}");
            foreach (var message in group)
            {
                var fix = DeadLetterFixer.TryFix(message);
                _out.WriteLine($"    seq {message.SequenceNumber,-5} {message.CorrelationId ?? "(no dispute id)",-15} run {message.RunId ?? "-",-16} {message.DeadLetterDescription}");
                _out.WriteLine($"          {(fix.Fixable ? "fixable" : "needs a person")}: {fix.Explanation}");
            }
        }

        return 0;
    }

    private async Task<int> ResubmitAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var (_, target, runner, broker) = Prepare(args);
        await using var _ = broker as IAsyncDisposable;
        var selection = new ResubmitSelection(args.Options.TryGetValue("run", out var run) ? run : null, args.Sequences());
        var report = await runner.ResubmitAsync(target, selection, args.Flags.Contains("yes"), args.Flags.Contains("send"), cancellationToken).ConfigureAwait(false);
        if (!report.Allowed)
            _out.WriteLine("Blocked: " + report.BlockReason);
        return report.Allowed ? 0 : 1;
    }

    private (ScenarioDefinition Scenario, RegETarget Target, LiveScenarioRunner Runner, IScenarioBroker Broker) Prepare(CliArgs args)
    {
        var id = args.Subject ?? throw new CliUsageException("Name the scenario, for example gsl-reg-e-dispute.");
        var scenario = Load(id);
        var target = RegETarget.From(scenario);
        var address = new AzureQueueAddress(
            args.Require("subscription", "LFME_SUBSCRIPTION"),
            args.Require("resource-group", "LFME_RESOURCE_GROUP"),
            target.FullyQualifiedNamespace,
            target.Queue);
        var extra = (Environment.GetEnvironmentVariable("LFME_ALLOWED_SUBSCRIPTIONS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var policy = SandboxPolicy.LoadEmbedded().WithSubscriptions(extra);
        var broker = _brokerFactory(address, !args.Flags.Contains("tcp"));
        return (scenario, target, new LiveScenarioRunner(broker, policy, _clock, _delay, _out), broker);
    }

    private static ScenarioDefinition Load(string id)
    {
        if (!LiveScenarios.Contains(id, StringComparer.Ordinal))
            throw new CliUsageException("Unknown live scenario '" + id + "'. Try: lfme scenario list");
        return ScenarioParser.LoadEmbedded(id + ".yaml");
    }
}
