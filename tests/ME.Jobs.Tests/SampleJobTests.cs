// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Xml.Linq;
using Acornsoft.MessageExplorer.Jobs.Cli;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

/// <summary>The sample jobs end to end through real plugin processes, the CLI, and determinism.</summary>
public sealed class SampleJobTests
{
    private static async Task<string> RunJUnit(string jobPath, long? seed = null)
    {
        var job = JobLoader.Load(jobPath, null, _ => null);
        if (seed is not null)
            job = job with { Seed = seed.Value };
        return JUnitWriter.Write(await new JobRunner().RunAsync(job));
    }

    private static Dictionary<string, string> Properties(string junit) =>
        XDocument.Parse(junit).Descendants("property").ToDictionary(p => (string)p.Attribute("name")!, p => (string)p.Attribute("value")!);

    [Fact]
    public async Task Erp_sample_passes_with_credit_holds_throttling_retries_and_dead_letters()
    {
        var result = await new JobRunner().RunAsync(JobLoader.Load(Repo.ErpJob, null, _ => null));

        Assert.True(result.Succeeded, string.Join("\n", result.Records.Where(r => r.Verdict.Status != Protocol.VerdictStatus.Pass).Select(r => $"{r.CorrelationId}: {r.Verdict.Reason}")));
        Assert.Equal(55, result.Records.Count);
        Assert.Contains(result.Records, r => r.Verdict.Reason.Contains("CREDIT_HOLD", StringComparison.Ordinal));
        Assert.Contains(result.Records, r => r.Verdict.Reason.Contains("dead-lettered after 5 deliveries", StringComparison.Ordinal));
        Assert.True(result.Events.GetValueOrDefault("erp-standin.throttled") > 0, "the burst should be throttled");
        Assert.True(result.Events.GetValueOrDefault("erp-standin.retry") > 0, "some calls should be retried");
        Assert.True(result.Events.GetValueOrDefault("erp-standin.deadLettered") > 0, "poison orders should be dead-lettered");
    }

    [Fact]
    public async Task Same_seed_gives_byte_identical_junit_and_a_new_seed_does_not()
    {
        var first = await RunJUnit(Repo.ErpJob);
        var second = await RunJUnit(Repo.ErpJob);
        var other = await RunJUnit(Repo.ErpJob, seed: 777);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal("erp-standin.erp-day.seed-777", Properties(other)["runId"]);
        Assert.DoesNotContain(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), first, StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task Crew_stock_sample_models_fan_out_dedupe_throttle_and_dead_letters()
    {
        var result = await new JobRunner().RunAsync(JobLoader.Load(Repo.Path("samples", "jobs", "crew-stock", "jobs", "shift-sync.job.yaml"), null, _ => null));

        Assert.True(result.Succeeded, string.Join("\n", result.Records.Where(r => r.Verdict.Status != Protocol.VerdictStatus.Pass).Select(r => $"{r.CorrelationId}: {r.Verdict.Reason}")));
        Assert.True(result.SentCount > result.Records.Count, "the app re-sends some changes");
        Assert.True(result.Events.GetValueOrDefault("hop-relay.duplicateDropped") > 0);
        Assert.True(result.Events.GetValueOrDefault("hop-relay.throttled") > 0);
        Assert.True(result.Events.GetValueOrDefault("hop-relay.retry") > 0);
        Assert.True(result.Events.GetValueOrDefault("hop-relay.deadLettered") > 0);
        Assert.Contains(result.Records, r => r.Verdict.Reason.Contains("usage still recorded", StringComparison.Ordinal));
    }

    [PythonFact]
    public async Task Crew_stock_sample_is_deterministic()
    {
        var path = Repo.Path("samples", "jobs", "crew-stock", "jobs", "shift-sync.job.yaml");
        Assert.Equal(await RunJUnit(path), await RunJUnit(path));
    }

    [PythonFact]
    public async Task Python_sample_job_runs_through_the_host()
    {
        var result = await new JobRunner().RunAsync(JobLoader.Load(Repo.PythonJob, null, _ => null));

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.Records.Count);
        Assert.Equal("0.1.0", result.PluginVersions["hello-generator"]);
    }

    [Fact]
    public async Task Cli_run_twice_writes_identical_junit_and_exits_zero()
    {
        var dir = Directory.CreateTempSubdirectory("lfme-cli-").FullName;
        try
        {
            var a = Path.Combine(dir, "a.xml");
            var b = Path.Combine(dir, "b.xml");
            var output = new StringWriter();
            Assert.Equal(JobsCli.Ok, await JobsCli.RunAsync(["run", Repo.ErpJob, "--junit", a], output, new StringWriter(), _ => null));
            Assert.Equal(JobsCli.Ok, await JobsCli.RunAsync(["run", Repo.ErpJob, "--junit", b], new StringWriter(), new StringWriter(), _ => null));

            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            Assert.Contains("55 passed, 0 failed", output.ToString(), StringComparison.Ordinal);
            var suite = XDocument.Load(a).Descendants("testsuite").Single();
            Assert.Equal("55", (string)suite.Attribute("tests")!);
            Assert.Equal("0", (string)suite.Attribute("failures")!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Cli_reports_validation_errors_with_exit_code_2()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: c\nplugins: {}\n");
        temp.Write("environments/local.yaml", "provider: in-memory\n");
        var job = temp.Write("jobs/x.job.yaml", "job: x\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: nope@1.0.0, role: generator }\n");
        var errors = new StringWriter();

        Assert.Equal(JobsCli.SetupError, await JobsCli.RunAsync(["run", job], new StringWriter(), errors, _ => null));
        Assert.Contains("nope", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_rejects_unknown_commands_with_exit_code_64()
    {
        Assert.Equal(JobsCli.Usage, await JobsCli.RunAsync(["launch"], new StringWriter(), new StringWriter(), _ => null));
        Assert.Equal(JobsCli.Usage, await JobsCli.RunAsync(["run"], new StringWriter(), new StringWriter(), _ => null));
    }

    [PythonFact]
    public async Task Cli_plugin_check_runs_the_python_plugin()
    {
        var output = new StringWriter();
        var code = await JobsCli.RunAsync(["plugin-check", "--name", "hello-generator", "--version", "0.1.0", "--role", "generator", "--", "python3", Repo.PythonPlugin], output, new StringWriter(), _ => null);

        Assert.Equal(JobsCli.Ok, code);
        Assert.Contains("Handshake OK: hello-generator@0.1.0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("nextBatch(3) returned 3", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Error_report_is_valid_junit()
    {
        var suite = XDocument.Parse(JUnitWriter.WriteError("demo.job", "plugin crashed")).Descendants("testsuite").Single();
        Assert.Equal("1", (string)suite.Attribute("errors")!);
    }

    [Theory]
    [InlineData(typeof(JobRunner))]
    [InlineData(typeof(JobsCli))]
    public void Test_jobs_cannot_reach_a_real_broker(Type type)
    {
        // The runner has no Service Bus (or any other broker SDK) to send with. Sending to real brokers is not built.
        var references = type.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain(references, name => name.StartsWith("Azure.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.Contains("ServiceBus", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Acornsoft.MessageExplorer.Adapters", StringComparison.Ordinal) || name.StartsWith("ME.Adapters", StringComparison.Ordinal));
        Assert.NotNull(Assembly.GetAssembly(type));
    }
}
