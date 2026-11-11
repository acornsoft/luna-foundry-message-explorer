// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

public enum BrowserPlatform
{
    MacOS,
    Windows,
    Linux,
    Other,
}

/// <summary>
/// Where the sign-in page opens. <see cref="BrowserChoices.SystemDefaultId"/> is the OS default browser;
/// <see cref="BrowserChoices.CopyLinkId"/> opens nothing and only offers the link to copy into any browser.
/// </summary>
/// <param name="Executable">The app bundle (macOS) or program (Windows, Linux) to start; null for the two special choices.</param>
public sealed record BrowserChoice(string Id, string Name, string? Executable = null)
{
    public override string ToString() => Name;
}

public static class BrowserChoices
{
    public const string SystemDefaultId = "default";
    public const string CopyLinkId = "copy-link";

    public static BrowserChoice SystemDefault { get; } = new(SystemDefaultId, "System default browser");

    public static BrowserChoice CopyLinkOnly { get; } = new(CopyLinkId, "Don't open a browser (copy the link)");

    public static BrowserPlatform Current =>
        OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS
        : OperatingSystem.IsWindows() ? BrowserPlatform.Windows
        : OperatingSystem.IsLinux() ? BrowserPlatform.Linux
        : BrowserPlatform.Other;

    private static readonly (string Id, string Name, string MacApp, string[] WindowsPaths, string[] LinuxNames)[] Known =
    [
        ("safari", "Safari", "Safari.app", [], []),
        ("chrome", "Google Chrome", "Google Chrome.app", [@"Google\Chrome\Application\chrome.exe"], ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser"]),
        ("edge", "Microsoft Edge", "Microsoft Edge.app", [@"Microsoft\Edge\Application\msedge.exe"], ["microsoft-edge", "microsoft-edge-stable"]),
        ("firefox", "Firefox", "Firefox.app", [@"Mozilla Firefox\firefox.exe"], ["firefox"]),
        ("brave", "Brave", "Brave Browser.app", [@"BraveSoftware\Brave-Browser\Application\brave.exe"], ["brave-browser", "brave"]),
        ("arc", "Arc", "Arc.app", [@"Arc\Arc.exe"], []),
    ];

    /// <summary>
    /// The system default, every known browser found on this computer, then copy-link-only. Looks in
    /// /Applications and ~/Applications on macOS, Program Files and the user's local app data on Windows,
    /// and PATH on Linux. <paramref name="exists"/> and <paramref name="environment"/> are injectable for tests.
    /// </summary>
    public static IReadOnlyList<BrowserChoice> Detect(BrowserPlatform platform, Func<string, bool> exists, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(environment);
        var found = new List<BrowserChoice> { SystemDefault };
        foreach (var browser in Known)
        {
            var executable = platform switch
            {
                BrowserPlatform.MacOS => MacCandidates(browser.MacApp, environment).FirstOrDefault(exists),
                BrowserPlatform.Windows => WindowsCandidates(browser.WindowsPaths, environment).FirstOrDefault(exists),
                BrowserPlatform.Linux => LinuxCandidates(browser.LinuxNames, environment).FirstOrDefault(exists),
                _ => null,
            };
            if (executable is not null)
                found.Add(new BrowserChoice(browser.Id, browser.Name, executable));
        }

        found.Add(CopyLinkOnly);
        return found;
    }

    /// <summary>The browsers on this computer.</summary>
    public static IReadOnlyList<BrowserChoice> DetectHere() =>
        Detect(Current, path => File.Exists(path) || Directory.Exists(path), Environment.GetEnvironmentVariable);

    /// <summary>The saved choice by ID, or the system default when it is unknown or no longer installed.</summary>
    public static BrowserChoice Find(IReadOnlyList<BrowserChoice> choices, string? id) =>
        choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? SystemDefault;

    private static IEnumerable<string> MacCandidates(string app, Func<string, string?> environment)
    {
        yield return Path.Combine("/Applications", app);
        if (environment("HOME") is { Length: > 0 } home)
            yield return Path.Combine(home, "Applications", app);
    }

    private static IEnumerable<string> WindowsCandidates(string[] relative, Func<string, string?> environment)
    {
        foreach (var root in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            if (environment(root) is not { Length: > 0 } folder)
                continue;
            foreach (var path in relative)
                yield return folder.TrimEnd('\\') + "\\" + path;
        }
    }

    private static IEnumerable<string> LinuxCandidates(string[] names, Func<string, string?> environment)
    {
        var path = environment("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
                yield return dir.TrimEnd('/') + "/" + name;
        }
    }
}

/// <summary>Opens the sign-in URL in the chosen browser.</summary>
public interface IBrowserLauncher
{
    /// <summary>Returns false when nothing was opened (copy-link-only, or the start failed); the link is still offered.</summary>
    bool Open(Uri url, BrowserChoice browser);
}

/// <summary>
/// Starts the browser process directly with the URL as one argument (no shell, so the URL can't be
/// reinterpreted). The localhost redirect comes back to MSAL's listener whichever browser is used.
/// </summary>
public sealed class ProcessBrowserLauncher(BrowserPlatform platform, Func<ProcessStartInfo, bool>? start = null) : IBrowserLauncher
{
    private readonly Func<ProcessStartInfo, bool> _start = start ?? (info => Process.Start(info) is not null);

    public static ProcessStartInfo? Command(BrowserPlatform platform, Uri url, BrowserChoice browser)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(browser);
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Only http and https sign-in links are opened.", nameof(url));
        if (browser.Id == BrowserChoices.CopyLinkId)
            return null;

        var info = browser.Executable is null
            ? platform switch
            {
                BrowserPlatform.MacOS => new ProcessStartInfo("/usr/bin/open"),
                BrowserPlatform.Windows => new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true },
                _ => new ProcessStartInfo("xdg-open"),
            }
            : platform == BrowserPlatform.MacOS
                ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-a", browser.Executable } }
                : new ProcessStartInfo(browser.Executable);
        if (!info.UseShellExecute)
            info.ArgumentList.Add(url.AbsoluteUri);
        return info;
    }

    public bool Open(Uri url, BrowserChoice browser)
    {
        var info = Command(platform, url, browser);
        if (info is null)
            return false;
        try
        {
            return _start(info);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
