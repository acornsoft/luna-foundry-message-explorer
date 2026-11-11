// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Admin;
using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Admin.Tests;

public class AdminGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Template_is_owner_applied_and_has_no_key_material()
    {
        var text = EventGridTapTemplate.Load();
        Assert.Contains("The explorer never deploys this file.", text, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EventGrid/topics/eventSubscriptions", text, StringComparison.Ordinal);
        Assert.Contains("ServiceBusQueue", text, StringComparison.Ordinal);
        Assert.Contains("deliveryWithResourceIdentity", text, StringComparison.Ordinal);
        Assert.Contains("maxSizeInMegabytes", text, StringComparison.Ordinal);
        Assert.Contains("defaultMessageTimeToLive", text, StringComparison.Ordinal);
        Assert.Contains("diag-message-explorer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared" + "AccessKey", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Account" + "Key=", text, StringComparison.Ordinal);
        Assert.False(AdminAssembly.ReferencesEventGridManagement());
    }

    [Fact]
    public void Admin_off_returns_403_and_no_template()
    {
        var gate = new AdminGate(enabled: false, new FixedClock(Now));
        var decision = gate.ExportEventGridTapTemplate(Admin());

        Assert.Equal(403, decision.StatusCode);
        Assert.Null(decision.TemplateText);
        Assert.False(decision.Executed);
        Assert.Single(gate.Audit);
        Assert.Equal("denied", gate.Audit[0].Outcome);
    }

    [Fact]
    public void Admin_without_the_role_is_forbidden()
    {
        var gate = new AdminGate(enabled: true, new FixedClock(Now));
        var decision = gate.ExportEventGridTapTemplate(Viewer());
        Assert.Equal(403, decision.StatusCode);
        Assert.Null(decision.TemplateText);
    }

    [Fact]
    public void Export_returns_the_template_and_one_audit_row()
    {
        var gate = new AdminGate(enabled: true, new FixedClock(Now));
        var decision = gate.ExportEventGridTapTemplate(Admin());

        Assert.Equal(200, decision.StatusCode);
        Assert.Equal(EventGridTapTemplate.Load(), decision.TemplateText);
        Assert.False(decision.Executed);
        Assert.Single(gate.Audit);
        Assert.Equal("template-only-not-created", gate.Audit[0].Outcome);
        Assert.Contains("did not create", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_request_is_filed_and_not_executed()
    {
        var gate = new AdminGate(enabled: true, new FixedClock(Now));
        var missing = gate.FilePurge(Admin(), "disputes/case-sync", "", false, false, null, [9]);
        Assert.Equal(400, missing.StatusCode);

        var runtime = gate.FilePurge(Admin(), "disputes/case-sync", "drill", true, true, Admin().Id, [9]);
        Assert.Equal(400, runtime.StatusCode);

        var filed = gate.FilePurge(Admin(), "disputes/case-sync", "drill", true, true, "second-person", [9, 10]);
        Assert.Equal(202, filed.StatusCode);
        Assert.False(filed.Executed);
        Assert.NotNull(filed.Ticket);
        Assert.Equal("pending", filed.Ticket!.Status);
        Assert.Equal([9L, 10L], filed.Ticket.Sequences.ToArray());
        Assert.Equal(Now.AddMinutes(15), filed.Ticket.ExpiresAt);
        Assert.False(filed.Ticket.Executed);
    }

    [Fact]
    public void Requester_cannot_approve_and_a_human_approval_still_does_not_execute()
    {
        var clock = new FixedClock(Now);
        var gate = new AdminGate(enabled: true, clock);
        var filed = gate.FileResubmit(Admin(), "disputes/case-sync", "copy", true, [4]);
        Assert.NotNull(filed.Ticket);

        Assert.Throws<SelfApprovalException>(() => gate.Decide(filed.Ticket!.Id, Admin(), approve: true));
        Assert.Equal("pending", filed.Ticket.Status);
        Assert.False(filed.Ticket.Executed);

        var human = gate.Decide(filed.Ticket.Id, new Caller("human-1", [Roles.ExplorerAdmin], [Scopes.AdminRequest]), true);
        Assert.Equal("approved", human.Ticket!.Status);
        Assert.False(human.Executed);
        Assert.False(human.Ticket.Executed);

        var later = gate.FileMove(Admin(), "disputes/case-sync", "move", true, [5]);
        clock.UtcNow = Now.AddMinutes(16);
        var expired = gate.Decide(later.Ticket!.Id, new Caller("human-1", [Roles.ExplorerAdmin], [Scopes.AdminRequest]), true);
        Assert.Equal("expired", expired.Ticket!.Status);
        Assert.False(expired.Executed);
    }

    private static Caller Admin() => new("admin-1", [Roles.ExplorerAdmin], [Scopes.AdminRequest]);

    private static Caller Viewer() => new("viewer-1", [Roles.Viewer], [Scopes.ReadMetadata]);

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; set; }
    }
}
