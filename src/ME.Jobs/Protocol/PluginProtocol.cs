// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Model;

namespace Acornsoft.MessageExplorer.Jobs.Protocol;

/// <summary>Constants and wire shapes for plugin protocol version 1 (JSON-RPC 2.0, one JSON object per line).</summary>
public static class PluginProtocol
{
    public const string Version = "lfme-plugin/1.0";
    public const string MajorPrefix = "lfme-plugin/1.";

    public static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string RoleName(PluginRole role) => role.ToString().ToLowerInvariant();
}

public sealed record PluginInfo(string Name, string Version, string Protocol, IReadOnlySet<PluginRole> Roles, JsonObject Capabilities)
{
    /// <summary>Entities a simulator listens on, from <c>capabilities.subscribe</c>.</summary>
    public IReadOnlyList<string> Subscriptions =>
        Capabilities["subscribe"] is JsonArray array
            ? array.Select(node => node?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0).ToList()
            : [];
}

/// <summary>A message a plugin wants sent. <see cref="To"/> null means the job's target entity.</summary>
public sealed record OutboundMessage(string? To, string CorrelationId, JsonObject Body, long DelayMs);

/// <summary>A message the in-memory broker holds.</summary>
public sealed record BusMessage(string Entity, long Sequence, string CorrelationId, DateTimeOffset AvailableAt, JsonObject Body)
{
    public JsonObject ToWire() => new()
    {
        ["entity"] = Entity,
        ["sequence"] = Sequence,
        ["correlationId"] = CorrelationId,
        ["enqueuedAt"] = PluginProtocol.FormatTime(AvailableAt),
        ["body"] = Body.DeepClone(),
    };
}

/// <summary>Something an observer saw on its sink.</summary>
public sealed record Observation(string Sink, string CorrelationId, DateTimeOffset At, JsonObject Body)
{
    public JsonObject ToWire() => new()
    {
        ["sink"] = Sink,
        ["correlationId"] = CorrelationId,
        ["at"] = PluginProtocol.FormatTime(At),
        ["body"] = Body.DeepClone(),
    };
}

public enum VerdictStatus
{
    Pass,
    Fail,
    Pending,
}

public sealed record Verdict(VerdictStatus Status, string Reason);
