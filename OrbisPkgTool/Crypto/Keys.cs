namespace OrbisPkgTool.Crypto;

/// <summary>
/// Fixed key constants for PKG building (keystone HMAC keys, etc.).
/// These are the public scene constants used by every PS4 package tool.
/// </summary>
public static class Keys
{
    /// <summary>Used to hash the first SHA256-HMAC in keystone.</summary>
    public static readonly byte[] KeystoneHmacKey = Hex(
        "C74405F67424BA342BC1276251BBC2F555F16025B6A1B6714780DBAEC852FA2F");

    /// <summary>Used to hash the second SHA256-HMAC in keystone.</summary>
    public static readonly byte[] KeystoneMacData = Hex(
        "783D6F3AE91C0E0712FCAAB7950BDE06855CF7A22DCDBDE127E9BFCBAD0FF0FE");

    /// <summary>PFS seed used by the fake-package builder (arbitrary 16 bytes).</summary>
    public static readonly byte[] FakeKeySeed = Hex(
        "EDEA573E7AD2F6F59DB26D4A76798704");

    private static byte[] Hex(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }
}
