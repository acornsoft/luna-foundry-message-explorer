// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net.Http;
using System.Net.Sockets;
using Acornsoft.MessageExplorer.Brokers;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>Maps SDK and identity failures to <see cref="BrokerException"/>. The message shown to the user is ours, not the SDK's.</summary>
public static class ServiceBusErrorMapper
{
    public static BrokerException Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is BrokerException broker)
            return broker;

        var kind = Classify(exception);
        return new BrokerException(kind, BrokerException.Explain(kind), exception);
    }

    public static BrokerErrorKind Classify(Exception exception) => exception switch
    {
        CredentialUnavailableException => BrokerErrorKind.Unauthenticated,
        AuthenticationFailedException => BrokerErrorKind.Unauthenticated,
        UnauthorizedAccessException => BrokerErrorKind.Forbidden,
        ServiceBusException sb => sb.Reason switch
        {
            ServiceBusFailureReason.MessagingEntityNotFound => BrokerErrorKind.NotFound,
            ServiceBusFailureReason.ServiceBusy => BrokerErrorKind.Throttled,
            ServiceBusFailureReason.QuotaExceeded => BrokerErrorKind.Throttled,
            ServiceBusFailureReason.ServiceTimeout => BrokerErrorKind.Timeout,
            ServiceBusFailureReason.ServiceCommunicationProblem => BrokerErrorKind.Unreachable,
            _ => BrokerErrorKind.Unknown,
        },
        RequestFailedException request => request.Status switch
        {
            0 => BrokerErrorKind.Unreachable,
            401 => BrokerErrorKind.Unauthenticated,
            403 => BrokerErrorKind.Forbidden,
            404 => BrokerErrorKind.NotFound,
            408 => BrokerErrorKind.Timeout,
            429 or 503 => BrokerErrorKind.Throttled,
            _ => BrokerErrorKind.Unknown,
        },
        HttpRequestException => BrokerErrorKind.Unreachable,
        SocketException => BrokerErrorKind.Unreachable,
        TimeoutException => BrokerErrorKind.Timeout,
        FormatException => BrokerErrorKind.InvalidConnection,
        ArgumentException => BrokerErrorKind.InvalidConnection,
        AggregateException aggregate when aggregate.InnerExceptions.Count == 1 => Classify(aggregate.InnerExceptions[0]),
        _ => BrokerErrorKind.Unknown,
    };
}
