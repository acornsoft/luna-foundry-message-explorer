// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;

namespace Acornsoft.MessageExplorer.Brokers.Browse;

/// <summary>
/// Turns a message body into named fields for redaction: the top-level members of a JSON object, or one
/// <c>text</c> field, or a <c>binary</c> field that holds only the length. Values are capped at 16 KB.
/// </summary>
public static class BodyFields
{
    public const int MaxChars = 16 * 1024;

    public static IReadOnlyDictionary<string, string> From(ReadOnlyMemory<byte> body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.Length == 0)
            return fields;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body.Span);
        }
        catch (DecoderFallbackException)
        {
            fields["binary"] = body.Length + " bytes";
            return fields;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in document.RootElement.EnumerateObject())
                    fields[member.Name] = Cap(member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() ?? string.Empty : member.Value.GetRawText());
                return fields;
            }
        }
        catch (JsonException)
        {
        }

        fields["text"] = Cap(text);
        return fields;
    }

    private static string Cap(string value) => value.Length <= MaxChars ? value : value[..MaxChars];
}
