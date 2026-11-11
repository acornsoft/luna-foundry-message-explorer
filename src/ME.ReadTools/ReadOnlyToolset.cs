// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.ReadTools;

/// <summary>One read-only tool: a name, a one-line description and a JSON Schema for its arguments.</summary>
public sealed record ReadOnlyTool(string Name, string Title, string Description, JsonObject InputSchema);

/// <summary>A tool's answer: plain text for people and voices, and the same data as JSON for programs.</summary>
public sealed record ReadOnlyToolResult(string Text, JsonObject Structured, bool IsError = false);

/// <summary>
/// The read-only tools shared by the MCP server and the voice panel. Every tool reads entity names and message
/// counts, nothing else: no message body, no property, no key. There is no tool that sends, receives, settles,
/// moves, creates or deletes, and an unknown name is refused rather than guessed at.
/// </summary>
public sealed class ReadOnlyToolset
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<EntitySnapshot>>> _read;
    private readonly string _source;

    /// <param name="read">Reads every entity with its counts, for example <see cref="InventorySources.ReadAllAsync"/>.</param>
    /// <param name="source">A label for the source (a namespace host, or "synthetic"). Never a secret.</param>
    public ReadOnlyToolset(Func<CancellationToken, Task<IReadOnlyList<EntitySnapshot>>> read, string source)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>A toolset over an opened source. The inventory stays open for the toolset's lifetime.</summary>
    public static ReadOnlyToolset Over(OpenedSource opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return new ReadOnlyToolset(ct => InventorySources.ReadAllAsync(opened.Inventory, ct), opened.Label);
    }

    public string Source => _source;

    public static IReadOnlyList<ReadOnlyTool> Tools { get; } =
    [
        new("list_entities", "List queues, topics and subscriptions",
            "Lists every queue, topic and subscription with its active, dead-letter and scheduled message counts. Read-only.",
            Schema(("entity", Str("Optional: a queue or topic name, or topic/subscriptions/name, to limit the list.")),
                   ("kind", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("queue", "topic", "subscription"), ["description"] = "Optional: only this kind." }))),
        new("get_entity", "Get one entity's counts",
            "Gets one queue, topic or subscription (topic/subscriptions/name) with its message counts and status. Read-only.",
            Schema(required: ["path"], properties: ("path", Str("The queue or topic name, or topic/subscriptions/name.")))),
        new("check_limits", "Check counts against limits",
            "Checks message counts against limits and lists every entity over a limit. A limit that is left out is not checked. Read-only.",
            Schema(("maxDeadLetter", Int("Most dead-lettered messages allowed per entity.")),
                   ("maxActive", Int("Most active messages allowed per entity.")),
                   ("maxScheduled", Int("Most scheduled messages allowed per entity.")),
                   ("entities", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Optional: queue or topic names, or full paths, to check." }))),
        new("summarize_dead_letters", "Summarize dead letters",
            "Lists the entities that have dead-lettered messages, most first, with the total. Counts only, never message bodies. Read-only.",
            Schema()),
    ];

    public static bool IsKnown(string name) => Tools.Any(t => t.Name == name);

    public async Task<ReadOnlyToolResult> CallAsync(string name, JsonObject? arguments, CancellationToken cancellationToken = default)
    {
        arguments ??= [];
        if (!IsKnown(name))
            return Error($"There is no tool called '{name}'. These tools only read: {string.Join(", ", Tools.Select(t => t.Name))}.");

        IReadOnlyList<EntitySnapshot> all;
        try
        {
            all = await _read(cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerException exception)
        {
            return Error($"{exception.Kind}: {exception.Message.Split('\n')[0].Trim()}");
        }

        try
        {
            return name switch
            {
                "list_entities" => ListEntities(all, arguments),
                "get_entity" => GetEntity(all, arguments),
                "check_limits" => CheckLimits(all, arguments),
                _ => SummarizeDeadLetters(all),
            };
        }
        catch (ArgumentException exception)
        {
            return Error(exception.Message);
        }
    }

    private ReadOnlyToolResult ListEntities(IReadOnlyList<EntitySnapshot> all, JsonObject arguments)
    {
        var entities = EntityChecks.Filter(all, OptionalString(arguments, "entity") is { } entity ? [entity] : null);
        if (OptionalString(arguments, "kind") is { } kind)
        {
            entities = kind switch
            {
                "queue" => entities.Where(e => e.Kind == EntityKind.Queue).ToList(),
                "topic" => entities.Where(e => e.Kind == EntityKind.Topic).ToList(),
                "subscription" => entities.Where(e => e.Kind == EntityKind.Subscription).ToList(),
                _ => throw new ArgumentException($"kind is queue, topic or subscription, not '{kind}'."),
            };
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{entities.Count} {(entities.Count == 1 ? "entity" : "entities")} in {_source} (read-only):");
        foreach (var e in entities)
            text.AppendLine(Line(e));
        return new ReadOnlyToolResult(text.ToString().TrimEnd(), Envelope(new JsonObject { ["entities"] = Array(entities) }));
    }

    private ReadOnlyToolResult GetEntity(IReadOnlyList<EntitySnapshot> all, JsonObject arguments)
    {
        var path = OptionalString(arguments, "path") ?? throw new ArgumentException("get_entity needs a path.");
        var entity = all.FirstOrDefault(e => string.Equals(e.Path, path.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entity is null)
            return Error($"No queue, topic or subscription called '{path}' in {_source}.");
        return new ReadOnlyToolResult(Line(entity), Envelope(new JsonObject { ["entity"] = Json(entity) }));
    }

    private ReadOnlyToolResult CheckLimits(IReadOnlyList<EntitySnapshot> all, JsonObject arguments)
    {
        var limits = new CountLimits(OptionalCount(arguments, "maxDeadLetter"), OptionalCount(arguments, "maxActive"), OptionalCount(arguments, "maxScheduled"));
        if (limits is { MaxDeadLetter: null, MaxActive: null, MaxScheduled: null })
            throw new ArgumentException("check_limits needs at least one of maxDeadLetter, maxActive or maxScheduled.");
        var filters = arguments["entities"] is JsonArray names
            ? names.Select(n => n?.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : throw new ArgumentException("entities is a list of names.")).ToList()
            : null;
        var entities = EntityChecks.Filter(all, filters);
        var breaches = EntityChecks.Breaches(entities, limits);
        var text = breaches.Count == 0
            ? $"All {entities.Count} entities in {_source} are within the limits."
            : $"{breaches.Count} {(breaches.Count == 1 ? "check" : "checks")} failed in {_source}:\n" + string.Join("\n", breaches.Select(b => $"- {b.Path}: {b.Reason}"));
        return new ReadOnlyToolResult(text, Envelope(new JsonObject
        {
            ["checked"] = entities.Count,
            ["passed"] = breaches.Count == 0,
            ["breaches"] = new JsonArray(breaches.Select(b => (JsonNode)new JsonObject { ["path"] = b.Path, ["reason"] = b.Reason }).ToArray()),
        }));
    }

    private ReadOnlyToolResult SummarizeDeadLetters(IReadOnlyList<EntitySnapshot> all)
    {
        var dead = all.Where(e => e.Counts.DeadLetter > 0).OrderByDescending(e => e.Counts.DeadLetter).ThenBy(e => e.Path, StringComparer.Ordinal).ToList();
        var total = dead.Sum(e => e.Counts.DeadLetter ?? 0);
        var text = dead.Count == 0
            ? $"No dead-lettered messages in {_source}."
            : $"{total} dead-lettered {(total == 1 ? "message" : "messages")} in {dead.Count} {(dead.Count == 1 ? "entity" : "entities")} in {_source}:\n"
              + string.Join("\n", dead.Select(e => $"- {e.Path}: {e.Counts.DeadLetter}"));
        return new ReadOnlyToolResult(text, Envelope(new JsonObject { ["total"] = total, ["entities"] = Array(dead) }));
    }

    private JsonObject Envelope(JsonObject body)
    {
        body["source"] = _source;
        body["readOnly"] = true;
        return body;
    }

    private ReadOnlyToolResult Error(string message) =>
        new(message, new JsonObject { ["source"] = _source, ["readOnly"] = true, ["error"] = message }, IsError: true);

    private static string Line(EntitySnapshot e) =>
        $"- {e.Kind.ToString().ToLowerInvariant()} {e.Path}: active {N(e.Counts.Active)}, dead-letter {N(e.Counts.DeadLetter)}, scheduled {N(e.Counts.Scheduled)}"
        + (e.Status is { Length: > 0 } status ? $", {status}" : string.Empty);

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static JsonArray Array(IEnumerable<EntitySnapshot> entities) => new(entities.Select(e => (JsonNode)Json(e)).ToArray());

    private static JsonObject Json(EntitySnapshot e) => new()
    {
        ["kind"] = e.Kind.ToString().ToLowerInvariant(),
        ["path"] = e.Path,
        ["active"] = e.Counts.Active,
        ["deadLetter"] = e.Counts.DeadLetter,
        ["scheduled"] = e.Counts.Scheduled,
        ["transferDeadLetter"] = e.Counts.TransferDeadLetter,
        ["status"] = e.Status,
    };

    private static string? OptionalString(JsonObject arguments, string name) => arguments[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        _ => throw new ArgumentException($"{name} is a string."),
    };

    private static long? OptionalCount(JsonObject arguments, string name) => arguments[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && n >= 0 && n == decimal.Truncate(n) && n <= long.MaxValue => (long)n,
        _ => throw new ArgumentException($"{name} is a whole number, 0 or more."),
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["minimum"] = 0, ["description"] = description };

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] properties) => Schema([], properties);

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;
        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0)
            schemaObject["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schemaObject;
    }
}
