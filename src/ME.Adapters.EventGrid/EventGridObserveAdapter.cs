// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Adapters.EventGrid;

public sealed class EventGridObserveAdapter
{
    public AdapterCapabilities Capabilities => ProviderMatrix.EventGrid;

    public Task<IReadOnlyList<ObservedMessage>> PeekAsync(string entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entity))
            throw new ArgumentException("Entity is required.", nameof(entity));

        throw new ReadRefusedException(
            "Event Grid has no read API. A diagnostic subscription is a write (tap setup). The owner applies the admin template. Observe mode does not create it.");
    }
}
