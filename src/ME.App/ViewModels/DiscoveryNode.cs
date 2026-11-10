// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum DiscoveryNodeKind
{
    Account,
    Subscription,
    Namespace,
    Message,
    Error,
}

/// <summary>One row of the Account &gt; Subscription &gt; Namespace tree. Rows start expanded.</summary>
public sealed class DiscoveryNode : ViewModelBase
{
    private bool _isExpanded = true;

    public DiscoveryNode(DiscoveryNodeKind kind, string label, string? detail = null, NamespaceRef? serviceBusNamespace = null, AccountItemViewModel? account = null)
    {
        Kind = kind;
        Label = label;
        Detail = detail;
        Namespace = serviceBusNamespace;
        Account = account;
    }

    public DiscoveryNodeKind Kind { get; }

    public string Label { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsError => Kind == DiscoveryNodeKind.Error;

    public bool IsNamespace => Kind == DiscoveryNodeKind.Namespace;

    public NamespaceRef? Namespace { get; }

    public AccountItemViewModel? Account { get; }

    public ObservableCollection<DiscoveryNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public override string ToString() => Detail is null ? Label : $"{Label} ({Detail})";
}
