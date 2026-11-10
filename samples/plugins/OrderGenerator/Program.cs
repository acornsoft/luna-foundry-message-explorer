// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Minimal C# plugin: a generator and an assertion for synthetic shop orders.
// Orders arrive spread over config.window, plus an optional burst (config.burst: count, at, within)
// like an end-of-day batch. About config.malformedRate of orders have no lines: a poison message
// that must end on the dead-letter queue, not in a confirmed or rejected state.
// Protocol: JSON-RPC 2.0 over stdio, one JSON object per line. Logs go to stderr.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;

const string Name = "order-generator";
const string Version = "0.2.0";

var schedule = new List<(DateTimeOffset At, JsonObject Message)>();
var emitted = 0;
var malformed = new HashSet<string>(StringComparer.Ordinal);
var deadLetterSink = "orders.created/$deadletterqueue";

while (Console.ReadLine() is { } line)
{
    var request = JsonNode.Parse(line)!.AsObject();
    var id = request["id"]?.DeepClone();
    var p = request["params"] as JsonObject ?? new JsonObject();
    switch ((string?)request["method"])
    {
        case "initialize":
        {
            var seed = (long)p["seed"]!;
            var start = Time((string)p["clockStart"]!);
            var config = p["config"] as JsonObject ?? new JsonObject();
            var count = (int?)config["count"] ?? 20;
            var window = XmlConvert.ToTimeSpan((string?)config["window"] ?? "PT6H");
            var malformedRate = (double?)config["malformedRate"] ?? 0;
            var burst = config["burst"] as JsonObject;
            var burstCount = (int?)burst?["count"] ?? 0;
            var burstAt = start + XmlConvert.ToTimeSpan((string?)burst?["at"] ?? "PT0S");
            var burstWithin = XmlConvert.ToTimeSpan((string?)burst?["within"] ?? "PT1M");
            deadLetterSink = ((string?)config["deadLetterSink"]) ?? deadLetterSink;
            var rng = new Random(unchecked((int)seed));
            schedule.Clear();
            malformed.Clear();
            emitted = 0;
            for (var i = 1; i <= count + burstCount; i++)
            {
                var at = i <= count
                    ? start.AddSeconds(rng.Next(0, (int)window.TotalSeconds))
                    : burstAt.AddSeconds(rng.Next(0, Math.Max(1, (int)burstWithin.TotalSeconds)));
                var lines = rng.Next(1, 4);
                var orderRef = $"ord-{i:D4}";
                if (rng.NextDouble() < malformedRate)
                {
                    lines = 0;
                    malformed.Add(orderRef);
                }

                schedule.Add((at, new JsonObject
                {
                    ["correlationId"] = orderRef,
                    ["body"] = new JsonObject { ["orderRef"] = orderRef, ["lines"] = lines, ["totalCents"] = Math.Max(lines, 1) * rng.Next(500, 9000) },
                }));
            }

            schedule = schedule.OrderBy(s => s.At).ThenBy(s => (string)s.Message["correlationId"]!, StringComparer.Ordinal).ToList();
            Reply(id, new JsonObject
            {
                ["protocol"] = "lfme-plugin/1.0",
                ["name"] = Name,
                ["version"] = Version,
                ["roles"] = new JsonArray("generator", "assertion"),
                ["capabilities"] = new JsonObject { ["timeline"] = true, ["count"] = count + burstCount },
            });
            break;
        }

        case "tick":
        {
            var now = Time((string)p["now"]!);
            var due = new JsonArray();
            while (emitted < schedule.Count && schedule[emitted].At <= now)
                due.Add(schedule[emitted++].Message.DeepClone());
            Reply(id, new JsonObject { ["messages"] = due });
            break;
        }

        case "nextBatch":
        {
            var max = (int)p["max"]!;
            var batch = new JsonArray();
            while (emitted < schedule.Count && batch.Count < max)
                batch.Add(schedule[emitted++].Message.DeepClone());
            Reply(id, new JsonObject { ["messages"] = batch });
            break;
        }

        case "expect":
        {
            var message = p["message"]!.AsObject();
            var poison = malformed.Contains((string)message["correlationId"]!);
            Reply(id, new JsonObject
            {
                ["expectations"] = new JsonArray(new JsonObject
                {
                    ["correlationId"] = message["correlationId"]!.DeepClone(),
                    ["sentAt"] = message["enqueuedAt"]!.DeepClone(),
                    ["within"] = "PT4H",
                    ["poison"] = poison,
                    ["description"] = poison
                        ? $"no lines: retried, then dead-lettered on {deadLetterSink}, never confirmed"
                        : "orders.confirmed then orders.shipped within PT4H, or orders.rejected with a code",
                }),
            });
            break;
        }

        case "assert":
        {
            var expected = p["expected"]![0]!.AsObject();
            var sentAt = Time((string)expected["sentAt"]!);
            var within = XmlConvert.ToTimeSpan((string)expected["within"]!);
            var actual = p["actual"]!.AsArray().Select(a => a!.AsObject()).ToList();
            JsonObject? Seen(string sink) => actual.FirstOrDefault(a => (string?)a["sink"] == sink);

            var rejected = Seen("orders.rejected");
            var confirmed = Seen("orders.confirmed");
            var shipped = Seen("orders.shipped");
            var deadLettered = Seen(deadLetterSink);
            JsonObject verdict;
            if ((bool?)expected["poison"] == true)
            {
                verdict = deadLettered is null
                    ? Verdict("fail", "poison order was never dead-lettered")
                    : confirmed is not null || rejected is not null
                        ? Verdict("fail", "poison order was dead-lettered but also processed")
                        : Verdict("pass", $"dead-lettered after {(int?)deadLettered["body"]?["deliveryCount"]} deliveries: {(string?)deadLettered["body"]?["deadLetterErrorDescription"]}");
            }
            else if (deadLettered is not null)
                verdict = Verdict("fail", $"dead-lettered: {(string?)deadLettered["body"]?["deadLetterErrorDescription"]}");
            else if (rejected is not null && !string.IsNullOrEmpty((string?)rejected["body"]?["code"]))
                verdict = Verdict("pass", $"rejected with {(string)rejected["body"]!["code"]!}");
            else if (confirmed is null && shipped is null)
                verdict = Verdict("fail", "no outcome was observed");
            else if (confirmed is null)
                verdict = Verdict("fail", "shipped without a confirmation");
            else if (shipped is null)
                verdict = Verdict("fail", "confirmed but never shipped");
            else if (Time((string)shipped["at"]!) < Time((string)confirmed["at"]!))
                verdict = Verdict("fail", "shipped before it was confirmed");
            else if (Time((string)shipped["at"]!) - sentAt > within)
                verdict = Verdict("fail", $"shipped after {(Time((string)shipped["at"]!) - sentAt).TotalHours:0.##} h, limit {within.TotalHours:0.##} h");
            else
                verdict = Verdict("pass", $"confirmed then shipped after {(Time((string)shipped["at"]!) - sentAt).TotalHours:0.##} h");
            Reply(id, verdict);
            break;
        }

        case "shutdown":
            Reply(id, new JsonObject());
            return;

        default:
            Console.Out.WriteLine(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
            }.ToJsonString());
            Console.Out.Flush();
            break;
    }
}

static DateTimeOffset Time(string value) =>
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

static JsonObject Verdict(string status, string reason) => new() { ["status"] = status, ["reason"] = reason };

static void Reply(JsonNode? id, JsonNode result)
{
    Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
    Console.Out.Flush();
}
