// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Bus;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

/// <summary>Plugins that ship with the runner and run in-process. They follow the same contract as any other plugin.</summary>
public static class BuiltinPlugins
{
    public const string BusObserver = "bus-observer";
    public const string ObservedAssertion = "observed-assertion";

    public static IReadOnlyDictionary<string, string> Known { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [BusObserver] = "1.0.0",
        [ObservedAssertion] = "1.0.0",
    };

    public static bool IsKnown(string name, string version) =>
        Known.TryGetValue(name, out var known) && known == version;

    public static IPluginHost Create(PluginBinding binding, InMemoryBus bus, string targetEntity)
    {
        if (!IsKnown(binding.Name, binding.Version))
            throw new PluginException(binding.Name, $"there is no built-in plugin {binding.Name}@{binding.Version}.");
        IPluginHost host = binding.Name switch
        {
            BusObserver => new BusObserverPlugin(bus, binding.Config),
            ObservedAssertion => new ObservedAssertionPlugin(binding.Config, targetEntity),
            _ => throw new PluginException(binding.Name, "unknown built-in."),
        };
        var missing = binding.Roles.Where(role => !host.Info.Roles.Contains(role)).ToList();
        if (missing.Count > 0)
            throw new PluginHandshakeException(binding.Name, $"does not offer the role {string.Join(", ", missing.Select(PluginProtocol.RoleName))}.");
        return host;
    }
}

/// <summary>Base for in-process plugins. Roles a plugin does not offer refuse the call.</summary>
public abstract class InProcessPlugin : IPluginHost
{
    public abstract PluginInfo Info { get; }

    public virtual Task<IReadOnlyList<OutboundMessage>> TickAsync(DateTimeOffset now, CancellationToken cancellationToken) => Refuse<IReadOnlyList<OutboundMessage>>("tick");

    public virtual Task<IReadOnlyList<OutboundMessage>> NextBatchAsync(int max, CancellationToken cancellationToken) => Refuse<IReadOnlyList<OutboundMessage>>("nextBatch");

    public virtual Task<IReadOnlyList<OutboundMessage>> DeliverAsync(BusMessage message, DateTimeOffset now, CancellationToken cancellationToken) => Refuse<IReadOnlyList<OutboundMessage>>("deliver");

    public virtual Task<IReadOnlyList<JsonObject>> ExpectAsync(BusMessage sent, CancellationToken cancellationToken) => Refuse<IReadOnlyList<JsonObject>>("expect");

    public virtual Task<IReadOnlyList<Observation>> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken) => Refuse<IReadOnlyList<Observation>>("observe");

    public virtual Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<JsonObject> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken) => Refuse<Verdict>("assert");

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<T> Refuse<T>(string method) =>
        Task.FromException<T>(new PluginCallException(Info.Name, method, -32601, "method not offered by this plugin"));

    protected static PluginInfo Describe(string name, string version, params PluginRole[] roles) =>
        new(name, version, PluginProtocol.Version, roles.ToHashSet(), new JsonObject());
}

/// <summary>Observer: reports every message that lands on the configured sinks of the in-memory broker.</summary>
public sealed class BusObserverPlugin : InProcessPlugin
{
    private readonly InMemoryBus _bus;
    private readonly IReadOnlyList<string> _sinks;

    public BusObserverPlugin(InMemoryBus bus, JsonObject config)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _sinks = config["sinks"] is JsonArray array
            ? array.Select(node => node?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0).ToList()
            : throw new PluginException(BuiltinPlugins.BusObserver, "config.sinks must list at least one entity to watch.");
        if (_sinks.Count == 0)
            throw new PluginException(BuiltinPlugins.BusObserver, "config.sinks must list at least one entity to watch.");
    }

    public override PluginInfo Info { get; } = Describe(BuiltinPlugins.BusObserver, "1.0.0", PluginRole.Observer);

    public override Task<IReadOnlyList<Observation>> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var seen = new List<Observation>();
        foreach (var sink in _sinks)
        {
            foreach (var message in _bus.TakeDue("observer:" + BuiltinPlugins.BusObserver, sink, now))
                seen.Add(new Observation(sink, message.CorrelationId, message.AvailableAt, message.Body));
        }

        return Task.FromResult<IReadOnlyList<Observation>>(seen);
    }
}

/// <summary>Assertion: every sent message must be observed on <c>config.sink</c> (default: the job target).</summary>
public sealed class ObservedAssertionPlugin : InProcessPlugin
{
    private readonly string _sink;

    public ObservedAssertionPlugin(JsonObject config, string targetEntity)
    {
        _sink = config["sink"] is JsonValue value && value.TryGetValue<string>(out var sink) ? sink : targetEntity;
    }

    public override PluginInfo Info { get; } = Describe(BuiltinPlugins.ObservedAssertion, "1.0.0", PluginRole.Assertion);

    public override Task<IReadOnlyList<JsonObject>> ExpectAsync(BusMessage sent, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JsonObject>>(
        [
            new JsonObject { ["correlationId"] = sent.CorrelationId, ["sink"] = _sink, ["description"] = $"seen on {_sink}" },
        ]);

    public override Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<JsonObject> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken) =>
        Task.FromResult(actual.Any(observation => observation.Sink == _sink)
            ? new Verdict(VerdictStatus.Pass, $"seen on {_sink}")
            : new Verdict(VerdictStatus.Fail, $"never seen on {_sink}"));
}
