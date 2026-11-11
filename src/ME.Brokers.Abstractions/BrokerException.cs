// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

public enum BrokerErrorKind
{
    InvalidConnection,
    Unauthenticated,
    Forbidden,
    NotFound,
    Throttled,
    Unreachable,
    Timeout,
    ProviderNotEnabled,
    Unknown,
}

/// <summary>A broker failure mapped to something the UI can explain. The message never carries a secret.</summary>
public sealed class BrokerException : Exception
{
    public BrokerException(BrokerErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BrokerErrorKind Kind { get; }

    public bool IsRetryable => Kind is BrokerErrorKind.Throttled or BrokerErrorKind.Unreachable or BrokerErrorKind.Timeout;

    public static string Explain(BrokerErrorKind kind) => kind switch
    {
        BrokerErrorKind.InvalidConnection => "The connection details are not valid. Check the namespace or connection string.",
        BrokerErrorKind.Unauthenticated => "Sign-in failed. Sign in with an account that can reach this namespace, or check the connection string.",
        BrokerErrorKind.Forbidden => "Signed in, but this identity cannot read the namespace. Listing entities needs a management read role such as Reader or Azure Service Bus Data Owner.",
        BrokerErrorKind.NotFound => "The namespace or entity was not found.",
        BrokerErrorKind.Throttled => "The broker is busy. Try again in a moment.",
        BrokerErrorKind.Unreachable => "The broker could not be reached. Check the network and the host name.",
        BrokerErrorKind.Timeout => "The broker did not answer in time.",
        BrokerErrorKind.ProviderNotEnabled => "This provider is planned and is not enabled yet.",
        _ => "The broker returned an unexpected error.",
    };
}
