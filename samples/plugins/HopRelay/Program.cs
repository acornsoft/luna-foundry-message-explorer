// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Hop relay: a Simulator for a chain of integration hops, set up entirely in the job file.
// Each hop takes messages from one entity and forwards them to one or more entities after a
// delay with seeded jitter. Several hops on the same entity make a fan-out: every branch gets
// its own copy and fails or succeeds on its own.
//
// Per hop, optionally:
//   dedupeBy:  a body field; a repeat value is dropped (duplicate detection).
//   require:   body fields the handler needs; a message without them is poison.
//   assign:    { field: PREFIX } adds a seeded reference such as PREFIX-48213 to the body.
//   faults:    throttle, transient faults, backoff and dead-lettering (see Shared/Faults.cs).
//
// Retries go to "<from>.retry.<hop>" so other subscribers of <from> never see them.
// Dead letters go to "<from>/$deadletterqueue".
using System.Globalization;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Samples;

const string Name = "hop-relay";
const string Version = "0.1.0";

long seed = 0;
var hops = new List<Hop>();

while (Console.ReadLine() is { } line)
{
    var request = JsonNode.Parse(line)!.AsObject();
    var id = request["id"]?.DeepClone();
    var p = request["params"] as JsonObject ?? new JsonObject();
    switch ((string?)request["method"])
    {
        case "initialize":
        {
            seed = (long)p["seed"]!;
            var config = p["config"] as JsonObject ?? new JsonObject();
            hops = (config["hops"] as JsonArray ?? []).Select(node => Hop.Parse(node!.AsObject(), seed)).ToList();
            if (hops.Count == 0)
            {
                Error(id, -32602, "config.hops must list at least one hop");
                break;
            }

            var subscribe = new JsonArray();
            foreach (var entity in hops.Select(h => h.From).Distinct().Concat(hops.Select(h => h.RetryEntity)))
                subscribe.Add(entity);
            Console.Error.WriteLine($"{Name} ready: seed {seed}, {hops.Count} hops");
            Reply(id, new JsonObject
            {
                ["protocol"] = "lfme-plugin/1.0",
                ["name"] = Name,
                ["version"] = Version,
                ["roles"] = new JsonArray("simulator"),
                ["capabilities"] = new JsonObject { ["subscribe"] = subscribe, ["stateful"] = true },
            });
            break;
        }

        case "deliver":
        {
            var message = p["message"]!.AsObject();
            var entity = (string)message["entity"]!;
            var now = DateTimeOffset.Parse((string)p["now"]!, CultureInfo.InvariantCulture);
            var correlationId = (string)message["correlationId"]!;
            var body = message["body"] as JsonObject ?? new JsonObject();
            var replies = new JsonArray();
            foreach (var hop in hops.Where(h => h.From == entity || h.RetryEntity == entity))
                hop.Handle(correlationId, body, now, replies);
            Reply(id, new JsonObject { ["replies"] = replies });
            break;
        }

        case "tick":
            Reply(id, new JsonObject { ["messages"] = new JsonArray() });
            break;

        case "shutdown":
            Reply(id, new JsonObject());
            return;

        default:
            Error(id, -32601, "method not found");
            break;
    }
}

static void Reply(JsonNode? id, JsonNode result)
{
    Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
    Console.Out.Flush();
}

static void Error(JsonNode? id, int code, string message)
{
    Console.Out.WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString());
    Console.Out.Flush();
}

internal sealed class Hop
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public required string Name { get; init; }

    public required string From { get; init; }

    public required IReadOnlyList<string> To { get; init; }

    public TimeSpan Delay { get; init; }

    public TimeSpan Jitter { get; init; }

    public string? DedupeBy { get; init; }

    public IReadOnlyList<string> Require { get; init; } = [];

    public IReadOnlyDictionary<string, string> Assign { get; init; } = new Dictionary<string, string>();

    public required FaultyService Service { get; init; }

    public required long Seed { get; init; }

    public string RetryEntity => $"{From}.retry.{Name}";

    public static Hop Parse(JsonObject config, long seed)
    {
        var name = (string?)config["name"] ?? throw new InvalidOperationException("every hop needs a name");
        return new Hop
        {
            Name = name,
            From = (string?)config["from"] ?? throw new InvalidOperationException($"hop {name} needs from"),
            To = (config["to"] as JsonArray ?? []).Select(n => (string)n!).ToList(),
            Delay = FaultProfile.Duration(config["delay"], "PT0S"),
            Jitter = FaultProfile.Duration(config["jitter"], "PT0S"),
            DedupeBy = (string?)config["dedupeBy"],
            Require = (config["require"] as JsonArray ?? []).Select(n => (string)n!).ToList(),
            Assign = (config["assign"] as JsonObject ?? []).ToDictionary(pair => pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal),
            Service = new FaultyService(name, seed, FaultProfile.Parse(config["faults"])),
            Seed = seed,
        };
    }

    public void Handle(string correlationId, JsonObject body, DateTimeOffset now, JsonArray replies)
    {
        var attempt = Envelopes.CountOf(body);
        if (DedupeBy is not null && attempt == 1 && (string?)body[DedupeBy] is { } dedupeKey && !_seen.Add(dedupeKey))
        {
            Event("duplicateDropped", correlationId, $"{DedupeBy} {dedupeKey} already relayed");
            return;
        }

        var missing = Require.Where(field => body[field] is null || (body[field] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0)).ToList();
        var poison = missing.Count == 0 ? null : $"missing {string.Join(", ", missing)}; the handler cannot continue";

        var decision = Service.Decide(correlationId, attempt, now, poison);
        switch (decision.Outcome)
        {
            case Outcome.Throttled:
                replies.Add(Envelopes.Retry(RetryEntity, correlationId, body, decision.Delay, countIt: false));
                Event("throttled", correlationId, decision.Reason);
                return;
            case Outcome.Retry:
                replies.Add(Envelopes.Retry(RetryEntity, correlationId, body, decision.Delay, countIt: true));
                Event("retry", correlationId, decision.Reason);
                return;
            case Outcome.DeadLetter:
                replies.Add(Envelopes.DeadLetter(From, correlationId, body, decision.Reason));
                Event("deadLettered", correlationId, decision.Reason);
                return;
        }

        var forwarded = body.DeepClone().AsObject();
        forwarded.Remove(Envelopes.DeliveryCount);
        forwarded["hop"] = Name;
        foreach (var (field, prefix) in Assign)
            forwarded[field] = $"{prefix}-{(int)(Rolls.Roll(Seed, $"{Name}:{correlationId}:{field}") * 90000) + 10000}";
        var delay = Delay + TimeSpan.FromSeconds(Math.Round(Rolls.Roll(Seed, $"{Name}:{correlationId}:delay") * Jitter.TotalSeconds));
        foreach (var to in To)
            replies.Add(Envelopes.Out(to, correlationId, forwarded.DeepClone().AsObject(), delay));
    }

    private static void Event(string kind, string correlationId, string detail)
    {
        Console.Out.WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "event",
            ["params"] = new JsonObject { ["kind"] = kind, ["correlationId"] = correlationId, ["detail"] = detail },
        }.ToJsonString());
        Console.Out.Flush();
    }
}
