// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Model.Tests;

public class RepoPolicyTests
{
    [Fact]
    public void License_file_is_mpl_2_0()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LICENSE"));
        Assert.Contains("Mozilla Public License Version 2.0", text, StringComparison.Ordinal);
        Assert.Contains("Exhibit A - Source Code Form License Notice", text, StringComparison.Ordinal);
        Assert.Contains("Exhibit B - \"Incompatible With Secondary Licenses\" Notice", text, StringComparison.Ordinal);
        Assert.Contains("https://mozilla.org/MPL/2.0/", text, StringComparison.Ordinal);
        Assert.True(text.Length > 15000);
    }

    [Fact]
    public void Source_files_carry_the_spdx_header()
    {
        var missing = SourceFiles()
            .Where(path => !File.ReadAllText(path).Contains("SPDX-License-Identifier: MPL-2.0", StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Repo_has_no_banned_names_or_secret_patterns()
    {
        var customer = "E" + "SL";
        var internalName = "Gno" + "mah";
        var secrets = new[]
        {
            "Shared" + "AccessKey",
            "Account" + "Key=",
            "Default" + "EndpointsProtocol",
            "BEGIN PRIVATE" + " KEY",
            "BEGIN RSA PRIVATE" + " KEY",
        };

        foreach (var path in TextFiles())
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(customer, text, StringComparison.Ordinal);
            Assert.DoesNotContain(internalName, text, StringComparison.Ordinal);
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "src", "tests" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var ext = Path.GetExtension(path);
                if (ext is ".cs" or ".axaml" or ".bicep" or ".yaml")
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> TextFiles()
    {
        var root = RepoRoot();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path);
            if (name == "LICENSE"
                || ext is ".cs" or ".axaml" or ".bicep" or ".yaml" or ".md" or ".json" or ".yml" or ".props" or ".txt" or ".sh")
            {
                yield return path;
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MessageExplorer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("MessageExplorer.sln was not found above the test output.");
    }
}
