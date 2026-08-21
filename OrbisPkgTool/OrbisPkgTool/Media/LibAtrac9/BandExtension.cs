using System;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class BandExtension
{
    public static readonly byte[][] BexGroupInfo = new byte[8][]
    {
        new byte[3] { 16, 21, 0 },
        new byte[3] { 18, 22, 1 },
        new byte[3] { 20, 22, 2 },
        new byte[3] { 21, 22, 3 },
        new byte[3] { 21, 22, 3 },
        new byte[3] { 23, 24, 4 },
        new byte[3] { 23, 24, 4 },
        new byte[3] { 24, 24, 5 }
    };

    public static readonly byte[][] BexEncodedValueCounts = new byte[5][]
    {
        new byte[6] { 0, 0, 0, 4, 4, 2 },
        new byte[6],
        new byte[6] { 0, 0, 0, 2, 2, 1 },
        new byte[6] { 0, 0, 0, 2, 2, 2 },
        new byte[6] { 1, 1, 1, 0, 0, 0 }
    };

    public static readonly byte[][][] BexDataLengths = new byte[5][][]
    {
        new byte[6][]
        {
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4] { 5, 4, 3, 3 },
            new byte[4] { 4, 4, 3, 4 },
            new byte[4] { 4, 5, 0, 0 }
        },
        new byte[6][]
        {
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4]
        },
        new byte[6][]
        {
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4] { 6, 6, 0, 0 },
            new byte[4] { 6, 6, 0, 0 },
            new byte[4] { 6, 0, 0, 0 }
        },
        new byte[6][]
        {
            new byte[4],
            new byte[4],
            new byte[4],
            new byte[4] { 4, 4, 0, 0 },
            new byte[4] { 4, 4, 0, 0 },
            new byte[4] { 4, 4, 0, 0 }
        },
        new byte[6][]
        {
            new byte[4] { 3, 0, 0, 0 },
            new byte[4] { 3, 0, 0, 0 },
            new byte[4] { 3, 0, 0, 0 },
            new byte[4],
            new byte[4],
            new byte[4]
        }
    };

    public static readonly double[][] BexMode0Bands3 = new double[5][]
    {
        new double[32]
        {
            0.0, 0.198822, 0.2514343, 0.296051, 0.326355, 0.3771362, 0.3786926, 0.4540405, 0.4877625, 0.5262451,
            0.5447083, 0.5737, 0.6212158, 0.6222839, 0.6560974, 0.6896667, 0.7555542, 0.7677917, 0.7918091, 0.7971497,
            0.8188171, 0.8446045, 0.9790649, 0.9822083, 0.9846191, 0.9859314, 0.9863586, 0.9863892, 0.9873352, 0.9881287,
            0.9898682, 0.991333
        },
        new double[32]
        {
            0.0, 0.998291, 0.07592773, 0.7179565, 0.9851379, 0.5340271, 0.9013672, 0.6349182, 0.7226257, 0.1948547,
            0.7628174, 0.9873657, 0.8112183, 0.2715454, 0.9734192, 0.1443787, 0.4640198, 0.3249207, 0.3790894, 0.08276367,
            0.595459, 0.286438, 0.9806824, 0.7929077, 0.6292114, 0.4887085, 0.2905273, 0.130188, 0.3140869, 0.5482483,
            0.4210815, 0.1182861
        },
        new double[16]
        {
            0.0, 0.03155518, 0.08581543, 0.1364746, 0.1858826, 0.2368469, 0.2888184, 0.3432617, 0.4012451, 0.4623108,
            0.5271301, 0.5954895, 0.6681213, 0.7448425, 0.8245239, 0.909729
        },
        new double[8] { 0.0, 0.04418945, 0.1303711, 0.227356, 0.3395996, 0.4735718, 0.626709, 0.8003845 },
        new double[8] { 0.0, 0.02804565, 0.09683228, 0.1849976, 0.3005981, 0.447052, 0.6168518, 0.8007813 }
    };

    public static readonly double[][] BexMode0Bands4 = new double[5][]
    {
        new double[16]
        {
            0.0, 0.270874, 0.3479614, 0.3578186, 0.5083618, 0.5299072, 0.5819092, 0.6381836, 0.7276917, 0.759552,
            0.7878723, 0.9707336, 0.9713135, 0.9736023, 0.9759827, 0.9832458
        },
        new double[16]
        {
            0.0, 0.2330627, 0.5891418, 0.717041, 0.2036438, 0.1613464, 0.6668701, 0.9481201, 0.9769897, 0.5111694,
            0.3522644, 0.8209534, 0.293396, 0.975769, 0.5289917, 0.4372253
        },
        new double[16]
        {
            0.0, 0.04360962, 0.1056519, 0.1590576, 0.2078857, 0.2572937, 0.3082581, 0.3616028, 0.4191589, 0.4792175,
            0.5438538, 0.6125183, 0.6841125, 0.7589417, 0.8365173, 0.9148254
        },
        new double[8] { 0.0, 0.04074097, 0.1164551, 0.2077026, 0.3184509, 0.4532166, 0.6124268, 0.7932129 },
        new double[16]
        {
            0.0, 0.008880615, 0.02932739, 0.05593872, 0.08825684, 0.1259155, 0.1721497, 0.2270813, 0.2901611, 0.3579712,
            0.4334106, 0.5147095, 0.6023254, 0.6956177, 0.7952881, 0.8977356
        }
    };

    public static readonly double[][] BexMode0Bands5 = new double[3][]
    {
        new double[16]
        {
            0.0,
            0.0737915,
            0.1806335,
            0.2687073,
            0.3407898,
            0.4047546,
            0.4621887,
            0.5168762,
            73.0 / 128.0,
            0.6237488,
            0.6763611,
            0.7288208,
            0.7808533,
            0.8337708,
            0.8874512,
            0.941803
        },
        new double[32]
        {
            0.0, 0.07980347, 0.1615295, 0.1665649, 0.1822205, 0.2185669, 0.2292175, 0.2456665, 0.2666321, 0.330658,
            0.3330688, 0.3765259, 0.4085083, 0.4400024, 0.4407654, 0.4817505, 0.4924011, 0.532074, 0.589386, 0.6131287,
            0.6212463, 0.6278076, 0.6308899, 0.7660828, 0.7850647, 0.7910461, 0.7929382, 0.803833, 0.98349, 0.9846191,
            0.9852295, 0.9862671
        },
        new double[32]
        {
            0.0, 0.608429, 0.3672791, 0.3151855, 0.1488953, 0.2571716, 0.5103455, 0.3311157, 0.05426025, 0.4254456,
            0.7998352, 0.787323, 0.5418701, 0.292511, 0.08468628, 0.1410522, 0.9819641, 0.960907, 0.03530884, 0.09729004,
            0.5758362, 0.9941711, 0.7215576, 0.7183228, 0.2028809, 0.09588623, 0.2032166, 0.1338806, 0.5003357, 0.187439,
            0.9804993, 0.1107788
        }
    };

    public static readonly double[] BexMode2Scale = new double[64]
    {
        0.0004272461, 0.001312256, 0.002441406, 0.003692627, 0.00491333, 0.006134033, 0.007507324, 0.008972168, 0.01049805, 0.01223755,
        0.0140686, 0.01599121, 0.01800537, 0.02026367, 0.02264404, 0.025177, 0.02792358, 0.0307312, 0.03344727, 0.03631592,
        0.03952026, 0.04275513, 0.04608154, 0.04968262, 0.05355835, 0.05783081, 0.06195068, 0.06677246, 0.07196045, 0.07745361,
        0.08319092, 0.0899353, 0.09759521, 0.1056213, 0.1138916, 0.1236267, 0.1348267, 0.1470337, 0.1603394, 0.1755676,
        0.1905823, 0.2071228, 0.2245178, 0.2444153, 0.2658997, 0.2897644, 0.3146057, 0.3450012, 0.3766174, 0.412262,
        0.4505615, 0.4893799, 0.5305481, 0.5731201, 0.6157837, 0.6580811, 0.6985168, 0.7435303, 0.7865906, 0.8302612,
        0.8718567, 0.9125671, 0.9575806, 0.9996643
    };

    public static readonly double[] BexMode3Initial = new double[16]
    {
        0.3491211, 0.5371094, 0.6782227, 0.7910156, 0.9057617, 1.024902, 1.15625, 1.290527, 1.458984, 1.664551,
        1.929688, 2.27832, 2.831543, 3.65918, 5.257813, 8.373047
    };

    public static readonly double[] BexMode3Rate = new double[16]
    {
        -0.2913818, -0.2541504, -0.1664429, -0.147644, -0.1342163, -0.1220703, -0.1117554, -0.1026611, -0.09436035, -0.08483887,
        -0.07476807, -0.06304932, -0.04492188, -0.0244751, 0.0001831055, 0.04174805
    };

    public static readonly double[] BexMode4Multiplier = new double[8] { 0.03610229, 0.1260681, 0.2227478, 0.3338318, 0.466217, 0.6221313, 0.7989197, 0.9939575 };

    public static void ApplyBandExtension(Block block)
    {
        if (block.BandExtensionEnabled && block.HasExtensionData)
        {
            Channel[] channels = block.Channels;
            for (int i = 0; i < channels.Length; i++)
            {
                ApplyBandExtensionChannel(channels[i]);
            }
        }
    }

    private static void ApplyBandExtensionChannel(Channel channel)
    {
        int quantizationUnitCount = channel.Block.QuantizationUnitCount;
        int[] scaleFactors = channel.ScaleFactors;
        double[] spectra = channel.Spectra;
        double[] bexScales = channel.BexScales;
        int[] bexValues = channel.BexValues;
        int groupBUnit = 0;
        GetBexBandInfo(out var bandCount, out var groupAUnit, out groupBUnit, quantizationUnitCount);
        int num = Math.Max(groupBUnit, 22);
        int num2 = Tables.QuantUnitToCoeffIndex[quantizationUnitCount];
        int num3 = Tables.QuantUnitToCoeffIndex[groupAUnit];
        int num4 = Tables.QuantUnitToCoeffIndex[groupBUnit];
        int num5 = Tables.QuantUnitToCoeffIndex[num];
        FillHighFrequencies(spectra, num2, num3, num4, num5);
        switch (channel.BexMode)
        {
        case 0:
        {
            int num10 = num - quantizationUnitCount;
            switch (bandCount)
            {
            case 3:
                bexScales[0] = BexMode0Bands3[0][bexValues[0]];
                bexScales[1] = BexMode0Bands3[1][bexValues[0]];
                bexScales[2] = BexMode0Bands3[2][bexValues[1]];
                bexScales[3] = BexMode0Bands3[3][bexValues[2]];
                bexScales[4] = BexMode0Bands3[4][bexValues[3]];
                break;
            case 4:
                bexScales[0] = BexMode0Bands4[0][bexValues[0]];
                bexScales[1] = BexMode0Bands4[1][bexValues[0]];
                bexScales[2] = BexMode0Bands4[2][bexValues[1]];
                bexScales[3] = BexMode0Bands4[3][bexValues[2]];
                bexScales[4] = BexMode0Bands4[4][bexValues[3]];
                break;
            case 5:
                bexScales[0] = BexMode0Bands5[0][bexValues[0]];
                bexScales[1] = BexMode0Bands5[1][bexValues[1]];
                bexScales[2] = BexMode0Bands5[2][bexValues[1]];
                break;
            }
            bexScales[num10 - 1] = Tables.SpectrumScale[scaleFactors[quantizationUnitCount]];
            AddNoiseToSpectrum(channel, Tables.QuantUnitToCoeffIndex[num - 1], Tables.QuantUnitToCoeffCount[num - 1]);
            ScaleBexQuantUnits(spectra, bexScales, quantizationUnitCount, num);
            break;
        }
        case 1:
        {
            for (int m = quantizationUnitCount; m < num; m++)
            {
                bexScales[m - quantizationUnitCount] = Tables.SpectrumScale[scaleFactors[m]];
            }
            AddNoiseToSpectrum(channel, num2, num5 - num2);
            ScaleBexQuantUnits(spectra, bexScales, quantizationUnitCount, num);
            break;
        }
        case 2:
        {
            double num13 = BexMode2Scale[bexValues[0]];
            double num14 = BexMode2Scale[bexValues[1]];
            for (int n = num2; n < num3; n++)
            {
                spectra[n] *= num13;
            }
            for (int num15 = num3; num15 < num4; num15++)
            {
                spectra[num15] *= num14;
            }
            break;
        }
        case 3:
        {
            double num11 = Math.Pow(2.0, BexMode3Rate[bexValues[1]]);
            double num12 = BexMode3Initial[bexValues[0]];
            for (int l = num2; l < num5; l++)
            {
                num12 *= num11;
                spectra[l] *= num12;
            }
            break;
        }
        case 4:
        {
            double num6 = BexMode4Multiplier[bexValues[0]];
            double num7 = 0.7079468 * num6;
            double num8 = 0.5011902 * num6;
            double num9 = 0.3548279 * num6;
            for (int i = num2; i < num3; i++)
            {
                spectra[i] *= num7;
            }
            for (int j = num3; j < num4; j++)
            {
                spectra[j] *= num8;
            }
            for (int k = num4; k < num5; k++)
            {
                spectra[k] *= num9;
            }
            break;
        }
        }
    }

    private static void ScaleBexQuantUnits(double[] spectra, double[] scales, int startUnit, int totalUnits)
    {
        for (int i = startUnit; i < totalUnits; i++)
        {
            for (int j = Tables.QuantUnitToCoeffIndex[i]; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
            {
                spectra[j] *= scales[i - startUnit];
            }
        }
    }

    private static void FillHighFrequencies(double[] spectra, int groupABin, int groupBBin, int groupCBin, int totalBins)
    {
        for (int i = 0; i < groupBBin - groupABin; i++)
        {
            spectra[groupABin + i] = spectra[groupABin - i - 1];
        }
        for (int j = 0; j < groupCBin - groupBBin; j++)
        {
            spectra[groupBBin + j] = spectra[groupBBin - j - 1];
        }
        for (int k = 0; k < totalBins - groupCBin; k++)
        {
            spectra[groupCBin + k] = spectra[groupCBin - k - 1];
        }
    }

    private static void AddNoiseToSpectrum(Channel channel, int index, int count)
    {
        if (channel.Rng == null)
        {
            int[] scaleFactors = channel.ScaleFactors;
            ushort seed = (ushort)(543 * (scaleFactors[8] + scaleFactors[12] + scaleFactors[15] + 1));
            channel.Rng = new Atrac9Rng(seed);
        }
        for (int i = 0; i < count; i++)
        {
            channel.Spectra[i + index] = (double)(int)channel.Rng.Next() / 65535.0 * 2.0 - 1.0;
        }
    }

    public static void GetBexBandInfo(out int bandCount, out int groupAUnit, out int groupBUnit, int quantUnits)
    {
        groupAUnit = BexGroupInfo[quantUnits - 13][0];
        groupBUnit = BexGroupInfo[quantUnits - 13][1];
        bandCount = BexGroupInfo[quantUnits - 13][2];
    }
}
