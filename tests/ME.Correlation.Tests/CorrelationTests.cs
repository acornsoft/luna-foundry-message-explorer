// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Correlation;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Correlation.Tests;

public class CorrelationTests
{
    private const string Valid = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public void Traceparent_round_trips()
    {
        Assert.True(TraceParent.TryParse(Valid, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(Valid, parsed!.ToHeader());
    }

    [Theory]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7")]
    [InlineData("ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("not-a-trace")]
    public void Traceparent_rejects_invalid_headers(string header)
    {
        Assert.False(TraceParent.TryParse(header, out _));
    }

    [Fact]
    public void Composer_keeps_a_valid_traceparent_and_drops_a_bad_one()
    {
        var redacted = Pipeline().Redact(new Dictionary<string, string>
        {
            ["account_ref"] = "ACCT-SYNTH-100",
            ["case_id"] = "CASE-SYNTH-100",
            ["status"] = "open",
        });

        var kept = EnvelopeComposer.Compose("id-1", "servicebus://observe/disputes", DateTimeOffset.Parse("2026-10-21T12:00:00Z"), redacted, Valid);
        Assert.Equal(Valid, kept.Extensions["traceparent"]);
        Assert.StartsWith("tok_", kept.Data["account_ref"], StringComparison.Ordinal);
        Assert.DoesNotContain("ACCT-SYNTH-100", string.Join("\n", kept.Data.Values), StringComparison.OrdinalIgnoreCase);

        var dropped = EnvelopeComposer.Compose("id-2", "servicebus://observe/disputes", DateTimeOffset.Parse("2026-10-21T12:00:00Z"), redacted, "bad");
        Assert.False(dropped.Extensions.ContainsKey("traceparent"));
    }

    [Fact]
    public void Allow_list_is_the_correlation_fields()
    {
        Assert.Equal(["account_ref", "case_id"], CorrelationKeys.AllowList);
    }

    private static RedactionPipeline Pipeline()
    {
        var material = Encoding.UTF8.GetBytes("synthetic-key-correlation-0001");
        return DisputeRedactionProfile.Create(new ProjectTokenKey("correlation", material), "synthetic-salt-dispute");
    }
}
