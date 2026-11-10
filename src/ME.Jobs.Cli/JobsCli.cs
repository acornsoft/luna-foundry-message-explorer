// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Cli;

/// <summary>
/// <c>me-jobs run &lt;job&gt;</c> runs a test job headless. Exit codes: 0 all passed, 1 an assertion failed,
/// 2 a setup or plugin error, 64 bad arguments.
/// </summary>
public static class JobsCli
{
    public const int Ok = 0;
    public const int AssertionFailed = 1;
    public const int SetupError = 2;
    public const int Usage = 64;

    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?> environment)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await stdout.WriteLineAsync(HelpText).ConfigureAwait(false);
            return args.Length == 0 ? Usage : Ok;
        }

        return args[0] switch
        {
            "run" => await RunJobAsync(args[1..], stdout, stderr, environment).ConfigureAwait(false),
            "plugin-check" => await PluginCheckAsync(args[1..], stdout, stderr).ConfigureAwait(false),
            _ => await UsageError(stderr, $"Unknown command '{args[0]}'.").ConfigureAwait(false),
        };
    }

    public const string HelpText = """
        me-jobs: run synthetic test jobs against the in-memory broker.

          me-jobs run <job-file> [--env <name>] [--junit <path>] [--verbose]
          me-jobs plugin-check --name <name> --version <x.y.z> [--role <role>] -- <command> [args...]

        Exit codes: 0 passed, 1 an assertion failed, 2 setup or plugin error, 64 bad arguments.
        """;

    private static async Task<int> RunJobAsync(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?> environment)
    {
        string? jobPath = null, envName = null, junitPath = null;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--env" when i + 1 < args.Length:
                    envName = args[++i];
                    break;
                case "--junit" when i + 1 < args.Length:
                    junitPath = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || jobPath is not null)
                        return await UsageError(stderr, $"Unexpected argument '{args[i]}'.").ConfigureAwait(false);
                    jobPath = args[i];
                    break;
            }
        }

        if (jobPath is null)
            return await UsageError(stderr, "Give the job file to run.").ConfigureAwait(false);

        JobDefinition job;
        try
        {
            job = JobLoader.Load(jobPath, envName, environment);
        }
        catch (JobValidationException exception)
        {
            await stderr.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return SetupError;
        }

        RunResult result;
        try
        {
            result = await new JobRunner().RunAsync(job).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PluginException or JobRunException)
        {
            await stderr.WriteLineAsync($"Run {job.RunId} stopped: {exception.Message}").ConfigureAwait(false);
            if (junitPath is not null)
                await WriteFileAsync(junitPath, JUnitWriter.WriteError(job.SuiteName, exception.Message)).ConfigureAwait(false);
            return SetupError;
        }

        if (junitPath is not null)
            await WriteFileAsync(junitPath, JUnitWriter.Write(result)).ConfigureAwait(false);

        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Run {job.RunId} ({job.EnvironmentName}): {result.SentCount} sent, {result.Records.Count} checked, {result.Passed} passed, {result.Failed} failed.")).ConfigureAwait(false);
        foreach (var record in result.Records)
        {
            if (verbose || record.Verdict.Status != VerdictStatus.Pass)
            {
                await stdout.WriteLineAsync($"  {(record.Verdict.Status == VerdictStatus.Pass ? "PASS" : "FAIL")} {record.CorrelationId}: {record.Verdict.Reason}").ConfigureAwait(false);
                if (verbose)
                {
                    foreach (var line in JUnitWriter.Describe(record).Split('\n'))
                        await stdout.WriteLineAsync("       " + line).ConfigureAwait(false);
                }
            }
        }

        if (junitPath is not null)
            await stdout.WriteLineAsync($"JUnit: {junitPath}").ConfigureAwait(false);
        return result.Succeeded ? Ok : AssertionFailed;
    }

    private static async Task<int> PluginCheckAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var split = Array.IndexOf(args, "--");
        if (split < 0 || split == args.Length - 1)
            return await UsageError(stderr, "Put the plugin command after --.").ConfigureAwait(false);

        string? name = null, version = null;
        var roles = new HashSet<PluginRole>();
        for (var i = 0; i < split; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < split:
                    name = args[++i];
                    break;
                case "--version" when i + 1 < split:
                    version = args[++i];
                    break;
                case "--role" when i + 1 < split && Enum.TryParse<PluginRole>(args[i + 1], true, out var role):
                    roles.Add(role);
                    i++;
                    break;
                default:
                    return await UsageError(stderr, $"Unexpected argument '{args[i]}'.").ConfigureAwait(false);
            }
        }

        if (name is null || version is null)
            return await UsageError(stderr, "Give --name and --version to pin the plugin.").ConfigureAwait(false);

        var command = args[(split + 1)..];
        var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        try
        {
            await using var host = await StdioJsonRpcPluginHost.StartAsync(
                name, version, command, Directory.GetCurrentDirectory(), roles,
                new PluginInitialize("plugin-check", 1, start, new System.Text.Json.Nodes.JsonObject(), new Dictionary<string, string>())).ConfigureAwait(false);
            await stdout.WriteLineAsync($"Handshake OK: {host.Info.Name}@{host.Info.Version}, {host.Info.Protocol}, roles {string.Join(", ", host.Info.Roles.Select(PluginProtocol.RoleName))}").ConfigureAwait(false);
            if (host.Info.Roles.Contains(PluginRole.Generator))
            {
                var batch = await host.NextBatchAsync(3, CancellationToken.None).ConfigureAwait(false);
                await stdout.WriteLineAsync($"nextBatch(3) returned {batch.Count} message(s):").ConfigureAwait(false);
                foreach (var message in batch)
                    await stdout.WriteLineAsync($"  {message.CorrelationId} {message.Body.ToJsonString()}").ConfigureAwait(false);
            }

            return Ok;
        }
        catch (PluginException exception)
        {
            await stderr.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return SetupError;
        }
    }

    private static async Task WriteFileAsync(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static async Task<int> UsageError(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync(message).ConfigureAwait(false);
        await stderr.WriteLineAsync(HelpText).ConfigureAwait(false);
        return Usage;
    }
}
