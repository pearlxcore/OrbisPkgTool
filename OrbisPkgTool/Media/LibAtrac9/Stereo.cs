namespace OrbisPkgTool.Media.LibAtrac9;

internal static class Stereo
{
    public static void ApplyIntensityStereo(Block block)
    {
        if (block.BlockType != BlockType.Stereo)
        {
            return;
        }
        int quantizationUnitCount = block.QuantizationUnitCount;
        int stereoQuantizationUnit = block.StereoQuantizationUnit;
        if (stereoQuantizationUnit >= quantizationUnitCount)
        {
            return;
        }
        Channel primaryChannel = block.PrimaryChannel;
        Channel secondaryChannel = block.SecondaryChannel;
        for (int i = stereoQuantizationUnit; i < quantizationUnitCount; i++)
        {
            int num = block.JointStereoSigns[i];
            for (int j = Tables.QuantUnitToCoeffIndex[i]; j < Tables.QuantUnitToCoeffIndex[i + 1]; j++)
            {
                if (num > 0)
                {
                    secondaryChannel.Spectra[j] = 0.0 - primaryChannel.Spectra[j];
                }
                else
                {
                    secondaryChannel.Spectra[j] = primaryChannel.Spectra[j];
                }
            }
        }
    }
}
