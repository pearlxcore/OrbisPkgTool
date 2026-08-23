using System;
using System.IO;
using OrbisPkgTool.Media.LibAtrac9.Utilities;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class Unpack
{
    public static void UnpackFrame(BitReader reader, Frame frame)
    {
        Block[] blocks = frame.Blocks;
        foreach (Block block in blocks)
        {
            UnpackBlock(reader, block);
        }
    }

    private static void UnpackBlock(BitReader reader, Block block)
    {
        ReadBlockHeader(reader, block);
        if (block.BlockType == BlockType.LFE)
        {
            UnpackLfeBlock(reader, block);
        }
        else
        {
            UnpackStandardBlock(reader, block);
        }
        reader.AlignPosition(8);
    }

    private static void ReadBlockHeader(BitReader reader, Block block)
    {
        bool flag = block.Frame.FrameIndex == 0;
        block.FirstInSuperframe = !reader.ReadBool();
        block.ReuseBandParams = reader.ReadBool();
        if (block.FirstInSuperframe != flag)
        {
            throw new InvalidDataException();
        }
        if (flag && block.ReuseBandParams && block.BlockType != BlockType.LFE)
        {
            throw new InvalidDataException();
        }
    }

    private static void UnpackStandardBlock(BitReader reader, Block block)
    {
        Channel[] channels = block.Channels;
        if (!block.ReuseBandParams)
        {
            ReadBandParams(reader, block);
        }
        ReadGradientParams(reader, block);
        BitAllocation.CreateGradient(block);
        ReadStereoParams(reader, block);
        ReadExtensionParams(reader, block);
        Channel[] array = channels;
        foreach (Channel channel in array)
        {
            channel.UpdateCodedUnits();
            ScaleFactors.Read(reader, channel);
            BitAllocation.CalculateMask(channel);
            BitAllocation.CalculatePrecisions(channel);
            CalculateSpectrumCodebookIndex(channel);
            ReadSpectra(reader, channel);
            ReadSpectraFine(reader, channel);
        }
        block.QuantizationUnitsPrev = (block.BandExtensionEnabled ? block.ExtensionUnit : block.QuantizationUnitCount);
    }

    private static void ReadBandParams(BitReader reader, Block block)
    {
        int num = Tables.MinBandCount(block.Config.HighSampleRate);
        int num2 = Tables.MaxExtensionBand(block.Config.HighSampleRate);
        block.BandCount = reader.ReadInt(4);
        block.BandCount += num;
        block.QuantizationUnitCount = Tables.BandToQuantUnitCount[block.BandCount];
        if (block.BandCount < num || block.BandCount > Tables.MaxBandCount[block.Config.SampleRateIndex])
        {
            return;
        }
        if (block.BlockType == BlockType.Stereo)
        {
            block.StereoBand = reader.ReadInt(4);
            block.StereoBand += num;
            block.StereoQuantizationUnit = Tables.BandToQuantUnitCount[block.StereoBand];
        }
        else
        {
            block.StereoBand = block.BandCount;
        }
        block.BandExtensionEnabled = reader.ReadBool();
        if (block.BandExtensionEnabled)
        {
            block.ExtensionBand = reader.ReadInt(4);
            block.ExtensionBand += num;
            if (block.ExtensionBand < block.BandCount || block.ExtensionBand > num2)
            {
                throw new InvalidDataException();
            }
            block.ExtensionUnit = Tables.BandToQuantUnitCount[block.ExtensionBand];
        }
        else
        {
            block.ExtensionBand = block.BandCount;
            block.ExtensionUnit = block.QuantizationUnitCount;
        }
    }

    private static void ReadGradientParams(BitReader reader, Block block)
    {
        block.GradientMode = reader.ReadInt(2);
        if (block.GradientMode > 0)
        {
            block.GradientEndUnit = 31;
            block.GradientEndValue = 31;
            block.GradientStartUnit = reader.ReadInt(5);
            block.GradientStartValue = reader.ReadInt(5);
        }
        else
        {
            block.GradientStartUnit = reader.ReadInt(6);
            block.GradientEndUnit = reader.ReadInt(6) + 1;
            block.GradientStartValue = reader.ReadInt(5);
            block.GradientEndValue = reader.ReadInt(5);
        }
        block.GradientBoundary = reader.ReadInt(4);
        if (block.GradientBoundary > block.QuantizationUnitCount)
        {
            throw new InvalidDataException();
        }
        if (block.GradientStartUnit < 1 || block.GradientStartUnit >= 48)
        {
            throw new InvalidDataException();
        }
        if (block.GradientEndUnit < 1 || block.GradientEndUnit >= 48)
        {
            throw new InvalidDataException();
        }
        if (block.GradientStartUnit > block.GradientEndUnit)
        {
            throw new InvalidDataException();
        }
        if (block.GradientStartValue < 0 || block.GradientStartValue >= 32)
        {
            throw new InvalidDataException();
        }
        if (block.GradientEndValue < 0 || block.GradientEndValue >= 32)
        {
            throw new InvalidDataException();
        }
    }

    private static void ReadStereoParams(BitReader reader, Block block)
    {
        if (block.BlockType != BlockType.Stereo)
        {
            return;
        }
        block.PrimaryChannelIndex = reader.ReadInt(1);
        block.HasJointStereoSigns = reader.ReadBool();
        if (block.HasJointStereoSigns)
        {
            for (int i = block.StereoQuantizationUnit; i < block.QuantizationUnitCount; i++)
            {
                block.JointStereoSigns[i] = reader.ReadInt(1);
            }
        }
        else
        {
            Array.Clear(block.JointStereoSigns, 0, block.JointStereoSigns.Length);
        }
    }

    private static void ReadExtensionParams(BitReader reader, Block block)
    {
        int bandCount = 0;
        if (block.BandExtensionEnabled)
        {
            int groupBUnit = 0;
            BandExtension.GetBexBandInfo(out bandCount, out var _, out groupBUnit, block.QuantizationUnitCount);
            if (block.BlockType == BlockType.Stereo)
            {
                ReadHeader(block.Channels[1], reader, bandCount);
            }
            else
            {
                reader.Position++;
            }
        }
        block.HasExtensionData = reader.ReadBool();
        if (!block.HasExtensionData)
        {
            return;
        }
        if (!block.BandExtensionEnabled)
        {
            block.BexMode = reader.ReadInt(2);
            block.BexDataLength = reader.ReadInt(5);
            reader.Position += block.BexDataLength;
            return;
        }
        ReadHeader(block.Channels[0], reader, bandCount);
        block.BexDataLength = reader.ReadInt(5);
        if (block.BexDataLength > 0)
        {
            int num = reader.Position + block.BexDataLength;
            ReadData(block.Channels[0], reader, bandCount);
            if (block.BlockType == BlockType.Stereo)
            {
                ReadData(block.Channels[1], reader, bandCount);
            }
            if (reader.Position > num)
            {
                throw new InvalidDataException();
            }
        }
    }

    private static void ReadHeader(Channel channel, BitReader reader, int bexBand)
    {
        int num = reader.ReadInt(2);
        channel.BexMode = ((bexBand > 2) ? num : 4);
        channel.BexValueCount = BandExtension.BexEncodedValueCounts[channel.BexMode][bexBand];
    }

    private static void ReadData(Channel channel, BitReader reader, int bexBand)
    {
        for (int i = 0; i < channel.BexValueCount; i++)
        {
            int bitCount = BandExtension.BexDataLengths[channel.BexMode][bexBand][i];
            channel.BexValues[i] = reader.ReadInt(bitCount);
        }
    }

    private static void CalculateSpectrumCodebookIndex(Channel channel)
    {
        Array.Clear(channel.CodebookSet, 0, channel.CodebookSet.Length);
        int codedQuantUnits = channel.CodedQuantUnits;
        int[] scaleFactors = channel.ScaleFactors;
        if (codedQuantUnits <= 1 || channel.Config.HighSampleRate)
        {
            return;
        }
        int num = scaleFactors[codedQuantUnits];
        scaleFactors[codedQuantUnits] = scaleFactors[codedQuantUnits - 1];
        int num2 = 0;
        if (codedQuantUnits > 12)
        {
            for (int i = 0; i < 12; i++)
            {
                num2 += scaleFactors[i];
            }
            num2 = (num2 + 6) / 12;
        }
        for (int j = 8; j < codedQuantUnits; j++)
        {
            int num3 = scaleFactors[j - 1];
            int num4 = scaleFactors[j + 1];
            int num5 = Math.Min(num3, num4);
            if (scaleFactors[j] - num5 >= 3 || scaleFactors[j] - num3 + scaleFactors[j] - num4 >= 3)
            {
                channel.CodebookSet[j] = 1;
            }
        }
        for (int k = 12; k < codedQuantUnits; k++)
        {
            if (channel.CodebookSet[k] == 0)
            {
                int num6 = Math.Min(scaleFactors[k - 1], scaleFactors[k + 1]);
                if (scaleFactors[k] - num6 >= 2 && scaleFactors[k] >= num2 - ((Tables.QuantUnitToCoeffCount[k] == 16) ? 1 : 0))
                {
                    channel.CodebookSet[k] = 1;
                }
            }
        }
        scaleFactors[codedQuantUnits] = num;
    }

    private static void ReadSpectra(BitReader reader, Channel channel)
    {
        int[] spectraValuesBuffer = channel.SpectraValuesBuffer;
        Array.Clear(channel.QuantizedSpectra, 0, channel.QuantizedSpectra.Length);
        int num = Tables.MaxHuffPrecision(channel.Config.HighSampleRate);
        for (int i = 0; i < channel.CodedQuantUnits; i++)
        {
            int num2 = Tables.QuantUnitToCoeffCount[i];
            int num3 = channel.Precisions[i] + 1;
            if (num3 <= num)
            {
                HuffmanCodebook huffmanCodebook = Tables.HuffmanSpectrum[channel.CodebookSet[i]][num3][Tables.QuantUnitToCodebookIndex[i]];
                int num4 = num2 >> huffmanCodebook.ValueCountPower;
                for (int j = 0; j < num4; j++)
                {
                    spectraValuesBuffer[j] = ReadHuffmanValue(huffmanCodebook, reader);
                }
                DecodeHuffmanValues(channel.QuantizedSpectra, Tables.QuantUnitToCoeffIndex[i], num2, huffmanCodebook, spectraValuesBuffer);
            }
            else
            {
                for (int k = Tables.QuantUnitToCoeffIndex[i]; k < Tables.QuantUnitToCoeffIndex[i + 1]; k++)
                {
                    channel.QuantizedSpectra[k] = reader.ReadSignedInt(num3);
                }
            }
        }
    }

    private static void ReadSpectraFine(BitReader reader, Channel channel)
    {
        Array.Clear(channel.QuantizedSpectraFine, 0, channel.QuantizedSpectraFine.Length);
        for (int i = 0; i < channel.CodedQuantUnits; i++)
        {
            if (channel.PrecisionsFine[i] > 0)
            {
                int bitCount = channel.PrecisionsFine[i] + 1;
                short num = Tables.QuantUnitToCoeffIndex[i];
                int num2 = Tables.QuantUnitToCoeffIndex[i + 1];
                for (int j = num; j < num2; j++)
                {
                    channel.QuantizedSpectraFine[j] = reader.ReadSignedInt(bitCount);
                }
            }
        }
    }

    private static void DecodeHuffmanValues(int[] spectrum, int index, int bandCount, HuffmanCodebook huff, int[] values)
    {
        int num = bandCount >> huff.ValueCountPower;
        int num2 = (1 << huff.ValueBits) - 1;
        for (int i = 0; i < num; i++)
        {
            int num3 = values[i];
            for (int j = 0; j < huff.ValueCount; j++)
            {
                spectrum[index++] = Bit.SignExtend32(num3 & num2, huff.ValueBits);
                num3 >>= huff.ValueBits;
            }
        }
    }

    public static int ReadHuffmanValue(HuffmanCodebook huff, BitReader reader, bool signed = false)
    {
        int num = reader.PeekInt(huff.MaxBitSize);
        byte b = huff.Lookup[num];
        int num2 = huff.Bits[b];
        reader.Position += num2;
        if (!signed)
        {
            return b;
        }
        return Bit.SignExtend32(b, huff.ValueBits);
    }

    private static void UnpackLfeBlock(BitReader reader, Block block)
    {
        Channel channel = block.Channels[0];
        block.QuantizationUnitCount = 2;
        DecodeLfeScaleFactors(reader, channel);
        CalculateLfePrecision(channel);
        channel.CodedQuantUnits = block.QuantizationUnitCount;
        ReadLfeSpectra(reader, channel);
    }

    private static void DecodeLfeScaleFactors(BitReader reader, Channel channel)
    {
        Array.Clear(channel.ScaleFactors, 0, channel.ScaleFactors.Length);
        for (int i = 0; i < channel.Block.QuantizationUnitCount; i++)
        {
            channel.ScaleFactors[i] = reader.ReadInt(5);
        }
    }

    private static void CalculateLfePrecision(Channel channel)
    {
        Block block = channel.Block;
        int num = (block.ReuseBandParams ? 8 : 4);
        for (int i = 0; i < block.QuantizationUnitCount; i++)
        {
            channel.Precisions[i] = num;
            channel.PrecisionsFine[i] = 0;
        }
    }

    private static void ReadLfeSpectra(BitReader reader, Channel channel)
    {
        Array.Clear(channel.QuantizedSpectra, 0, channel.QuantizedSpectra.Length);
        for (int i = 0; i < channel.CodedQuantUnits; i++)
        {
            if (channel.Precisions[i] > 0)
            {
                int bitCount = channel.Precisions[i] + 1;
                for (int j = Tables.QuantUnitToCoeffIndex[i]; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
                {
                    channel.QuantizedSpectra[j] = reader.ReadSignedInt(bitCount);
                }
            }
        }
    }
}
