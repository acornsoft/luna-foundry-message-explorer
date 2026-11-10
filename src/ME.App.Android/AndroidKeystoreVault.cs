// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Acornsoft.MessageExplorer.Android;

/// <summary>Builds the Android vault backend: <see cref="SealedSecretStore"/> over the Keystore key and app-private preferences.</summary>
public static class AndroidKeystoreVault
{
    public const string KeyAlias = "lfme.logins.v1";
    public const string PreferencesName = "lfme.sealed-logins";

    public static ISecretStore Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SealedSecretStore("Android Keystore", new AndroidKeystoreCipher(KeyAlias), new SharedPreferencesBlobStore(context, PreferencesName));
    }
}

/// <summary>
/// AES-256-GCM with a key generated inside the Android Keystore (hardware-backed where the device has it).
/// The key cannot be exported; this class only asks the Keystore to encrypt and decrypt. Blob layout:
/// one byte IV length, the IV, then ciphertext with the 128-bit tag.
/// </summary>
public sealed class AndroidKeystoreCipher : ISecretCipher
{
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private readonly string _alias;
    private readonly string? _unavailable;

    public AndroidKeystoreCipher(string alias)
    {
        _alias = alias;
        try
        {
            EnsureKey();
        }
        catch (Exception exception) when (exception is GeneralSecurityException or Java.Lang.Exception)
        {
            _unavailable = "The Android Keystore key could not be created (" + exception.GetType().Name + "), so secrets can't be saved.";
        }
    }

    public string? UnavailableReason => _unavailable;

    public byte[] Seal(byte[] plaintext, byte[] associatedData)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, Key());
        cipher.UpdateAAD(associatedData);
        var body = cipher.DoFinal(plaintext)!;
        var iv = cipher.GetIV()!;
        var blob = new byte[1 + iv.Length + body.Length];
        blob[0] = (byte)iv.Length;
        iv.CopyTo(blob, 1);
        body.CopyTo(blob, 1 + iv.Length);
        return blob;
    }

    public byte[] Open(byte[] sealedBlob, byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(sealedBlob);
        if (sealedBlob.Length < 1 || sealedBlob[0] is 0 || sealedBlob.Length < 1 + sealedBlob[0] + 16)
            throw new SecretStoreException("The saved secret is not a sealed blob.");
        var ivLength = sealedBlob[0];
        var iv = sealedBlob[1..(1 + ivLength)];
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, iv));
        cipher.UpdateAAD(associatedData);
        return cipher.DoFinal(sealedBlob[(1 + ivLength)..])!;
    }

    private IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store.GetKey(_alias, null) ?? throw new SecretStoreException("The Android Keystore key is missing.");
    }

    private void EnsureKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.ContainsAlias(_alias))
            return;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        var spec = new KeyGenParameterSpec.Builder(_alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .Build();
        generator.Init(spec);
        generator.GenerateKey();
    }
}

/// <summary>Sealed blobs in app-private SharedPreferences. Only ciphertext is written here.</summary>
public sealed class SharedPreferencesBlobStore(Context context, string name) : ISealedBlobStore
{
    private readonly ISharedPreferences _preferences = context.GetSharedPreferences(name, FileCreationMode.Private)!;

    public string? Read(string key) => _preferences.GetString(key, null);

    public void Write(string key, string base64Blob)
    {
        using var editor = _preferences.Edit()!;
        editor.PutString(key, base64Blob);
        if (!editor.Commit())
            throw new SecretStoreException("The sealed secret could not be written to app storage.");
    }

    public void Delete(string key)
    {
        using var editor = _preferences.Edit()!;
        editor.Remove(key);
        editor.Commit();
    }
}
