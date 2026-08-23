namespace OrbisPkgTool.Media.LibAtrac9.Utilities;

internal static class Bit
{
    private static uint BitReverse32(uint value)
    {
        value = ((value & 0xAAAAAAAAu) >> 1) | ((value & 0x55555555) << 1);
        value = ((value & 0xCCCCCCCCu) >> 2) | ((value & 0x33333333) << 2);
        value = ((value & 0xF0F0F0F0u) >> 4) | ((value & 0xF0F0F0F) << 4);
        value = ((value & 0xFF00FF00u) >> 8) | ((value & 0xFF00FF) << 8);
        return (value >> 16) | (value << 16);
    }

    private static uint BitReverse32(uint value, int bitCount)
    {
        return BitReverse32(value) >> 32 - bitCount;
    }

    public static int BitReverse32(int value, int bitCount)
    {
        return (int)BitReverse32((uint)value, bitCount);
    }

    public static int SignExtend32(int value, int bits)
    {
        int num = 32 - bits;
        return value << num >> num;
    }
}
