// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Model;

namespace Acornsoft.MessageExplorer.Jobs.Loading;

/// <summary>
/// Loads a job file, finds its collection (the nearest <c>collection.yaml</c> above it), loads the environment,
/// resolves variables, and checks everything. All problems in a file are reported together.
/// </summary>
public static partial class JobLoader
{
    public const string InMemoryProvider = "in-memory";

    /// <summary>A real Azure Service Bus namespace, Entra sign-in only. Runs only through <c>me-jobs send</c>.</summary>
    public const string AzureServiceBusProvider = "azure-servicebus";

    public static IReadOnlyList<string> Providers { get; } = [InMemoryProvider, AzureServiceBusProvider];
    public static readonly TimeSpan DefaultStep = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultDrain = TimeSpan.FromHours(24);

    private static readonly string[] CollectionNames = ["collection.yaml", "collection.yml", "collection.json"];

    public static JobDefinition Load(string jobPath, string? environmentName = null, Func<string, string?>? environmentVariables = null)
    {
        environmentVariables ??= Environment.GetEnvironmentVariable;
        var fullJobPath = Path.GetFullPath(jobPath);
        if (!File.Exists(fullJobPath))
            throw new JobValidationException(jobPath, ["the job file was not found"]);

        var collectionPath = FindCollection(fullJobPath)
            ?? throw new JobValidationException(jobPath, ["no collection.yaml was found in this folder or any folder above it"]);
        var collectionDir = Path.GetDirectoryName(collectionPath)!;

        var collection = ReadFile<CollectionFile>(collectionPath);
        var job = ReadFile<JobFile>(fullJobPath);

        CheckCollection(collection, collectionPath);

        var envName = environmentName ?? collection.Defaults?.Environment ?? "local";
        var envPath = FindEnvironment(collectionDir, envName)
            ?? throw new JobValidationException(collectionPath, [$"environment '{envName}' was not found under environments/"]);
        var environment = ReadFile<EnvironmentFile>(envPath);
        CheckEnvironment(environment, envPath);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { collection.Variables, environment.Variables, job.Variables })
        {
            if (source is null)
                continue;
            foreach (var (key, value) in source)
                variables[key] = value;
        }

        var errors = new List<string>();
        string Resolve(string value, string where) => Substitute(value, where, variables, environmentVariables, errors);

        if (job.Seed < 0)
            errors.Add("seed: must be zero or more");

        var start = ParseStart(job.Clock?.Start, errors);
        var step = ParseDuration(job.Clock?.Step, "clock.step", DefaultStep, errors);
        var duration = ParseDuration(job.Clock?.Duration, "clock.duration", DefaultDuration, errors);
        var drain = ParseDuration(job.Clock?.Drain, "clock.drain", DefaultDrain, errors);

        var target = Resolve(job.Target.Send, "target.send");
        if (string.IsNullOrWhiteSpace(target) && errors.Count == 0)
            errors.Add("target.send: name the entity that generated messages go to");

        var bindings = new List<PluginBinding>();
        if (job.Plugins.Count == 0)
            errors.Add("plugins: list at least one plugin");

        for (var i = 0; i < job.Plugins.Count; i++)
        {
            var reference = job.Plugins[i];
            var where = $"plugins[{i}]";
            var roles = new HashSet<PluginRole>();
            var roleNames = (reference.Roles ?? []).Concat(reference.Role is null ? [] : [reference.Role]).ToList();
            if (roleNames.Count == 0)
                errors.Add($"{where}: give a role (generator, simulator, assertion, or observer)");
            foreach (var roleName in roleNames)
            {
                if (Enum.TryParse<PluginRole>(roleName, ignoreCase: true, out var role) && Enum.IsDefined(role))
                    roles.Add(role);
                else
                    errors.Add($"{where}.role: '{roleName}' is not a role. Use generator, simulator, assertion, or observer");
            }

            var match = UseRegex().Match(reference.Use ?? string.Empty);
            if (!match.Success)
            {
                errors.Add($"{where}.use: '{reference.Use}' must be name@major.minor.patch, for example erp-standin@0.1.0");
                continue;
            }

            var name = match.Groups["name"].Value;
            var version = match.Groups["version"].Value;
            if (!collection.Plugins.TryGetValue(name, out var declaration))
            {
                errors.Add($"{where}.use: plugin '{name}' is not declared in the collection");
                continue;
            }

            if (!string.Equals(declaration.Version, version, StringComparison.Ordinal))
            {
                errors.Add($"{where}.use: the job pins {name}@{version} but the collection declares {name}@{declaration.Version}");
                continue;
            }

            var config = (JsonObject)SubstituteNode(reference.Config?.DeepClone() ?? new JsonObject(), $"{where}.config", Resolve)!;
            var command = declaration.Builtin ? [] : declaration.Command!.Select((part, n) => Resolve(part, $"plugins.{name}.command[{n}]")).ToList();
            var send = reference.Send is null ? null : Resolve(reference.Send, $"{where}.send");
            if (send is not null && !roles.Contains(PluginRole.Generator))
                errors.Add($"{where}.send: only a generator sends");
            bindings.Add(new PluginBinding(name, version, roles, command, declaration.Builtin, collectionDir, config, send));
        }

        if (bindings.Count > 0 && !bindings.Any(binding => binding.Roles.Contains(PluginRole.Generator)))
            errors.Add("plugins: at least one plugin must have the generator role");

        string? ns = null;
        Live.LiveSendOptions? live = null;
        if (environment.Provider == AzureServiceBusProvider)
        {
            ns = environment.Namespace is null ? null : Resolve(environment.Namespace, "namespace");
            if (string.IsNullOrWhiteSpace(ns))
                errors.Add($"namespace: provider {AzureServiceBusProvider} needs the namespace to send to (environment {envName})");
            live = new Live.LiveSendOptions
            {
                PerMinute = job.Rate?.PerMinute ?? new Live.LiveSendOptions().PerMinute,
                MaxMessages = job.MaxMessages ?? Live.LiveSendOptions.HardCap,
                TimeToLive = ParseDuration(job.Ttl, "ttl", Live.LiveSendOptions.DefaultTimeToLive, errors),
            };
            errors.AddRange(live.Problems());
        }

        if (errors.Count > 0)
            throw new JobValidationException(jobPath, errors);

        return new JobDefinition(
            collection.Collection,
            job.Job,
            fullJobPath,
            collectionDir,
            envName,
            environment.Provider,
            job.Seed,
            start,
            step,
            duration,
            drain,
            target,
            bindings,
            variables,
            ns,
            live);
    }

    private static T ReadFile<T>(string path)
    {
        var name = Path.GetFileName(path);
        return YamlJson.Bind<T>(YamlJson.Parse(File.ReadAllText(path), name), name);
    }

    private static string? FindCollection(string jobPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(jobPath)!);
        while (dir is not null)
        {
            foreach (var candidate in CollectionNames)
            {
                var path = Path.Combine(dir.FullName, candidate);
                if (File.Exists(path))
                    return path;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? FindEnvironment(string collectionDir, string name)
    {
        if (!EnvironmentNameRegex().IsMatch(name))
            return null;
        foreach (var extension in new[] { ".yaml", ".yml", ".json" })
        {
            var path = Path.Combine(collectionDir, "environments", name + extension);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static void CheckCollection(CollectionFile collection, string path)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(collection.Collection))
            errors.Add("collection: give the collection a name");
        foreach (var (name, declaration) in collection.Plugins)
        {
            if (!NameRegex().IsMatch(name))
                errors.Add($"plugins.{name}: names use lower-case letters, digits, and hyphens");
            if (!VersionRegex().IsMatch(declaration.Version ?? string.Empty))
                errors.Add($"plugins.{name}.version: '{declaration.Version}' must be major.minor.patch");
            var hasCommand = declaration.Command is { Count: > 0 };
            if (declaration.Builtin == hasCommand)
                errors.Add($"plugins.{name}: give either a command or builtin: true, not both and not neither");
            if (declaration.Builtin && !BuiltinPlugins.IsKnown(name, declaration.Version ?? string.Empty))
                errors.Add($"plugins.{name}: there is no built-in plugin {name}@{declaration.Version}. Built-ins: {string.Join(", ", BuiltinPlugins.Known.Select(k => $"{k.Key}@{k.Value}"))}");
        }

        if (errors.Count > 0)
            throw new JobValidationException(Path.GetFileName(path), errors);
    }

    private static void CheckEnvironment(EnvironmentFile environment, string path)
    {
        if (!Providers.Contains(environment.Provider, StringComparer.Ordinal))
        {
            throw new JobValidationException(Path.GetFileName(path),
            [
                $"provider: '{environment.Provider}' is not supported. Use '{InMemoryProvider}' (test jobs) or '{AzureServiceBusProvider}' (live sends through me-jobs send). The Service Bus adapter stays read-only.",
            ]);
        }

        if (environment.Provider == InMemoryProvider && environment.Namespace is not null)
            throw new JobValidationException(Path.GetFileName(path), [$"namespace: only provider {AzureServiceBusProvider} takes a namespace"]);
    }

    private static DateTimeOffset ParseStart(string? value, List<string> errors)
    {
        if (value is null)
            return new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            return start;
        errors.Add($"clock.start: '{value}' is not a date and time such as 2026-03-02T09:00:00Z");
        return default;
    }

    private static TimeSpan ParseDuration(string? value, string where, TimeSpan fallback, List<string> errors)
    {
        if (value is null)
            return fallback;
        try
        {
            var parsed = XmlConvert.ToTimeSpan(value);
            if (parsed > TimeSpan.Zero)
                return parsed;
        }
        catch (FormatException)
        {
        }

        errors.Add($"{where}: '{value}' is not a positive ISO 8601 duration such as PT5M");
        return fallback;
    }

    private static string Substitute(string value, string where, IReadOnlyDictionary<string, string> variables, Func<string, string?> environment, List<string> errors) =>
        PlaceholderRegex().Replace(value, match =>
        {
            var key = match.Groups["key"].Value;
            if (key.StartsWith("env.", StringComparison.Ordinal))
            {
                var fromEnvironment = environment(key[4..]);
                if (fromEnvironment is not null)
                    return fromEnvironment;
                errors.Add($"{where}: environment variable {key[4..]} is not set");
                return match.Value;
            }

            if (variables.TryGetValue(key, out var resolved))
                return resolved;
            errors.Add($"{where}: variable '{key}' is not defined in the job, environment, or collection");
            return match.Value;
        });

    private static JsonNode? SubstituteNode(JsonNode? node, string where, Func<string, string, string> resolve)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                    obj[key] = SubstituteNode(obj[key]?.DeepClone(), $"{where}.{key}", resolve);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = SubstituteNode(array[i]?.DeepClone(), $"{where}[{i}]", resolve);
                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(resolve(text, where));
            default:
                return node;
        }
    }

    [GeneratedRegex(@"^(?<name>[a-z0-9][a-z0-9-]*)@(?<version>\d+\.\d+\.\d+)$")]
    private static partial Regex UseRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex EnvironmentNameRegex();

    [GeneratedRegex(@"\{\{\s*(?<key>[A-Za-z0-9_.-]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
