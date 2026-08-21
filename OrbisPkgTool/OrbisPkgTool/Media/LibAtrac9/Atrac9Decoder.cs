using System;
using OrbisPkgTool.Media.LibAtrac9.Utilities;

namespace OrbisPkgTool.Media.LibAtrac9;

public class Atrac9Decoder
{
    private bool _initialized;

    public Atrac9Config Config { get; private set; }

    private Frame Frame { get; set; }

    private BitReader Reader { get; set; }

    public void Initialize(byte[] configData)
    {
        Config = new Atrac9Config(configData);
        Frame = new Frame(Config);
        Reader = new BitReader(null);
        _initialized = true;
    }

    public void Decode(byte[] atrac9Data, short[][] pcmOut)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Decoder must be initialized before decoding.");
        }
        ValidateDecodeBuffers(atrac9Data, pcmOut);
        Reader.SetBuffer(atrac9Data);
        DecodeSuperFrame(pcmOut);
    }

    private void ValidateDecodeBuffers(byte[] atrac9Buffer, short[][] pcmBuffer)
    {
        if (atrac9Buffer == null)
        {
            throw new ArgumentNullException("atrac9Buffer");
        }
        if (pcmBuffer == null)
        {
            throw new ArgumentNullException("pcmBuffer");
        }
        if (atrac9Buffer.Length < Config.SuperframeBytes)
        {
            throw new ArgumentException("ATRAC9 buffer is too small");
        }
        if (pcmBuffer.Length < Config.ChannelCount)
        {
            throw new ArgumentException("PCM buffer is too small");
        }
        for (int i = 0; i < Config.ChannelCount; i++)
        {
            if (pcmBuffer[i] == null || pcmBuffer[i].Length < Config.SuperframeSamples)
            {
                throw new ArgumentException("PCM buffer is too small");
            }
        }
    }

    private void DecodeSuperFrame(short[][] pcmOut)
    {
        for (int i = 0; i < Config.FramesPerSuperframe; i++)
        {
            Frame.FrameIndex = i;
            DecodeFrame(Reader, Frame);
            PcmFloatToShort(pcmOut, i * Config.FrameSamples);
            Reader.AlignPosition(8);
        }
    }

    private void PcmFloatToShort(short[][] pcmOut, int start)
    {
        int num = start + Config.FrameSamples;
        int num2 = 0;
        Block[] blocks = Frame.Blocks;
        for (int i = 0; i < blocks.Length; i++)
        {
            Channel[] channels = blocks[i].Channels;
            for (int j = 0; j < channels.Length; j++)
            {
                double[] pcm = channels[j].Pcm;
                short[] array = pcmOut[num2++];
                int num3 = 0;
                for (int k = start; k < num; k++)
                {
                    int value = (int)Math.Floor(pcm[num3] + 0.5);
                    array[k] = Helpers.Clamp16(value);
                    num3++;
                }
            }
        }
    }

    private static void DecodeFrame(BitReader reader, Frame frame)
    {
        Unpack.UnpackFrame(reader, frame);
        Block[] blocks = frame.Blocks;
        foreach (Block block in blocks)
        {
            Quantization.DequantizeSpectra(block);
            Stereo.ApplyIntensityStereo(block);
            Quantization.ScaleSpectrum(block);
            BandExtension.ApplyBandExtension(block);
            ImdctBlock(block);
        }
    }

    private static void ImdctBlock(Block block)
    {
        Channel[] channels = block.Channels;
        foreach (Channel channel in channels)
        {
            channel.Mdct.RunImdct(channel.Spectra, channel.Pcm);
        }
    }
}
