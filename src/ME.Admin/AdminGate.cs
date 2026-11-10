// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Admin;

public enum AdminDecisionKind
{
    Forbidden,
    Invalid,
    TemplateReady,
    RequestFiled,
    Decided,
}

public sealed record AuditRecord(
    string Action,
    string Outcome,
    string Actor,
    DateTimeOffset At,
    string Entity,
    string Reason);

public sealed class ApprovalTicket
{
    public ApprovalTicket(
        string id,
        string tool,
        string requesterId,
        string entity,
        IReadOnlyList<long> sequences,
        DateTimeOffset expiresAt)
    {
        Id = id;
        Tool = tool;
        RequesterId = requesterId;
        Entity = entity;
        Sequences = sequences;
        ExpiresAt = expiresAt;
        Status = "pending";
    }

    public string Id { get; }
    public string Tool { get; }
    public string RequesterId { get; }
    public string Entity { get; }
    public IReadOnlyList<long> Sequences { get; }
    public int Count => Sequences.Count;
    public DateTimeOffset ExpiresAt { get; }
    public string Status { get; private set; }

    public bool Executed => false;

    public void Mark(string status)
    {
        if (status is not ("approved" or "rejected" or "expired"))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown approval status.");
        Status = status;
    }
}

public sealed class SelfApprovalException : InvalidOperationException
{
    public SelfApprovalException(string message)
        : base(message)
    {
    }
}

public sealed class AdminDecision
{
    public AdminDecision(
        AdminDecisionKind kind,
        int statusCode,
        string message,
        string? templateText,
        AuditRecord audit,
        bool executed,
        ApprovalTicket? ticket)
    {
        Kind = kind;
        StatusCode = statusCode;
        Message = message;
        TemplateText = templateText;
        Audit = audit;
        Executed = executed;
        Ticket = ticket;
    }

    public AdminDecisionKind Kind { get; }
    public int StatusCode { get; }
    public string Message { get; }
    public string? TemplateText { get; }
    public AuditRecord Audit { get; }
    public bool Executed { get; }
    public ApprovalTicket? Ticket { get; }
}

public static class EventGridTapTemplate
{
    public const string ResourceName = "eventgrid-diagnostic-subscription.bicep";

    public static string Load()
    {
        var assembly = typeof(EventGridTapTemplate).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The diagnostic subscription template is not in the admin assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class AdminGate
{
    private readonly bool _enabled;
    private readonly IClock _clock;
    private readonly List<AuditRecord> _audit = [];
    private readonly List<ApprovalTicket> _approvals = [];

    public AdminGate(bool enabled, IClock clock)
    {
        _enabled = enabled;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool Enabled => _enabled;
    public IReadOnlyList<AuditRecord> Audit => _audit;
    public IReadOnlyList<ApprovalTicket> Approvals => _approvals;

    public AdminDecision ExportEventGridTapTemplate(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Allowed(caller))
            return Deny("export-eventgrid-tap-template", caller, "eventgrid");

        var template = EventGridTapTemplate.Load();
        var audit = Record("export-eventgrid-tap-template", "template-only-not-created", caller, "eventgrid", "owner-applied-template");
        return new AdminDecision(
            AdminDecisionKind.TemplateReady,
            200,
            "Template only. The explorer did not create a subscription.",
            template,
            audit,
            executed: false,
            ticket: null);
    }

    public AdminDecision FilePurge(
        Caller caller,
        string entity,
        string reason,
        bool typedConfirmation,
        bool runtimeProfile,
        string? secondApproverId,
        IReadOnlyList<long> sequences)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Allowed(caller))
            return Deny("request_purge", caller, entity);
        if (runtimeProfile && (string.IsNullOrWhiteSpace(secondApproverId) || secondApproverId == caller.Id))
            return Invalid("request_purge", caller, entity, "A runtime purge needs a second person.");

        return FileRequest("request_purge", caller, entity, reason, typedConfirmation, sequences);
    }

    public AdminDecision FileResubmit(
        Caller caller,
        string entity,
        string reason,
        bool typedConfirmation,
        IReadOnlyList<long> sequences)
    {
        return FileRequest("request_resubmit", caller, entity, reason, typedConfirmation, sequences);
    }

    public AdminDecision FileMove(
        Caller caller,
        string entity,
        string reason,
        bool typedConfirmation,
        IReadOnlyList<long> sequences)
    {
        return FileRequest("request_move_dead_letter", caller, entity, reason, typedConfirmation, sequences);
    }

    public AdminDecision Decide(string ticketId, Caller human, bool approve)
    {
        ArgumentNullException.ThrowIfNull(human);
        var ticket = _approvals.SingleOrDefault(item => item.Id == ticketId)
            ?? throw new InvalidOperationException("Unknown approval ticket.");
        if (human.Id == ticket.RequesterId)
            throw new SelfApprovalException("The requester cannot approve their own request.");

        if (_clock.UtcNow > ticket.ExpiresAt)
        {
            ticket.Mark("expired");
            var expired = Record("decide-" + ticket.Tool, "expired", human, ticket.Entity, "window elapsed");
            return new AdminDecision(AdminDecisionKind.Decided, 409, "The request expired.", null, expired, executed: false, ticket);
        }

        ticket.Mark(approve ? "approved" : "rejected");
        var audit = Record("decide-" + ticket.Tool, ticket.Status, human, ticket.Entity, "human decision");
        return new AdminDecision(
            AdminDecisionKind.Decided,
            200,
            "Recorded. Nothing was sent or removed.",
            null,
            audit,
            executed: false,
            ticket);
    }

    private AdminDecision FileRequest(
        string tool,
        Caller caller,
        string entity,
        string reason,
        bool typedConfirmation,
        IReadOnlyList<long> sequences)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sequences);
        if (!Allowed(caller))
            return Deny(tool, caller, entity);
        if (!typedConfirmation || string.IsNullOrWhiteSpace(reason))
            return Invalid(tool, caller, entity, "A typed confirmation and a reason are required.");

        var ticket = new ApprovalTicket(
            "apr-" + _approvals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tool,
            caller.Id,
            entity,
            sequences.ToArray(),
            _clock.UtcNow.AddMinutes(15));
        _approvals.Add(ticket);
        var audit = Record(tool, "request-filed", caller, entity, reason);
        return new AdminDecision(
            AdminDecisionKind.RequestFiled,
            202,
            "Request filed. Nothing was sent or removed.",
            null,
            audit,
            executed: false,
            ticket);
    }

    private bool Allowed(Caller caller) =>
        _enabled && caller.HasRole(Roles.ExplorerAdmin);

    private AdminDecision Deny(string action, Caller caller, string entity)
    {
        var audit = Record(action, "denied", caller, entity, "");
        return new AdminDecision(
            AdminDecisionKind.Forbidden,
            403,
            "Admin is off or the caller is not explorer-admin.",
            null,
            audit,
            executed: false,
            ticket: null);
    }

    private AdminDecision Invalid(string action, Caller caller, string entity, string reason)
    {
        var audit = Record(action, "invalid", caller, entity, reason);
        return new AdminDecision(AdminDecisionKind.Invalid, 400, reason, null, audit, executed: false, ticket: null);
    }

    private AuditRecord Record(string action, string outcome, Caller caller, string entity, string reason)
    {
        var record = new AuditRecord(action, outcome, caller.Id, _clock.UtcNow, entity, reason);
        _audit.Add(record);
        return record;
    }
}

public static class AdminAssembly
{
    public static bool ReferencesEventGridManagement()
    {
        return typeof(AdminGate).Assembly
            .GetReferencedAssemblies()
            .Any(name => name.Name?.Contains("EventGrid", StringComparison.Ordinal) == true);
    }
}
