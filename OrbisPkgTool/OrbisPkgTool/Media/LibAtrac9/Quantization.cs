using System;

namespace OrbisPkgTool.Media.LibAtrac9;

internal static class Quantization
{
    public static void DequantizeSpectra(Block block)
    {
        Channel[] channels = block.Channels;
        foreach (Channel channel in channels)
        {
            Array.Clear(channel.Spectra, 0, channel.Spectra.Length);
            for (int j = 0; j < channel.CodedQuantUnits; j++)
            {
                DequantizeQuantUnit(channel, j);
            }
        }
    }

    private static void DequantizeQuantUnit(Channel channel, int band)
    {
        int num = Tables.QuantUnitToCoeffIndex[band];
        int num2 = Tables.QuantUnitToCoeffCount[band];
        double num3 = Tables.QuantizerStepSize[channel.Precisions[band]];
        double num4 = Tables.QuantizerFineStepSize[channel.PrecisionsFine[band]];
        for (int i = 0; i < num2; i++)
        {
            double num5 = (double)channel.QuantizedSpectra[num + i] * num3;
            double num6 = (double)channel.QuantizedSpectraFine[num + i] * num4;
            channel.Spectra[num + i] = num5 + num6;
        }
    }

    public static void ScaleSpectrum(Block block)
    {
        Channel[] channels = block.Channels;
        for (int i = 0; i < channels.Length; i++)
        {
            ScaleSpectrum(channels[i]);
        }
    }

    private static void ScaleSpectrum(Channel channel)
    {
        int quantizationUnitCount = channel.Block.QuantizationUnitCount;
        double[] spectra = channel.Spectra;
        for (int i = 0; i < quantizationUnitCount; i++)
        {
            for (int j = Tables.QuantUnitToCoeffIndex[i]; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
            {
                spectra[j] *= Tables.SpectrumScale[channel.ScaleFactors[i]];
            }
        }
    }
}
