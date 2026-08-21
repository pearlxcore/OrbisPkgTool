using System.IO;
using OrbisPkgTool.Media.LibAtrac9.Utilities;

namespace OrbisPkgTool.Media.LibAtrac9;

public class Atrac9Config
{
    public byte[] ConfigData { get; }

    public int SampleRateIndex { get; }

    public int ChannelConfigIndex { get; }

    public int FrameBytes { get; }

    public int SuperframeIndex { get; }

    public ChannelConfig ChannelConfig { get; }

    public int ChannelCount { get; }

    public int SampleRate { get; }

    public bool HighSampleRate { get; }

    public int FramesPerSuperframe { get; }

    public int FrameSamplesPower { get; }

    public int FrameSamples { get; }

    public int SuperframeBytes { get; }

    public int SuperframeSamples { get; }

    public Atrac9Config(byte[] configData)
    {
        if (configData == null || configData.Length != 4)
        {
            throw new InvalidDataException("Config data must be 4 bytes long");
        }
        int superframeIndex = 0;
        ReadConfigData(configData, out var sampleRateIndex, out var channelConfigIndex, out var frameBytes, out superframeIndex);
        SampleRateIndex = sampleRateIndex;
        ChannelConfigIndex = channelConfigIndex;
        FrameBytes = frameBytes;
        SuperframeIndex = superframeIndex;
        ConfigData = configData;
        FramesPerSuperframe = 1 << SuperframeIndex;
        SuperframeBytes = FrameBytes << SuperframeIndex;
        ChannelConfig = Tables.ChannelConfig[ChannelConfigIndex];
        ChannelCount = ChannelConfig.ChannelCount;
        SampleRate = Tables.SampleRates[SampleRateIndex];
        HighSampleRate = SampleRateIndex > 7;
        FrameSamplesPower = Tables.SamplingRateIndexToFrameSamplesPower[SampleRateIndex];
        FrameSamples = 1 << FrameSamplesPower;
        SuperframeSamples = FrameSamples * FramesPerSuperframe;
    }

    private static void ReadConfigData(byte[] configData, out int sampleRateIndex, out int channelConfigIndex, out int frameBytes, out int superframeIndex)
    {
        BitReader bitReader = new BitReader(configData);
        int num = bitReader.ReadInt(8);
        sampleRateIndex = bitReader.ReadInt(4);
        channelConfigIndex = bitReader.ReadInt(3);
        int num2 = bitReader.ReadInt(1);
        frameBytes = bitReader.ReadInt(11) + 1;
        superframeIndex = bitReader.ReadInt(2);
        if (num != 254 || num2 != 0)
        {
            throw new InvalidDataException("ATRAC9 Config Data is invalid");
        }
    }
}
