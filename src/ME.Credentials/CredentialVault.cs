// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials;

/// <summary>
/// The one credential vault. Non-secret metadata goes to <see cref="ILoginMetadataStore"/> as JSON; secrets go
/// only to the <see cref="ISecretStore"/>, keyed by login ID. Validation runs before anything is written, so a
/// rejected login leaves nothing behind. Log lines carry IDs, systems and kinds only.
/// </summary>
public sealed partial class CredentialVault : ICredentialVault
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretStore _secrets;
    private readonly ILoginMetadataStore _metadata;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private List<LoginEntry> _entries;

    public CredentialVault(ISecretStore secrets, ILoginMetadataStore metadata, TimeProvider? time = null, Action<string>? log = null)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _entries = LoadEntries();
    }

    public event EventHandler? Changed;

    public string SecureStoreName => _secrets.Name;

    public SecretProtection Protection => _secrets.UnavailableReason is null ? _secrets.Protection : SecretProtection.None;

    public string? SecretsUnavailableReason => _secrets.UnavailableReason;

    public IReadOnlyList<LoginEntry> List()
    {
        lock (_gate)
            return _entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public LoginEntry? Find(string id)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Id == id);
    }

    public async Task<LoginEntry> AddAsync(LoginDraft draft, RuntimeSecret? secret, CancellationToken cancellationToken = default)
    {
        Validate(draft, secret, isNew: true, hadSecret: false);
        var entry = new LoginEntry(
            "login-" + Guid.NewGuid().ToString("N")[..16],
            draft.DisplayName.Trim(),
            LoginKinds.Of(draft.Kind).System,
            draft.Kind,
            Clean(draft.Fields),
            secret is not null,
            Blank(draft.AccountId),
            _time.GetUtcNow(),
            null);

        if (secret is not null)
            await _secrets.WriteAsync(entry.Id, secret, cancellationToken).ConfigureAwait(false);
        try
        {
            Commit(entries => entries.Add(entry));
        }
        catch
        {
            if (secret is not null)
                await _secrets.DeleteAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _log($"login added: {entry.Id} {entry.System}/{entry.Kind}{(entry.HasSecret ? " (secret in " + _secrets.Name + ")" : string.Empty)}");
        return entry;
    }

    public async Task<LoginEntry> UpdateAsync(string id, LoginDraft draft, RuntimeSecret? secret, CancellationToken cancellationToken = default)
    {
        var existing = Find(id) ?? throw new LoginRejectedException("That login no longer exists.");
        if (LoginKinds.Of(draft.Kind).System != existing.System)
            throw new LoginRejectedException("A login can't move to another system. Add a new one instead.");
        var keepsSecret = existing.HasSecret && LoginKinds.Of(draft.Kind).HasSecret;
        Validate(draft, secret, isNew: false, hadSecret: keepsSecret);

        var updated = existing with
        {
            DisplayName = draft.DisplayName.Trim(),
            Kind = draft.Kind,
            Fields = Clean(draft.Fields),
            AccountId = Blank(draft.AccountId),
            HasSecret = secret is not null || keepsSecret,
        };

        if (secret is not null)
            await _secrets.WriteAsync(id, secret, cancellationToken).ConfigureAwait(false);
        else if (existing.HasSecret && !updated.HasSecret)
            await _secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        Commit(entries => entries[entries.FindIndex(e => e.Id == id)] = updated);
        _log($"login updated: {id} {updated.System}/{updated.Kind}{(secret is null ? string.Empty : " (secret replaced)")}");
        return updated;
    }

    public LoginEntry Put(string id, LoginDraft draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (LoginKinds.Of(draft.Kind).HasSecret)
            throw new LoginRejectedException("Logins with a secret are added with AddAsync.");
        Validate(draft, secret: null, isNew: true, hadSecret: false);
        var existing = Find(id);
        var entry = new LoginEntry(id, draft.DisplayName.Trim(), LoginKinds.Of(draft.Kind).System, draft.Kind, Clean(draft.Fields), false, Blank(draft.AccountId), existing?.Created ?? _time.GetUtcNow(), existing?.LastUsed);
        Commit(entries =>
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0)
                entries.Add(entry);
            else
                entries[index] = entry;
        });
        _log($"login {(existing is null ? "added" : "updated")}: {id} {entry.System}/{entry.Kind}");
        return entry;
    }

    public LoginEntry Rename(string id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new LoginRejectedException("Give the login a name.");
        var existing = Find(id) ?? throw new LoginRejectedException("That login no longer exists.");
        var renamed = existing with { DisplayName = displayName.Trim() };
        Commit(entries => entries[entries.FindIndex(e => e.Id == id)] = renamed);
        _log($"login renamed: {id}");
        return renamed;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var existing = Find(id);
        if (existing is null)
            return;
        if (existing.HasSecret && _secrets.UnavailableReason is null)
            await _secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        Commit(entries => entries.RemoveAll(e => e.Id == id));
        _log($"login deleted: {id} {existing.System}/{existing.Kind}");
    }

    public LoginEntry MarkUsed(string id)
    {
        var existing = Find(id) ?? throw new LoginRejectedException("That login no longer exists.");
        var used = existing with { LastUsed = _time.GetUtcNow() };
        Commit(entries => entries[entries.FindIndex(e => e.Id == id)] = used);
        return used;
    }

    public async Task<RuntimeSecret?> ReadSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        var existing = Find(id);
        if (existing is not { HasSecret: true })
            return null;
        if (_secrets.UnavailableReason is { } reason)
            throw new SecretStoreException(reason);
        var secret = await _secrets.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        _log($"login secret read: {id} {(secret is null ? "missing" : "ok")}");
        return secret;
    }

    public override string ToString() => $"Credential vault: {_entries.Count} logins, secrets in {_secrets.Name}";

    private void Validate(LoginDraft draft, RuntimeSecret? secret, bool isNew, bool hadSecret)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var info = LoginKinds.Of(draft.Kind);
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new LoginRejectedException("Give the login a name.");

        foreach (var name in draft.Fields.Keys)
        {
            if (info.Fields.All(f => f.Name != name))
                throw new LoginRejectedException($"'{name}' is not a field of a {info.Label} login.");
        }

        foreach (var field in info.Fields.Where(f => f.Required))
        {
            if (string.IsNullOrWhiteSpace(draft.Fields.GetValueOrDefault(field.Name)))
                throw new LoginRejectedException($"{field.Label} is required.");
        }

        var revealed = secret?.Reveal();
        foreach (var value in draft.Fields.Values.Where(v => !string.IsNullOrEmpty(v)))
        {
            if (revealed is not null && value.Contains(revealed, StringComparison.Ordinal))
                throw new LoginRejectedException($"A field contains the secret. Put it only in the {info.SecretLabel ?? "secret"} box.");
            if (LooksSecret(value))
                throw new LoginRejectedException("A field looks like it holds a key, signature or password. Put secrets only in the secret box.");
        }

        if (info.UsesAccount && string.IsNullOrWhiteSpace(draft.AccountId))
            throw new LoginRejectedException("Choose the Entra account this login uses.");

        if (secret is not null && !info.HasSecret)
            throw new LoginRejectedException($"A {info.Label} login has no secret.");
        if (secret is null && info.SecretRequired && (isNew || !hadSecret))
            throw new LoginRejectedException($"{info.SecretLabel} is required.");
        if (secret is not null && _secrets.UnavailableReason is { } reason)
            throw new LoginRejectedException("Not saved. " + reason);
    }

    /// <summary>True for text that carries a key, signature or password, so it can't sit in a non-secret field.</summary>
    public static bool LooksSecret(string value) => SecretShape().IsMatch(value);

    [GeneratedRegex("Shared" + "AccessKey=|SharedAccessSignature|[;&?]sig=|pass" + "word=|Account" + "Key=|-----BEGIN", RegexOptions.IgnoreCase)]
    private static partial Regex SecretShape();

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string> fields) =>
        fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value.Trim(), StringComparer.Ordinal);

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Commit(Action<List<LoginEntry>> change)
    {
        lock (_gate)
        {
            var next = _entries.ToList();
            change(next);
            _metadata.Save(JsonSerializer.Serialize(new Document(1, next.Select(Stored.From).ToList()), Json));
            _entries = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<LoginEntry> LoadEntries()
    {
        var text = _metadata.Load();
        if (string.IsNullOrWhiteSpace(text))
            return [];
        try
        {
            var document = JsonSerializer.Deserialize<Document>(text, Json);
            return document?.Logins.Where(s => Enum.IsDefined(s.Kind)).Select(s => s.ToEntry()).ToList() ?? [];
        }
        catch (JsonException)
        {
            _log("login metadata could not be read; starting empty");
            return [];
        }
    }

    private sealed record Document(int Version, List<Stored> Logins);

    private sealed record Stored(
        string Id,
        string DisplayName,
        LoginSystem System,
        LoginKind Kind,
        Dictionary<string, string> Fields,
        bool HasSecret,
        string? AccountId,
        DateTimeOffset Created,
        DateTimeOffset? LastUsed)
    {
        public static Stored From(LoginEntry e) =>
            new(e.Id, e.DisplayName, e.System, e.Kind, new Dictionary<string, string>(e.Fields, StringComparer.Ordinal), e.HasSecret, e.AccountId, e.Created, e.LastUsed);

        public LoginEntry ToEntry() => new(Id, DisplayName, System, Kind, Fields ?? [], HasSecret, AccountId, Created, LastUsed);
    }
}

/// <summary>Login metadata in a JSON file, written atomically. Never holds a secret.</summary>
public sealed class FileLoginMetadataStore(string path) : ILoginMetadataStore
{
    public string Path { get; } = path;

    public string? Load() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Save(string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Replaces known secrets in text (also their URL-encoded form) with ***.</summary>
public static class SecretRedactor
{
    public const string Mask = "***";

    public static string Redact(string? text, params RuntimeSecret?[] secrets)
    {
        var result = text ?? string.Empty;
        foreach (var secret in secrets)
        {
            if (secret is null)
                continue;
            var value = secret.Reveal();
            if (value.Length < 4)
                continue;
            result = result.Replace(value, Mask, StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(value), Mask, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
