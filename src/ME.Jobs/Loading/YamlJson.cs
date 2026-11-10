// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Acornsoft.MessageExplorer.Jobs.Loading;

/// <summary>Reads YAML (or JSON, which is YAML) into JSON nodes. Plain scalars become numbers, booleans, or null when they look like one.</summary>
public static class YamlJson
{
    public static JsonNode? Parse(string text, string fileName)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            throw new JobValidationException(fileName, [$"line {exception.Start.Line}, column {exception.Start.Column}: {Plain(exception)}"]);
        }

        if (stream.Documents.Count == 0)
            throw new JobValidationException(fileName, ["the file is empty"]);
        return Convert(stream.Documents[0].RootNode);
    }

    public static T Bind<T>(JsonNode? node, string fileName)
    {
        if (node is not JsonObject)
            throw new JobValidationException(fileName, ["the top level must be a mapping of keys to values"]);
        try
        {
            return node.Deserialize<T>(Options) ?? throw new JobValidationException(fileName, ["the file is empty"]);
        }
        catch (JsonException exception)
        {
            var path = string.IsNullOrEmpty(exception.Path) ? "$" : exception.Path;
            var message = exception.Message;
            var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
            if (cut > 0)
                message = message[..cut];
            throw new JobValidationException(fileName, [$"{path}: {message}"]);
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new LenientStringConverter() },
    };

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var result = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    var name = key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
                    result[name] = Convert(value);
                }

                return result;
            }

            case YamlSequenceNode sequence:
            {
                var result = new JsonArray();
                foreach (var item in sequence.Children)
                    result.Add(Convert(item));
                return result;
            }

            case YamlScalarNode scalar:
                return scalar.Style == ScalarStyle.Plain ? PlainScalar(scalar.Value) : JsonValue.Create(scalar.Value ?? string.Empty);

            default:
                return null;
        }
    }

    private static JsonNode? PlainScalar(string? value)
    {
        if (value is null or "" or "~" or "null")
            return null;
        if (value is "true")
            return JsonValue.Create(true);
        if (value is "false")
            return JsonValue.Create(false);
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            return JsonValue.Create(whole);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            return JsonValue.Create(real);
        return JsonValue.Create(value);
    }

    private static string Plain(YamlException exception)
    {
        var message = exception.Message;
        var cut = message.IndexOf("): ", StringComparison.Ordinal);
        return cut > 0 ? message[(cut + 3)..] : message;
    }

    /// <summary>Lets a number or boolean bind to a string field, so <c>version: 1.0</c> still reads as "1.0".</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"expected a text value, found {reader.TokenType}."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
