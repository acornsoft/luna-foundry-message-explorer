// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

/// <summary>
/// A secret typed by the user at runtime. It is never written to disk by this code,
/// and <see cref="ToString"/> never returns the value.
/// </summary>
public sealed class RuntimeSecret
{
    private readonly string _value;

    public RuntimeSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A secret value is required.", nameof(value));
        _value = value.Trim();
    }

    public string Reveal() => _value;

    public override string ToString() => "***";
}

/// <summary>Where and how to connect. The endpoint is not secret. The secret is optional and in memory only.</summary>
public sealed record BrokerConnection
{
    private BrokerConnection(string providerId, AuthMode authMode, string? endpoint, RuntimeSecret? secret, object? credential)
    {
        Credential = credential;
        ProviderId = providerId;
        AuthMode = authMode;
        Endpoint = endpoint;
        Secret = secret;
    }

    public string ProviderId { get; }

    public AuthMode AuthMode { get; }

    /// <summary>A host name such as a namespace FQDN. Null when the secret carries the endpoint.</summary>
    public string? Endpoint { get; }

    public RuntimeSecret? Secret { get; }

    /// <summary>
    /// A provider-specific signed-in account credential (for Azure, a TokenCredential), or null to use the
    /// provider's default. Held in memory only and never part of <see cref="ToString"/>.
    /// </summary>
    public object? Credential { get; }

    public static BrokerConnection WithEntra(string providerId, string endpoint, object? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return new BrokerConnection(providerId, AuthMode.Entra, endpoint.Trim(), null, credential);
    }

    /// <param name="credential">Used when the pasted text names only an endpoint and the account signs in.</param>
    public static BrokerConnection WithConnectionString(string providerId, RuntimeSecret connectionString, object? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(connectionString);
        return new BrokerConnection(providerId, AuthMode.ConnectionString, null, connectionString, credential);
    }

    public override string ToString() =>
        $"{ProviderId} via {AuthMode} to {Endpoint ?? "(endpoint in secret)"}";
}
