// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Serialization;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

namespace Acornsoft.MessageExplorer.Shell.Settings;

/// <summary>Light, Dark, or follow the operating system (the default).</summary>
public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// One connection opened recently, for the Recent list. Never holds a secret: a saved login is kept by its ID
/// (its secret stays in the OS secure store), a namespace by its ARM identity and the account that opened it.
/// A pasted connection string that was not saved is not kept.
/// </summary>
public sealed record RecentConnection(
    string Label,
    DateTimeOffset UsedAt,
    string? LoginId = null,
    NamespaceRef? Namespace = null,
    string? AccountId = null,
    string? AccountName = null)
{
    /// <summary>Same target: the same saved login, or the same namespace with the same account.</summary>
    public bool SameAs(RecentConnection other) =>
        other is not null
        && (LoginId is not null
            ? string.Equals(LoginId, other.LoginId, StringComparison.Ordinal)
            : other.LoginId is null
              && string.Equals(Namespace?.ResourceId, other.Namespace?.ResourceId, StringComparison.OrdinalIgnoreCase)
              && string.Equals(AccountId, other.AccountId, StringComparison.Ordinal));
}

/// <summary>What the app remembers between launches about its own look and recent connections.</summary>
public sealed record UiSettingsData
{
    public const int MaxRecent = 10;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    public IReadOnlyList<RecentConnection> Recent { get; init; } = [];

    /// <summary>The kind filter over the Resources list: All, Queues or Topics.</summary>
    public Brokers.ResourceKindFilter KindFilter { get; init; } = Brokers.ResourceKindFilter.All;
}

public interface IUiSettingsStore
{
    UiSettingsData Load();

    void Save(UiSettingsData settings);
}

/// <summary>In memory: tests, the phone preview, and windows with no config folder.</summary>
public sealed class MemoryUiSettingsStore(UiSettingsData? initial = null) : IUiSettingsStore
{
    public UiSettingsData Current { get; private set; } = initial ?? new UiSettingsData();

    public int Saves { get; private set; }

    public UiSettingsData Load() => Current;

    public void Save(UiSettingsData settings)
    {
        Current = settings ?? throw new ArgumentNullException(nameof(settings));
        Saves++;
    }
}

/// <summary>The theme goes to <paramref name="inner"/>; recent connections stay in memory. For the synthetic demo.</summary>
public sealed class ThemeOnlyUiSettingsStore(IUiSettingsStore inner) : IUiSettingsStore
{
    private IReadOnlyList<RecentConnection> _recent = [];

    public UiSettingsData Load() => inner.Load() with { Recent = _recent };

    public void Save(UiSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _recent = settings.Recent;
        inner.Save(inner.Load() with { Theme = settings.Theme, KindFilter = settings.KindFilter });
    }
}

/// <summary>
/// ui-settings.json in the config folder. A missing, empty or damaged file reads as the defaults (System theme,
/// nothing recent), so a bad file never stops the app starting. Writes go to a temporary file first.
/// </summary>
public sealed class FileUiSettingsStore : IUiSettingsStore
{
    public const string FileName = "ui-settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public FileUiSettingsStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(configDirectory);
        Location = Path.Combine(configDirectory, FileName);
    }

    public string Location { get; }

    public UiSettingsData Load()
    {
        try
        {
            if (!File.Exists(Location))
                return new UiSettingsData();
            var data = JsonSerializer.Deserialize<UiSettingsData>(File.ReadAllText(Location), Json);
            if (data is null)
                return new UiSettingsData();
            var theme = Enum.IsDefined(data.Theme) ? data.Theme : ThemeChoice.System;
            return data with { Theme = theme, Recent = (data.Recent ?? []).Where(r => r is not null).Take(UiSettingsData.MaxRecent).ToList() };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new UiSettingsData();
        }
    }

    public void Save(UiSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
        var temporary = Location + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, Location, overwrite: true);
    }
}
