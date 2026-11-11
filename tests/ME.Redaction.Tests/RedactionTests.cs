// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Redaction;
using FsCheck.Xunit;

namespace Acornsoft.MessageExplorer.Redaction.Tests;

public class RedactionTests
{
    private const string Salt = "synthetic-salt-dispute";

    [Property(MaxTest = 100)]
    public void Planted_secrets_never_reach_ui_log_prompt_mcp_or_export(int seed)
    {
        var sample = SyntheticMessageFactory.Create(seed);
        var result = Pipeline("project-property").Redact(sample.Fields);

        Assert.False(result.Truncated);
        Assert.Equal("[masked:member_name]", result.Fields["member_name"]);
        Assert.StartsWith("hash_", result.Fields["loyalty_code"], StringComparison.Ordinal);
        Assert.StartsWith("tok_", result.Fields["account_ref"], StringComparison.Ordinal);
        Assert.StartsWith("tok_", result.Fields["case_id"], StringComparison.Ordinal);
        Assert.DoesNotContain(sample.AccountRef, result.Fields["account_ref"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("open", result.Fields["status"]);
        Assert.Equal("reg-e-dispute", result.Fields["message_type"]);
        Assert.Contains("T", result.Fields["occurred_at"], StringComparison.Ordinal);
        Assert.DoesNotContain("maiden_name", result.Fields.Keys);
        Assert.Contains(sample.UnknownField, result.UnclassifiedFields);
        Assert.DoesNotContain(sample.UnknownField, result.Fields.Keys);

        foreach (var surface in result.Surfaces())
        {
            foreach (var variant in sample.Variants)
                Assert.DoesNotContain(variant, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Pattern_net_removes_a_card_that_appears_only_in_free_text()
    {
        var result = Pipeline("project-card").Redact(new Dictionary<string, string>
        {
            ["notes"] = "Card 4111 1111 1111 1111 was typed into a note.",
            ["status"] = "open",
        });

        foreach (var surface in result.Surfaces())
        {
            Assert.DoesNotContain("4111111111111111", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("4111 1111 1111 1111", surface, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Known_test_pan_passes_luhn()
    {
        Assert.True(Luhn.IsValid("4111111111111111"));
        Assert.Equal('1', Luhn.CheckDigit("411111111111111"));
    }

    [Fact]
    public void Free_text_patterns_are_removed_without_a_matching_field()
    {
        var result = Pipeline("project-patterns").Redact(new Dictionary<string, string>
        {
            ["notes"] = "Email solo.person@example.test SSN 219-09-9999 phone +1-202-555-0199 dob 1984-06-17 code Z99.1",
            ["status"] = "open",
            ["occurred_at"] = "2026-03-04T15:04:05Z",
        });

        var surface = string.Join("\n", result.Surfaces());
        Assert.DoesNotContain("solo.person@example.test", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("219-09-9999", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("219099999", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("+1-202-555-0199", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("1984-06-17", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("Z99.1", surface, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2026-03-04T15:04:05Z", result.Fields["occurred_at"]);
        Assert.Equal("open", result.Fields["status"]);
    }

    [Fact]
    public void Unknown_field_is_dropped_and_listed()
    {
        var result = Pipeline("project-unknown").Redact(new Dictionary<string, string>
        {
            ["favorite_color"] = "blue-whale-secret",
            ["status"] = "open",
        });

        Assert.Equal(["favorite_color"], result.UnclassifiedFields);
        Assert.DoesNotContain("blue-whale-secret", string.Join("\n", result.Surfaces()), StringComparison.Ordinal);
        Assert.Equal("open", result.Fields["status"]);
    }

    [Fact]
    public void Echo_removes_a_masked_name_from_notes()
    {
        const string name = "Quilla Narthex";
        var result = Pipeline("project-echo").Redact(new Dictionary<string, string>
        {
            ["member_name"] = name,
            ["notes"] = "Hello " + name + " from the counter.",
            ["status"] = "open",
        });

        Assert.DoesNotContain(name, string.Join("\n", result.Surfaces()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QuillaNarthex", string.Join("\n", result.Surfaces()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canary_marker_is_removed_from_free_text()
    {
        var result = Pipeline("project-canary").Redact(new Dictionary<string, string>
        {
            ["notes"] = "marker SYNTHETIC-NAME-QUILLA-001 in a note",
            ["status"] = "open",
        });

        Assert.DoesNotContain("SYNTHETIC-NAME-QUILLA-001", string.Join("\n", result.Surfaces()), StringComparison.Ordinal);
    }

    [Fact]
    public void Keep_field_with_an_email_is_scrubbed()
    {
        var result = Pipeline("project-keep").Redact(new Dictionary<string, string>
        {
            ["status"] = "reach me at solo.person@example.test",
        });

        Assert.DoesNotContain("solo.person@example.test", result.Fields["status"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reach me at", result.Fields["status"], StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_body_is_capped_after_secrets_are_gone()
    {
        var notes = new string('a', 20_000) + " canary.person@example.test";
        var result = Pipeline("project-cap").Redact(new Dictionary<string, string>
        {
            ["notes"] = notes,
            ["email"] = "canary.person@example.test",
            ["status"] = "open",
        });

        Assert.True(result.Truncated);
        Assert.True(result.BodyBytes <= RedactionPipeline.MaxBodyBytes);
        foreach (var surface in result.Surfaces())
            Assert.DoesNotContain("canary.person@example.test", surface, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tokens_do_not_join_across_projects()
    {
        var fields = new Dictionary<string, string> { ["account_ref"] = "ACCT-SYNTH-100" };
        var left = Pipeline("project-a").Redact(fields);
        var right = Pipeline("project-b").Redact(fields);
        var again = Pipeline("project-a").Redact(fields);

        Assert.Equal(left.Fields["account_ref"], again.Fields["account_ref"]);
        Assert.NotEqual(left.Fields["account_ref"], right.Fields["account_ref"]);
        Assert.DoesNotContain("ACCT-SYNTH-100", left.Fields["account_ref"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ACCT-SYNTH-100", right.Fields["account_ref"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_is_not_the_raw_value()
    {
        var result = Pipeline("project-hash").Redact(new Dictionary<string, string>
        {
            ["loyalty_code"] = "LOYAL-SYNTH-100",
        });

        Assert.StartsWith("hash_", result.Fields["loyalty_code"], StringComparison.Ordinal);
        Assert.DoesNotContain("LOYAL-SYNTH-100", result.Fields["loyalty_code"], StringComparison.OrdinalIgnoreCase);
    }

    private static RedactionPipeline Pipeline(string projectId)
    {
        var material = Encoding.UTF8.GetBytes("synthetic-key-" + projectId + "-0123456789");
        return DisputeRedactionProfile.Create(new ProjectTokenKey(projectId, material), Salt);
    }
}
