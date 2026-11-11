// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Demo;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>How Move mode reaches the open namespace: a transport to open, or a plain reason it can't.</summary>
public sealed record MoveAccess(Func<IMoveTransport>? Open, string? Refusal)
{
    public static MoveAccess Refused(string reason) => new(null, reason);
}

/// <summary>The confirm dialog. The window shows a real one; tests answer yes or no.</summary>
public interface IMoveConfirmer
{
    Task<bool> ConfirmAsync(MovePlan plan);
}

/// <summary>What Move mode needs from the computer: the audit log and the way to open a transport for a connection.</summary>
public sealed class MoveServices(IMoveAuditLog audit, string auditLocation, Func<IEntityInventory, BrokerConnection?, MoveAccess> resolve)
{
    public IMoveAuditLog Audit { get; } = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <summary>Where the audit log is, in words for Move mode.</summary>
    public string AuditLocation { get; } = auditLocation;

    public MoveAccess Resolve(IEntityInventory inventory, BrokerConnection? connection) => resolve(inventory, connection);

    /// <summary>The audit log in the app's config folder (audit/moves.jsonl, readable only by you).</summary>
    public static MoveServices ForThisComputer(Func<string, string?> environment)
    {
        var path = Path.Combine(AppPaths.ConfigDirectory(environment), "audit", "moves.jsonl");
        return new MoveServices(new JsonlMoveAuditLog(path), path, Resolver(null));
    }

    /// <summary>Audit in memory. Used when nothing else is supplied (tests of other screens).</summary>
    public static MoveServices InMemory() => new(new MemoryMoveAuditLog(), "kept in memory for this window", Resolver(null));

    /// <summary>The synthetic demo: every namespace opens the in-memory demo broker; audit in a temp file.</summary>
    public static MoveServices ForDemo(InMemoryMoveBroker broker)
    {
        var path = Path.Combine(Path.GetTempPath(), "lfme-demo", "audit", "moves.jsonl");
        return new MoveServices(new JsonlMoveAuditLog(path), path, Resolver(broker));
    }

    public static Func<IEntityInventory, BrokerConnection?, MoveAccess> Resolver(InMemoryMoveBroker? demo) => (inventory, connection) =>
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (demo is not null && (inventory.Provider.Id == SyntheticDemoProvider.Id || inventory is ArmEntityInventory))
            return new MoveAccess(() => new InMemoryMoveTransport(demo, providerName: "Synthetic demo"), null);
        if (inventory is ArmEntityInventory arm)
            return new MoveAccess(() => ServiceBusMoveTransport.Open(arm.Namespace.FullyQualifiedNamespace, arm.Credential), null);
        if (connection is { ProviderId: ServiceBusBrokerProvider.ProviderId })
            return new MoveAccess(() => ServiceBusMoveTransport.Open(connection), null);
        return MoveAccess.Refused(MoveProviders.Refusal(inventory.Provider.Id));
    };
}
