using System.Buffers.Binary;
using System.Security.Cryptography;
using OrbisPkgTool.Crypto;

// Replicates shadPS4's PKG::Extract crypto chain EXACTLY (crypto.cpp):
//   dk3  = RSA2048Decrypt(key1[3], is_dk3=true)   [DebugRifKeyset]
//   ivKey = SHA256(entry_struct(0x20) || dk3)
//   imgKey = AES-CBC(ivKey[16..], ivKey[0..16], image_key_data)
//   ekpfs = RSA2048Decrypt(imgKey, is_dk3=false)  [FakeKeyset]
//   (dataKey, tweakKey) = HMAC-SHA256(ekpfs, LE32(1) || seed)
// Then decrypts the first cache*2 bytes of the outer PFS and checks
// whether "PFSC" magic is found at the expected offset (block 17).
//
// If the ORIGINAL yields PFSC and the REBUILT does not, the key
// derivation chain differs -> garbage decryption -> crash.

namespace OrbisPkgTool;

static class Shad4CryptoTest
{
    public static int Run(string pkgPath)
    {
        Console.WriteLine($"=== shadPS4 crypto-chain replica: {Path.GetFileName(pkgPath)} ===");
        using var fs = File.OpenRead(pkgPath);

        byte[] hdr = new byte[0x1100];
        fs.ReadExactly(hdr);
        uint entryCount   = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10));
        uint tableOffset  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x18));
        ulong pfsImageOff = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x410));
        uint  pfsCacheSz  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x43C));
        uint  length      = pfsCacheSz * 2;
        Console.WriteLine($"  entryCount={entryCount} tableOff=0x{tableOffset:X} pfsOff=0x{pfsImageOff:X} cacheSz=0x{pfsCacheSz:X} length=0x{length:X}");

        // Read entry table
        var entries = new List<(uint Id, uint Flags1, uint Flags2, uint Offset, uint Size)>();
        fs.Position = tableOffset;
        for (int i = 0; i < entryCount; i++)
        {
            byte[] eb = new byte[32]; fs.ReadExactly(eb);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(eb);
            uint f1 = BinaryPrimitives.ReadUInt32BigEndian(eb.AsSpan(8));
            uint f2 = BinaryPrimitives.ReadUInt32BigEndian(eb.AsSpan(12));
            uint off = BinaryPrimitives.ReadUInt32BigEndian(eb.AsSpan(16));
            uint sz  = BinaryPrimitives.ReadUInt32BigEndian(eb.AsSpan(20));
            entries.Add((id, f1, f2, off, sz));
        }

        // 1. dk3 from ENTRY_KEYS[3] (entry 0x10)
        var ek = entries.First(e => e.Id == 0x10);
        byte[] ekData = new byte[ek.Size];
        fs.Position = ek.Offset; fs.ReadExactly(ekData);
        // layout: seed_digest[32] + 7*digest[32] + 7*key[256]
        byte[] key1_3 = ekData.AsSpan(32 + 7 * 32 + 3 * 256, 256).ToArray();
        var dk3 = PkgCrypto.TryRsaDecrypt(key1_3, PkgKeySet.Standard.DerivedKey3);
        Console.WriteLine($"  dk3: {(dk3 == null ? "RSA DECRYPT FAILED (null)" : Convert.ToHexString(dk3[..8]) + "...")}");

        // 2. ivKey = SHA256(entry_struct(0x20) || dk3)
        var imgEntry = entries.First(e => e.Id == 0x20);
        byte[] entryStruct = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(0), imgEntry.Id);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(8), imgEntry.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(12), imgEntry.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(16), imgEntry.Offset);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(20), imgEntry.Size);
        byte[] ivKey = SHA256.HashData(entryStruct.Concat(dk3!).ToArray());
        Console.WriteLine($"  ivKey: {Convert.ToHexString(ivKey[..8])}...");

        // 3. imgKey = AES-CBC(ivKey[16..32], ivKey[0..16], image_key_data)
        byte[] imgData = new byte[imgEntry.Size];
        fs.Position = imgEntry.Offset; fs.ReadExactly(imgData);
        byte[] imgKey = new byte[256];
        using (var aes = Aes.Create())
        {
            aes.Key = ivKey[16..32];
            aes.IV = ivKey[0..16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            imgKey = dec.TransformFinalBlock(imgData, 0, imgData.Length);
        }
        Console.WriteLine($"  imgKey: {Convert.ToHexString(imgKey[..8])}...");

        // 4. ekpfs = RSA2048Decrypt(imgKey, FakeKeyset)
        var ekpfs = PkgCrypto.TryRsaDecrypt(imgKey, PkgKeySet.Standard.FakeKeyset);
        Console.WriteLine($"  ekpfs: {(ekpfs == null ? "RSA DECRYPT FAILED (null)" : Convert.ToHexString(ekpfs[..8]) + "...")}");
        if (ekpfs == null) { Console.WriteLine("  RESULT: key chain broken at ekpfs"); return 1; }

        // 5. seed + XTS keys
        fs.Position = (long)pfsImageOff + 0x370;
        byte[] seed = new byte[16]; fs.ReadExactly(seed);
        Console.WriteLine($"  seed: {Convert.ToHexString(seed)}");
        byte[] hmac = PkgCrypto.HmacSha256(ekpfs, new byte[] { 1, 0, 0, 0 }.Concat(seed).ToArray());
        byte[] tweakKey = hmac[0..16];
        byte[] dataKey = hmac[16..32];
        Console.WriteLine($"  dataKey: {Convert.ToHexString(dataKey[..8])}...  tweakKey: {Convert.ToHexString(tweakKey[..8])}...");

        // 6. Decrypt first `length` bytes of outer PFS with XTS, sector 0..n
        // XTS = AES-ECB(tweakKey, LE64(sector)) then AES-ECB(dataKey, block^tweak)
        byte[] enc = new byte[length];
        fs.Position = (long)pfsImageOff; fs.ReadExactly(enc);
        byte[] decrypted = XtsDecrypt(enc, dataKey, tweakKey);
        Console.WriteLine($"  decrypted {decrypted.Length} bytes");

        // 7. GetPFSCOffset: search 0x10000-aligned from 0x20000
        uint pfscMagic = 0x43534650;
        long pfscOff = -1;
        for (long i = 0x20000; i + 4 <= decrypted.Length; i += 0x10000)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan((int)i, 4));
            if (v == pfscMagic) { pfscOff = i; break; }
        }
        Console.WriteLine($"  PFSC magic at: 0x{(pfscOff < 0 ? "NOT FOUND" : pfscOff.ToString("X"))}");
        if (pfscOff < 0) { Console.WriteLine("  RESULT: PFSC NOT FOUND — key chain mismatch or encryption differs"); return 2; }

        // 8. Read PFSC header as shadPS4 does
        long blockTable = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x18, 8));
        long dataLength = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x28, 8));
        long blockSz2   = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x10, 8));
        int numBlocks = (int)(dataLength / blockSz2);
        Console.WriteLine($"  PFSC: blockSz2={blockSz2} dataLen={dataLength} numBlocks={numBlocks} table@0x{blockTable:X}");
        Console.WriteLine($"  RESULT: PFSC FOUND at 0x{pfscOff:X} — key chain MATCHES");
        return 0;
    }

    static byte[] XtsDecrypt(byte[] data, byte[] dataKey, byte[] tweakKey)
    {
        var result = (byte[])data.Clone();
        using var aesT = Aes.Create();
        aesT.Key = tweakKey; aesT.Mode = CipherMode.ECB; aesT.Padding = PaddingMode.None;
        using var aesD = Aes.Create();
        aesD.Key = dataKey; aesD.Mode = CipherMode.ECB; aesD.Padding = PaddingMode.None;

        for (int sectorStart = 0; sectorStart + 0x1000 <= data.Length; sectorStart += 0x1000)
        {
            ulong sector = (ulong)(sectorStart / 0x1000);
            byte[] tweak = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(tweak, sector);
            byte[] encTweak = aesT.CreateEncryptor().TransformFinalBlock(tweak, 0, 16);
            for (int off = sectorStart; off < sectorStart + 0x1000; off += 16)
            {
                byte[] block = data.AsSpan(off, 16).ToArray();
                for (int b = 0; b < 16; b++) block[b] ^= encTweak[b];
                block = aesD.CreateDecryptor().TransformFinalBlock(block, 0, 16);
                for (int b = 0; b < 16; b++) result[off + b] = (byte)(block[b] ^ encTweak[b]);
                XtsMult(encTweak); // multiply tweak by alpha per 16-byte block
            }
        }
        return result;
    }

    // XTS multiplication by alpha (0x87 polynomial): shift left, xor 0x87 if carry
    static void XtsMult(byte[] tweak)
    {
        byte carry = 0;
        for (int i = 0; i < 16; i++)
        {
            byte nextCarry = (byte)(tweak[i] >> 7);
            tweak[i] = (byte)((tweak[i] << 1) | carry);
            carry = nextCarry;
        }
        if (carry != 0) tweak[0] ^= 0x87;
    }
}
