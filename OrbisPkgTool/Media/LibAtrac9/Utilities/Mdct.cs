using System;
using System.Collections.Generic;

namespace OrbisPkgTool.Media.LibAtrac9.Utilities;

internal class Mdct
{
    private static readonly object TableLock = new object();

    private static int _tableBits = -1;

    private static readonly List<double[]> SinTables = new List<double[]>();

    private static readonly List<double[]> CosTables = new List<double[]>();

    private static readonly List<int[]> ShuffleTables = new List<int[]>();

    private readonly double[] _imdctPrevious;

    private readonly double[] _imdctWindow;

    private readonly double[] _scratchMdct;

    private readonly double[] _scratchDct;

    private int MdctBits { get; }

    private int MdctSize { get; }

    private double Scale { get; }

    public Mdct(int mdctBits, double[] window, double scale = 1.0)
    {
        SetTables(mdctBits);
        MdctBits = mdctBits;
        MdctSize = 1 << mdctBits;
        Scale = scale;
        if (window.Length < MdctSize)
        {
            throw new ArgumentException("Window must be as long as the MDCT size.", "window");
        }
        _imdctPrevious = new double[MdctSize];
        _scratchMdct = new double[MdctSize];
        _scratchDct = new double[MdctSize];
        _imdctWindow = window;
    }

    private static void SetTables(int maxBits)
    {
        lock (TableLock)
        {
            if (maxBits > _tableBits)
            {
                for (int i = _tableBits + 1; i <= maxBits; i++)
                {
                    GenerateTrigTables(i, out var sin, out var cos);
                    SinTables.Add(sin);
                    CosTables.Add(cos);
                    ShuffleTables.Add(GenerateShuffleTable(i));
                }
                _tableBits = maxBits;
            }
        }
    }

    public void RunImdct(double[] input, double[] output)
    {
        if (input.Length < MdctSize)
        {
            throw new ArgumentException("Input must be as long as the MDCT size.", "input");
        }
        if (output.Length < MdctSize)
        {
            throw new ArgumentException("Output must be as long as the MDCT size.", "output");
        }
        int mdctSize = MdctSize;
        int num = mdctSize / 2;
        double[] scratchMdct = _scratchMdct;
        Dct4(input, scratchMdct);
        for (int i = 0; i < num; i++)
        {
            output[i] = _imdctWindow[i] * scratchMdct[i + num] + _imdctPrevious[i];
            output[i + num] = _imdctWindow[i + num] * (0.0 - scratchMdct[mdctSize - 1 - i]) - _imdctPrevious[i + num];
            _imdctPrevious[i] = _imdctWindow[mdctSize - 1 - i] * (0.0 - scratchMdct[num - i - 1]);
            _imdctPrevious[i + num] = _imdctWindow[num - i - 1] * scratchMdct[i];
        }
    }

    private void Dct4(double[] input, double[] output)
    {
        int[] array = ShuffleTables[MdctBits];
        double[] array2 = SinTables[MdctBits];
        double[] array3 = CosTables[MdctBits];
        double[] scratchDct = _scratchDct;
        int mdctSize = MdctSize;
        int num = mdctSize - 1;
        int num2 = mdctSize / 2;
        for (int i = 0; i < num2; i++)
        {
            int num3 = i * 2;
            double num4 = input[num3];
            double num5 = input[num - num3];
            double num6 = array2[i];
            double num7 = array3[i];
            scratchDct[num3] = num4 * num7 + num5 * num6;
            scratchDct[num3 + 1] = num4 * num6 - num5 * num7;
        }
        int num8 = MdctBits - 1;
        for (int j = 0; j < num8; j++)
        {
            int num9 = 1 << j;
            int num10 = num8 - j;
            int num11 = num10 - 1;
            int num12 = 1 << num10;
            int num13 = 1 << num11;
            array2 = SinTables[num11];
            array3 = CosTables[num11];
            for (int k = 0; k < num9; k++)
            {
                for (int l = 0; l < num13; l++)
                {
                    int num14 = (k * num12 + l) * 2;
                    int num15 = num14 + num12;
                    double num16 = scratchDct[num14] - scratchDct[num15];
                    double num17 = scratchDct[num14 + 1] - scratchDct[num15 + 1];
                    double num18 = array2[l];
                    double num19 = array3[l];
                    scratchDct[num14] += scratchDct[num15];
                    scratchDct[num14 + 1] += scratchDct[num15 + 1];
                    scratchDct[num15] = num16 * num19 + num17 * num18;
                    scratchDct[num15 + 1] = num16 * num18 - num17 * num19;
                }
            }
        }
        for (int m = 0; m < MdctSize; m++)
        {
            output[m] = scratchDct[array[m]] * Scale;
        }
    }

    internal static void GenerateTrigTables(int sizeBits, out double[] sin, out double[] cos)
    {
        int num = 1 << sizeBits;
        sin = new double[num];
        cos = new double[num];
        for (int i = 0; i < num; i++)
        {
            double num2 = Math.PI * (double)(4 * i + 1) / (double)(4 * num);
            sin[i] = Math.Sin(num2);
            cos[i] = Math.Cos(num2);
        }
    }

    internal static int[] GenerateShuffleTable(int sizeBits)
    {
        int num = 1 << sizeBits;
        int[] array = new int[num];
        for (int i = 0; i < num; i++)
        {
            array[i] = Bit.BitReverse32(i ^ (i / 2), sizeBits);
        }
        return array;
    }
}
