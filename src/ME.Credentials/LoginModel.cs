// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Credentials;

/// <summary>A system the app can log in to.</summary>
public enum LoginSystem
{
    Entra,
    ServiceBus,
    EventHubs,
    Kafka,
    RabbitMq,
    AwsSqsSns,
    Emulator,
}

/// <summary>How a login authenticates. Each kind fixes which non-secret fields it has and whether it has a secret.</summary>
public enum LoginKind
{
    EntraAccount,
    ServiceBusConnectionString,
    ServiceBusEntra,
    EventHubsConnectionString,
    EventHubsEntra,
    KafkaSasl,
    KafkaMutualTls,
    RabbitMqPassword,
    AwsAccessKey,
    AwsProfile,
    EmulatorConnectionString,
}

/// <summary>A non-secret field of a login, such as an endpoint or a user name.</summary>
public sealed record LoginField(string Name, string Label, string Watermark, bool Required = false, IReadOnlyList<string>? Choices = null);

/// <summary>What a kind of login looks like: its system, its non-secret fields, and its secret, if any.</summary>
public sealed record LoginKindInfo(
    LoginKind Kind,
    LoginSystem System,
    string Label,
    IReadOnlyList<LoginField> Fields,
    string? SecretLabel,
    bool SecretRequired,
    bool UsesAccount = false)
{
    public bool HasSecret => SecretLabel is not null;
}

/// <summary>The catalog of login kinds, per system.</summary>
public static class LoginKinds
{
    public static IReadOnlyList<LoginKindInfo> All { get; } =
    [
        new(LoginKind.EntraAccount, LoginSystem.Entra, "Microsoft Entra account",
            [new("username", "User", "alice@contoso.example", Required: true), new("tenantId", "Tenant", string.Empty), new("requestedTenant", "Signed in to", string.Empty),
             new("homeAccountId", "Account ID", string.Empty), new("authenticationRecord", "Authentication record", string.Empty), new("signedOut", "Signed out", "false"), new("browser", "Browser", "default"), new("cloud", "Cloud", "public")],
            SecretLabel: null, SecretRequired: false),
        new(LoginKind.ServiceBusConnectionString, LoginSystem.ServiceBus, "Connection string or SAS token",
            [new("endpoint", "Namespace", "sbns-demo-001.servicebus.windows.net"), new("entityPath", "Entity (EntityPath)", "optional"), new("keyName", "Key name", "optional"),
             new("accountName", "Entra account (endpoint-only strings)", string.Empty)],
            "Connection string or SAS token", SecretRequired: true),
        new(LoginKind.ServiceBusEntra, LoginSystem.ServiceBus, "Namespace with an Entra account",
            [new("namespace", "Namespace", "sbns-demo-001.servicebus.windows.net", Required: true), new("accountName", "Entra account", string.Empty)],
            SecretLabel: null, SecretRequired: false, UsesAccount: true),
        new(LoginKind.EventHubsConnectionString, LoginSystem.EventHubs, "Connection string",
            [new("endpoint", "Namespace", "evhns-demo-001.servicebus.windows.net"), new("entityPath", "Event hub", "optional")],
            "Connection string", SecretRequired: true),
        new(LoginKind.EventHubsEntra, LoginSystem.EventHubs, "Namespace with an Entra account",
            [new("namespace", "Namespace", "evhns-demo-001.servicebus.windows.net", Required: true), new("entityPath", "Event hub", "optional"), new("accountName", "Entra account", string.Empty)],
            SecretLabel: null, SecretRequired: false, UsesAccount: true),
        new(LoginKind.KafkaSasl, LoginSystem.Kafka, "SASL user name and password",
            [new("bootstrapServers", "Bootstrap servers", "kafka-demo.example:9093", Required: true),
             new("mechanism", "Mechanism", "SCRAM-SHA-512", Required: true, Choices: ["PLAIN", "SCRAM-SHA-256", "SCRAM-SHA-512"]),
             new("username", "User name", "demo-user", Required: true)],
            "Password", SecretRequired: true),
        new(LoginKind.KafkaMutualTls, LoginSystem.Kafka, "Mutual TLS certificate",
            [new("bootstrapServers", "Bootstrap servers", "kafka-demo.example:9094", Required: true),
             new("certificatePath", "Client certificate file", "~/certs/client.pem", Required: true),
             new("keyPath", "Private key file", "~/certs/client.key", Required: true),
             new("caPath", "CA certificate file", "optional")],
            "Private key password (optional)", SecretRequired: false),
        new(LoginKind.RabbitMqPassword, LoginSystem.RabbitMq, "User name and password",
            [new("host", "Host", "rabbit-demo.example", Required: true), new("port", "Port", "5671"), new("virtualHost", "Virtual host", "/"),
             new("username", "User name", "demo-user", Required: true)],
            "Password", SecretRequired: true),
        new(LoginKind.AwsAccessKey, LoginSystem.AwsSqsSns, "Access key",
            [new("region", "Region", "us-east-1", Required: true), new("accessKeyId", "Access key ID", "AKIA...", Required: true)],
            "Secret access key", SecretRequired: true),
        new(LoginKind.AwsProfile, LoginSystem.AwsSqsSns, "Named profile or SSO",
            [new("profileName", "Profile", "sandbox", Required: true), new("region", "Region", "us-east-1")],
            SecretLabel: null, SecretRequired: false),
        new(LoginKind.EmulatorConnectionString, LoginSystem.Emulator, "Service Bus emulator",
            [new("endpoint", "Endpoint", "localhost")],
            "Connection string", SecretRequired: true),
    ];

    public static LoginKindInfo Of(LoginKind kind) => All.Single(k => k.Kind == kind);

    public static IReadOnlyList<LoginKindInfo> For(LoginSystem system) => All.Where(k => k.System == system).ToList();

    public static string SystemLabel(LoginSystem system) => system switch
    {
        LoginSystem.Entra => "Microsoft Entra ID",
        LoginSystem.ServiceBus => "Azure Service Bus",
        LoginSystem.EventHubs => "Azure Event Hubs",
        LoginSystem.Kafka => "Apache Kafka",
        LoginSystem.RabbitMq => "RabbitMQ",
        LoginSystem.AwsSqsSns => "AWS SQS / SNS",
        LoginSystem.Emulator => "Local emulator",
        _ => system.ToString(),
    };

    /// <summary>A short badge for the system icon.</summary>
    public static string SystemBadge(LoginSystem system) => system switch
    {
        LoginSystem.Entra => "ID",
        LoginSystem.ServiceBus => "SB",
        LoginSystem.EventHubs => "EH",
        LoginSystem.Kafka => "K",
        LoginSystem.RabbitMq => "RQ",
        LoginSystem.AwsSqsSns => "AWS",
        LoginSystem.Emulator => "EM",
        _ => "?",
    };
}

/// <summary>
/// One saved login. Everything here is non-secret and is kept in the config JSON. The secret, when the kind
/// has one, lives only in the OS secure store under <see cref="Id"/>.
/// </summary>
public sealed record LoginEntry(
    string Id,
    string DisplayName,
    LoginSystem System,
    LoginKind Kind,
    IReadOnlyDictionary<string, string> Fields,
    bool HasSecret,
    string? AccountId,
    DateTimeOffset Created,
    DateTimeOffset? LastUsed)
{
    public LoginKindInfo Info => LoginKinds.Of(Kind);

    public string Field(string name) => Fields.GetValueOrDefault(name) ?? string.Empty;

    /// <summary>Safe to log: name, system and kind only.</summary>
    public override string ToString() => $"{DisplayName} ({LoginKinds.SystemLabel(System)}, {Info.Label})";

    public bool Equals(LoginEntry? other) =>
        other is not null && Id == other.Id && DisplayName == other.DisplayName && System == other.System && Kind == other.Kind
        && HasSecret == other.HasSecret && AccountId == other.AccountId && Created == other.Created && LastUsed == other.LastUsed
        && Fields.Count == other.Fields.Count && Fields.All(f => other.Fields.TryGetValue(f.Key, out var v) && v == f.Value);

    public override int GetHashCode() => HashCode.Combine(Id, DisplayName, Kind, LastUsed);
}

/// <summary>What the user typed for a new or changed login, without the secret.</summary>
public sealed record LoginDraft(
    string DisplayName,
    LoginKind Kind,
    IReadOnlyDictionary<string, string> Fields,
    string? AccountId = null);
