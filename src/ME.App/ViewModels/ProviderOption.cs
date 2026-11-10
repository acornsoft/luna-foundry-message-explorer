// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public sealed class ProviderOption : ViewModelBase
{
    private bool _isSelected;

    public ProviderOption(BrokerProviderDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public BrokerProviderDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public bool IsEnabled => Descriptor.IsEnabled;

    public string Label => IsEnabled ? Descriptor.DisplayName : $"{Descriptor.DisplayName} (planned)";

    public string Note => Descriptor.Note;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsEnabled)
                value = false;
            Set(ref _isSelected, value);
        }
    }
}
