// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus;

/// <summary>How a connection will be opened. Pure, so auth-mode selection is testable without a network.</summary>
public sealed record ServiceBusConnectionPlan(AuthMode Mode, string FullyQualifiedNamespace);

/// <summary>Azure Service Bus behind the broker-neutral provider interface.</summary>
public sealed class ServiceBusBrokerProvider : IBrokerProvider
{
    public const string ProviderId = "azure-service-bus";
    public const string DefaultSuffix = ".servicebus.windows.net";

    private readonly Func<TokenCredential> _credentialFactory;
    private readonly Func<ServiceBusAdministrationClient, IServiceBusAdminReader> _readerFactory;

    public ServiceBusBrokerProvider()
        : this(() => new DefaultAzureCredential(), client => new AdministrationClientReader(client))
    {
    }

    public ServiceBusBrokerProvider(
        Func<TokenCredential> credentialFactory,
        Func<ServiceBusAdministrationClient, IServiceBusAdminReader> readerFactory)
    {
        _credentialFactory = credentialFactory ?? throw new ArgumentNullException(nameof(credentialFactory));
        _readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
    }

    public static BrokerProviderDescriptor Descriptor { get; } = new(
        ProviderId,
        "Azure Service Bus",
        ProviderAvailability.Enabled,
        BrokerCapabilities.ListQueues
            | BrokerCapabilities.ListTopics
            | BrokerCapabilities.ListSubscriptions
            | BrokerCapabilities.ActiveCounts
            | BrokerCapabilities.DeadLetterCounts
            | BrokerCapabilities.ScheduledCounts
            | BrokerCapabilities.TransferDeadLetterCounts
            | BrokerCapabilities.EntityStatus
            | BrokerCapabilities.EntitySize
            | BrokerCapabilities.Peek,
        [AuthMode.Entra, AuthMode.ConnectionString],
        "namespace, for example contoso-synthetic.servicebus.windows.net",
        "Read-only inventory through the management plane. Entra sign-in or a connection string held in memory.");

    BrokerProviderDescriptor IBrokerProvider.Descriptor => Descriptor;

    public string? Validate(BrokerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.ProviderId != ProviderId)
            return $"This connection is for '{connection.ProviderId}', not Azure Service Bus.";

        return connection.AuthMode switch
        {
            AuthMode.Entra => NormalizeNamespace(connection.Endpoint, out _),
            AuthMode.ConnectionString => ValidateConnectionString(connection.Secret, out _),
            _ => "Unsupported sign-in mode.",
        };
    }

    public static ServiceBusConnectionPlan Plan(BrokerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        switch (connection.AuthMode)
        {
            case AuthMode.Entra:
            {
                var error = NormalizeNamespace(connection.Endpoint, out var fqdn);
                if (error is not null)
                    throw new BrokerException(BrokerErrorKind.InvalidConnection, error);
                return new ServiceBusConnectionPlan(AuthMode.Entra, fqdn);
            }

            case AuthMode.ConnectionString:
            {
                var error = ValidateConnectionString(connection.Secret, out var fqdn);
                if (error is not null)
                    throw new BrokerException(BrokerErrorKind.InvalidConnection, error);
                return new ServiceBusConnectionPlan(AuthMode.ConnectionString, fqdn);
            }

            default:
                throw new BrokerException(BrokerErrorKind.InvalidConnection, "Unsupported sign-in mode.");
        }
    }

    public IEntityInventory OpenInventory(BrokerConnection connection)
    {
        var plan = Plan(connection);
        var client = plan.Mode == AuthMode.Entra
            ? new ServiceBusAdministrationClient(plan.FullyQualifiedNamespace, _credentialFactory())
            : new ServiceBusAdministrationClient(connection.Secret!.Reveal());
        return new ServiceBusEntityInventory(Descriptor, _readerFactory(client));
    }

    /// <summary>
    /// Accepts "contoso", "contoso.servicebus.windows.net", or "https://contoso.servicebus.windows.net/".
    /// Returns null and the host name when valid.
    /// </summary>
    public static string? NormalizeNamespace(string? input, out string fullyQualifiedNamespace)
    {
        fullyQualifiedNamespace = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return "Enter the Service Bus namespace.";

        var value = input.Trim();
        foreach (var scheme in new[] { "sb://", "https://", "amqps://" })
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                value = value[scheme.Length..];
        }

        value = value.TrimEnd('/');
        if (value.Contains('=', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
            return "That looks like a connection string. Choose the connection string option instead.";
        if (value.Contains('/', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
            return "Enter only the namespace host name, without a path or port.";

        if (!value.Contains('.', StringComparison.Ordinal))
            value += DefaultSuffix;

        if (Uri.CheckHostName(value) != UriHostNameType.Dns)
            return "The namespace is not a valid host name.";

        var label = value.Split('.')[0];
        if (label.Length is < 6 or > 50)
            return "A Service Bus namespace name is 6 to 50 characters.";

        fullyQualifiedNamespace = value.ToLowerInvariant();
        return null;
    }

    private static string? ValidateConnectionString(RuntimeSecret? secret, out string fullyQualifiedNamespace)
    {
        fullyQualifiedNamespace = string.Empty;
        if (secret is null)
            return "Enter a connection string.";

        ServiceBusConnectionStringProperties properties;
        try
        {
            properties = ServiceBusConnectionStringProperties.Parse(secret.Reveal());
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return "The connection string could not be read. It needs an Endpoint and a key or signature.";
        }

        if (string.IsNullOrEmpty(properties.FullyQualifiedNamespace))
            return "The connection string has no Endpoint.";

        fullyQualifiedNamespace = properties.FullyQualifiedNamespace;
        return null;
    }
}
