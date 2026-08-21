using System;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class Tables
{
    public static readonly int[] SampleRates = new int[16]
    {
        11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 44100, 48000,
        64000, 88200, 96000, 128000, 176400, 192000
    };

    public static readonly byte[] SamplingRateIndexToFrameSamplesPower = new byte[16]
    {
        6, 6, 7, 7, 7, 8, 8, 8, 6, 6,
        7, 7, 7, 8, 8, 8
    };

    public static readonly byte[] MaxBandCount = new byte[16]
    {
        8, 8, 12, 12, 12, 18, 18, 18, 8, 8,
        12, 12, 12, 16, 16, 16
    };

    public static readonly byte[] BandToQuantUnitCount = new byte[19]
    {
        0, 4, 8, 10, 12, 13, 14, 15, 16, 18,
        20, 21, 22, 23, 24, 25, 26, 28, 30
    };

    public static readonly byte[] QuantUnitToCoeffCount = new byte[30]
    {
        2, 2, 2, 2, 2, 2, 2, 2, 4, 4,
        4, 4, 8, 8, 8, 8, 8, 8, 8, 8,
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16
    };

    public static readonly short[] QuantUnitToCoeffIndex = new short[31]
    {
        0, 2, 4, 6, 8, 10, 12, 14, 16, 20,
        24, 28, 32, 40, 48, 56, 64, 72, 80, 88,
        96, 112, 128, 144, 160, 176, 192, 208, 224, 240,
        256
    };

    public static readonly byte[] QuantUnitToCodebookIndex = new byte[30]
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
        1, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3
    };

    public static readonly ChannelConfig[] ChannelConfig = new ChannelConfig[6]
    {
        new ChannelConfig(default(BlockType)),
        new ChannelConfig(default(BlockType), default(BlockType)),
        new ChannelConfig(BlockType.Stereo),
        new ChannelConfig(BlockType.Stereo, BlockType.Mono, BlockType.LFE, BlockType.Stereo),
        new ChannelConfig(BlockType.Stereo, BlockType.Mono, BlockType.LFE, BlockType.Stereo, BlockType.Stereo),
        new ChannelConfig(BlockType.Stereo, BlockType.Stereo)
    };

    public static readonly HuffmanCodebook[] HuffmanScaleFactorsUnsigned = HuffmanCodebooks.GenerateHuffmanCodebooks(HuffmanCodebooks.HuffmanScaleFactorsACodes, HuffmanCodebooks.HuffmanScaleFactorsABits, HuffmanCodebooks.HuffmanScaleFactorsGroupSizes);

    public static readonly HuffmanCodebook[] HuffmanScaleFactorsSigned = HuffmanCodebooks.GenerateHuffmanCodebooks(HuffmanCodebooks.HuffmanScaleFactorsBCodes, HuffmanCodebooks.HuffmanScaleFactorsBBits, HuffmanCodebooks.HuffmanScaleFactorsGroupSizes);

    public static readonly HuffmanCodebook[][][] HuffmanSpectrum = new HuffmanCodebook[2][][]
    {
        HuffmanCodebooks.GenerateHuffmanCodebooks(HuffmanCodebooks.HuffmanSpectrumACodes, HuffmanCodebooks.HuffmanSpectrumABits, HuffmanCodebooks.HuffmanSpectrumAGroupSizes),
        HuffmanCodebooks.GenerateHuffmanCodebooks(HuffmanCodebooks.HuffmanSpectrumBCodes, HuffmanCodebooks.HuffmanSpectrumBBits, HuffmanCodebooks.HuffmanSpectrumBGroupSizes)
    };

    public static readonly double[][] ImdctWindow = new double[3][]
    {
        GenerateImdctWindow(6),
        GenerateImdctWindow(7),
        GenerateImdctWindow(8)
    };

    public static readonly double[] SpectrumScale = Generate(32, SpectrumScaleFunction);

    public static readonly double[] QuantizerStepSize = Generate(16, QuantizerStepSizeFunction);

    public static readonly double[] QuantizerFineStepSize = Generate(16, QuantizerFineStepSizeFunction);

    public static readonly byte[][] GradientCurves = BitAllocation.GenerateGradientCurves();

    public static int MaxHuffPrecision(bool highSampleRate)
    {
        if (!highSampleRate)
        {
            return 7;
        }
        return 1;
    }

    public static int MinBandCount(bool highSampleRate)
    {
        if (!highSampleRate)
        {
            return 3;
        }
        return 1;
    }

    public static int MaxExtensionBand(bool highSampleRate)
    {
        if (!highSampleRate)
        {
            return 18;
        }
        return 16;
    }

    private static double QuantizerStepSizeFunction(int x)
    {
        return 2.0 / (double)((1 << x + 1) - 1);
    }

    private static double QuantizerFineStepSizeFunction(int x)
    {
        return QuantizerStepSizeFunction(x) / 65535.0;
    }

    private static double SpectrumScaleFunction(int x)
    {
        return Math.Pow(2.0, x - 15);
    }

    private static double[] GenerateImdctWindow(int frameSizePower)
    {
        int num = 1 << frameSizePower;
        double[] array = new double[num];
        double[] array2 = GenerateMdctWindow(frameSizePower);
        for (int i = 0; i < num; i++)
        {
            array[i] = array2[i] / (array2[num - 1 - i] * array2[num - 1 - i] + array2[i] * array2[i]);
        }
        return array;
    }

    private static double[] GenerateMdctWindow(int frameSizePower)
    {
        int num = 1 << frameSizePower;
        double[] array = new double[num];
        for (int i = 0; i < num; i++)
        {
            array[i] = (Math.Sin((((double)i + 0.5) / (double)num - 0.5) * Math.PI) + 1.0) * 0.5;
        }
        return array;
    }

    private static T[] Generate<T>(int count, Func<int, T> elementGenerator)
    {
        T[] array = new T[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = elementGenerator(i);
        }
        return array;
    }
}
