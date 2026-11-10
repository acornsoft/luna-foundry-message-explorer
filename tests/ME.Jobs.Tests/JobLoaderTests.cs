// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

public sealed class JobLoaderTests
{
    private const string Collection = """
        collection: demo
        defaults:
          environment: local
        variables:
          target: from.collection
          shared: collection
        plugins:
          gen:
            version: "1.2.3"
            command: ["python3", "gen.py", "--name", "{{ who }}"]
          bus-observer:
            version: 1.0.0
            builtin: true
        """;

    private const string LocalEnvironment = """
        provider: in-memory
        variables:
          shared: environment
          who: local-user
        """;

    private static TempCollection NewCollection(string? collection = null, string? environment = null)
    {
        var temp = new TempCollection();
        temp.Write("collection.yaml", collection ?? Collection);
        temp.Write("environments/local.yaml", environment ?? LocalEnvironment);
        return temp;
    }

    private static JobValidationException LoadFails(TempCollection temp, string job, Func<string, string?>? env = null)
    {
        var path = temp.Write("jobs/bad.job.yaml", job);
        return Assert.Throws<JobValidationException>(() => JobLoader.Load(path, null, env ?? (_ => null)));
    }

    [Fact]
    public void Loads_the_erp_sample_with_variables_resolved()
    {
        var job = JobLoader.Load(Repo.ErpJob, null, _ => null);

        Assert.Equal("erp-standin.erp-day", job.SuiteName);
        Assert.Equal("erp-standin.erp-day.seed-424242", job.RunId);
        Assert.Equal("local", job.EnvironmentName);
        Assert.Equal(JobLoader.InMemoryProvider, job.Provider);
        Assert.Equal(424242, job.Seed);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero), job.ClockStart);
        Assert.Equal(TimeSpan.FromMinutes(5), job.Step);
        Assert.Equal(TimeSpan.FromHours(8), job.Duration);
        Assert.Equal(TimeSpan.FromHours(24), job.Drain);
        Assert.Equal("orders.created", job.TargetEntity);

        Assert.Equal(["order-generator", "erp-standin", "bus-observer"], job.Plugins.Select(p => p.Name));
        var generator = job.Plugins[0];
        Assert.Equal("0.2.0", generator.Version);
        Assert.True(generator.Roles.SetEquals([PluginRole.Generator, PluginRole.Assertion]));
        Assert.Equal(40, generator.Config["count"]!.GetValue<int>());

        var erp = job.Plugins[1];
        Assert.Equal("orders.created", erp.Config["subscribe"]!.GetValue<string>());
        Assert.Equal("PT2H", erp.Config["shipDelay"]!.GetValue<string>());
        Assert.Equal(0.05, erp.Config["failureRate"]!.GetValue<double>());
        Assert.Equal("dotnet", erp.Command[0]);
        Assert.True(Path.IsPathRooted(erp.WorkingDirectory));

        Assert.True(job.Plugins[2].Builtin);
    }

    [Fact]
    public void Job_variables_win_over_environment_and_collection()
    {
        using var temp = NewCollection();
        var path = temp.Write("jobs/ok.job.yaml", """
            job: ok
            seed: 1
            variables:
              target: from.job
            target:
              send: "{{ target }}-{{ shared }}"
            plugins:
              - use: gen@1.2.3
                role: generator
            """);

        var job = JobLoader.Load(path, null, _ => null);

        Assert.Equal("from.job-environment", job.TargetEntity);
        Assert.Equal(["python3", "gen.py", "--name", "local-user"], job.Plugins[0].Command);
        Assert.Equal("from.job", job.Variables["target"]);
    }

    [Fact]
    public void Environment_variables_come_from_the_process_by_name()
    {
        using var temp = NewCollection();
        var path = temp.Write("jobs/ok.job.yaml", """
            job: ok
            seed: 3
            target: { send: "orders-{{ env.LANE }}" }
            plugins:
              - { use: gen@1.2.3, role: generator }
            """);

        var job = JobLoader.Load(path, null, name => name == "LANE" ? "blue" : null);

        Assert.Equal("orders-blue", job.TargetEntity);
    }

    [Fact]
    public void Picks_the_named_environment()
    {
        using var temp = NewCollection();
        temp.Write("environments/ci.yaml", "provider: in-memory\nvariables:\n  who: ci-user\n");
        var path = temp.Write("jobs/ok.job.yaml", "job: ok\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: gen@1.2.3, role: generator }\n");

        var job = JobLoader.Load(path, "ci", _ => null);

        Assert.Equal("ci", job.EnvironmentName);
        Assert.Equal("ci-user", job.Plugins[0].Command[^1]);
    }

    [Fact]
    public void Collects_every_error_in_one_report()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, """
            job: bad
            seed: -1
            clock:
              step: five minutes
            target: { send: "{{ nowhere }}" }
            plugins:
              - use: gen@9.9.9
                role: generator
              - use: missing@1.0.0
                role: observer
              - use: gen
                role: wizard
            """);

        Assert.Contains(error.Errors, e => e.Contains("seed", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("clock.step", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("nowhere", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("9.9.9", StringComparison.Ordinal) && e.Contains("1.2.3", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("name@major.minor.patch", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("wizard", StringComparison.Ordinal));
        Assert.True(error.Errors.Count >= 7);
        Assert.Contains("bad.job.yaml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_are_errors_with_a_path()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, "job: x\nseed: 1\ntarget: { send: q, sned: r }\nplugins:\n  - { use: gen@1.2.3, role: generator }\n");

        Assert.Contains(error.Errors, e => e.Contains("sned", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_required_fields_are_errors()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, "job: x\nplugins:\n  - { use: gen@1.2.3, role: generator }\n");

        Assert.Contains(error.Errors, e => e.Contains("seed", StringComparison.Ordinal));
    }

    [Fact]
    public void Yaml_syntax_errors_name_the_line()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, "job: x\nseed: 1\ntarget: [unclosed\n");

        Assert.Contains(error.Errors, e => e.Contains("line", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_job_needs_a_generator()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, "job: x\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: bus-observer@1.0.0, role: observer, config: { sinks: [q] } }\n");

        Assert.Contains(error.Errors, e => e.Contains("generator", StringComparison.Ordinal));
    }

    [Fact]
    public void Undefined_environment_variables_are_errors()
    {
        using var temp = NewCollection();
        var error = LoadFails(temp, "job: x\nseed: 1\ntarget: { send: \"{{ env.NOT_SET }}\" }\nplugins:\n  - { use: gen@1.2.3, role: generator }\n");

        Assert.Contains(error.Errors, e => e.Contains("NOT_SET", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_broker_providers_are_refused()
    {
        using var temp = NewCollection(environment: "provider: azure-service-bus\n");
        var error = LoadFails(temp, "job: x\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: gen@1.2.3, role: generator }\n");

        Assert.Contains(error.Errors, e => e.Contains("azure-service-bus", StringComparison.Ordinal) && e.Contains("in-memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_declarations_need_a_command_or_builtin_but_not_both()
    {
        using var temp = NewCollection(collection: """
            collection: demo
            plugins:
              both: { version: 1.0.0, builtin: true, command: [x] }
              neither: { version: 1.0.0 }
              made-up: { version: 1.0.0, builtin: true }
            """);
        var error = LoadFails(temp, """
            job: x
            seed: 1
            target: { send: q }
            plugins:
              - { use: both@1.0.0, role: generator }
              - { use: neither@1.0.0, role: generator }
              - { use: made-up@1.0.0, role: generator }
            """);

        Assert.Contains(error.Errors, e => e.Contains("both", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("neither", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("made-up", StringComparison.Ordinal));
    }

    [Fact]
    public void A_job_outside_a_collection_is_an_error()
    {
        var dir = Directory.CreateTempSubdirectory("lfme-orphan-").FullName;
        try
        {
            var path = Path.Combine(dir, "lonely.job.yaml");
            File.WriteAllText(path, "job: x\nseed: 1\n");
            var error = Assert.Throws<JobValidationException>(() => JobLoader.Load(path, null, _ => null));
            Assert.Contains(error.Errors, e => e.Contains("collection.yaml", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Missing_job_file_is_an_error()
    {
        var error = Assert.Throws<JobValidationException>(() => JobLoader.Load("/no/such/file.job.yaml", null, _ => null));
        Assert.NotEmpty(error.Errors);
    }
}
