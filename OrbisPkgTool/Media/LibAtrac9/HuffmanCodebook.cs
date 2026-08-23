using System;
using OrbisPkgTool.Media.LibAtrac9.Utilities;

namespace OrbisPkgTool.Media.LibAtrac9;

internal class HuffmanCodebook
{
    public short[] Codes { get; }

    public byte[] Bits { get; }

    public byte[] Lookup { get; }

    public int ValueCount { get; }

    public int ValueCountPower { get; }

    public int ValueBits { get; }

    public int ValueMax { get; }

    public int MaxBitSize { get; }

    public HuffmanCodebook(short[] codes, byte[] bits, byte valueCountPower)
    {
        Codes = codes;
        Bits = bits;
        if (Codes != null && Bits != null)
        {
            ValueCount = 1 << (int)valueCountPower;
            ValueCountPower = valueCountPower;
            ValueBits = Helpers.Log2(codes.Length) >> (int)valueCountPower;
            ValueMax = 1 << ValueBits;
            int val = 0;
            foreach (byte val2 in bits)
            {
                val = Math.Max(val, val2);
            }
            MaxBitSize = val;
            Lookup = CreateLookupTable();
        }
    }

    private byte[] CreateLookupTable()
    {
        if (Codes == null || Bits == null)
        {
            return null;
        }
        byte[] array = new byte[1 << MaxBitSize];
        for (int i = 0; i < Bits.Length; i++)
        {
            if (Bits[i] != 0)
            {
                int num = MaxBitSize - Bits[i];
                int num2 = Codes[i] << num;
                int num3 = 1 << num;
                int num4 = num2 + num3;
                for (int j = num2; j < num4; j++)
                {
                    array[j] = (byte)i;
                }
            }
        }
        return array;
    }
}
