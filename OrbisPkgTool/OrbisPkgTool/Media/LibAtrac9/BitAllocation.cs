using System;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class BitAllocation
{
    public static void CreateGradient(Block block)
    {
        int num = block.GradientEndValue - block.GradientStartValue;
        int num2 = block.GradientEndUnit - block.GradientStartUnit;
        for (int i = 0; i < block.GradientEndUnit; i++)
        {
            block.Gradient[i] = block.GradientStartValue;
        }
        for (int j = block.GradientEndUnit; j <= block.QuantizationUnitCount; j++)
        {
            block.Gradient[j] = block.GradientEndValue;
        }
        if (num2 <= 0 || num == 0)
        {
            return;
        }
        byte[] array = Tables.GradientCurves[num2 - 1];
        if (num <= 0)
        {
            double num3 = (double)(-num - 1) / 31.0;
            int num4 = block.GradientStartValue - 1;
            for (int k = block.GradientStartUnit; k < block.GradientEndUnit; k++)
            {
                block.Gradient[k] = num4 - (int)((double)(int)array[k - block.GradientStartUnit] * num3);
            }
        }
        else
        {
            double num5 = (double)(num - 1) / 31.0;
            int num6 = block.GradientStartValue + 1;
            for (int l = block.GradientStartUnit; l < block.GradientEndUnit; l++)
            {
                block.Gradient[l] = num6 + (int)((double)(int)array[l - block.GradientStartUnit] * num5);
            }
        }
    }

    public static void CalculateMask(Channel channel)
    {
        Array.Clear(channel.PrecisionMask, 0, channel.PrecisionMask.Length);
        for (int i = 1; i < channel.Block.QuantizationUnitCount; i++)
        {
            int num = channel.ScaleFactors[i] - channel.ScaleFactors[i - 1];
            if (num > 1)
            {
                channel.PrecisionMask[i] += Math.Min(num - 1, 5);
            }
            else if (num < -1)
            {
                channel.PrecisionMask[i - 1] += Math.Min(num * -1 - 1, 5);
            }
        }
    }

    public static void CalculatePrecisions(Channel channel)
    {
        Block block = channel.Block;
        if (block.GradientMode != 0)
        {
            for (int i = 0; i < block.QuantizationUnitCount; i++)
            {
                channel.Precisions[i] = channel.ScaleFactors[i] + channel.PrecisionMask[i] - block.Gradient[i];
                if (channel.Precisions[i] > 0)
                {
                    switch (block.GradientMode)
                    {
                    case 1:
                        channel.Precisions[i] /= 2;
                        break;
                    case 2:
                        channel.Precisions[i] = 3 * channel.Precisions[i] / 8;
                        break;
                    case 3:
                        channel.Precisions[i] /= 4;
                        break;
                    }
                }
            }
        }
        else
        {
            for (int j = 0; j < block.QuantizationUnitCount; j++)
            {
                channel.Precisions[j] = channel.ScaleFactors[j] - block.Gradient[j];
            }
        }
        for (int k = 0; k < block.QuantizationUnitCount; k++)
        {
            if (channel.Precisions[k] < 1)
            {
                channel.Precisions[k] = 1;
            }
        }
        for (int l = 0; l < block.GradientBoundary; l++)
        {
            channel.Precisions[l]++;
        }
        for (int m = 0; m < block.QuantizationUnitCount; m++)
        {
            channel.PrecisionsFine[m] = 0;
            if (channel.Precisions[m] > 15)
            {
                channel.PrecisionsFine[m] = channel.Precisions[m] - 15;
                channel.Precisions[m] = 15;
            }
        }
    }

    public static byte[][] GenerateGradientCurves()
    {
        byte[] array = new byte[48]
        {
            1, 1, 1, 1, 2, 2, 2, 2, 3, 3,
            3, 4, 4, 5, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 15, 16, 18, 19, 20, 21, 22,
            23, 24, 25, 26, 26, 27, 27, 28, 28, 28,
            29, 29, 29, 29, 30, 30, 30, 30
        };
        byte[][] array2 = new byte[array.Length][];
        for (int i = 1; i <= array.Length; i++)
        {
            array2[i - 1] = new byte[i];
            for (int j = 0; j < i; j++)
            {
                array2[i - 1][j] = array[j * array.Length / i];
            }
        }
        return array2;
    }
}
