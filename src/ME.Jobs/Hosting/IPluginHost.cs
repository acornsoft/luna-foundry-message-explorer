// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

/// <summary>
/// One running plugin, whatever its language or transport. The runner owns the broker. A plugin only sees
/// what the runner hands it and returns messages and verdicts for the runner to act on.
/// </summary>
public interface IPluginHost : IAsyncDisposable
{
    PluginInfo Info { get; }

    /// <summary>Notifications the plugin sent with method <c>event</c> (for example throttled, retry, deadLettered).</summary>
    IReadOnlyCollection<System.Text.Json.Nodes.JsonObject> Events => [];

    /// <summary>Generator: messages due by <paramref name="now"/> on the simulated clock.</summary>
    Task<IReadOnlyList<OutboundMessage>> TickAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Generator: the next batch of up to <paramref name="max"/> messages, for count-driven jobs.</summary>
    Task<IReadOnlyList<OutboundMessage>> NextBatchAsync(int max, CancellationToken cancellationToken);

    /// <summary>Simulator: a message arrived on a subscribed entity. Returns replies, each with an optional delay.</summary>
    Task<IReadOnlyList<OutboundMessage>> DeliverAsync(BusMessage message, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Assertion: what should happen because this message was sent.</summary>
    Task<IReadOnlyList<JsonObject>> ExpectAsync(BusMessage sent, CancellationToken cancellationToken);

    /// <summary>Observer: what the plugin saw on its sink up to <paramref name="now"/>.</summary>
    Task<IReadOnlyList<Observation>> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Assertion: compare expected and actual for one correlation ID.</summary>
    Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<JsonObject> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken);
}

public sealed record PluginInitialize(
    string RunId,
    long Seed,
    DateTimeOffset ClockStart,
    JsonObject Config,
    IReadOnlyDictionary<string, string> Variables)
{
    public JsonObject ToWire()
    {
        var variables = new JsonObject();
        foreach (var (key, value) in Variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            variables[key] = value;
        return new JsonObject
        {
            ["protocol"] = PluginProtocol.Version,
            ["runId"] = RunId,
            ["seed"] = Seed,
            ["clockStart"] = PluginProtocol.FormatTime(ClockStart),
            ["config"] = Config.DeepClone(),
            ["variables"] = variables,
        };
    }
}

public class PluginException : Exception
{
    public PluginException(string plugin, string message, Exception? innerException = null)
        : base($"Plugin '{plugin}': {message}", innerException)
    {
        Plugin = plugin;
    }

    public string Plugin { get; }
}

public sealed class PluginHandshakeException(string plugin, string message) : PluginException(plugin, message);

public sealed class PluginTimeoutException(string plugin, string method, TimeSpan timeout)
    : PluginException(plugin, $"no answer to '{method}' within {timeout.TotalSeconds:0.#} s. The plugin was stopped.");

public sealed class PluginCrashedException(string plugin, int? exitCode, string stderrTail)
    : PluginException(plugin, $"the plugin process exited{(exitCode is null ? string.Empty : $" with code {exitCode}")}." + (string.IsNullOrWhiteSpace(stderrTail) ? string.Empty : $" Last stderr: {stderrTail}"))
{
    public int? ExitCode { get; } = exitCode;
}

public sealed class PluginProtocolException(string plugin, string message) : PluginException(plugin, message);

public sealed class PluginCallException(string plugin, string method, int code, string message)
    : PluginException(plugin, $"'{method}' failed with error {code}: {message}")
{
    public int Code { get; } = code;
}
