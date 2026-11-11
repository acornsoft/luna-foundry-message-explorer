// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;

namespace Acornsoft.MessageExplorer.Jobs.Model;

// File shapes. These bind straight from YAML or JSON. Unknown keys are errors.

public sealed class CollectionFile
{
    public required string Collection { get; init; }

    public string? Description { get; init; }

    public CollectionDefaults? Defaults { get; init; }

    public Dictionary<string, string>? Variables { get; init; }

    public required Dictionary<string, PluginDeclaration> Plugins { get; init; }
}

public sealed class CollectionDefaults
{
    public string? Environment { get; init; }
}

public sealed class PluginDeclaration
{
    public required string Version { get; init; }

    public List<string>? Command { get; init; }

    public bool Builtin { get; init; }
}

public sealed class EnvironmentFile
{
    public required string Provider { get; init; }

    /// <summary>For provider azure-servicebus: the namespace to send to. It must also be on the local live allow-list.</summary>
    public string? Namespace { get; init; }

    public Dictionary<string, string>? Variables { get; init; }
}

public sealed class JobFile
{
    public required string Job { get; init; }

    public string? Description { get; init; }

    public required long Seed { get; init; }

    public ClockSpec? Clock { get; init; }

    public required TargetSpec Target { get; init; }

    public Dictionary<string, string>? Variables { get; init; }

    public required List<JobPluginRef> Plugins { get; init; }

    /// <summary>Live sends only: wall-clock pace.</summary>
    public RateSpec? Rate { get; init; }

    /// <summary>Live sends only: the most messages one run may send (1 to 500).</summary>
    public int? MaxMessages { get; init; }

    /// <summary>Live sends only: time-to-live on every message, ISO 8601 (default PT1H).</summary>
    public string? Ttl { get; init; }
}

public sealed class RateSpec
{
    public int? PerMinute { get; init; }
}

public sealed class ClockSpec
{
    public string? Start { get; init; }

    public string? Step { get; init; }

    public string? Duration { get; init; }

    public string? Drain { get; init; }
}

public sealed class TargetSpec
{
    public required string Send { get; init; }
}

public sealed class JobPluginRef
{
    public required string Use { get; init; }

    public string? Role { get; init; }

    public List<string>? Roles { get; init; }

    public JsonObject? Config { get; init; }

    /// <summary>A generator's own target entity; defaults to target.send.</summary>
    public string? Send { get; init; }
}

// Resolved shapes. Everything is checked, typed, and variable-free.

public enum PluginRole
{
    Generator,
    Simulator,
    Assertion,
    Observer,
}

public sealed record PluginBinding(
    string Name,
    string Version,
    IReadOnlySet<PluginRole> Roles,
    IReadOnlyList<string> Command,
    bool Builtin,
    string WorkingDirectory,
    JsonObject Config,
    string? Send = null);

public sealed record JobDefinition(
    string CollectionName,
    string JobName,
    string JobPath,
    string CollectionDirectory,
    string EnvironmentName,
    string Provider,
    long Seed,
    DateTimeOffset ClockStart,
    TimeSpan Step,
    TimeSpan Duration,
    TimeSpan Drain,
    string TargetEntity,
    IReadOnlyList<PluginBinding> Plugins,
    IReadOnlyDictionary<string, string> Variables,
    string? Namespace = null,
    global::Acornsoft.MessageExplorer.Jobs.Live.LiveSendOptions? Live = null)
{
    public string SuiteName => $"{CollectionName}.{JobName}";

    public string RunId => $"{CollectionName}.{JobName}.seed-{Seed}";
}
