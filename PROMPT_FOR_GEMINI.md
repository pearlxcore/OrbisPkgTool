I'm building a PS4 fake PKG tool in C# (.NET 10). My tool produces valid PKGs that are accepted by orbis-pub-cmd for SMALL PKGs (few data blocks, no indirect blocks needed), but FAILS for LARGE PKGs (>12 data blocks requiring indirect block pointers).

## Symptom

orbis-pub-cmd can list Sc0 files but shows `Image0` with NO children when our PFS has >12 data blocks. The outer PFS inode for `pfs_image.dat` correctly has `ib=[5,6,0...]` pointing to indirect blocks, but orbis can't read data through them.

Small PKGs (≤12 data blocks, no indirect blocks) work perfectly — orbis lists all Image0 files.

## Architecture

The outer PFS is XTS-encrypted with mode=0xD (signed+encrypted). It contains 4 inodes: superroot, flat_path_table, uroot, and pfs_image.dat (the file inode). The pfs_image.dat inode uses S32 format (0x2C8 bytes) with up to 12 direct db entries and 5 indirect ib entries. Each entry has a 32-byte HMAC signature followed by a 4-byte block number.

## Current Block Layout (BuildOuterPfs)

```
Block 0: PFS header
Block 1: inode table (4 × 0x2C8 = 0xB20 bytes)
Block 2: superroot dirents
Block 3: flat path table (FPT)
Block 4: empty (plaintext, not encrypted)
Block 5: ib0 — singly-indirect block (if dataBlocks > 12)
Block 6: ib1 — doubly-indirect block (if dataBlocks > 12+1820)
Block 7..: additional indirect blocks
Block N: uroot dirents (empty)
Block N+1..: data blocks
```

## The Code (BuildOuterPfs)

```csharp
long dataBlocks = CeilDiv(fileData.Length, (int)BlockSize);
long indirect1 = dataBlocks > 12 ? 1 : 0;
long indirect2 = dataBlocks > 12 + 1820 ? 1 : 0;
long nIndirect = dataBlocks > 12 + 1820 ? CeilDiv(dataBlocks - 12 - 1820, 1820L) : 0;
long urootBlock = 5 + indirect1 + indirect2 + nIndirect;
long dataStart = urootBlock + 1;
long ndblock = dataStart + dataBlocks;
long emptyBlock = 4;

// Inode 3 (pfs_image.dat) — note indirect1/indirect2 block numbers
WriteS32Inode(w, inode3, 0x816D, 1, 0x0000000D, fileData.Length, uncompressed,
    dataBlocks, dataStart,
    indirect1: indirect1 > 0 ? 5 : 0, indirect2: indirect2 > 0 ? 6 : 0, fileTime: fileTime);

// Write indirect blocks
long dataPos = 0;
if (indirect1 > 0)
{
    WriteIndirectS32(w, 5 * BlockSize, 12, dataStart, ref dataPos, dataBlocks);
    if (indirect2 > 0)
    {
        long ibBlock = 7;
        var remaining = dataBlocks - 12 - 1820;
        while (remaining > 0)
        {
            WriteS32Pointer(w, 6 * BlockSize, ibBlock);
            WriteIndirectS32(w, ibBlock * BlockSize, 12 + 1820, dataStart, ref dataPos, dataBlocks);
            ibBlock++;
            remaining -= 1820;
        }
    }
}

// Signing: sign direct blocks
for (int i = 0; i < Math.Min(dataBlocks, 12); i++)
    WriteBlockSig(w, inode3 + 0x64 + 36 * i, signKey, image, (dataStart + i) * BlockSize);

// Sign indirect block entries
if (indirect1 > 0)
{
    SignIndirectEntries(w, 5 * BlockSize, signKey, image, dataStart, 12, dataBlocks);
    if (indirect2 > 0)
    {
        long ibBlock = 7;
        while (remaining > 0) { ... }
        // Sign the indirect blocks' own entries in the inode's ib slots
        WriteBlockSig(w, inode3 + 0x64 + 12 * 36, signKey, image, 5 * BlockSize);
        WriteBlockSig(w, inode3 + 0x64 + 13 * 36, signKey, image, 6 * BlockSize);
    }
}
```

## WriteS32Inode

```csharp
static void WriteS32Inode(..., long blocks, long db0,
    long indirect1 = 0, long indirect2 = 0, ...)
{
    // ...header fields...
    WriteLe(w, (uint)blocks);  // at offset 0x60
    // 12 direct db entries: each is 32 sig + 4 block
    for (int i = 0; i < 12; i++)
    {
        w.Write(new byte[32]);           // sig placeholder (zero)
        WriteLe(w, i < blocks ? (int)(db0 + i) : 0);
    }
    // 5 indirect block entries: each is 32 sig + 4 block
    for (int i = 0; i < 5; i++)
    {
        w.Write(new byte[32]);
        long ib = i == 0 ? indirect1 : i == 1 ? indirect2 : 0;
        WriteLe(w, (int)ib);
    }
}
```

## WriteIndirectS32

```csharp
static void WriteIndirectS32(BinaryWriter w, long blockOffset, long firstDataIndex,
    long dataStart, ref long dataPos, long totalBlocks)
{
    w.BaseStream.Position = blockOffset;
    for (int i = 0; i < 1820; i++)
    {
        long dataIdx = firstDataIndex + dataPos;
        long blk = dataIdx < totalBlocks ? dataStart + dataIdx : 0;
        w.Write(new byte[32]);  // sig placeholder
        WriteLe(w, (int)blk);
        dataPos++;
    }
}
```

## The Question

For a PKG with ~7759 data blocks (dataBlocks=7759), the inode correctly gets `ib=[5,6,0,0,0]`. Block 5 (ib0) should contain 1820 signed pointers to data blocks 12-1831. Block 6 (ib1) should point to more indirect blocks (7, 8, ...) that each contain 1820 pointers.

orbis-pub-cmd's signverify shows:
- Header signature: OK
- ino[0] db[0] (block 2): SIG FAIL (but our tool's verify passes)
- ino[3] db[0] stored sig: ALL ZEROS

What's wrong with the indirect block writing/signing? Is the block layout correct? Are the indirect block entry indices computed correctly? Are the signatures being written to the right inode offsets?
