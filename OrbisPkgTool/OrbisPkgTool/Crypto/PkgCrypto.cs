using System.Security.Cryptography;
using System.Text;
using OrbisPkgTool.Pkg;

namespace OrbisPkgTool.Crypto;

/// <summary>
/// PS4 PKG key derivation and entry decryption.
///
/// All of this is pure managed code over System.Security.Cryptography —
/// the same algorithms the native orbis-pub-cmd performs with its embedded
/// OpenSSL 1.0.2g.
///
/// Derivation chain:
///   dk_i  = SHA256( SHA256(BE32(i)) || SHA256(cid padded to 48) || passcode(32 ASCII) )
///   per-entry key material:
///     iv_key = SHA256( raw_32_byte_entry || dk_keyIndex )
///     IV  = iv_key[0..16]
///     KEY = iv_key[16..32]
///     AES-128-CBC decrypt, truncate to DataSize
///
/// The RSA-encrypted ENTRY_KEYS table carries the same derived keys for the
/// console to recover with its private keys; for reading, the passcode-derived
/// values are equivalent (dk3 = RSA-decrypt(ENTRY_KEYS[3], key3 private)).
/// </summary>
public static class PkgCrypto
{
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    /// <summary>
    /// Sony's RSA-2048 key encryption: PKCS#1 v1.5 padding filled with
    /// Mersenne-Twister pseudo-random bytes (seeded from the double-SHA256
    /// of modulus||value), then raw modular exponentiation.
    /// Used to build the ENTRY_KEYS and IMAGE_KEY entries.
    /// </summary>
    public static byte[] RSA2048EncryptKey(byte[] modulus, byte[] value)
    {
        if (value.Length != 32)
            throw new ArgumentException("RSA2048EncryptKey expects a 32-byte value", nameof(value));

        var buffer = new byte[256 + 32];
        Buffer.BlockCopy(modulus, 0, buffer, 0, 256);
        Buffer.BlockCopy(value, 0, buffer, 256, 32);
        var finalHash = Sha256(Sha256(buffer));
        var seed = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            seed[i] = (uint)((finalHash[i * 4] << 24) | (finalHash[i * 4 + 1] << 16) |
                             (finalHash[i * 4 + 2] << 8) | finalHash[i * 4 + 3]);
        }
        var mt = new OrbisPkgTool.Util.MersenneTwister(seed);

        // PKCS#1 v1.5: 00 02 [non-zero random] 00 [value]
        var padded = new byte[256];
        padded[0] = 0;
        padded[1] = 2;
        padded[223] = 0;
        Buffer.BlockCopy(value, 0, padded, 224, 32);
        var shaSource = new byte[48];
        int k = 2;
        while (k < 223)
        {
            for (int i = 0; i < 12; i++)
            {
                uint r = mt.Int32();
                shaSource[i * 4] = (byte)(r >> 24);
                shaSource[i * 4 + 1] = (byte)(r >> 16);
                shaSource[i * 4 + 2] = (byte)(r >> 8);
                shaSource[i * 4 + 3] = (byte)r;
            }
            var random = Sha256(shaSource);
            foreach (var b in random)
            {
                if (k >= 223) break;
                if (b != 0)
                    padded[k++] = b;
            }
        }

        // Raw RSA: c = padded^e mod n
        var m = new System.Numerics.BigInteger(padded, isUnsigned: true, isBigEndian: true);
        var n = new System.Numerics.BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var c = System.Numerics.BigInteger.ModPow(m, 65537, n);
        var result = c.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (result.Length > 256)
            throw new InvalidOperationException("RSA result too large");
        var outBuf = new byte[256];
        Buffer.BlockCopy(result, 0, outBuf, 256 - result.Length, result.Length);
        return outBuf;
    }

    /// <summary>
    /// AES-128-CBC encryption (mirror of the PKG entry decryption).
    /// Returns the FULL padded ciphertext — CBC requires complete blocks.
    /// The PKG stores encrypted entries at their 16-aligned size; truncating
    /// the last partial block corrupts decryption (e.g. npbind.dat, 532 → 544).
    /// </summary>
    public static byte[] EncryptAesCbc(byte[] key, byte[] iv, byte[] data, int originalSize)
    {
        int padded = (data.Length + 15) & ~15;
        var paddedData = new byte[padded];
        Buffer.BlockCopy(data, 0, paddedData, 0, data.Length);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(paddedData, 0, padded);
    }

    /// <summary>
    /// Creates a keystone file (sce_sys/keystone) for the given passcode.
    /// </summary>
    public static byte[] CreateKeystone(string passcode)
    {
        var header = "6b657973746f6e65020001000000000000000000000000000000000000000000".FromHexCompact();
        var fingerprint = HmacSha256(Keys.KeystoneHmacKey, Encoding.ASCII.GetBytes(passcode));
        var final = HmacSha256(Keys.KeystoneMacData, header.Concat(fingerprint).ToArray());
        return header.Concat(fingerprint).Concat(final).ToArray();
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] FromHexCompact(this string hex)
    {
        hex = hex.Replace(" ", "");
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>Derives derived key <paramref name="index"/> (0-6) from the passcode.</summary>
    public static byte[] DeriveKey(string contentId, string passcode, uint index)
    {
        if (contentId.Length > 36)
            throw new ArgumentException("Content ID must be at most 36 characters", nameof(contentId));
        if (passcode.Length != 32)
            throw new ArgumentException("Passcode must be exactly 32 characters", nameof(passcode));

        var indexHash = SHA256.HashData(BitConverter.GetBytes(index).Reverse().ToArray());
        var cidHash = SHA256.HashData(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0')));
        var passcodeBytes = Encoding.ASCII.GetBytes(passcode);

        var data = new byte[96];
        Buffer.BlockCopy(indexHash, 0, data, 0, 32);
        Buffer.BlockCopy(cidHash, 0, data, 32, 32);
        Buffer.BlockCopy(passcodeBytes, 0, data, 64, 32);
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Derives the (IV, key) pair for a single encrypted entry.
    /// Matches the native tool: key material is the SHA256 of the raw 32-byte
    /// entry record concatenated with the derived key for the entry's key index.
    /// </summary>
    public static (byte[] Iv, byte[] Key) DeriveEntryKey(PkgEntry entry, byte[] derivedKey)
    {
        var material = new byte[64];
        Buffer.BlockCopy(entry.Raw, 0, material, 0, 32);
        Buffer.BlockCopy(derivedKey, 0, material, 32, 32);
        var hash = SHA256.HashData(material);
        return (hash.Take(16).ToArray(), hash.Skip(16).Take(16).ToArray());
    }

    /// <summary>
    /// AES-128-CBC decryption. <paramref name="data"/> is decrypted in place
    /// (padded to a multiple of 16 with zeros) and truncated to
    /// <paramref name="originalSize"/>.
    /// </summary>
    public static byte[] DecryptAesCbc(byte[] key, byte[] iv, byte[] data, int originalSize)
    {
        int padded = (data.Length + 15) & ~15;
        var paddedData = new byte[padded];
        Buffer.BlockCopy(data, 0, paddedData, 0, data.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        var decrypted = dec.TransformFinalBlock(paddedData, 0, padded);
        Array.Resize(ref decrypted, originalSize);
        return decrypted;
    }

    /// <summary>
    /// RSA-2048 decrypt with PKCS#1 v1.5 padding. Returns null when the
    /// ciphertext or padding is invalid.
    /// </summary>
    public static byte[]? TryRsaDecrypt(byte[] data, RSAParameters key)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(key);
            return rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts an ENTRY_KEYS section: 32-byte seed digest, 7 x 32-byte digests,
    /// 7 x 256-byte RSA-encrypted derived keys. Returns the 7 derived keys
    /// (dk0..dk6) recovered via RSA using the key-set, or null if decrypting
    /// ENTRY_KEYS[3] fails with the standard key (i.e. a custom-key PKG).
    /// </summary>
    public static byte[][]? DecryptEntryKeys(byte[] entryKeysData, PkgKeySet keySet)
    {
        if (entryKeysData.Length < 32 + 7 * 32 + 7 * 256)
            return null;
        var keys = new byte[7][];
        for (int i = 0; i < 7; i++)
        {
            var enc = new byte[256];
            Buffer.BlockCopy(entryKeysData, 32 + 7 * 32 + i * 256, enc, 0, 256);
            var rsaKey = i == 3 ? keySet.DerivedKey3 : keySet.DerivedKey3; // only dk3 private key is known
            var dec = TryRsaDecrypt(enc, rsaKey);
            if (dec == null || dec.Length != 32)
                return null;
            keys[i] = dec;
        }
        return keys;
    }

    /// <summary>
    /// Recovers the PFS EKPFS from the IMAGE_KEY entry:
    ///   1. AES-CBC decrypt the image key with SHA256(image_key_meta || dk3)
    ///   2. RSA-2048 decrypt the result with the fake keyset
    /// Returns null when the chain fails (e.g. custom-key PKG).
    /// </summary>
    public static byte[]? DecryptEkpfs(PkgEntry imageKeyEntry, byte[] imageKeyData, byte[] dk3, PkgKeySet keySet)
    {
        var (iv, key) = DeriveEntryKey(imageKeyEntry, dk3);
        var aesDecrypted = DecryptAesCbc(key, iv, imageKeyData, imageKeyData.Length);
        return TryRsaDecrypt(aesDecrypted, keySet.FakeKeyset);
    }
}
