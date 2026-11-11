// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Bus;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Running;

/// <summary>Starts the plugin for one binding. The default starts built-ins in-process and everything else over stdio.</summary>
public interface IPluginLauncher
{
    Task<IPluginHost> LaunchAsync(PluginBinding binding, JobDefinition job, InMemoryBus bus, CancellationToken cancellationToken);
}

public sealed class DefaultPluginLauncher(PluginHostOptions? options = null) : IPluginLauncher
{
    public async Task<IPluginHost> LaunchAsync(PluginBinding binding, JobDefinition job, InMemoryBus bus, CancellationToken cancellationToken)
    {
        if (binding.Builtin)
            return BuiltinPlugins.Create(binding, bus, job.TargetEntity);

        var initialize = new PluginInitialize(job.RunId, job.Seed, job.ClockStart, binding.Config, job.Variables);
        return await StdioJsonRpcPluginHost.StartAsync(
            binding.Name, binding.Version, binding.Command, binding.WorkingDirectory, binding.Roles, initialize, options, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Expected and actual for one correlation ID, with the verdict. This is the row the live view will show.</summary>
public sealed record CorrelationRecord(
    string CorrelationId,
    DateTimeOffset SentAt,
    IReadOnlyList<JsonObject> Expected,
    IReadOnlyList<Observation> Actual,
    Verdict Verdict)
{
    /// <summary>Simulated time from send to the last observation.</summary>
    public TimeSpan SimulatedDuration => Actual.Count == 0 ? TimeSpan.Zero : Actual.Max(a => a.At) - SentAt;
}

public sealed record RunResult(
    JobDefinition Job,
    IReadOnlyList<CorrelationRecord> Records,
    int SentCount,
    long BusMessages,
    IReadOnlyDictionary<string, string> PluginVersions,
    DateTimeOffset SimulatedEnd,
    IReadOnlyDictionary<string, int>? EventCounts = null)
{
    /// <summary>Plugin events counted as "plugin.kind", for example "erp-standin.throttled". Sorted by key.</summary>
    public IReadOnlyDictionary<string, int> Events => EventCounts ?? new SortedDictionary<string, int>(StringComparer.Ordinal);

    public int Passed => Records.Count(record => record.Verdict.Status == VerdictStatus.Pass);

    public int Failed => Records.Count - Passed;

    public bool Succeeded => Failed == 0;
}

/// <summary>A correlation ID sent this step, with the expectations the assertion plugins gave for it.</summary>
public sealed record SentRecord(string CorrelationId, DateTimeOffset SentAt, IReadOnlyList<JsonObject> Expected);

/// <summary>An <c>event</c> notification from a plugin, for example throttled, retry, deadLettered, duplicateDropped.</summary>
public sealed record PluginEvent(string Plugin, string Kind, JsonObject Data);

/// <summary>What changed in one pass of the run loop. The live view applies these in order.</summary>
public sealed record RunStep(
    DateTimeOffset Now,
    IReadOnlyList<SentRecord> Sent,
    IReadOnlyList<Observation> Observed,
    IReadOnlyList<PluginEvent> Events);

/// <summary>Receives each step of a run as it happens. Awaited, so an observer can pace a run for watching.</summary>
public interface IRunObserver
{
    ValueTask OnStepAsync(RunStep step, CancellationToken cancellationToken);
}

public sealed class JobRunException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Runs one job on a simulated clock against the in-memory broker. The order of every step is fixed
/// (plugins in file order, messages by time then sequence), so the same job and seed give the same result.
/// </summary>
public sealed class JobRunner
{
    public const int MaxDeliveriesPerStep = 100_000;
    public const int MaxSteps = 1_000_000;

    private readonly IPluginLauncher _launcher;
    private readonly int _maxDeliveriesPerStep;

    public JobRunner(IPluginLauncher? launcher = null, int maxDeliveriesPerStep = MaxDeliveriesPerStep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeliveriesPerStep, 1);
        _launcher = launcher ?? new DefaultPluginLauncher();
        _maxDeliveriesPerStep = maxDeliveriesPerStep;
    }

    public Task<RunResult> RunAsync(JobDefinition job, CancellationToken cancellationToken = default) =>
        RunAsync(job, null, cancellationToken);

    public async Task<RunResult> RunAsync(JobDefinition job, IRunObserver? observer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Provider != Loading.JobLoader.InMemoryProvider)
            throw new JobRunException($"Provider '{job.Provider}' cannot run simulated test jobs. Only the in-memory broker is supported; use me-jobs send for a live send.");

        var bus = new InMemoryBus();
        var hosts = new List<(PluginBinding Binding, IPluginHost Host)>();
        try
        {
            foreach (var binding in job.Plugins)
                hosts.Add((binding, await _launcher.LaunchAsync(binding, job, bus, cancellationToken).ConfigureAwait(false)));

            return await RunCoreAsync(job, bus, hosts, _maxDeliveriesPerStep, observer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, host) in Enumerable.Reverse(hosts))
                await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<RunResult> RunCoreAsync(
        JobDefinition job,
        InMemoryBus bus,
        IReadOnlyList<(PluginBinding Binding, IPluginHost Host)> hosts,
        int maxDeliveriesPerStep,
        IRunObserver? observer,
        CancellationToken cancellationToken)
    {
        string? SendOf(IPluginHost host) => hosts.First(h => ReferenceEquals(h.Host, host)).Binding.Send;
        IPluginHost[] With(PluginRole role) => hosts.Where(h => h.Binding.Roles.Contains(role)).Select(h => h.Host).ToArray();
        var generators = With(PluginRole.Generator);
        var simulators = With(PluginRole.Simulator);
        var assertions = With(PluginRole.Assertion);
        var observers = With(PluginRole.Observer);

        foreach (var simulator in simulators)
        {
            if (simulator.Info.Subscriptions.Count == 0)
                throw new JobRunException($"Simulator '{simulator.Info.Name}' declares no subscriptions. Set capabilities.subscribe in its initialize answer.");
        }

        var order = new List<string>();
        var sentAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var expected = new Dictionary<string, List<(IPluginHost Asserter, JsonObject Expectation)>>(StringComparer.Ordinal);
        var actual = new Dictionary<string, List<Observation>>(StringComparer.Ordinal);
        var sentCount = 0;

        var now = job.ClockStart;
        var end = job.ClockStart + job.Duration;
        var hardStop = end + job.Drain;
        DateTimeOffset? nextTick = job.ClockStart;
        var eventsSeen = new int[hosts.Count];

        for (var step = 0; ; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step > MaxSteps)
                throw new JobRunException("The run took too many clock steps. Check clock.step and clock.duration.");

            // Generators tick on clock.step boundaries. Between ticks the clock jumps straight to the next
            // message that becomes visible, so a 5-second hop takes 5 simulated seconds, not one whole step.
            var stepSent = new List<SentRecord>();
            var stepObserved = new List<Observation>();
            var ticking = nextTick == now;
            if (ticking)
                nextTick = now >= end ? null : (now + job.Step > end ? end : now + job.Step);

            foreach (var generator in ticking ? generators : [])
            {
                foreach (var outbound in await Call(generator, () => generator.TickAsync(now, cancellationToken)).ConfigureAwait(false))
                {
                    var message = bus.Enqueue(outbound.To ?? SendOf(generator) ?? job.TargetEntity, outbound.CorrelationId, outbound.Body, now.AddMilliseconds(outbound.DelayMs));
                    sentCount++;
                    if (!sentAt.ContainsKey(message.CorrelationId))
                    {
                        sentAt[message.CorrelationId] = message.AvailableAt;
                        order.Add(message.CorrelationId);
                    }

                    var given = new List<JsonObject>();
                    foreach (var assertion in assertions)
                    {
                        foreach (var expectation in await Call(assertion, () => assertion.ExpectAsync(message, cancellationToken)).ConfigureAwait(false))
                        {
                            var key = expectation["correlationId"]?.GetValue<string>() ?? message.CorrelationId;
                            if (!expected.TryGetValue(key, out var list))
                                expected[key] = list = [];
                            list.Add((assertion, expectation));
                            given.Add(expectation);
                        }
                    }

                    if (given.Count > 0)
                        stepSent.Add(new SentRecord(message.CorrelationId, sentAt[message.CorrelationId], given));
                }
            }

            var deliveries = 0;
            bool delivered;
            do
            {
                delivered = false;
                foreach (var simulator in simulators)
                {
                    foreach (var entity in simulator.Info.Subscriptions)
                    {
                        foreach (var incoming in bus.TakeDue("simulator:" + simulator.Info.Name, entity, now))
                        {
                            delivered = true;
                            if (++deliveries > maxDeliveriesPerStep)
                                throw new JobRunException("Simulators kept replying to each other without the clock moving. Check for a reply loop.");
                            foreach (var reply in await Call(simulator, () => simulator.DeliverAsync(incoming, now, cancellationToken)).ConfigureAwait(false))
                                bus.Enqueue(reply.To ?? job.TargetEntity, reply.CorrelationId, reply.Body, now.AddMilliseconds(reply.DelayMs));
                        }
                    }
                }
            }
            while (delivered);

            foreach (var watcher in observers)
            {
                foreach (var observation in await Call(watcher, () => watcher.ObserveAsync(now, cancellationToken)).ConfigureAwait(false))
                {
                    if (!actual.TryGetValue(observation.CorrelationId, out var list))
                        actual[observation.CorrelationId] = list = [];
                    list.Add(observation);
                    stepObserved.Add(observation);
                }
            }

            if (observer is not null)
            {
                var stepEvents = new List<PluginEvent>();
                for (var h = 0; h < hosts.Count; h++)
                {
                    var all = hosts[h].Host.Events;
                    foreach (var notice in all.Skip(eventsSeen[h]))
                        stepEvents.Add(new PluginEvent(hosts[h].Host.Info.Name, KindOf(notice), notice));
                    eventsSeen[h] = all.Count;
                }

                await observer.OnStepAsync(new RunStep(now, stepSent, stepObserved, stepEvents), cancellationToken).ConfigureAwait(false);
            }

            var due = bus.NextAvailableAfter(now);
            if (due > hardStop)
                due = null;
            if (nextTick is null && due is null)
                break;
            now = nextTick is null ? due!.Value : due is null || nextTick <= due ? nextTick.Value : due.Value;
        }

        var records = new List<CorrelationRecord>();
        foreach (var correlationId in order.Concat(expected.Keys.Where(key => !sentAt.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal)))
        {
            if (!expected.TryGetValue(correlationId, out var expectations))
                continue;
            var seen = actual.TryGetValue(correlationId, out var list) ? list.OrderBy(o => o.At).ThenBy(o => o.Sink, StringComparer.Ordinal).ToList() : [];
            var verdicts = new List<Verdict>();
            foreach (var group in expectations.GroupBy(pair => pair.Asserter))
            {
                var mine = group.Select(pair => pair.Expectation).ToList();
                verdicts.Add(await Call(group.Key, () => group.Key.AssertAsync(correlationId, mine, seen, cancellationToken)).ConfigureAwait(false));
            }

            records.Add(new CorrelationRecord(
                correlationId,
                sentAt.TryGetValue(correlationId, out var at) ? at : job.ClockStart,
                expectations.Select(pair => pair.Expectation).ToList(),
                seen,
                Combine(verdicts)));
        }

        var versions = hosts
            .GroupBy(h => h.Host.Info.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Host.Info.Version, StringComparer.Ordinal);
        var events = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, host) in hosts)
        {
            foreach (var notice in host.Events)
            {
                var key = host.Info.Name + "." + KindOf(notice);
                events[key] = events.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        return new RunResult(job, records, sentCount, bus.TotalMessages, versions, now, events);
    }

    private static string KindOf(JsonObject notice) =>
        notice["kind"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "event";

    private static Verdict Combine(IReadOnlyList<Verdict> verdicts)
    {
        var failed = verdicts.Where(v => v.Status == VerdictStatus.Fail).ToList();
        if (failed.Count > 0)
            return new Verdict(VerdictStatus.Fail, string.Join("; ", failed.Select(v => v.Reason)));
        var pending = verdicts.Where(v => v.Status == VerdictStatus.Pending).ToList();
        if (pending.Count > 0)
            return new Verdict(VerdictStatus.Fail, "still pending when the run ended: " + string.Join("; ", pending.Select(v => v.Reason)));
        return new Verdict(VerdictStatus.Pass, string.Join("; ", verdicts.Select(v => v.Reason).Where(r => r.Length > 0)));
    }

    private static async Task<T> Call<T>(IPluginHost host, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (PluginException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PluginException(host.Info.Name, exception.Message, exception);
        }
    }
}
