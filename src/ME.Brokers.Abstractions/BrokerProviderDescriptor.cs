// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

/// <summary>What a provider can do. Every flag is a read. There is no write flag on purpose.</summary>
[Flags]
public enum BrokerCapabilities
{
    None = 0,
    ListQueues = 1 << 0,
    ListTopics = 1 << 1,
    ListSubscriptions = 1 << 2,
    ActiveCounts = 1 << 3,
    DeadLetterCounts = 1 << 4,
    ScheduledCounts = 1 << 5,
    TransferDeadLetterCounts = 1 << 6,
    EntityStatus = 1 << 7,
    EntitySize = 1 << 8,
    Peek = 1 << 9,
}

public enum ProviderAvailability
{
    Enabled,
    Planned,
}

public enum AuthMode
{
    /// <summary>Microsoft Entra ID through the default credential chain. No secret is typed.</summary>
    Entra,

    /// <summary>A connection string typed at runtime. It is held in memory only.</summary>
    ConnectionString,
}

/// <summary>Describes one broker family. Planned providers are listed so the user sees the road map, but cannot connect.</summary>
public sealed record BrokerProviderDescriptor(
    string Id,
    string DisplayName,
    ProviderAvailability Availability,
    BrokerCapabilities Capabilities,
    IReadOnlyList<AuthMode> AuthModes,
    string EndpointHint,
    string Note)
{
    public bool IsEnabled => Availability == ProviderAvailability.Enabled;

    public bool Supports(BrokerCapabilities capability) => (Capabilities & capability) == capability;
}
