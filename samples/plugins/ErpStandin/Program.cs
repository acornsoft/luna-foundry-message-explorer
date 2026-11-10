// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// ERP stand-in: a Simulator for an ERP-like outside system, for system tests.
// Our own message names and fields only. It does not implement any real ERP.
//
// orders.created  -> orders.confirmed at once, then orders.shipped after shipDelay
//                 -> orders.rejected with failureCode for about failureRate of orders
// a repeat orders.created for the same order -> orders.rejected with code DUPLICATE
//
// With config.faults it also behaves like a real busy back end (see Shared/Faults.cs):
// it throttles bursts, has seeded transient faults with exponential backoff, and an order with no
// lines is a poison message that ends on orders.created/$deadletterqueue after maxDeliveries.
//
// Which orders fail depends only on the seed and the order reference, so a rerun fails the same ones.
// State lives per run and resets on every initialize.
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Samples;

const string Name = "erp-standin";
const string Version = "0.2.0";

long seed = 0;
var subscribe = "orders.created";
var retryEntity = subscribe + ".retry";
var shipDelay = TimeSpan.FromHours(2);
var failureRate = 0.05;
var failureCode = "CREDIT_HOLD";
var state = new Dictionary<string, string>(StringComparer.Ordinal);
var service = new FaultyService(Name, 0, new FaultProfile());

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
            subscribe = (string?)config["subscribe"] ?? subscribe;
            retryEntity = subscribe + ".retry";
            shipDelay = FaultProfile.Duration(config["shipDelay"], "PT2H");
            failureRate = (double?)config["failureRate"] ?? failureRate;
            failureCode = (string?)config["failureCode"] ?? failureCode;
            service = new FaultyService(Name, seed, FaultProfile.Parse(config["faults"]));
            state.Clear();
            Console.Error.WriteLine($"{Name} ready: seed {seed}, failure rate {failureRate}");
            Reply(id, new JsonObject
            {
                ["protocol"] = "lfme-plugin/1.0",
                ["name"] = Name,
                ["version"] = Version,
                ["roles"] = new JsonArray("simulator"),
                ["capabilities"] = new JsonObject
                {
                    ["subscribe"] = new JsonArray(subscribe, retryEntity),
                    ["emits"] = new JsonArray("orders.confirmed", "orders.shipped", "orders.rejected", Envelopes.DeadLetterEntity(subscribe)),
                    ["stateful"] = true,
                },
            });
            break;
        }

        case "deliver":
        {
            var message = p["message"]!.AsObject();
            var now = DateTimeOffset.Parse((string)p["now"]!, System.Globalization.CultureInfo.InvariantCulture);
            var correlationId = (string)message["correlationId"]!;
            var body = message["body"] as JsonObject ?? new JsonObject();
            var orderRef = (string?)body["orderRef"] ?? correlationId;
            var attempt = Envelopes.CountOf(body);
            var poison = (int?)body["lines"] is > 0 ? null : "order has no lines; the handler cannot price it";
            var replies = new JsonArray();

            var decision = service.Decide(orderRef, attempt, now, poison);
            switch (decision.Outcome)
            {
                case Outcome.Throttled:
                    replies.Add(Envelopes.Retry(retryEntity, correlationId, body, decision.Delay, countIt: false));
                    Event("throttled", orderRef, decision.Reason);
                    break;
                case Outcome.Retry:
                    replies.Add(Envelopes.Retry(retryEntity, correlationId, body, decision.Delay, countIt: true));
                    Event("retry", orderRef, decision.Reason);
                    break;
                case Outcome.DeadLetter:
                    replies.Add(Envelopes.DeadLetter(subscribe, correlationId, body, decision.Reason));
                    Event("deadLettered", orderRef, decision.Reason);
                    break;
                default:
                    if (state.ContainsKey(orderRef))
                    {
                        replies.Add(Out("orders.rejected", correlationId, TimeSpan.Zero, orderRef, "rejected", "DUPLICATE"));
                    }
                    else if (Rolls.Roll(seed, orderRef) < failureRate)
                    {
                        state[orderRef] = "rejected";
                        replies.Add(Out("orders.rejected", correlationId, TimeSpan.Zero, orderRef, "rejected", failureCode));
                    }
                    else
                    {
                        state[orderRef] = "confirmed";
                        replies.Add(Out("orders.confirmed", correlationId, TimeSpan.Zero, orderRef, "confirmed", null));
                        replies.Add(Out("orders.shipped", correlationId, shipDelay, orderRef, "shipped", null));
                        Event("state", orderRef, "confirmed");
                    }

                    break;
            }

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

static JsonObject Out(string to, string correlationId, TimeSpan delay, string orderRef, string status, string? code)
{
    var body = new JsonObject { ["orderRef"] = orderRef, ["state"] = status };
    if (code is not null)
        body["code"] = code;
    return Envelopes.Out(to, correlationId, body, delay);
}

static void Event(string kind, string orderRef, string detail)
{
    Console.Out.WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "event",
        ["params"] = new JsonObject { ["kind"] = kind, ["orderRef"] = orderRef, ["detail"] = detail },
    }.ToJsonString());
    Console.Out.Flush();
}

static void Reply(JsonNode? id, JsonNode result)
{
    Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
    Console.Out.Flush();
}
