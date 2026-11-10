// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using System.Runtime.Loader;

namespace Acornsoft.MessageExplorer.Jobs.Hosting;

/// <summary>
/// Runs a framework-dependent plugin assembly inside this process, in its own load context, the way
/// <c>dotnet plugin.dll</c> would. Stdin and stdout are this process's, so the JSON-RPC wire is unchanged.
/// </summary>
public static class PluginSelfHost
{
    public static int Run(string assemblyPath, string[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        ArgumentNullException.ThrowIfNull(args);
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Plugin assembly not found: {path}");
            return 2;
        }

        var context = new PluginLoadContext(path);
        var entry = context.LoadFromAssemblyPath(path).EntryPoint;
        if (entry is null)
        {
            Console.Error.WriteLine($"Plugin assembly has no entry point: {path}");
            return 2;
        }

        var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [args]);
        return result is int code ? code : 0;
    }

    /// <summary>
    /// Resolves the plugin's own dependencies from its .deps.json, or from its folder when the host cannot
    /// (a single-file app has no separate host library to ask). Framework assemblies come from this process.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _folder;
        private readonly AssemblyDependencyResolver? _resolver;

        public PluginLoadContext(string mainAssemblyPath)
            : base(Path.GetFileNameWithoutExtension(mainAssemblyPath))
        {
            _folder = Path.GetDirectoryName(mainAssemblyPath)!;
            try
            {
                _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            }
            catch (InvalidOperationException)
            {
                _resolver = null;
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName.Name + ".dll");
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) ?? Probe(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        private string? Probe(string fileName)
        {
            var candidate = Path.Combine(_folder, fileName);
            return File.Exists(candidate) ? candidate : null;
        }
    }
}
