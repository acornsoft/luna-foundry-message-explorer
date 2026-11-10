// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.Tests;

public class MoveAuditTests
{
    [Fact]
    public async Task Every_move_writes_started_one_line_per_message_and_finished()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(2);

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(2));

        var entries = lab.Audit.Entries;
        Assert.Equal(["move-started", "message-moved", "message-moved", "move-finished"], entries.Select(e => e.Event));
        Assert.All(entries, e => Assert.Equal(result.MoveId, e.MoveId));
        var started = entries[0];
        Assert.Equal("alice@contoso.example", started.Actor);
        Assert.Equal($"{Lab.Ns}/orders/$deadletterqueue", started.Source);
        Assert.Equal($"{Lab.Ns}/orders", started.Target);
        Assert.Equal("DeadLetterToParent", started.Route);
        Assert.Equal("transaction", started.Mode);
        Assert.Equal(2, started.Requested);
        Assert.Equal(["order-001", "order-002"], entries.Where(e => e.Event == "message-moved").Select(e => e.MessageId));
        Assert.Equal("Completed", entries[^1].Outcome);
        Assert.Equal(2, entries[^1].Moved);
    }

    [Fact]
    public async Task The_jsonl_file_has_no_bodies_properties_or_secrets_and_only_the_user_can_read_it()
    {
        var folder = Directory.CreateTempSubdirectory("lfme-audit-");
        try
        {
            var path = Path.Combine(folder.FullName, "audit", "moves.jsonl");
            var log = new JsonlMoveAuditLog(path);
            var moveMode = new MoveModeSwitch();
            moveMode.TurnOn();
            var broker = new InMemoryMoveBroker(Lab.Ns).AddQueue("orders");
            var dlq = MoveEndpoint.Queue(Lab.Ns, "orders", true);
            var secretProps = new Dictionary<string, object?>(StringComparer.Ordinal) { ["apiToken"] = "PROPERTY-VALUE-NOT-LOGGED" };
            broker.Seed(dlq, InMemoryMoveBroker.Message("o-1", body: "{\"card\":\"BODY-NOT-LOGGED\"}", properties: secretProps), "Poison");
            broker.Seed(dlq, InMemoryMoveBroker.Message("o-2"), "Poison");
            var engine = new MoveEngine(moveMode, log) { ReceiveWait = TimeSpan.Zero };
            var plan = await engine.PrepareAsync(new MoveRequest(dlq, MoveEndpoint.Queue(Lab.Ns, "orders"), MoveSelection.OldestFirst(2)), new InMemoryMoveTransport(broker));
            plan.Confirm();
            await engine.ExecuteAsync(plan);

            var lines = File.ReadAllLines(path);
            Assert.Equal(4, lines.Length);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("BODY-NOT-LOGGED", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PROPERTY-VALUE-NOT-LOGGED", text, StringComparison.Ordinal);
            Assert.DoesNotContain("apiToken", text, StringComparison.Ordinal);
            Assert.Contains("\"messageId\":\"o-1\"", text, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void The_audit_entry_has_no_field_that_could_hold_a_body_or_secret()
    {
        var names = typeof(MoveAuditEntry).GetProperties().Select(p => p.Name).ToList();
        foreach (var banned in new[] { "Body", "Properties", "ApplicationProperties", "Secret", "Key", "Token", "ConnectionString", "Password" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locked_and_outgoing_messages_never_print_their_body()
    {
        var message = InMemoryMoveBroker.Message("o-1", body: "BODY-NOT-PRINTED");
        Assert.DoesNotContain("BODY", message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_messages_show_only_safe_words()
    {
        var lab = new Lab();
        lab.SeedDeadLetters(1);
        lab.Broker.Fault = (operation, _) => operation == "send" ? new InvalidOperationException("Endpoint=sb://x/;" + "Shared" + "AccessKeyName=root;KEYVALUE") : null;

        var result = await lab.MoveAsync(lab.OrdersDlq, lab.Orders, MoveSelection.OldestFirst(1));

        Assert.Equal(MoveOutcome.Failed, result.Outcome);
        Assert.Contains("The broker returned an error (InvalidOperationException).", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("KEYVALUE", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(lab.Audit.Entries, e => (e.Reason ?? string.Empty).Contains("KEYVALUE", StringComparison.Ordinal));
    }
}
