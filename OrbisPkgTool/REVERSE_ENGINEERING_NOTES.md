# orbis-pub-cmd.exe — Reverse Engineering Notes

Binary: `PS4_Fake_PKG_Tools_3.87_V7\orbis-pub-cmd.exe` (3,274,784 B, PE32 x86)
Companion: `orbis-pub-prx.dll` (3,835,424 B)

## Binary Profile (from static analysis)

| Attribute | Finding |
|---|---|
| Crypto | **OpenSSL 1.0.2g statically linked** in BOTH the exe and prx dll (libcrypto source paths, `6AES part of OpenSSL 1.0.2g 1 Mar 2016` = AES-XTS module, `MD5 part of OpenSSL 1.0.2g`) |
| Runtime | MSVCR90 / MSVCP90 (MSVC 2008) — native C++ |
| Win imports | KERNEL32, SETUPAPI, WINHTTP, USER32, ADVAPI32, SHELL32, WS2_32 |
| prx dll imports | same + **mscoree.dll** (can host CLR) |
| AES modes present | all OpenSSL EVP ciphers incl. `aes-128-xts`, `aes-128-cbc`, `aes-128-ctr` |

## Commands (confirmed strings)

- `img_create`, `img_file_list`, `img_extract`, `img_verify`, `img_chunk_list`, `pkg_chunk_list`
- `img_file_list` help: "A command to list files/dirs in an image file."
- `img_extract` help: "A command to extract files/dirs from an image file."
- Usage forms: `[options] in_path[:targ_path] out_path`

## Options (confirmed strings)

- `--passcode passcode` — "Specify the passcode. (e.g. GvE6xCpZxd96scOUGuLPbuLp8O800B0s)" → **passcode is 32 ASCII chars (not hex-decoded)**
- `--no_passcode` — "Execute without Passcode."
- `--oformat [short|long]+[recursive|nonrecursive]+[original_size|packed_size]+[layer]+[chunks]` (img_file_list)
- `--oformat long` shows original size (default); `--oformat packed_size` shows packed size
- `--integrity_check [on|off]`, `--format_check [on|off]` (extract)
- `--entitlement_key` — "e.g. 00112233445566778899aabbccddeeff" (16-byte hex)
- `--reserve_for_large_pkg` — CyB1K >100 GB patch

## PKG Internals (confirmed strings)

- `CPkgFile::load(path, passcode, logs, setup_proj_root=...)` — the loader API
- `Entry Count : %d` and `Entry #%03d : id=%04d, type=%d, size=%08x, ofs=%016llx,` → **entry offsets are 64-bit** (CyB1K large-PKG patch); sizes 32-bit
- `entKey() = %02x-%02x-...` — derived entry key dump (8+ bytes)
- `pkgHeadSize() = %08llx.` — header size accessor
- `Sc0/%s` and `Image0/%s` — path prefixes used when listing/extracting
- `Sc0/param.sfo`, `Sc0/playgo-chunk.dat`, `Sc0/license.info`, `Image0`, `sce_sys`, `sce_sys/param.sfo`, `sce_sys/app`
- `ps4_param_json`, `PUBTOOLINFO`, `c_date=...`, `c_time=...`
- `Trophy Pack File Digest:`, `PlayGo Chunk information`
- `input file size must be a multiple of 16(byte).` → block-cipher requirement (16-byte blocks)
- `key is required` / `key is not required` / `key is invalid` — passcode validation
- `[error]`, `[info]`, `[debug]`, `[warn]` — log tag format `[tag] message`
- `Image extract succeeded.`, `Image comparison failed.`
- `**** INVALID ENTRY ****`

## CyB1K Patches (confirmed strings)

- Version banner: `Fake PKG Tools Command Line Version for PS4` + `-Custom PKG Key`
- `pkg1_path` / `pkg0_path` comparison commands retained
- No standard scene RSA keys found in binary → **custom PKG key embedded in raw form** (needs Ghidra disassembly to extract; not DER, not the known scene keys)

## Key Format Conclusions (for the C# port)

1. Passcode = 32 ASCII bytes, used in the standard PS4 PKG key derivation:
   `dk_i = SHA256( SHA256(BE32(i)) || SHA256(content_id padded to 48) || ASCII(passcode) )`
2. ENTRY_KEYS entry (id 0x10): seed digest (32) + 7 key digests (32 each) + 7 RSA-encrypted derived keys (256 each)
3. Per-entry AES: SHA256(entry_meta_32bytes || dk[3]) → IV = hash[0..16], KEY = hash[16..32], AES-128-CBC
4. Image key (id 0x20, at ~0x2800): RSA-decrypt with the embedded custom key → EKPFS
5. PFS image: AES-XTS-256, tweak+data keys from `PfsGenEncKey(EKPFS, seed)` (HMAC-SHA256 derivation, index 1), 0x1000-byte data units
6. Sc0 files = PKG entry table entries; Image0 files = inner PFS filesystem

## Validated Implementation Details (from the C# port + real-PKG testing)

All validated against the real orbis-pub-cmd output on a 7.3 GB fake game PKG
(Digimon World Next Order CUSA05392): listing paths+sizes 100% identical,
extracted files byte-identical (SHA256 match) for Sc0 entries, single-block,
multi-block direct, and doubly-indirect Image0 files.

### PFS inode semantics (from flatz/pkg_pfs_tool + GameArchives, empirically verified)
- On-disk dinode = 0x60-byte top + format-specific block union.
  Signed-32 (sdi32) union = 0x268 → full dinode 0x2C8; PFS_DINODE_STRUCT_SIZE (in-memory) = 0x310.
- Inode fields: mode(u16 @0), link_count(u16 @2), flags(u32 @4), size(u64 @8 = ON-DISK/compressed size),
  size_uncompressed(u64 @0x10), times @0x18.., uid/gid, then block pointers.
- **size @0x08 drives block iteration; size_uncompressed @0x10 is the display size for PFSC files.**
- Block pointers: direct db[0..11]; ib[0] single-indirect; ib[1] doubly-indirect; ib[2]+ deeper.
- **0xFFFFFFFF pointer = contiguous run** (extend from previous pointer) — very common!
- Indirect block entries: 36 bytes (sig 32 + block 4 LE) for signed; 4 bytes for unsigned.
- The inode table is at block 1 (superroot dinode in the header at 0x50 points to it, sdi64).
- Inodes packed contiguously; skip to next block when the remainder is smaller than one inode.

### PFS AES-XTS — the critical detail (LE-first GF multiply)
- Keys: HMAC-SHA256(EKPFS, LE32(1) || seed) → tweakKey = [0..16], dataKey = [16..32].
- Sector = 0x1000; tweak input = LE64(sector) in bytes 0-7 (rest zero); AES-ECB with tweakKey.
- **Per-block tweak advance: bits shift toward byte 15; the carry (from byte 15's old MSB)
  is XORed at byte 0 with 0x87** — the little-endian-first convention used by GameArchives'
  XtsCryptStream (NOT the mbedtls/OpenSSL convention which XORs at byte 15).
  This single detail broke every decryption after the first 16 bytes of a sector.
- Encryption starts at sector 16 (block 1 = inode table); the header block is plaintext.

### PFSC (compressed inner PFS image)
- pfs_image.dat = PFSC image: header (0x30 bytes: magic "PFSC", block_size 0x10000,
  block_table_offset, block_data_offset, rounded_file_size), then a u64 block-offset table.
- Block i data at [offsets[i], offsets[i+1]); if size == block_size it's stored raw,
  otherwise zlib (window bits 12 — decodable by .NET ZLibStream).
- Decompressed length = rounded_file_size; the inner PFS inside is a normal (mode 0x8) PFS.

### Sc0 entry decryption (validated byte-identical)
- Per-entry: iv_key = SHA256(raw 32-byte entry || dk_keyIndex); IV = [0..16], KEY = [16..32];
  AES-128-CBC, truncate to DataSize. dk_i = SHA256(SHA256(BE32(i)) || SHA256(cid||pad48) || passcode).
- ENTRY_KEYS[3] RSA-decrypt (with the leaked dk3 private key) equals the passcode-derived dk3.
- EKPFS: AES-CBC decrypt IMAGE_KEY with (image_key meta || dk3), then RSA-2048 with the fake keyset.

## Validated on the user's 1023-PKG collection (I:\PKG, 3.7 TB)

- 150-PKG sweep (games/patches/DLC/themes, official + fake): **0 failures** —
  every PKG opens, param.sfo parses, type detected.
- Add-on type detection (DLC vs Theme): param.sfo CATEGORY ("ac"/"th"/"av"/"wa"),
  plus content-id prefix **IP9100 = official theme** and "Theme" in the title
  (scene themes like "FIX Permanent Theme" / "Warframe Theme" reuse DLC content ids).
- Official PKG Sc0 decryption WITHOUT the passcode: RSA-recover dk3 from
  ENTRY_KEYS[3] with the leaked key-3 private key (validated on the Disgaea
  official patch — npbind.dat decrypts to a structurally valid binding file).
- Digest-table integrity check semantics: **encrypted entries are hashed over
  the 16-byte-aligned ciphertext; plaintext entries over exactly DataSize**
  (a subtle 12-byte difference that broke naive verification on npbind.dat).
- Listing on a 35.7 GB game (A Plague Tale, Unicode path): 298 entries, eboot.bin
  extracts to a valid SELF (magic 1D3D154F) — orbis-pub-cmd fails on the same
  path ("Could not open or read image file") because it mangles Unicode.

## Follow-up RE Tasks (need Ghidra/x64dbg)

- [ ] Extract the embedded custom RSA key — static scans (BE/LE byte patterns,
      DER, PKCS#8, hex-string keys, n=p·q struct self-consistency) found no key
      other than the standard scene keys; the entire 1023-PKG corpus decrypts
      with the standard keys, so a custom-key FPKG would be needed to confirm
      whether one exists in this build at all
- [ ] Confirm per-entry cipher mode (CBC vs CTR) by tracing `EVP_DecryptInit_ex` calls in img_extract
- [ ] Confirm the exact img_file_list stdout format string (probably `%c %s` variants near 0x26DABC)
- [ ] Map `entKey()` call sites to confirm which derived key index is used for which entry
