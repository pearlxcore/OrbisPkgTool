using System;
using System.IO;
using OrbisPkgTool.Media.LibAtrac9.Utilities;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class ScaleFactors
{
    public static readonly byte[][] ScaleFactorWeights = new byte[8][]
    {
        new byte[32]
        {
            0, 0, 0, 1, 1, 2, 2, 2, 2, 2,
            2, 3, 2, 3, 3, 4, 4, 4, 4, 4,
            4, 5, 5, 6, 6, 7, 7, 8, 10, 12,
            12, 12
        },
        new byte[32]
        {
            3, 2, 2, 1, 1, 1, 1, 1, 0, 1,
            1, 1, 0, 0, 0, 1, 0, 1, 1, 1,
            1, 1, 1, 2, 3, 3, 4, 5, 7, 10,
            10, 10
        },
        new byte[32]
        {
            0, 2, 4, 5, 6, 6, 6, 6, 6, 6,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            6, 6, 6, 7, 7, 7, 7, 8, 9, 12,
            12, 12
        },
        new byte[32]
        {
            0, 1, 1, 2, 2, 2, 3, 3, 3, 3,
            3, 4, 4, 4, 5, 5, 5, 6, 6, 6,
            6, 7, 8, 8, 10, 11, 11, 12, 13, 13,
            13, 13
        },
        new byte[32]
        {
            0, 2, 2, 3, 3, 4, 4, 5, 4, 5,
            5, 5, 5, 6, 7, 8, 8, 8, 8, 9,
            9, 9, 10, 10, 11, 12, 12, 13, 13, 14,
            14, 14
        },
        new byte[32]
        {
            1, 1, 0, 0, 0, 0, 1, 0, 0, 1,
            1, 1, 1, 1, 2, 2, 2, 2, 2, 3,
            3, 3, 4, 4, 5, 6, 7, 7, 9, 11,
            11, 11
        },
        new byte[32]
        {
            0, 5, 8, 10, 11, 11, 12, 12, 12, 13,
            13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
            13, 13, 13, 13, 12, 12, 12, 12, 13, 15,
            15, 15
        },
        new byte[32]
        {
            0, 2, 3, 4, 5, 6, 6, 7, 7, 8,
            8, 8, 9, 9, 10, 10, 10, 11, 11, 11,
            11, 11, 11, 12, 12, 12, 12, 13, 13, 15,
            15, 15
        }
    };

    public static void Read(BitReader reader, Channel channel)
    {
        Array.Clear(channel.ScaleFactors, 0, channel.ScaleFactors.Length);
        channel.ScaleFactorCodingMode = reader.ReadInt(2);
        if (channel.ChannelIndex == 0)
        {
            switch (channel.ScaleFactorCodingMode)
            {
            case 0:
                ReadVlcDeltaOffset(reader, channel);
                break;
            case 1:
                ReadClcOffset(reader, channel);
                break;
            case 2:
                if (channel.Block.FirstInSuperframe)
                {
                    throw new InvalidDataException();
                }
                ReadVlcDistanceToBaseline(reader, channel, channel.ScaleFactorsPrev, channel.Block.QuantizationUnitsPrev);
                break;
            case 3:
                if (channel.Block.FirstInSuperframe)
                {
                    throw new InvalidDataException();
                }
                ReadVlcDeltaOffsetWithBaseline(reader, channel, channel.ScaleFactorsPrev, channel.Block.QuantizationUnitsPrev);
                break;
            }
        }
        else
        {
            switch (channel.ScaleFactorCodingMode)
            {
            case 0:
                ReadVlcDeltaOffset(reader, channel);
                break;
            case 1:
                ReadVlcDistanceToBaseline(reader, channel, channel.Block.Channels[0].ScaleFactors, channel.Block.ExtensionUnit);
                break;
            case 2:
                ReadVlcDeltaOffsetWithBaseline(reader, channel, channel.Block.Channels[0].ScaleFactors, channel.Block.ExtensionUnit);
                break;
            case 3:
                if (channel.Block.FirstInSuperframe)
                {
                    throw new InvalidDataException();
                }
                ReadVlcDistanceToBaseline(reader, channel, channel.ScaleFactorsPrev, channel.Block.QuantizationUnitsPrev);
                break;
            }
        }
        for (int i = 0; i < channel.Block.ExtensionUnit; i++)
        {
            if (channel.ScaleFactors[i] < 0 || channel.ScaleFactors[i] > 31)
            {
                throw new InvalidDataException("Scale factor values are out of range.");
            }
        }
        Array.Copy(channel.ScaleFactors, channel.ScaleFactorsPrev, channel.ScaleFactors.Length);
    }

    private static void ReadClcOffset(BitReader reader, Channel channel)
    {
        int[] scaleFactors = channel.ScaleFactors;
        int num = reader.ReadInt(2) + 2;
        int num2 = ((num < 5) ? reader.ReadInt(5) : 0);
        for (int i = 0; i < channel.Block.ExtensionUnit; i++)
        {
            scaleFactors[i] = reader.ReadInt(num) + num2;
        }
    }

    private static void ReadVlcDeltaOffset(BitReader reader, Channel channel)
    {
        int num = reader.ReadInt(3);
        byte[] array = ScaleFactorWeights[num];
        int[] scaleFactors = channel.ScaleFactors;
        int num2 = reader.ReadInt(5);
        int num3 = reader.ReadInt(2) + 3;
        HuffmanCodebook huffmanCodebook = Tables.HuffmanScaleFactorsUnsigned[num3];
        scaleFactors[0] = reader.ReadInt(num3);
        for (int i = 1; i < channel.Block.ExtensionUnit; i++)
        {
            int num4 = Unpack.ReadHuffmanValue(huffmanCodebook, reader);
            scaleFactors[i] = (scaleFactors[i - 1] + num4) & (huffmanCodebook.ValueMax - 1);
        }
        for (int j = 0; j < channel.Block.ExtensionUnit; j++)
        {
            scaleFactors[j] += num2 - array[j];
        }
    }

    private static void ReadVlcDistanceToBaseline(BitReader reader, Channel channel, int[] baseline, int baselineLength)
    {
        int[] scaleFactors = channel.ScaleFactors;
        int num = reader.ReadInt(2) + 2;
        HuffmanCodebook huff = Tables.HuffmanScaleFactorsSigned[num];
        int num2 = Math.Min(channel.Block.ExtensionUnit, baselineLength);
        for (int i = 0; i < num2; i++)
        {
            int num3 = Unpack.ReadHuffmanValue(huff, reader, signed: true);
            scaleFactors[i] = (baseline[i] + num3) & 0x1F;
        }
        for (int j = num2; j < channel.Block.ExtensionUnit; j++)
        {
            scaleFactors[j] = reader.ReadInt(5);
        }
    }

    private static void ReadVlcDeltaOffsetWithBaseline(BitReader reader, Channel channel, int[] baseline, int baselineLength)
    {
        int[] scaleFactors = channel.ScaleFactors;
        int num = reader.ReadOffsetBinary(5, BitReader.OffsetBias.Negative);
        int num2 = reader.ReadInt(2) + 1;
        HuffmanCodebook huffmanCodebook = Tables.HuffmanScaleFactorsUnsigned[num2];
        int num3 = Math.Min(channel.Block.ExtensionUnit, baselineLength);
        scaleFactors[0] = reader.ReadInt(num2);
        for (int i = 1; i < num3; i++)
        {
            int num4 = Unpack.ReadHuffmanValue(huffmanCodebook, reader);
            scaleFactors[i] = (scaleFactors[i - 1] + num4) & (huffmanCodebook.ValueMax - 1);
        }
        for (int j = 0; j < num3; j++)
        {
            scaleFactors[j] += num + baseline[j];
        }
        for (int k = num3; k < channel.Block.ExtensionUnit; k++)
        {
            scaleFactors[k] = reader.ReadInt(5);
        }
    }
}
