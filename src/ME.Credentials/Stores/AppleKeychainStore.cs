// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Text;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

public enum ApplePlatform
{
    MacOS,
    IOS,
}

/// <summary>How long a Keychain item stays readable. iOS items use WhenUnlockedThisDeviceOnly.</summary>
public enum KeychainAccessibility
{
    Default,
    WhenUnlockedThisDeviceOnly,
}

/// <summary>One generic-password item, addressed by service and account.</summary>
public sealed record KeychainItem(string Service, string Account, string Label, KeychainAccessibility Accessibility);

/// <summary>The four SecItem calls. The native one P/Invokes Security.framework; tests use a fake.</summary>
public interface IKeychainApi
{
    int Add(KeychainItem item, byte[] data);

    int Update(KeychainItem item, byte[] data);

    int CopyData(KeychainItem item, out byte[]? data);

    int Delete(KeychainItem item);
}

/// <summary>
/// Keychain on macOS and iOS through Security.framework SecItem* calls (shared code). Each login is one
/// generic-password item: service = the app's service name, account = the login ID. On iOS the item is
/// kSecAttrAccessibleWhenUnlockedThisDeviceOnly, so it never leaves the device in a backup or sync.
/// </summary>
public sealed class AppleKeychainStore(IKeychainApi api, string service, ApplePlatform platform) : ISecretStore
{
    public const int Success = 0;
    public const int ItemNotFound = -25300;
    public const int DuplicateItem = -25299;

    public string Name => "Keychain";

    public SecretProtection Protection => SecretProtection.OperatingSystem;

    public string? UnavailableReason { get; init; }

    public KeychainAccessibility Accessibility =>
        platform == ApplePlatform.IOS ? KeychainAccessibility.WhenUnlockedThisDeviceOnly : KeychainAccessibility.Default;

    public static ISecretStore Create(string service, ApplePlatform platform)
    {
        try
        {
            return new AppleKeychainStore(new SecItemKeychainApi(), service, platform);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new UnavailableSecretStore("Keychain", "The Keychain could not be opened, so secrets can't be saved.");
        }
    }

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        var data = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            var item = Item(key);
            var status = api.Update(item, data);
            if (status == ItemNotFound)
                status = api.Add(item, data);
            Check(status, "save");
        }
        finally
        {
            Array.Clear(data);
        }

        return Task.CompletedTask;
    }

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var status = api.CopyData(Item(key), out var data);
        if (status == ItemNotFound || data is null)
            return Task.FromResult<RuntimeSecret?>(null);
        try
        {
            Check(status, "read");
            return Task.FromResult<RuntimeSecret?>(new RuntimeSecret(Encoding.UTF8.GetString(data)));
        }
        finally
        {
            Array.Clear(data);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var status = api.Delete(Item(key));
        if (status != ItemNotFound)
            Check(status, "delete");
        return Task.CompletedTask;
    }

    private KeychainItem Item(string key) => new(service, key, $"Luna Foundry Message Explorer login {key}", Accessibility);

    private static void Check(int status, string action)
    {
        if (status != Success)
            throw new SecretStoreException($"The Keychain could not {action} the secret (status {status}).");
    }
}

/// <summary>Security.framework SecItem* through P/Invoke, with CoreFoundation dictionaries built and released here.</summary>
internal sealed class SecItemKeychainApi : IKeychainApi
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private readonly IntPtr _class;
    private readonly IntPtr _genericPassword;
    private readonly IntPtr _service;
    private readonly IntPtr _account;
    private readonly IntPtr _label;
    private readonly IntPtr _valueData;
    private readonly IntPtr _returnData;
    private readonly IntPtr _matchLimit;
    private readonly IntPtr _matchLimitOne;
    private readonly IntPtr _accessible;
    private readonly IntPtr _whenUnlockedThisDeviceOnly;
    private readonly IntPtr _true;
    private readonly IntPtr _keyCallbacks;
    private readonly IntPtr _valueCallbacks;

    public SecItemKeychainApi()
    {
        var security = NativeLibrary.Load(Security);
        var cf = NativeLibrary.Load(CoreFoundation);
        _class = Constant(security, "kSecClass");
        _genericPassword = Constant(security, "kSecClassGenericPassword");
        _service = Constant(security, "kSecAttrService");
        _account = Constant(security, "kSecAttrAccount");
        _label = Constant(security, "kSecAttrLabel");
        _valueData = Constant(security, "kSecValueData");
        _returnData = Constant(security, "kSecReturnData");
        _matchLimit = Constant(security, "kSecMatchLimit");
        _matchLimitOne = Constant(security, "kSecMatchLimitOne");
        _accessible = Constant(security, "kSecAttrAccessible");
        _whenUnlockedThisDeviceOnly = Constant(security, "kSecAttrAccessibleWhenUnlockedThisDeviceOnly");
        _true = Constant(cf, "kCFBooleanTrue");
        _keyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
        _valueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
    }

    public int Add(KeychainItem item, byte[] data)
    {
        using var scope = new CfScope();
        var pairs = Identity(item, scope);
        pairs.Add((_label, scope.String(item.Label)));
        pairs.Add((_valueData, scope.Data(data)));
        if (item.Accessibility == KeychainAccessibility.WhenUnlockedThisDeviceOnly)
            pairs.Add((_accessible, _whenUnlockedThisDeviceOnly));
        return SecItemAdd(Dictionary(pairs, scope), IntPtr.Zero);
    }

    public int Update(KeychainItem item, byte[] data)
    {
        using var scope = new CfScope();
        var query = Dictionary(Identity(item, scope), scope);
        var change = Dictionary([(_valueData, scope.Data(data))], scope);
        return SecItemUpdate(query, change);
    }

    public int CopyData(KeychainItem item, out byte[]? data)
    {
        data = null;
        using var scope = new CfScope();
        var pairs = Identity(item, scope);
        pairs.Add((_returnData, _true));
        pairs.Add((_matchLimit, _matchLimitOne));
        var status = SecItemCopyMatching(Dictionary(pairs, scope), out var result);
        if (status != 0 || result == IntPtr.Zero)
            return status;
        try
        {
            var length = (int)CFDataGetLength(result);
            data = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), data, 0, length);
            return status;
        }
        finally
        {
            CFRelease(result);
        }
    }

    public int Delete(KeychainItem item)
    {
        using var scope = new CfScope();
        return SecItemDelete(Dictionary(Identity(item, scope), scope));
    }

    private List<(IntPtr Key, IntPtr Value)> Identity(KeychainItem item, CfScope scope) =>
    [
        (_class, _genericPassword),
        (_service, scope.String(item.Service)),
        (_account, scope.String(item.Account)),
    ];

    private IntPtr Dictionary(List<(IntPtr Key, IntPtr Value)> pairs, CfScope scope) =>
        scope.Own(CFDictionaryCreate(IntPtr.Zero, pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(), pairs.Count, _keyCallbacks, _valueCallbacks));

    private static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    /// <summary>Releases every CoreFoundation object it created, and wipes the data buffers first.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr Own(IntPtr value)
        {
            _owned.Add(value);
            return value;
        }

        public IntPtr String(string value) => Own(CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length));

        public IntPtr Data(byte[] value) => Own(CFDataCreate(IntPtr.Zero, value, value.Length));

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                if (_owned[i] != IntPtr.Zero)
                    CFRelease(_owned[i]);
            }
        }
    }

    [DllImport(Security)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(Security)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(Security)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(Security)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string characters, nint length);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
}
