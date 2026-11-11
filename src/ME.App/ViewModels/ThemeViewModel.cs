// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.Settings;
using Avalonia;
using Avalonia.Styling;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The app's theme: System (follow the OS light or dark setting, the default), Light or Dark. Shown on the
/// Accounts page, the Connect panel and the Settings menu; all three share this one object, so they always agree.
/// The choice is applied to the whole app at once and remembered in ui-settings.json.
/// </summary>
public sealed class ThemeViewModel : ViewModelBase
{
    private readonly IUiSettingsStore _store;
    private readonly Action<ThemeVariant> _apply;
    private ThemeChoice _choice;

    /// <param name="store">Where the choice is kept between launches.</param>
    /// <param name="apply">Sets the variant on the app. Defaults to <see cref="Application.RequestedThemeVariant"/>.</param>
    public ThemeViewModel(IUiSettingsStore store, Action<ThemeVariant>? apply = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _apply = apply ?? ApplyToApplication;
        _choice = Load(store);
        _apply(VariantFor(_choice));
    }

    public ThemeChoice Choice
    {
        get => _choice;
        set
        {
            if (!Set(ref _choice, value))
                return;
            _apply(VariantFor(value));
            Save(value);
            OnPropertyChanged(nameof(IsSystem));
            OnPropertyChanged(nameof(IsLight));
            OnPropertyChanged(nameof(IsDark));
            OnPropertyChanged(nameof(Description));
        }
    }

    // Two-way radio buttons: only a true sets the choice, so un-checking the old one never fights the new one.
    public bool IsSystem
    {
        get => _choice == ThemeChoice.System;
        set
        {
            if (value)
                Choice = ThemeChoice.System;
        }
    }

    public bool IsLight
    {
        get => _choice == ThemeChoice.Light;
        set
        {
            if (value)
                Choice = ThemeChoice.Light;
        }
    }

    public bool IsDark
    {
        get => _choice == ThemeChoice.Dark;
        set
        {
            if (value)
                Choice = ThemeChoice.Dark;
        }
    }

    public string Description => _choice switch
    {
        ThemeChoice.Light => "Light, on every launch.",
        ThemeChoice.Dark => "Dark, on every launch.",
        _ => "Follows your computer's light or dark setting.",
    };

    /// <summary>Where the choice could not be saved, if it could not. The theme still changes for this session.</summary>
    public string? SaveError { get; private set; }

    public static ThemeVariant VariantFor(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private static ThemeChoice Load(IUiSettingsStore store)
    {
        try
        {
            return store.Load().Theme;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ThemeChoice.System;
        }
    }

    private void Save(ThemeChoice choice)
    {
        try
        {
            _store.Save(_store.Load() with { Theme = choice });
            SaveError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveError = "The theme could not be saved for next time: " + exception.Message;
        }

        OnPropertyChanged(nameof(SaveError));
    }

    // The app is only touched from its UI thread; view-model tests that build a MainViewModel off that thread
    // (with no window) have nothing to theme.
    private static void ApplyToApplication(ThemeVariant variant)
    {
        if (Application.Current is { } app && Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            app.RequestedThemeVariant = variant;
    }
}
