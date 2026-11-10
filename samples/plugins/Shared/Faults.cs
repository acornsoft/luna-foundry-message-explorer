// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Shared by the sample simulators: how an outside service misbehaves, on the simulated clock.
//
// - Throttle: a sliding window of accepted calls. Over the limit, the service answers "busy" with a
//   retry-after. The caller pauses the whole client until then and does not count it as a delivery.
// - Transient fault: a seeded roll per (key, attempt). The caller retries with exponential backoff
//   plus seeded jitter, and the delivery count goes up.
// - Poison: a message the handler can never process. It fails every attempt.
// - Dead letter: after maxDeliveries failed attempts the message moves to "<entity>/$deadletterqueue".
//
// Every decision depends only on the seed, the key, the attempt and the simulated time, so reruns match.
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

namespace Acornsoft.MessageExplorer.Samples;

internal enum Outcome
{
    Process,
    Throttled,
    Retry,
    DeadLetter,
}

internal sealed record Decision(Outcome Outcome, TimeSpan Delay, string Reason)
{
    public static readonly Decision Process = new(Outcome.Process, TimeSpan.Zero, string.Empty);
}

internal sealed class FaultProfile
{
    public int ThrottleLimit { get; init; }

    public TimeSpan ThrottleWindow { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    public double TransientRate { get; init; }

    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxDeliveries { get; init; } = 10;

    public static FaultProfile Parse(JsonNode? node)
    {
        if (node is not JsonObject config)
            return new FaultProfile();
        var throttle = config["throttle"] as JsonObject;
        return new FaultProfile
        {
            ThrottleLimit = (int?)throttle?["limit"] ?? 0,
            ThrottleWindow = Duration(throttle?["window"], "PT1M"),
            RetryAfter = Duration(throttle?["retryAfter"], "PT30S"),
            TransientRate = (double?)config["transientRate"] ?? 0,
            Backoff = Duration(config["backoff"], "PT5S"),
            BackoffCap = Duration(config["backoffCap"], "PT5M"),
            MaxDeliveries = (int?)config["maxDeliveries"] ?? 10,
        };
    }

    public static TimeSpan Duration(JsonNode? node, string fallback) => XmlConvert.ToTimeSpan((string?)node ?? fallback);
}

/// <summary>One downstream service with its own throttle window and seeded faults.</summary>
internal sealed class FaultyService(string name, long seed, FaultProfile profile)
{
    private readonly Queue<DateTimeOffset> _accepted = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public FaultProfile Profile => profile;

    /// <summary>Decide what happens to attempt number <paramref name="deliveryCount"/> (1-based) of <paramref name="key"/>.</summary>
    public Decision Decide(string key, int deliveryCount, DateTimeOffset now, string? poisonReason)
    {
        // A paused client does not call the service at all; the message waits and keeps its delivery count.
        if (now < _pausedUntil)
            return new Decision(Outcome.Throttled, _pausedUntil - now + Spread(key, deliveryCount, "pause"), "client paused after a busy answer");

        if (profile.ThrottleLimit > 0)
        {
            while (_accepted.Count > 0 && _accepted.Peek() <= now - profile.ThrottleWindow)
                _accepted.Dequeue();
            if (_accepted.Count >= profile.ThrottleLimit)
            {
                _pausedUntil = now + profile.RetryAfter;
                return new Decision(Outcome.Throttled, profile.RetryAfter + Spread(key, deliveryCount, "busy"), $"busy, retry after {profile.RetryAfter.TotalSeconds:0} s");
            }

            _accepted.Enqueue(now);
        }

        string? failure = poisonReason;
        if (failure is null && profile.TransientRate > 0 && Rolls.Roll(seed, $"{name}:{key}:{deliveryCount}") < profile.TransientRate)
            failure = "transient fault (simulated timeout)";
        if (failure is null)
            return Decision.Process;

        if (deliveryCount >= profile.MaxDeliveries)
            return new Decision(Outcome.DeadLetter, TimeSpan.Zero, failure);

        var exponential = profile.Backoff.TotalMilliseconds * Math.Pow(2, deliveryCount - 1);
        var capped = TimeSpan.FromMilliseconds(Math.Min(exponential, profile.BackoffCap.TotalMilliseconds));
        var jitter = TimeSpan.FromMilliseconds(Math.Round(Rolls.Roll(seed, $"{name}:{key}:{deliveryCount}:jitter") * profile.Backoff.TotalMilliseconds));
        return new Decision(Outcome.Retry, capped + jitter, failure);
    }

    // Spreads paused messages over a second so they do not all retry in the same instant.
    private TimeSpan Spread(string key, int deliveryCount, string salt) =>
        TimeSpan.FromMilliseconds(Math.Round(Rolls.Roll(seed, $"{name}:{key}:{deliveryCount}:{salt}") * 1000));
}

internal static class Rolls
{
    /// <summary>FNV-1a then a SplitMix64 finalizer, mapped to [0, 1). Stable across runs, machines and .NET versions.</summary>
    public static double Roll(long seed, string key)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{seed}:{key}"))
        {
            hash ^= b;
            hash *= prime;
        }

        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;
        return (hash >> 11) / (double)(1UL << 53);
    }
}

/// <summary>Builds retry and dead-letter messages that carry the original body and the delivery count.</summary>
internal static class Envelopes
{
    public const string DeliveryCount = "deliveryCount";

    public static string DeadLetterEntity(string entity) => entity + "/$deadletterqueue";

    public static int CountOf(JsonObject body) => (int?)body[DeliveryCount] ?? 1;

    public static JsonObject Retry(string to, string correlationId, JsonObject body, TimeSpan delay, bool countIt)
    {
        var copy = body.DeepClone().AsObject();
        copy[DeliveryCount] = CountOf(body) + (countIt ? 1 : 0);
        return Out(to, correlationId, copy, delay);
    }

    public static JsonObject DeadLetter(string entity, string correlationId, JsonObject body, string description)
    {
        var copy = body.DeepClone().AsObject();
        copy["deadLetterReason"] = "MaxDeliveryCountExceeded";
        copy["deadLetterErrorDescription"] = description;
        copy[DeliveryCount] = CountOf(body);
        return Out(DeadLetterEntity(entity), correlationId, copy, TimeSpan.Zero);
    }

    public static JsonObject Out(string to, string correlationId, JsonObject body, TimeSpan delay) => new()
    {
        ["to"] = to,
        ["correlationId"] = correlationId,
        ["delayMs"] = (long)delay.TotalMilliseconds,
        ["body"] = body,
    };
}
