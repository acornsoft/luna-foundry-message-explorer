// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction.Tests;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.QuickView;
using FsCheck.Xunit;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>
/// The redaction property harness, run through the quick view: the planted secrets go in as body fields,
/// application properties, the subject and the dead-letter text, and must not appear on any row, drawer or
/// clipboard surface.
/// </summary>
public class QuickViewRedactionTests
{
    [Property(MaxTest = 100)]
    public void Planted_secrets_never_reach_a_row_the_drawer_or_the_clipboard(int seed)
    {
        var sample = SyntheticMessageFactory.Create(seed);
        var frame = Frame(sample.Fields, MessageLane.DeadLetter) with
        {
            Subject = sample.Fields["subject"],
            DeadLetterReason = "Rejected " + sample.Fields["email"],
            DeadLetterDescription = sample.Fields["notes"],
            CorrelationId = sample.Fields["case_id"],
        };

        var message = QuickMessage.From(frame, QuickPeekSessionTests.Redaction());

        foreach (var surface in message.Surfaces())
        {
            foreach (var variant in sample.Variants)
                Assert.DoesNotContain(variant, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Property(MaxTest = 50)]
    public void Body_and_property_values_are_masked_but_their_names_are_listed(int seed)
    {
        var sample = SyntheticMessageFactory.Create(seed);
        var message = QuickMessage.From(Frame(sample.Fields, MessageLane.Active), QuickPeekSessionTests.Redaction());

        Assert.Equal(sample.Fields.Keys.Order(StringComparer.Ordinal), message.BodyFields);
        Assert.All(message.ApplicationProperties, p => Assert.Equal(QuickMessage.Masked, p.Value));
        foreach (var value in sample.Fields.Values.Where(v => v.Length >= 8))
            Assert.DoesNotContain(value, message.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("masked", message.BodyPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_is_kept_and_scrubbed_by_the_pattern_net()
    {
        var frame = Frame(new Dictionary<string, string> { ["note"] = "x" }, MessageLane.Active) with
        {
            Subject = "order.created for solo.person@example.test",
            MessageId = "msg-0001",
        };

        var message = QuickMessage.From(frame, QuickPeekSessionTests.Redaction());

        Assert.Equal("msg-0001", message.MessageId);
        Assert.StartsWith("order.created for", message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("solo.person@example.test", message.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_demo_messages_show_no_body_value()
    {
        var entity = BrowseEntity.Queue("orders");
        var browser = new SyntheticMessageBrowser().Generate(entity, active: 5);
        var frames = await browser.PeekAsync(entity, PeekSource.Main, 0, 5);

        foreach (var frame in frames)
        {
            var message = QuickMessage.From(frame, QuickPeekSessionTests.Redaction());
            foreach (var value in frame.Body.Values.Concat(frame.ApplicationProperties.Values).Where(v => v.Length >= 8))
                Assert.All(message.Surfaces(), s => Assert.DoesNotContain(value, s, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Body_fields_come_from_a_json_object_or_text()
    {
        var json = BodyFields.From("{\"ref\":\"REF-1\",\"amount\":12.5,\"lines\":[1,2]}"u8.ToArray());
        Assert.Equal("REF-1", json["ref"]);
        Assert.Equal("12.5", json["amount"]);
        Assert.Equal("[1,2]", json["lines"]);

        Assert.Equal("plain words", BodyFields.From("plain words"u8.ToArray())["text"]);
        Assert.Equal("3 bytes", BodyFields.From(new byte[] { 0xff, 0xfe, 0xfd })["binary"]);
        Assert.Empty(BodyFields.From(ReadOnlyMemory<byte>.Empty));
    }

    private static PeekedFrame Frame(IReadOnlyDictionary<string, string> body, MessageLane lane) => new(
        42, lane, "msg-42", new DateTimeOffset(2026, 11, 10, 19, 49, 11, TimeSpan.Zero), null, 10, "order.packed",
        "application/json", "corr-42", null, 1100, null, null, false, body,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["tenantRef"] = "TENANT-SECRET-0042", ["schemaVersion"] = "2" });
}
