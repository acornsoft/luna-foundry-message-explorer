// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Tests;

/// <summary>The vault with a fake secret store and in-memory metadata. Fake secrets only.</summary>
public sealed class CredentialVaultTests
{
    private const string Planted = "PLANTED-not-a-real-secret-7f3a";
    private readonly MemorySecretStore _store = new();
    private readonly MemoryMetadata _metadata = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero));
    private readonly List<string> _log = [];

    private CredentialVault Vault(ISecretStore? store = null) => new(store ?? _store, _metadata, _time, _log.Add);

    private static LoginDraft Rabbit(string name = "RabbitMQ dev (demo)") => new(name, LoginKind.RabbitMqPassword, new Dictionary<string, string>
    {
        ["host"] = "rabbit-demo.example",
        ["port"] = "5671",
        ["username"] = "demo-user",
    });

    [Fact]
    public async Task A_secret_goes_to_the_secret_store_and_only_metadata_goes_to_the_config()
    {
        var vault = Vault();

        var entry = await vault.AddAsync(Rabbit(), new RuntimeSecret(Planted));

        Assert.True(entry.HasSecret);
        Assert.Equal(LoginSystem.RabbitMq, entry.System);
        Assert.Equal(1, _store.Count);
        Assert.Equal(Planted, (await vault.ReadSecretAsync(entry.Id))!.Reveal());
        Assert.DoesNotContain(Planted, _metadata.Json, StringComparison.Ordinal);
        Assert.Contains("rabbit-demo.example", _metadata.Json, StringComparison.Ordinal);
        Assert.Contains("\"RabbitMqPassword\"", _metadata.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logins_come_back_from_the_config_with_their_secret_still_in_the_store()
    {
        var first = await Vault().AddAsync(Rabbit(), new RuntimeSecret(Planted));

        var reopened = Vault();

        Assert.Equal(first, Assert.Single(reopened.List()));
        Assert.Equal(Planted, (await reopened.ReadSecretAsync(first.Id))!.Reveal());
    }

    [Fact]
    public async Task With_no_secure_store_a_secret_is_refused_and_nothing_is_written()
    {
        var vault = Vault(new UnavailableSecretStore("Secret Service keyring", "No Secret Service keyring is running."));

        var error = await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(Rabbit(), new RuntimeSecret(Planted)));

        Assert.Equal("Not saved. No Secret Service keyring is running.", error.Message);
        Assert.Null(_metadata.Json);
        Assert.Empty(vault.List());
        Assert.Equal(SecretProtection.None, vault.Protection);
    }

    [Fact]
    public async Task With_no_secure_store_a_login_without_a_secret_still_saves()
    {
        var vault = Vault(new UnavailableSecretStore("Secret Service keyring", "No keyring."));

        var entry = await vault.AddAsync(new LoginDraft("AWS sandbox (demo)", LoginKind.AwsProfile, new Dictionary<string, string> { ["profileName"] = "sandbox" }), null);

        Assert.False(entry.HasSecret);
        Assert.Single(vault.List());
    }

    [Fact]
    public async Task A_field_holding_the_secret_is_refused_and_the_message_never_repeats_it()
    {
        var vault = Vault();
        var draft = Rabbit() with { Fields = new Dictionary<string, string> { ["host"] = "rabbit-demo.example", ["username"] = Planted } };

        var error = await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(draft, new RuntimeSecret(Planted)));

        Assert.DoesNotContain(Planted, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _store.Count);
    }

    [Theory]
    [InlineData("Endpoint=sb://sbns-demo-001.servicebus.windows.net/;Shared" + "AccessKey=abc")]
    [InlineData("SharedAccessSignature sr=x&sig=abc&se=1")]
    [InlineData("amqp://user:pw@host;pass" + "word=abc")]
    [InlineData("-----BEGIN ENCRYPTED KEY-----")]
    public async Task A_field_shaped_like_a_secret_is_refused(string value)
    {
        var draft = new LoginDraft("Demo", LoginKind.ServiceBusConnectionString, new Dictionary<string, string> { ["endpoint"] = value });

        await Assert.ThrowsAsync<LoginRejectedException>(() => Vault().AddAsync(draft, new RuntimeSecret("Endpoint=sb://x/;k=v")));
    }

    [Fact]
    public async Task Unknown_fields_missing_required_fields_and_missing_secrets_are_refused()
    {
        var vault = Vault();

        await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(Rabbit() with { Fields = new Dictionary<string, string>(Rabbit().Fields) { ["secretNote"] = "x" } }, new RuntimeSecret(Planted)));
        await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(Rabbit() with { Fields = new Dictionary<string, string> { ["host"] = "h" } }, new RuntimeSecret(Planted)));
        var missing = await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(Rabbit(), null));
        Assert.Equal("Password is required.", missing.Message);
        await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(Rabbit(" "), new RuntimeSecret(Planted)));
        Assert.Empty(vault.List());
    }

    [Fact]
    public async Task A_login_that_uses_an_entra_account_must_name_the_account()
    {
        var vault = Vault();
        var draft = new LoginDraft("Payments as alice", LoginKind.ServiceBusEntra, new Dictionary<string, string> { ["namespace"] = "sbns-demo-001" });

        var error = await Assert.ThrowsAsync<LoginRejectedException>(() => vault.AddAsync(draft, null));
        var saved = await vault.AddAsync(draft with { AccountId = "alice-home" }, null);

        Assert.Equal("Choose the Entra account this login uses.", error.Message);
        Assert.Equal("alice-home", saved.AccountId);
    }

    [Fact]
    public async Task Update_keeps_the_secret_unless_a_new_one_is_given()
    {
        var vault = Vault();
        var entry = await vault.AddAsync(Rabbit(), new RuntimeSecret(Planted));

        await vault.UpdateAsync(entry.Id, Rabbit("Renamed by edit"), null);
        Assert.Equal(Planted, (await vault.ReadSecretAsync(entry.Id))!.Reveal());

        await vault.UpdateAsync(entry.Id, Rabbit(), new RuntimeSecret("second-fake-password"));
        Assert.Equal("second-fake-password", (await vault.ReadSecretAsync(entry.Id))!.Reveal());
        Assert.Contains(_log, l => l.Contains("secret replaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_delete_and_last_used()
    {
        var vault = Vault();
        var entry = await vault.AddAsync(Rabbit(), new RuntimeSecret(Planted));

        Assert.Equal("Rabbit renamed", vault.Rename(entry.Id, "  Rabbit renamed ").DisplayName);
        _time.Now = _time.Now.AddHours(2);
        Assert.Equal(_time.Now, vault.MarkUsed(entry.Id).LastUsed);

        await vault.DeleteAsync(entry.Id);
        Assert.Empty(vault.List());
        Assert.Equal(0, _store.Count);
        Assert.Throws<LoginRejectedException>(() => vault.Rename(entry.Id, "x"));
    }

    [Fact]
    public async Task Every_kind_of_login_can_be_saved()
    {
        var vault = Vault();
        foreach (var info in LoginKinds.All)
        {
            var fields = info.Fields.Where(f => f.Required).ToDictionary(f => f.Name, f => f.Choices?[0] ?? f.Watermark);
            var secret = info.HasSecret ? new RuntimeSecret("fake-" + info.Kind) : null;
            var entry = await vault.AddAsync(new LoginDraft($"{info.Label} (demo)", info.Kind, fields, info.UsesAccount ? "alice-home" : null), secret);
            Assert.Equal(info.System, entry.System);
        }

        Assert.Equal(LoginKinds.All.Count, vault.List().Count);
        Assert.Equal(Enum.GetValues<LoginSystem>().Length, vault.List().Select(e => e.System).Distinct().Count());
    }

    [Fact]
    public async Task Log_lines_and_text_forms_never_carry_the_secret()
    {
        var vault = Vault();
        var entry = await vault.AddAsync(Rabbit(), new RuntimeSecret(Planted));
        await vault.ReadSecretAsync(entry.Id);
        await vault.UpdateAsync(entry.Id, Rabbit(), new RuntimeSecret(Planted + "-2"));
        await vault.DeleteAsync(entry.Id);

        Assert.NotEmpty(_log);
        Assert.All(_log, line => Assert.DoesNotContain("PLANTED", line, StringComparison.Ordinal));
        Assert.DoesNotContain("PLANTED", vault.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PLANTED", entry.ToString(), StringComparison.Ordinal);
        Assert.Equal("***", new RuntimeSecret(Planted).ToString());
    }

    [Fact]
    public void Put_adds_or_replaces_a_secretless_login_under_a_chosen_id()
    {
        var vault = Vault();
        var draft = new LoginDraft("alice@contoso.example", LoginKind.EntraAccount, new Dictionary<string, string> { ["username"] = "alice@contoso.example" });

        var first = vault.Put("entra-alice", draft);
        _time.Now = _time.Now.AddMinutes(5);
        var second = vault.Put("entra-alice", draft with { Fields = new Dictionary<string, string> { ["username"] = "alice@contoso.example", ["signedOut"] = "true" } });

        Assert.Equal(first.Created, second.Created);
        Assert.Equal("true", Assert.Single(vault.List()).Field("signedOut"));
        Assert.Throws<LoginRejectedException>(() => vault.Put("x", Rabbit()));
    }

    [Fact]
    public void The_redactor_masks_plain_and_url_encoded_secrets()
    {
        var secret = new RuntimeSecret("a+b/c=d-not-real");

        Assert.Equal("x *** y ***", SecretRedactor.Redact("x a+b/c=d-not-real y a%2Bb%2Fc%3Dd-not-real", secret));
    }

    [Fact]
    public void A_damaged_config_file_starts_empty_instead_of_failing()
    {
        _metadata.Json = "{ not json";

        Assert.Empty(Vault().List());
    }
}
