// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell;
using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Acornsoft.MessageExplorer.Android;

/// <summary>
/// The Android entry point. Before Avalonia starts, the credential vault is pointed at the Android Keystore
/// store, so saved secrets are sealed by a key that never leaves the device's Keystore.
/// </summary>
[Activity(
    Label = "Message Explorer",
    Theme = "@style/LfmeTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.PlatformSecretStore = () => AndroidKeystoreVault.Create(ApplicationContext!);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
