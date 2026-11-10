// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

/// <summary>Parses plugin results into typed values, with clear protocol errors.</summary>
internal static class WireParse
{
    public static PluginInfo Handshake(string plugin, JsonNode? result, string expectedName, string expectedVersion, IReadOnlySet<PluginRole> requiredRoles)
    {
        if (result is not JsonObject obj)
            throw new PluginHandshakeException(plugin, "initialize must return an object");

        var protocol = Text(obj, "protocol");
        if (protocol is null || !protocol.StartsWith(PluginProtocol.MajorPrefix, StringComparison.Ordinal))
            throw new PluginHandshakeException(plugin, $"speaks protocol '{protocol ?? "(none)"}', but this runner speaks {PluginProtocol.Version}. The major versions must match.");

        var name = Text(obj, "name");
        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            throw new PluginHandshakeException(plugin, $"reports name '{name ?? "(none)"}', expected '{expectedName}'.");

        var version = Text(obj, "version");
        if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
            throw new PluginHandshakeException(plugin, $"reports version {version ?? "(none)"}, but the job pins {expectedVersion}. Update the pin or install {expectedName}@{expectedVersion}.");

        var roles = new HashSet<PluginRole>();
        if (obj["roles"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var roleName) && Enum.TryParse<PluginRole>(roleName, true, out var role))
                    roles.Add(role);
            }
        }

        var missing = requiredRoles.Where(role => !roles.Contains(role)).ToList();
        if (missing.Count > 0)
            throw new PluginHandshakeException(plugin, $"does not offer the role{(missing.Count == 1 ? string.Empty : "s")} {string.Join(", ", missing.Select(PluginProtocol.RoleName))} that the job asks for.");

        var capabilities = obj["capabilities"] as JsonObject ?? new JsonObject();
        return new PluginInfo(name!, version!, protocol, roles, (JsonObject)capabilities.DeepClone());
    }

    public static IReadOnlyList<OutboundMessage> Messages(string plugin, JsonNode? result, string arrayName)
    {
        if (result is not JsonObject obj)
            throw new PluginProtocolException(plugin, $"result must be an object with '{arrayName}'");
        if (obj[arrayName] is null)
            return [];
        if (obj[arrayName] is not JsonArray array)
            throw new PluginProtocolException(plugin, $"'{arrayName}' must be a list");

        var messages = new List<OutboundMessage>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject message)
                throw new PluginProtocolException(plugin, $"each item in '{arrayName}' must be an object");
            var correlationId = Text(message, "correlationId");
            if (string.IsNullOrWhiteSpace(correlationId))
                throw new PluginProtocolException(plugin, $"a message in '{arrayName}' has no correlationId");
            var delay = message["delayMs"] is JsonValue d && d.TryGetValue<long>(out var ms) ? ms : 0;
            if (delay < 0)
                throw new PluginProtocolException(plugin, "delayMs must be zero or more");
            var body = message["body"] as JsonObject ?? new JsonObject();
            messages.Add(new OutboundMessage(Text(message, "to"), correlationId, (JsonObject)body.DeepClone(), delay));
        }

        return messages;
    }

    public static IReadOnlyList<JsonObject> Expectations(string plugin, JsonNode? result, string correlationId)
    {
        if (result is not JsonObject obj || obj["expectations"] is not JsonArray array)
            return [];
        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item is not JsonObject expectation)
                throw new PluginProtocolException(plugin, "each expectation must be an object");
            var copy = (JsonObject)expectation.DeepClone();
            copy["correlationId"] ??= correlationId;
            list.Add(copy);
        }

        return list;
    }

    public static IReadOnlyList<Observation> Observations(string plugin, JsonNode? result)
    {
        if (result is not JsonObject obj || obj["observations"] is not JsonArray array)
            return [];
        var list = new List<Observation>();
        foreach (var item in array)
        {
            if (item is not JsonObject o)
                throw new PluginProtocolException(plugin, "each observation must be an object");
            var sink = Text(o, "sink") ?? throw new PluginProtocolException(plugin, "an observation has no sink");
            var correlationId = Text(o, "correlationId") ?? throw new PluginProtocolException(plugin, "an observation has no correlationId");
            var at = Text(o, "at") ?? throw new PluginProtocolException(plugin, "an observation has no time");
            list.Add(new Observation(sink, correlationId, PluginProtocol.ParseTime(at), (JsonObject)(o["body"] as JsonObject ?? new JsonObject()).DeepClone()));
        }

        return list;
    }

    public static Verdict Verdict(string plugin, JsonNode? result)
    {
        if (result is not JsonObject obj)
            throw new PluginProtocolException(plugin, "assert must return an object with status and reason");
        var status = Text(obj, "status");
        var reason = Text(obj, "reason") ?? string.Empty;
        return status switch
        {
            "pass" => new Verdict(VerdictStatus.Pass, reason),
            "fail" => new Verdict(VerdictStatus.Fail, reason),
            "pending" => new Verdict(VerdictStatus.Pending, reason),
            _ => throw new PluginProtocolException(plugin, $"assert status '{status}' is not pass, fail, or pending"),
        };
    }

    private static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
