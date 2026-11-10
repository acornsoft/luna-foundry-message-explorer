// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Testing;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Saved logins, the account picker and the 0.6.0 migration, against a fake vault. Fake names and keys only.</summary>
public sealed class SavedLoginsTests
{
    private const string Planted = "PLANTED-not-a-real-secret-4c9e";
    private const string KeyName = "Shared" + "AccessKeyName";
    private const string Key = "Shared" + "AccessKey";
    // Built at run time so secret scanners never see a key-shaped literal. It decodes to "not-a-real-key".
    private static readonly string FakeKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-real-key"));

    private readonly TestVault _vault = new();
    private readonly FakeAccounts _accounts = new();
    private readonly FakeBrokerProvider _provider = new();

    private sealed class FakeAccounts : IAccountCredentialSource
    {
        public List<AccountChoice> Signed { get; } = [];

        public IReadOnlyList<AccountChoice> SignedInAccounts => Signed;

        public AccountChoice SignIn(string username)
        {
            var choice = new AccountChoice($"{username}-home", username, new FakeCredential(username));
            Signed.Add(choice);
            return choice;
        }
    }

    private LoginsViewModel Logins(TestVault? vault = null) =>
        new((vault ?? _vault).Vault, new LoginTester((vault ?? _vault).Vault, _provider, () => _accounts), () => _accounts);

    private ConnectViewModel Connect()
    {
        var connect = new ConnectViewModel(new ProviderCatalog([_provider], []), _ => Task.CompletedTask, () => { }, _vault.Vault) { Accounts = _accounts };
        return connect;
    }

    private static Dictionary<string, string> F(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private Task<LoginEntry> AddKafkaAsync(string secret = "fake-kafka-demo") => _vault.Vault.AddAsync(
        new LoginDraft("Kafka dev (demo)", LoginKind.KafkaSasl, F(("bootstrapServers", "kafka-demo.contoso.example:9093"), ("mechanism", "SCRAM-SHA-512"), ("username", "demo-reader"))),
        new RuntimeSecret(secret));

    [Fact]
    public async Task The_list_shows_every_system_with_badge_kind_and_last_used_and_entra_accounts_are_read_only_here()
    {
        new VaultAccountStore(_vault.Vault).Save(FakeAuthenticator.Account("alice@contoso.example"));
        var kafka = await AddKafkaAsync();
        await _vault.Vault.AddAsync(new LoginDraft("Rabbit (demo)", LoginKind.RabbitMqPassword, F(("host", "rabbit-demo.fabrikam.example"), ("username", "demo"))), new RuntimeSecret("fake-rabbit"));
        await _vault.Vault.AddAsync(new LoginDraft("AWS profile (demo)", LoginKind.AwsProfile, F(("profileName", "contoso-demo"), ("region", "us-east-1"))), null);
        _vault.Vault.MarkUsed(kafka.Id);

        var logins = Logins();

        Assert.Equal(["ID", "K", "RQ", "AWS"], logins.Items.Select(i => i.Badge));
        var kafkaRow = logins.Items.Single(i => i.Name == "Kafka dev (demo)");
        Assert.Equal(("Apache Kafka", "SASL user name and password"), (kafkaRow.SystemText, kafkaRow.KindText));
        Assert.StartsWith("Last used ", kafkaRow.LastUsedText, StringComparison.Ordinal);
        Assert.Equal("Never used", logins.Items.Single(i => i.Name == "Rabbit (demo)").LastUsedText);
        Assert.Contains("No stored secret", logins.Items.Single(i => i.Name == "AWS profile (demo)").DetailText, StringComparison.Ordinal);

        logins.Selected = logins.Items[0];
        Assert.False(logins.EditCommand.CanExecute(null));
        Assert.False(logins.DeleteCommand.CanExecute(null));
        Assert.True(logins.TestCommand.CanExecute(null));
    }

    [Fact]
    public async Task Adding_a_login_puts_the_secret_only_in_the_store_and_clears_the_editor()
    {
        var logins = Logins();
        logins.AddCommand.Execute(null);
        var editor = logins.Editor!;
        editor.SelectedSystem = editor.Systems.Single(s => s.System == LoginSystem.RabbitMq);
        editor.Name = "Rabbit staging (demo)";
        editor.Fields.Single(f => f.Field.Name == "host").Value = "rabbit-demo.fabrikam.example";
        editor.Fields.Single(f => f.Field.Name == "username").Value = "demo-user";
        editor.Secret = Planted;

        await editor.SaveCommand.ExecuteAsync();

        Assert.Null(logins.Editor);
        Assert.Empty(editor.Secret);
        var saved = Assert.Single(_vault.Vault.List());
        Assert.Equal(LoginKind.RabbitMqPassword, saved.Kind);
        Assert.Equal(Planted, (await _vault.Vault.ReadSecretAsync(saved.Id))!.Reveal());
        Assert.DoesNotContain(Planted, _vault.Metadata.Json, StringComparison.Ordinal);
        Assert.Equal(saved.Id, logins.Selected!.Entry.Id);
    }

    [Fact]
    public async Task Editing_with_an_empty_secret_keeps_the_stored_one_and_rename_changes_only_the_name()
    {
        var kafka = await AddKafkaAsync();
        var logins = Logins();
        logins.Selected = logins.Items.Single();
        logins.EditCommand.Execute(null);
        logins.Editor!.Fields.Single(f => f.Field.Name == "username").Value = "demo-writer";
        Assert.Contains("Leave empty to keep it", logins.Editor.SecretHint, StringComparison.Ordinal);
        await logins.Editor.SaveCommand.ExecuteAsync();

        Assert.Equal("demo-writer", _vault.Vault.Find(kafka.Id)!.Field("username"));
        Assert.Equal("fake-kafka-demo", (await _vault.Vault.ReadSecretAsync(kafka.Id))!.Reveal());

        logins.RenameCommand.Execute(null);
        logins.RenameText = "Kafka writer (demo)";
        logins.ApplyRenameCommand.Execute(null);
        Assert.Equal("Kafka writer (demo)", _vault.Vault.Find(kafka.Id)!.DisplayName);
        Assert.Equal("fake-kafka-demo", (await _vault.Vault.ReadSecretAsync(kafka.Id))!.Reveal());
    }

    [Fact]
    public async Task Delete_asks_first_and_then_removes_the_secret_too()
    {
        await AddKafkaAsync();
        var logins = Logins();
        logins.Selected = logins.Items.Single();

        logins.DeleteCommand.Execute(null);
        Assert.True(logins.IsConfirmingDelete);
        Assert.Contains("can't be undone", logins.DeletePrompt, StringComparison.Ordinal);
        Assert.Single(_vault.Vault.List());
        logins.CancelDeleteCommand.Execute(null);
        Assert.Single(_vault.Vault.List());

        logins.DeleteCommand.Execute(null);
        await logins.ConfirmDeleteCommand.ExecuteAsync();
        Assert.Empty(_vault.Vault.List());
        Assert.Equal(0, _vault.Secrets.Count);
        Assert.False(logins.HasItems);
    }

    [Fact]
    public async Task With_no_secure_store_secret_logins_are_refused_and_secretless_ones_still_save()
    {
        var vault = new TestVault("No Secret Service keyring is running.");
        var logins = Logins(vault);
        Assert.Contains("No Secret Service keyring is running.", logins.StoreText, StringComparison.Ordinal);
        Assert.Equal("no secure store", logins.StoreBadge);

        logins.AddCommand.Execute(null);
        logins.Editor!.SelectedSystem = logins.Editor.Systems.Single(s => s.System == LoginSystem.RabbitMq);
        logins.Editor.Name = "Rabbit (demo)";
        logins.Editor.Fields.Single(f => f.Field.Name == "host").Value = "rabbit-demo.fabrikam.example";
        logins.Editor.Fields.Single(f => f.Field.Name == "username").Value = "demo";
        logins.Editor.Secret = Planted;
        await logins.Editor.SaveCommand.ExecuteAsync();

        Assert.True(logins.Editor.HasError);
        Assert.DoesNotContain(Planted, logins.Editor.Error, StringComparison.Ordinal);
        Assert.Empty(vault.Vault.List());
        Assert.Null(vault.Metadata.Json);

        logins.Editor.SelectedSystem = logins.Editor.Systems.Single(s => s.System == LoginSystem.AwsSqsSns);
        logins.Editor.SelectedKind = logins.Editor.Kinds.Single(k => k.Kind == LoginKind.AwsProfile);
        logins.Editor.Name = "AWS profile (demo)";
        logins.Editor.Fields.Single(f => f.Field.Name == "profileName").Value = "contoso-demo";
        logins.Editor.Fields.Single(f => f.Field.Name == "region").Value = "us-east-1";
        await logins.Editor.SaveCommand.ExecuteAsync();
        Assert.Equal(LoginKind.AwsProfile, Assert.Single(vault.Vault.List()).Kind);
    }

    [Fact]
    public void A_browser_store_is_labelled_weaker_than_the_os_store()
    {
        var logins = Logins(new TestVault(store: new FakeBrowserStore()));

        Assert.Equal("browser-protected (weaker than OS store)", logins.StoreBadge);
        Assert.True(logins.StoreIsWeak);
        Assert.Contains("Prefer Entra sign-in", logins.StoreText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_is_read_only_marks_the_login_used_and_says_when_a_system_is_not_built()
    {
        var sas = await _vault.Vault.AddAsync(
            new LoginDraft("Reader (demo)", LoginKind.ServiceBusConnectionString, F(("endpoint", "sbns-demo-001.servicebus.windows.net"))),
            new RuntimeSecret($"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={FakeKey}"));
        await AddKafkaAsync();
        var logins = Logins();

        logins.Selected = logins.Items.Single(i => i.Entry.Id == sas.Id);
        await logins.TestCommand.ExecuteAsync();
        Assert.False(logins.StatusIsError);
        Assert.StartsWith("Connected.", logins.Status, StringComparison.Ordinal);
        Assert.Equal(1, _provider.Inventory.ListCalls);
        Assert.NotNull(_vault.Vault.Find(sas.Id)!.LastUsed);

        logins.Selected = logins.Items.Single(i => i.Entry.Kind == LoginKind.KafkaSasl);
        await logins.TestCommand.ExecuteAsync();
        Assert.Contains("provider isn't built yet", logins.Status, StringComparison.Ordinal);
        Assert.Null(_vault.Vault.Find(logins.Selected!.Entry.Id)!.LastUsed);
    }

    [Fact]
    public async Task A_typed_namespace_uses_the_picked_account_and_saves_bound_to_it()
    {
        _accounts.SignIn("alice@contoso.example");
        var bob = _accounts.SignIn("bob@fabrikam.example");
        var connect = Connect();
        Assert.Equal(2, connect.AccountChoices.Count);
        Assert.True(connect.ShowAccountPicker);

        connect.SelectedAccount = connect.AccountChoices.Single(a => a.Id == bob.Id);
        connect.NamespaceText = "sbns-fab-001.servicebus.windows.net";
        Assert.Same(bob.Credential, connect.BuildConnection().Credential);
        Assert.Equal("Uses bob@fabrikam.example. No secret is stored.", connect.EntraHint);

        connect.SaveConnection = true;
        await connect.ConnectCommand.ExecuteAsync();
        Assert.Same(bob.Credential, _provider.LastConnection!.Credential);
        var saved = Assert.Single(_vault.Vault.List());
        Assert.Equal((LoginKind.ServiceBusEntra, bob.Id, "bob@fabrikam.example"), (saved.Kind, saved.AccountId, saved.Field("accountName")));
        Assert.False(saved.HasSecret);
    }

    [Fact]
    public async Task A_saved_login_whose_account_is_signed_out_says_so_and_connects_once_it_is_back()
    {
        var bob = new AccountChoice("bob@fabrikam.example-home", "bob@fabrikam.example", new FakeCredential("bob"));
        await _vault.Vault.AddAsync(new LoginDraft("Fabrikam orders (demo)", LoginKind.ServiceBusEntra, F(("namespace", "sbns-fab-001.servicebus.windows.net"), ("accountName", bob.Username)), bob.Id), null);
        _accounts.SignIn("alice@contoso.example");
        var connect = Connect();
        connect.SelectedSaved = connect.SavedConnections.Single();

        await connect.ConnectSavedCommand.ExecuteAsync();
        Assert.False(connect.IsConnected);
        Assert.Contains("uses bob@fabrikam.example", connect.ValidationMessage, StringComparison.Ordinal);

        _accounts.Signed.Add(bob);
        connect.RefreshAccountText();
        await connect.ConnectSavedCommand.ExecuteAsync();
        Assert.True(connect.IsConnected);
        Assert.Same(bob.Credential, _provider.LastConnection!.Credential);
    }

    [Fact]
    public void An_endpoint_only_string_shows_the_picker_and_uses_the_chosen_account()
    {
        _accounts.SignIn("alice@contoso.example");
        var bob = _accounts.SignIn("bob@fabrikam.example");
        var connect = new ConnectViewModel(new ProviderCatalog([new Adapters.ServiceBus.ServiceBusBrokerProvider()], []), _ => Task.CompletedTask, () => { }, _vault.Vault) { Accounts = _accounts };
        connect.UseConnectionString = true;
        connect.ConnectionString = "Endpoint=sb://sbns-fab-001.servicebus.windows.net/";
        Assert.True(connect.ShowAccountPicker);

        connect.SelectedAccount = connect.AccountChoices.Single(a => a.Id == bob.Id);
        Assert.Equal("sbns-fab-001.servicebus.windows.net with your Entra account bob@fabrikam.example", connect.DetectedText);
        Assert.Same(bob.Credential, connect.BuildConnection().Credential);

        connect.ConnectionString = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={FakeKey}";
        Assert.False(connect.ShowAccountPicker);
    }

    [Fact]
    public async Task The_060_saved_strings_move_into_the_vault_and_the_old_item_is_cleared()
    {
        var legacy = new FakeLegacy(new Dictionary<string, string>
        {
            ["reader (demo)"] = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={FakeKey};EntityPath=orders",
            ["emulator (demo)"] = $"Endpoint=sb://localhost;{KeyName}=demo;{Key}={FakeKey};UseDevelopmentEmulator=true;",
        });

        var moved = await LoginMigration.ImportSavedConnectionsAsync(legacy, _vault.Vault);

        Assert.Equal(2, moved);
        Assert.Empty(legacy.Items);
        var reader = _vault.Vault.List().Single(e => e.DisplayName == "reader (demo)");
        Assert.Equal((LoginKind.ServiceBusConnectionString, "orders", "reader"), (reader.Kind, reader.Field("entityPath"), reader.Field("keyName")));
        Assert.Equal(LoginKind.EmulatorConnectionString, _vault.Vault.List().Single(e => e.DisplayName == "emulator (demo)").Kind);
        Assert.Contains(FakeKey, (await _vault.Vault.ReadSecretAsync(reader.Id))!.Reveal(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, _vault.Metadata.Json, StringComparison.Ordinal);

        Assert.Equal(0, await LoginMigration.ImportSavedConnectionsAsync(legacy, _vault.Vault));
        Assert.Equal(2, _vault.Vault.List().Count);
    }

    [Fact]
    public async Task The_060_strings_stay_put_when_there_is_no_secure_store()
    {
        var vault = new TestVault("No secure store here.");
        var legacy = new FakeLegacy(new Dictionary<string, string> { ["reader (demo)"] = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={FakeKey}" });

        Assert.Equal(0, await LoginMigration.ImportSavedConnectionsAsync(legacy, vault.Vault));
        Assert.Single(legacy.Items);
        Assert.Empty(vault.Vault.List());
    }

    [Fact]
    public void The_060_account_files_move_into_the_vault_and_are_deleted()
    {
        var folder = Directory.CreateTempSubdirectory("lfme-migrate-").FullName;
        try
        {
            var files = new FileAccountStore(folder);
            files.Save(FakeAuthenticator.Account("alice@contoso.example") with { RequestedTenant = "contoso.example" });
            var store = new VaultAccountStore(_vault.Vault);

            Assert.Equal(1, LoginMigration.ImportAccountFiles(files, store));

            Assert.Empty(files.Load());
            var account = Assert.Single(store.Load());
            Assert.Equal(("alice@contoso.example", "contoso.example", false), (account.Username, account.RequestedTenant, account.SignedOut));
            Assert.Equal(LoginKind.EntraAccount, Assert.Single(_vault.Vault.List()).Kind);
            store.Save(account with { SignedOut = true });
            Assert.True(Assert.Single(store.Load()).SignedOut);
            store.Delete(account.Id);
            Assert.Empty(_vault.Vault.List());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_planted_secret_never_reaches_logs_exception_messages_tostring_or_the_config_file()
    {
        var folder = Directory.CreateTempSubdirectory("lfme-redact-").FullName;
        try
        {
            var log = new List<string>();
            var metadata = new FileLoginMetadataStore(Path.Combine(folder, "logins.json"));
            var store = new Credentials.Stores.MemorySecretStore();
            var vault = new CredentialVault(store, metadata, log: log.Add);
            var connectionString = $"Endpoint=sb://sbns-demo-001.servicebus.windows.net/;{KeyName}=reader;{Key}={Planted}";
            var messages = new List<string>();

            // Saved through the editor, the paste box and the vault directly.
            var logins = new LoginsViewModel(vault, new LoginTester(vault, _provider, () => _accounts), () => _accounts);
            logins.AddCommand.Execute(null);
            logins.Editor!.SelectedSystem = logins.Editor.Systems.Single(s => s.System == LoginSystem.Kafka);
            logins.Editor.Name = "Kafka (demo)";
            logins.Editor.Fields.Single(f => f.Field.Name == "bootstrapServers").Value = "kafka-demo.contoso.example:9093";
            logins.Editor.Fields.Single(f => f.Field.Name == "username").Value = "demo";
            logins.Editor.Secret = Planted;
            var editorText = logins.Editor.ToString();
            await logins.Editor.SaveCommand.ExecuteAsync();

            // The real Service Bus provider parses the paste; connecting builds clients but sends nothing.
            var connect = new ConnectViewModel(new ProviderCatalog([new Adapters.ServiceBus.ServiceBusBrokerProvider()], []), _ => Task.CompletedTask, () => { }, vault) { Accounts = _accounts };
            connect.UseConnectionString = true;
            connect.ConnectionString = connectionString;
            var connectText = connect.ToString();
            connect.SaveConnection = true;
            connect.SaveName = "Reader (demo)";
            await connect.ConnectCommand.ExecuteAsync();

            // Rejections and store failures.
            foreach (var attempt in new Func<Task>[]
            {
                () => vault.AddAsync(new LoginDraft("Leaky (demo)", LoginKind.KafkaSasl, F(("bootstrapServers", Planted), ("mechanism", "PLAIN"), ("username", "demo"))), new RuntimeSecret(Planted)),
                () => vault.AddAsync(new LoginDraft("Shape (demo)", LoginKind.RabbitMqPassword, F(("host", connectionString), ("username", "demo"))), new RuntimeSecret("other-fake")),
                () => new CredentialVault(new Credentials.Stores.UnavailableSecretStore("none", "No store."), metadata).AddAsync(new LoginDraft("x (demo)", LoginKind.RabbitMqPassword, F(("host", "h"), ("username", "u"))), new RuntimeSecret(Planted)),
            })
            {
                var exception = await Assert.ThrowsAnyAsync<Exception>(attempt);
                messages.Add(exception.Message);
                messages.Add(exception.ToString());
            }

            // A broker failure that echoes the secret back is redacted by Test connection.
            _provider.Inventory.FailWith = new BrokerException(BrokerErrorKind.Unauthenticated, $"Rejected {connectionString}");
            logins.Selected = logins.Items.Single(i => i.Name == "Reader (demo)");
            await logins.TestCommand.ExecuteAsync();
            messages.Add(logins.Status!);

            var everything = string.Join("\n", log
                .Concat(messages)
                .Concat([editorText, connectText, connect.ToString(), logins.ToString(), vault.ToString(), connect.ValidationMessage ?? string.Empty])
                .Concat(logins.Items.Select(i => i.ToString() + i.DetailText))
                .Concat(vault.List().Select(e => e.ToString()))
                .Concat(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));

            Assert.Equal(2, vault.List().Count);
            Assert.Equal(2, store.Count);
            Assert.Contains(Credentials.SecretRedactor.Mask, logins.Status, StringComparison.Ordinal);
            Assert.DoesNotContain(Planted, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(Planted + "/"), everything, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
