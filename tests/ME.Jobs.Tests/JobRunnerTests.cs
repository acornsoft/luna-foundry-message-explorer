// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

/// <summary>The run loop with fast in-process fakes: roles, timing, correlation, and failure handling.</summary>
public sealed class JobRunnerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static JobDefinition Job(params PluginBinding[] plugins) => new(
        "demo", "loop", "/tmp/loop.job.yaml", "/tmp", "local", JobLoader.InMemoryProvider, 1,
        Start, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(4),
        "in.q", plugins, new Dictionary<string, string>());

    private static PluginBinding Bind(string name, params PluginRole[] roles) =>
        new(name, "1.0.0", roles.ToHashSet(), [], false, "/tmp", new JsonObject());

    private static PluginBinding Observer(params string[] sinks) =>
        new(BuiltinPlugins.BusObserver, "1.0.0", new HashSet<PluginRole> { PluginRole.Observer }, [], true, "/tmp",
            new JsonObject { ["sinks"] = new JsonArray(sinks.Select(s => (JsonNode)s).ToArray()) });

    private static FakePlugin OneShotGenerator(params string[] ids)
    {
        var sent = false;
        return new FakePlugin("gen", null, PluginRole.Generator, PluginRole.Assertion)
        {
            OnTick = _ =>
            {
                if (sent)
                    return [];
                sent = true;
                return ids.Select(id => new OutboundMessage(null, id, new JsonObject { ["ref"] = id }, 0)).ToList();
            },
            OnExpect = m => [new JsonObject { ["correlationId"] = m.CorrelationId, ["description"] = "answered on out.q" }],
            OnAssert = (_, actual) => actual.Any(a => a.Sink == "out.q")
                ? new Verdict(VerdictStatus.Pass, "answered")
                : new Verdict(VerdictStatus.Pending, "no answer yet"),
        };
    }

    private static FakePlugin Echo(TimeSpan delay) => new(
        "echo", new JsonObject { ["subscribe"] = new JsonArray("in.q") }, PluginRole.Simulator)
    {
        OnDeliver = (m, _) => [new OutboundMessage("out.q", m.CorrelationId, new JsonObject { ["echo"] = m.CorrelationId }, (long)delay.TotalMilliseconds)],
    };

    [Fact]
    public async Task Links_expected_and_actual_by_correlation_id()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("a-1", "a-2"), ["echo"] = () => Echo(TimeSpan.FromSeconds(7)) });
        var result = await new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator), Observer("out.q")));

        Assert.Equal(2, result.SentCount);
        Assert.Equal(["a-1", "a-2"], result.Records.Select(r => r.CorrelationId));
        Assert.All(result.Records, r =>
        {
            Assert.Equal(VerdictStatus.Pass, r.Verdict.Status);
            Assert.Equal("answered on out.q", r.Expected.Single()["description"]!.GetValue<string>());
            var seen = Assert.Single(r.Actual);
            Assert.Equal(r.CorrelationId, seen.CorrelationId);
            Assert.Equal(r.CorrelationId, seen.Body["echo"]!.GetValue<string>());
        });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Short_delays_take_their_own_time_not_a_whole_clock_step()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("t-1"), ["echo"] = () => Echo(TimeSpan.FromSeconds(7)) });
        var result = await new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator), Observer("out.q")));

        Assert.Equal(TimeSpan.FromSeconds(7), result.Records.Single().SimulatedDuration);
    }

    [Fact]
    public async Task Delays_past_the_job_duration_are_drained()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("late"), ["echo"] = () => Echo(TimeSpan.FromHours(3)) });
        var result = await new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator), Observer("out.q")));

        Assert.Equal(TimeSpan.FromHours(3), result.Records.Single().SimulatedDuration);
        Assert.Equal(Start + TimeSpan.FromHours(3), result.SimulatedEnd);
    }

    [Fact]
    public async Task Still_pending_at_the_end_is_a_failure()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("lost"), ["echo"] = () => Echo(TimeSpan.FromHours(9)) });
        var result = await new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator), Observer("out.q")));

        var record = Assert.Single(result.Records);
        Assert.Equal(VerdictStatus.Fail, record.Verdict.Status);
        Assert.Contains("pending", record.Verdict.Reason, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_simulator_without_subscriptions_is_a_setup_error()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("x"), ["echo"] = () => new FakePlugin("echo", null, PluginRole.Simulator) });
        var error = await Assert.ThrowsAsync<JobRunException>(() =>
            new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator))));
        Assert.Contains("subscribe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_loop_is_stopped()
    {
        var loop = new FakePlugin("echo", new JsonObject { ["subscribe"] = new JsonArray("in.q") }, PluginRole.Simulator)
        {
            OnDeliver = (m, _) => [new OutboundMessage("in.q", m.CorrelationId, new JsonObject(), 0)],
        };
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("x"), ["echo"] = () => loop });
        var error = await Assert.ThrowsAsync<JobRunException>(() =>
            new JobRunner(launcher, maxDeliveriesPerStep: 500).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator))));
        Assert.Contains("reply loop", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_plugin_is_disposed_even_when_one_fails()
    {
        var generator = OneShotGenerator("x");
        var broken = new FakePlugin("echo", new JsonObject { ["subscribe"] = new JsonArray("in.q") }, PluginRole.Simulator)
        {
            OnDeliver = (_, _) => throw new InvalidOperationException("simulated crash"),
        };
        var launcher = new FakeLauncher(new() { ["gen"] = () => generator, ["echo"] = () => broken });

        var error = await Assert.ThrowsAsync<PluginException>(() =>
            new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator))));
        Assert.Contains("simulated crash", error.Message, StringComparison.Ordinal);
        Assert.True(generator.Disposed);
        Assert.True(broken.Disposed);
    }

    [Fact]
    public async Task Only_the_in_memory_provider_runs()
    {
        var job = Job(Bind("gen", PluginRole.Generator)) with { Provider = "azure-service-bus" };
        var error = await Assert.ThrowsAsync<JobRunException>(() => new JobRunner(new FakeLauncher([])).RunAsync(job));
        Assert.Contains("in-memory", error.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<RunStep> Steps { get; } = [];

        public ValueTask OnStepAsync(RunStep step, CancellationToken cancellationToken)
        {
            Steps.Add(step);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_observer_sees_every_send_and_observation_in_clock_order()
    {
        var launcher = new FakeLauncher(new() { ["gen"] = () => OneShotGenerator("o-1", "o-2"), ["echo"] = () => Echo(TimeSpan.FromSeconds(7)) });
        var observer = new RecordingObserver();
        var result = await new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator, PluginRole.Assertion), Bind("echo", PluginRole.Simulator), Observer("out.q")), observer);

        Assert.Equal(["o-1", "o-2"], observer.Steps.SelectMany(s => s.Sent).Select(s => s.CorrelationId));
        Assert.Equal(result.Records.SelectMany(r => r.Actual).Count(), observer.Steps.SelectMany(s => s.Observed).Count());
        Assert.Equal(observer.Steps.Select(s => s.Now).OrderBy(t => t), observer.Steps.Select(s => s.Now));
        Assert.Contains(observer.Steps, s => s.Now == Start + TimeSpan.FromSeconds(7) && s.Observed.Count == 2);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_disposes_plugins()
    {
        var generator = new FakePlugin("gen", null, PluginRole.Generator) { OnTick = _ => [] };
        var launcher = new FakeLauncher(new() { ["gen"] = () => generator });
        using var cancel = new CancellationTokenSource();
        var observer = new CancelAfter(3, cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JobRunner(launcher).RunAsync(Job(Bind("gen", PluginRole.Generator)), observer, cancel.Token));
        Assert.True(generator.Disposed);
    }

    private sealed class CancelAfter(int steps, CancellationTokenSource cancel) : IRunObserver
    {
        private int _seen;

        public ValueTask OnStepAsync(RunStep step, CancellationToken cancellationToken)
        {
            if (++_seen == steps)
                cancel.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
