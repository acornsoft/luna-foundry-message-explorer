// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;

namespace Acornsoft.MessageExplorer.Voice.Tests;

public sealed class VoicesAndRedactionTests
{
    [Fact]
    public void The_picker_lists_xais_built_in_voices_with_eve_first_as_the_default()
    {
        Assert.Equal(28, GrokVoices.BuiltIn.Count);
        Assert.Equal("eve", GrokVoices.Default.Id);
        Assert.Equal(GrokVoices.BuiltIn.Count, GrokVoices.BuiltIn.Select(v => v.Id).Distinct().Count());
        Assert.All(GrokVoices.BuiltIn, v => Assert.Equal(v.Id.ToLowerInvariant(), v.Id));
        foreach (var id in new[] { "ara", "rex", "sal", "leo", "luna", "rigel", "liora" })
            Assert.True(GrokVoices.IsBuiltIn(id), id);
    }

    [Fact]
    public void An_unknown_or_custom_voice_falls_back_to_the_default()
    {
        Assert.False(GrokVoices.IsBuiltIn("my-cloned-voice"));
        Assert.Equal("eve", GrokVoices.Find("my-cloned-voice").Id);
        Assert.Equal("eve", GrokVoices.Find(null).Id);
        Assert.Equal("Ara - warm and friendly", GrokVoices.Find("ara").Label);
    }

    [Fact]
    public void Redaction_drops_bodies_and_property_bags_and_scrubs_strings()
    {
        var node = new JsonObject
        {
            ["path"] = "payments",
            ["deadLetter"] = 3,
            ["body"] = "{\"card\":\"4111111111111111\"}",
            ["properties"] = new JsonObject { ["customer"] = "alice" },
            ["note"] = "contact alice@contoso.example",
            ["items"] = new JsonArray(new JsonObject { ["payload"] = "secret stuff", ["path"] = "orders" }),
        };

        var redacted = VoiceRedaction.ForModel(node, redact: true);

        Assert.Equal("payments", redacted["path"]!.GetValue<string>());
        Assert.Equal(3, redacted["deadLetter"]!.GetValue<int>());
        Assert.Equal("[redacted]", redacted["body"]!.GetValue<string>());
        Assert.Equal("[redacted]", redacted["properties"]!.GetValue<string>());
        Assert.Equal("[redacted]", redacted["items"]![0]!["payload"]!.GetValue<string>());
        Assert.DoesNotContain("alice@contoso.example", redacted["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("{\"card\":\"4111111111111111\"}", node["body"]!.GetValue<string>());
    }

    [Fact]
    public void Redaction_keeps_entity_paths_intact()
    {
        var node = new JsonObject { ["path"] = "orders-events/subscriptions/billing", ["source"] = "sbns-demo-001.servicebus.windows.net" };

        var redacted = VoiceRedaction.ForModel(node, redact: true);

        Assert.Equal("orders-events/subscriptions/billing", redacted["path"]!.GetValue<string>());
        Assert.Equal("sbns-demo-001.servicebus.windows.net", redacted["source"]!.GetValue<string>());
    }

    [Fact]
    public void With_redaction_off_everything_passes_through()
    {
        var node = new JsonObject { ["body"] = "hello" };

        Assert.Equal("hello", VoiceRedaction.ForModel(node, redact: false)["body"]!.GetValue<string>());
    }
}
