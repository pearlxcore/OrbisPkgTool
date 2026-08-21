using System;

namespace OrbisPkgTool.Media.LibAtrac9.Utilities;

internal class BitReader
{
    public enum OffsetBias
    {
        Negative
    }

    private byte[] Buffer { get; set; }

    private int LengthBits { get; set; }

    public int Position { get; set; }

    private int Remaining => LengthBits - Position;

    public BitReader(byte[] buffer)
    {
        SetBuffer(buffer);
    }

    public void SetBuffer(byte[] buffer)
    {
        Buffer = buffer;
        byte[] buffer2 = Buffer;
        LengthBits = ((buffer2 != null) ? (buffer2.Length * 8) : 0);
        Position = 0;
    }

    public int ReadInt(int bitCount)
    {
        int result = PeekInt(bitCount);
        Position += bitCount;
        return result;
    }

    public int ReadSignedInt(int bitCount)
    {
        int value = PeekInt(bitCount);
        Position += bitCount;
        return Bit.SignExtend32(value, bitCount);
    }

    public bool ReadBool()
    {
        return ReadInt(1) == 1;
    }

    public int ReadOffsetBinary(int bitCount, OffsetBias bias)
    {
        int num = (int)((1 << bitCount - 1) - bias);
        int result = PeekInt(bitCount) - num;
        Position += bitCount;
        return result;
    }

    public void AlignPosition(int multiple)
    {
        Position = Helpers.GetNextMultiple(Position, multiple);
    }

    public int PeekInt(int bitCount)
    {
        if (bitCount > Remaining)
        {
            if (Position >= LengthBits)
            {
                return 0;
            }
            int num = bitCount - Remaining;
            return PeekIntFallback(Remaining) << num;
        }
        int num2 = Position / 8;
        int num3 = Position % 8;
        if (bitCount <= 9 && Remaining >= 16)
        {
            return (((Buffer[num2] << 8) | Buffer[num2 + 1]) & (65535 >> num3)) >> 16 - bitCount - num3;
        }
        if (bitCount <= 17 && Remaining >= 24)
        {
            return (((Buffer[num2] << 16) | (Buffer[num2 + 1] << 8) | Buffer[num2 + 2]) & (16777215 >> num3)) >> 24 - bitCount - num3;
        }
        if (bitCount <= 25 && Remaining >= 32)
        {
            uint value = (uint)((Buffer[num2] << 24) | (Buffer[num2 + 1] << 16) | (Buffer[num2 + 2] << 8) | Buffer[num2 + 3]);
            return (int)((value & (0xFFFFFFFFu >> num3)) >> 32 - bitCount - num3);
        }
        return PeekIntFallback(bitCount);
    }

    private int PeekIntFallback(int bitCount)
    {
        int num = 0;
        int num2 = Position / 8;
        int num3 = Position % 8;
        while (bitCount > 0)
        {
            if (num3 >= 8)
            {
                num3 = 0;
                num2++;
            }
            int num4 = Math.Min(bitCount, 8 - num3);
            int num5 = ((255 >> num3) & Buffer[num2]) >> 8 - num3 - num4;
            num = (num << num4) | num5;
            num3 += num4;
            bitCount -= num4;
        }
        return num;
    }
}
