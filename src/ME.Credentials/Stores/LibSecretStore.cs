// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>Secret Service calls by attribute set. The native one P/Invokes libsecret; tests use a fake.</summary>
public interface ISecretServiceApi
{
    void Store(string label, IReadOnlyDictionary<string, string> attributes, string secret);

    string? Lookup(IReadOnlyDictionary<string, string> attributes);

    void Clear(IReadOnlyDictionary<string, string> attributes);
}

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet's Secret Service, KeePassXC) through libsecret over D-Bus.
/// When no Secret Service answers, the store is unavailable and every write is refused: no file fallback.
/// </summary>
public sealed class LibSecretStore : ISecretStore
{
    public const string Application = "ai.acornsoft.lfme";
    private readonly ISecretServiceApi _api;
    private readonly string _service;

    public LibSecretStore(ISecretServiceApi api, string service)
    {
        _api = api;
        _service = service;
        try
        {
            // A lookup that matches nothing proves libsecret loads and a Secret Service answers on D-Bus.
            _api.Lookup(Attributes("availability-probe"));
        }
        catch (Exception exception) when (exception is SecretStoreException or DllNotFoundException or EntryPointNotFoundException)
        {
            UnavailableReason = "No Secret Service keyring is running (for example GNOME Keyring or KeePassXC), so secrets can't be saved. Nothing is written to a file instead.";
        }
    }

    public string Name => "Secret Service keyring";

    public SecretProtection Protection => SecretProtection.OperatingSystem;

    public string? UnavailableReason { get; }

    public static ISecretStore Create(string service) => new LibSecretStore(new NativeLibSecret(), service);

    public IReadOnlyDictionary<string, string> Attributes(string key) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["application"] = Application,
        ["service"] = _service,
        ["login"] = key,
    };

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        Ready();
        _api.Store($"Luna Foundry Message Explorer login {key}", Attributes(key), secret.Reveal());
        return Task.CompletedTask;
    }

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Ready();
        var value = _api.Lookup(Attributes(key));
        return Task.FromResult(value is null ? null : new RuntimeSecret(value));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Ready();
        _api.Clear(Attributes(key));
        return Task.CompletedTask;
    }

    private void Ready()
    {
        if (UnavailableReason is { } reason)
            throw new SecretStoreException(reason);
    }
}

/// <summary>libsecret's non-variadic *v_sync calls with a GHashTable of attributes and a hand-built SecretSchema.</summary>
internal sealed class NativeLibSecret : ISecretServiceApi
{
    private const string NativeLibraryName = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const int SchemaSize = 592;
    private const int AttributeOffset = 16;
    private const int AttributeSize = 16;

    private static readonly string[] AttributeNames = ["application", "service", "login"];
    private static readonly Lazy<IntPtr> Schema = new(BuildSchema);

    public void Store(string label, IReadOnlyDictionary<string, string> attributes, string secret)
    {
        var password = Marshal.StringToCoTaskMemUTF8(secret);
        try
        {
            WithAttributes(attributes, table =>
            {
                var ok = secret_password_storev_sync(Schema.Value, table, "default", label, password, IntPtr.Zero, out var error);
                ThrowIf(error, ok ? null : "store");
                return 0;
            });
        }
        finally
        {
            Wipe(password, secret.Length * 4);
        }
    }

    public string? Lookup(IReadOnlyDictionary<string, string> attributes) => WithAttributes(attributes, table =>
    {
        var result = secret_password_lookupv_sync(Schema.Value, table, IntPtr.Zero, out var error);
        ThrowIf(error, null);
        if (result == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            secret_password_free(result);
        }
    });

    public void Clear(IReadOnlyDictionary<string, string> attributes) => WithAttributes(attributes, table =>
    {
        secret_password_clearv_sync(Schema.Value, table, IntPtr.Zero, out var error);
        ThrowIf(error, null);
        return 0;
    });

    private static T WithAttributes<T>(IReadOnlyDictionary<string, string> attributes, Func<IntPtr, T> call)
    {
        var glib = NativeLibrary.Load(GLib);
        var table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        var strings = new List<IntPtr>();
        try
        {
            foreach (var (name, value) in attributes)
            {
                var k = Marshal.StringToCoTaskMemUTF8(name);
                var v = Marshal.StringToCoTaskMemUTF8(value);
                strings.Add(k);
                strings.Add(v);
                g_hash_table_insert(table, k, v);
            }

            return call(table);
        }
        finally
        {
            g_hash_table_unref(table);
            foreach (var s in strings)
                Marshal.FreeCoTaskMem(s);
        }
    }

    private static IntPtr BuildSchema()
    {
        var schema = Marshal.AllocHGlobal(SchemaSize);
        Marshal.Copy(new byte[SchemaSize], 0, schema, SchemaSize);
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(LibSecretStore.Application + ".login"));
        Marshal.WriteInt32(schema, 8, 0);
        for (var i = 0; i < AttributeNames.Length; i++)
        {
            Marshal.WriteIntPtr(schema, AttributeOffset + (i * AttributeSize), Marshal.StringToCoTaskMemUTF8(AttributeNames[i]));
            Marshal.WriteInt32(schema, AttributeOffset + (i * AttributeSize) + 8, 0);
        }

        return schema;
    }

    private static void ThrowIf(IntPtr error, string? failedAction)
    {
        if (error != IntPtr.Zero)
        {
            var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
            g_error_free(error);
            throw new SecretStoreException("The Secret Service keyring refused the request: " + message);
        }

        if (failedAction is not null)
            throw new SecretStoreException($"The Secret Service keyring could not {failedAction} the secret.");
    }

    private static void Wipe(IntPtr buffer, int bytes)
    {
        var length = 0;
        while (length < bytes && Marshal.ReadByte(buffer, length) != 0)
            length++;
        Marshal.Copy(new byte[length], 0, buffer, length);
        Marshal.FreeCoTaskMem(buffer);
    }

    [DllImport(NativeLibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, [MarshalAs(UnmanagedType.LPUTF8Str)] string collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [DllImport(NativeLibraryName)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(NativeLibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(NativeLibraryName)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(GLib)]
    private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);

    [DllImport(GLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(GLib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(GLib)]
    private static extern void g_error_free(IntPtr error);
}
