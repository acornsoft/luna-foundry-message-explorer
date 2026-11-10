// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Voice;

/// <summary>
/// What leaves the machine for the voice model. With redaction on (the default) any message body or property
/// bag is dropped and every remaining string goes through the PII net. The read-only tools return counts
/// only, so this is a second line: a body can never be spoken even if a tool started returning one.
/// </summary>
public static class VoiceRedaction
{
    private static readonly HashSet<string> BodyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "bodies", "payload", "data", "properties", "applicationProperties", "userProperties", "headers",
    };

    public static JsonNode ForModel(JsonNode node, bool redact)
    {
        ArgumentNullException.ThrowIfNull(node);
        return redact ? Redact(node.DeepClone()) : node.DeepClone();
    }

    public static string ForModel(string text, bool redact) => redact ? PiiNet.Scrub(text) : text;

    private static JsonNode Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (BodyFields.Contains(key))
                    {
                        obj[key] = "[redacted]";
                        continue;
                    }

                    if (obj[key] is { } child)
                        obj[key] = Redact(child.DeepClone());
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item)
                        array[i] = Redact(item.DeepClone());
                }

                return array;
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                return JsonValue.Create(PiiNet.Scrub(value.GetValue<string>()))!;
            default:
                return node;
        }
    }
}
