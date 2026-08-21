using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OrbisPkgTool.Media;
using OrbisPkgTool.Media.LibAtrac9;
using Xunit;

namespace OrbisPkgTool.Tests;

public class At9DecoderTests
{
    /// <summary>
    /// Builds an ATRAC9 config dword: 8-bit sync 0xFE | 4-bit rate | 3-bit channel config |
    /// 1-bit validation (0) | 11-bit frameBytes-1 | 2-bit superframe index.
    /// </summary>
    private static byte[] MakeConfigBytes(int sampleRateIndex, int channelConfigIndex, int frameBytesMinusOne, int superframeIndex, int validationBit = 0, int magic = 0xFE)
    {
        int word = (magic << 24) | (sampleRateIndex << 20) | (channelConfigIndex << 17) | (validationBit << 16) | ((frameBytesMinusOne & 0x7FF) << 5) | ((superframeIndex & 0x3) << 3);
        return new byte[4] { (byte)(word >> 24), (byte)(word >> 16), (byte)(word >> 8), (byte)word };
    }

    // 48 kHz (rate index 7) stereo, frameBytes 80, one frame per superframe.
    private static byte[] SimpleStereoConfig => MakeConfigBytes(7, 2, 79, 0);

    // 48 kHz (rate index 7) stereo, frameBytes 80, four frames per superframe.
    private static byte[] MultiFrameStereoConfig => MakeConfigBytes(7, 2, 79, 2);

    /// <summary>MSB-first bit writer mirroring BitReader's read order.</summary>
    private sealed class BitBuilder
    {
        private readonly List<bool> _bits = new();

        public void Write(int value, int bitCount)
        {
            for (int i = bitCount - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public void WriteBool(bool value) => _bits.Add(value);

        /// <summary>The bits padded with zeros to a whole number of bytes.</summary>
        public byte[] ToBytes()
        {
            byte[] result = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    result[i / 8] |= (byte)(1 << (7 - (i % 8)));
                }
            }
            return result;
        }
    }

    // Band count 3 => BandToQuantUnitCount[3] = 10 quantization units; scale factor
    // weights for weight table 0, first 10 units: {0,0,0,1,1,2,2,2,2,2}.
    private const int QuantUnitCount = 10;

    /// <summary>
    /// Writes one minimally valid stereo frame: band count 3, no band extension, flat
    /// gradient, VLC delta-offset scale factors using the A6 codebook (raw values
    /// 8,9,10,10,... so every precision lands at 8-9), and CLC spectra (precision+1 &gt; 7
    /// skips Huffman entirely, consuming a fixed number of zero bits per coefficient).
    /// </summary>
    private static void WriteFrameBits(BitBuilder bb, int frameIndex)
    {
        // Block header: FirstInSuperframe = !bit (bit set for frames > 0), ReuseBandParams = bit.
        bb.WriteBool(frameIndex != 0);
        bb.WriteBool(frameIndex != 0);
        if (frameIndex == 0)
        {
            bb.Write(0, 4);          // BandCount -> 0 + 3 = 3 (10 units)
            bb.Write(0, 4);          // StereoBand -> 3 (10 units)
            bb.WriteBool(false);     // BandExtensionEnabled
        }
        bb.Write(0, 2);              // GradientMode = 0
        bb.Write(1, 6);              // GradientStartUnit = 1
        bb.Write(0, 6);              // GradientEndUnit = 0 + 1 = 1
        bb.Write(0, 5);              // GradientStartValue = 0
        bb.Write(0, 5);              // GradientEndValue = 0
        bb.Write(0, 4);              // GradientBoundary = 0
        bb.Write(0, 1);              // PrimaryChannelIndex = 0
        bb.WriteBool(false);         // HasJointStereoSigns
        bb.WriteBool(false);         // HasExtensionData
        for (int ch = 0; ch < 2; ch++)
        {
            // Scale factors, mode 0 (VLC delta offset): weight table, offset, table idx,
            // first raw value (6 bits CLC for the A6 codebook), then huffman deltas.
            // Raw sequence 8,9,10,10,... minus weights gives scale factors 8,9,9,9,9,8,8,8,8,8.
            bb.Write(0, 2);          // ScaleFactorCodingMode = 0
            bb.Write(0, 3);          // weight table 0
            bb.Write(0, 5);          // offset 0
            bb.Write(3, 2);          // table idx 3 -> A6 codebook (num3 = 6)
            bb.Write(8, 6);          // first raw value 8
            bb.Write(1, 3);          // A6 delta 1 -> raw 9
            bb.Write(1, 3);          // A6 delta 1 -> raw 10
            for (int i = 3; i < QuantUnitCount; i++)
            {
                bb.Write(0, 3);      // A6 delta 0 -> raw 10
            }
            // Spectra via CLC (precision+1 > MaxHuffPrecision(7) skips Huffman):
            // precisions are {8,9,10,9,9,8,8,8,8,8}; coefficients per unit {2,2,2,2,2,2,2,2,4,4}.
            bb.Write(0, 2 * 9);                            // unit 0: 9 bits x 2
            bb.Write(0, 2 * 10);                           // unit 1: 10 bits x 2
            bb.Write(0, 2 * 11);                           // unit 2: 11 bits x 2
            bb.Write(0, 2 * 10);                           // unit 3: 10 bits x 2
            bb.Write(0, 2 * 10);                           // unit 4: 10 bits x 2
            bb.Write(0, 2 * 9);                            // unit 5: 9 bits x 2
            bb.Write(0, 2 * 9);                            // unit 6: 9 bits x 2
            bb.Write(0, 2 * 9);                            // unit 7: 9 bits x 2
            bb.Write(0, 4 * 9);                            // unit 8: 9 bits x 4
            bb.Write(0, 4 * 9);                            // unit 9: 9 bits x 4
        }
    }

    private static byte[] BuildSuperframe(byte[] configData)
    {
        var config = new Atrac9Config(configData);
        using var ms = new MemoryStream();
        for (int frame = 0; frame < config.FramesPerSuperframe; frame++)
        {
            var bb = new BitBuilder();
            WriteFrameBits(bb, frame);
            byte[] frameBytes = bb.ToBytes();
            Assert.True(frameBytes.Length <= config.FrameBytes,
                $"crafted frame needs {frameBytes.Length} bytes but FrameBytes is {config.FrameBytes}");
            ms.Write(frameBytes, 0, frameBytes.Length);
        }
        byte[] superframe = new byte[config.SuperframeBytes];
        ms.Position = 0;
        ms.Read(superframe, 0, (int)ms.Length);
        return superframe;
    }

    private static byte[] BuildAt9File(byte[] configData, int superframeCount, int sampleCount, int encoderDelay)
    {
        var config = new Atrac9Config(configData);
        byte[] superframe = BuildSuperframe(configData);

        using var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        // RIFF header (size fixed up after)
        writer.Write(Encoding.UTF8.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.UTF8.GetBytes("WAVE"));
        // fmt chunk: 16 bytes standard + 36 bytes AT9 extensible (cbSize 34) = 52
        writer.Write(Encoding.UTF8.GetBytes("fmt "));
        writer.Write(52);
        writer.Write((short)-2);                                      // 0xFFFE WAVE_FORMAT_EXTENSIBLE
        writer.Write((short)config.ChannelCount);
        writer.Write(config.SampleRate);
        writer.Write(config.SampleRate * config.ChannelCount * 2);
        writer.Write((short)(config.ChannelCount * 2));
        writer.Write((short)16);
        writer.Write((short)34);                                      // cbSize
        writer.Write((short)16);                                      // valid bits per sample
        writer.Write((uint)0x3);                                      // channel mask
        writer.Write(new Guid("47E142D2-36BA-4d8d-88FC-61654F8C836C").ToByteArray());
        writer.Write(0);                                              // version info
        writer.Write(configData);                                     // AT9 config
        writer.Write(0);                                              // reserved
        // fact chunk
        writer.Write(Encoding.UTF8.GetBytes("fact"));
        writer.Write(12);
        writer.Write(sampleCount);
        writer.Write(0);                                              // input overlap delay
        writer.Write(encoderDelay);
        // data chunk
        writer.Write(Encoding.UTF8.GetBytes("data"));
        writer.Write(superframe.Length * superframeCount);
        for (int i = 0; i < superframeCount; i++)
        {
            writer.Write(superframe);
        }
        writer.Flush();

        byte[] file = ms.ToArray();
        int riffSize = file.Length - 8;
        file[4] = (byte)riffSize;
        file[5] = (byte)(riffSize >> 8);
        file[6] = (byte)(riffSize >> 16);
        file[7] = (byte)(riffSize >> 24);
        return file;
    }

    private static byte[] AddSmplChunk(byte[] at9, int loopStart, int loopEnd)
    {
        using var ms = new MemoryStream();
        ms.Write(at9, 0, at9.Length);
        var writer = new BinaryWriter(ms);
        writer.Write(Encoding.UTF8.GetBytes("smpl"));
        writer.Write(60);
        for (int i = 0; i < 7; i++)
        {
            writer.Write(0);
        }
        writer.Write(1);
        for (int i = 0; i < 3; i++)
        {
            writer.Write(0);
        }
        writer.Write(loopStart);
        writer.Write(loopEnd);
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        byte[] file = ms.ToArray();
        int riffSize = file.Length - 8;
        file[4] = (byte)riffSize;
        file[5] = (byte)(riffSize >> 8);
        file[6] = (byte)(riffSize >> 16);
        file[7] = (byte)(riffSize >> 24);
        return file;
    }

    [Fact]
    public void Atrac9Config_ParsesStereoConfig()
    {
        var config = new Atrac9Config(MakeConfigBytes(7, 2, 139, 2));

        Assert.Equal(7, config.SampleRateIndex);
        Assert.Equal(2, config.ChannelConfigIndex);
        Assert.Equal(140, config.FrameBytes);
        Assert.Equal(2, config.SuperframeIndex);
        Assert.Equal(4, config.FramesPerSuperframe);
        Assert.Equal(560, config.SuperframeBytes);
        Assert.Equal(2, config.ChannelCount);
        Assert.Equal(48000, config.SampleRate);
        Assert.False(config.HighSampleRate);
        Assert.Equal(8, config.FrameSamplesPower);
        Assert.Equal(256, config.FrameSamples);
        Assert.Equal(1024, config.SuperframeSamples);
    }

    [Fact]
    public void Atrac9Config_ParsesMonoConfig()
    {
        var config = new Atrac9Config(MakeConfigBytes(5, 0, 255, 0));

        Assert.Equal(1, config.ChannelCount);
        Assert.Equal(32000, config.SampleRate);
        Assert.Equal(256, config.FrameBytes);
        Assert.Equal(1, config.FramesPerSuperframe);
        Assert.Equal(256, config.FrameSamples);
        Assert.Equal(256, config.SuperframeSamples);
        Assert.Equal(256, config.SuperframeBytes);
    }

    [Fact]
    public void Atrac9Config_HighSampleRateFlag()
    {
        // Rate index 8+ => 44100 (high), frame samples 64
        var config = new Atrac9Config(MakeConfigBytes(8, 2, 139, 2));
        Assert.Equal(44100, config.SampleRate);
        Assert.True(config.HighSampleRate);
        Assert.Equal(6, config.FrameSamplesPower);
        Assert.Equal(64, config.FrameSamples);
    }

    [Theory]
    [InlineData(0xFD, 0)]   // bad magic
    [InlineData(0xFE, 1)]   // bad validation bit
    public void Atrac9Config_RejectsInvalidConfig(int magic, int validationBit)
    {
        Assert.Throws<InvalidDataException>(() => new Atrac9Config(MakeConfigBytes(7, 2, 139, 2, validationBit, magic)));
    }

    [Fact]
    public void Atrac9Config_RejectsWrongLength()
    {
        Assert.Throws<InvalidDataException>(() => new Atrac9Config(new byte[] { 0xFE, 0x64, 0x11 }));
        Assert.Throws<InvalidDataException>(() => new Atrac9Config(null));
    }

    [Fact]
    public void At9Decoder_Decode_CraftedSuperframe_ProducesSilence()
    {
        var decoder = new Atrac9Decoder();
        decoder.Initialize(SimpleStereoConfig);

        byte[] superframe = BuildSuperframe(SimpleStereoConfig);
        Assert.Equal(decoder.Config.SuperframeBytes, superframe.Length);

        short[][] pcm = new short[decoder.Config.ChannelCount][];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = new short[decoder.Config.SuperframeSamples];
        }

        decoder.Decode(superframe, pcm);

        // The crafted frame's spectra are all zero, so PCM decodes to silence.
        for (int ch = 0; ch < pcm.Length; ch++)
        {
            Assert.All(pcm[ch], s => Assert.Equal(0, s));
        }
    }

    [Fact]
    public void At9Decoder_Decode_CraftedMultiFrameSuperframe()
    {
        var decoder = new Atrac9Decoder();
        decoder.Initialize(MultiFrameStereoConfig);

        byte[] superframe = BuildSuperframe(MultiFrameStereoConfig);
        Assert.Equal(320, superframe.Length);

        short[][] pcm = new short[decoder.Config.ChannelCount][];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = new short[decoder.Config.SuperframeSamples];
        }

        decoder.Decode(superframe, pcm);
        Assert.All(pcm[0], s => Assert.Equal(0, s));
        Assert.All(pcm[1], s => Assert.Equal(0, s));
    }

    [Fact]
    public void At9Decoder_Decode_ThrowsBeforeInitialize()
    {
        var decoder = new Atrac9Decoder();
        Assert.Throws<InvalidOperationException>(() => decoder.Decode(new byte[8], new short[1][]));
    }

    [Fact]
    public void At9Decoder_Decode_ValidatesBuffers()
    {
        var decoder = new Atrac9Decoder();
        decoder.Initialize(SimpleStereoConfig);

        Assert.Throws<ArgumentException>(() => decoder.Decode(new byte[4], new short[2][]));

        short[][] pcm = { new short[decoder.Config.SuperframeSamples] };
        Assert.Throws<ArgumentException>(() => decoder.Decode(new byte[decoder.Config.SuperframeBytes], pcm));

        pcm = new short[][] { new short[8], new short[8] };
        Assert.Throws<ArgumentException>(() => decoder.Decode(new byte[decoder.Config.SuperframeBytes], pcm));
    }

    [Fact]
    public void At9Decoder_ReadFile_ParsesContainer()
    {
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 2, sampleCount: 512, encoderDelay: 0);

        using var ms = new MemoryStream(at9);
        At9File file = At9Decoder.ReadFile(ms);

        Assert.NotNull(file.Config);
        Assert.Equal(48000, file.Config.SampleRate);
        Assert.Equal(2, file.Config.ChannelCount);
        Assert.Equal(512, file.SampleCount);
        Assert.Equal(0, file.EncoderDelay);
        Assert.Equal(2, file.SuperframeCount);
        Assert.Equal(2, file.AudioData.Length);
        Assert.All(file.AudioData, d => Assert.Equal(80, d.Length));
        Assert.False(file.Looping);
        Assert.Equal(0, file.LoopStart);
        Assert.Equal(0, file.LoopEnd);
    }

    [Fact]
    public void At9Decoder_ReadFile_ParsesLoopPoints()
    {
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 2, sampleCount: 256, encoderDelay: 64);
        at9 = AddSmplChunk(at9, loopStart: 164, loopEnd: 320);

        using var ms = new MemoryStream(at9);
        At9File file = At9Decoder.ReadFile(ms);

        Assert.True(file.Looping);
        Assert.Equal(100, file.LoopStart);
        Assert.Equal(256, file.LoopEnd);
    }

    [Fact]
    public void At9Decoder_DecodeToWav_ProducesValidWav()
    {
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 2, sampleCount: 512, encoderDelay: 0);

        byte[] wav = At9Decoder.DecodeToWav(at9);

        Assert.Equal("RIFF", Encoding.UTF8.GetString(wav, 0, 4));
        int riffSize = BitConverter.ToInt32(wav, 4);
        Assert.Equal(wav.Length - 8, riffSize);
        Assert.Equal("WAVE", Encoding.UTF8.GetString(wav, 8, 4));

        Assert.Equal("fmt ", Encoding.UTF8.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));                       // PCM
        Assert.Equal(2, BitConverter.ToInt16(wav, 22));                       // channels
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24));                   // sample rate
        Assert.Equal(48000 * 2 * 2, BitConverter.ToInt32(wav, 28));           // bytes/sec
        Assert.Equal(4, BitConverter.ToInt16(wav, 32));                       // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));                      // bits per sample

        Assert.Equal("data", Encoding.UTF8.GetString(wav, 36, 4));
        int dataSize = BitConverter.ToInt32(wav, 40);
        Assert.Equal(512 * 2 * 2, dataSize);
        Assert.Equal(wav.Length, 44 + dataSize);

        Assert.All(wav.Skip(44), b => Assert.Equal(0, b));
    }

    [Fact]
    public void At9Decoder_DecodeToWav_MultiFrame()
    {
        byte[] at9 = BuildAt9File(MultiFrameStereoConfig, superframeCount: 2, sampleCount: 2048, encoderDelay: 0);

        byte[] wav = At9Decoder.DecodeToWav(at9);

        int dataSize = BitConverter.ToInt32(wav, 40);
        Assert.Equal(2048 * 2 * 2, dataSize);
        Assert.All(wav.Skip(44), b => Assert.Equal(0, b));
    }

    [Fact]
    public void At9Decoder_DecodeToWav_HonorsEncoderDelay()
    {
        // 2 superframes = 512 decoded samples; output keeps only sampleCount samples after the delay.
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 2, sampleCount: 384, encoderDelay: 128);

        byte[] wav = At9Decoder.DecodeToWav(at9);

        int dataSize = BitConverter.ToInt32(wav, 40);
        Assert.Equal(384 * 2 * 2, dataSize);
    }

    [Fact]
    public void At9Decoder_DecodeToWav_WithLoop_WritesSmplChunk()
    {
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 2, sampleCount: 256, encoderDelay: 64);
        at9 = AddSmplChunk(at9, loopStart: 164, loopEnd: 264);

        byte[] wav = At9Decoder.DecodeToWav(at9);

        int smplOffset = 44 + 256 * 2 * 2;
        Assert.Equal("smpl", Encoding.UTF8.GetString(wav, smplOffset, 4));
        Assert.Equal(60, BitConverter.ToInt32(wav, smplOffset + 4));
        // smpl data: 7 zero ints (28) + sampleLoops 1 (4) + 3 zero ints (12) + loopStart (4) + loopEnd (4)...
        // Loop points are written unaligned (smpl start/end minus encoder delay).
        Assert.Equal(100, BitConverter.ToInt32(wav, smplOffset + 8 + 44));
        Assert.Equal(200, BitConverter.ToInt32(wav, smplOffset + 8 + 48));
        Assert.Equal(wav.Length, smplOffset + 8 + 60);
    }

    [Fact]
    public void At9Decoder_DecodeToWav_FromPath()
    {
        byte[] at9 = BuildAt9File(SimpleStereoConfig, superframeCount: 1, sampleCount: 256, encoderDelay: 0);
        string path = Path.Combine(Path.GetTempPath(), $"at9test_{Guid.NewGuid():N}.at9");
        File.WriteAllBytes(path, at9);
        try
        {
            byte[] wav = At9Decoder.DecodeToWav(path);
            Assert.Equal(256 * 2 * 2, BitConverter.ToInt32(wav, 40));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void At9Decoder_DecodeToWav_RejectsMissingChunks()
    {
        using var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write(Encoding.UTF8.GetBytes("RIFF"));
        writer.Write(4);
        writer.Write(Encoding.UTF8.GetBytes("WAVE"));
        writer.Flush();
        byte[] file = ms.ToArray();

        Assert.Throws<InvalidDataException>(() => At9Decoder.DecodeToWav(file));
    }

    [Fact]
    public void At9Decoder_DecodeToWav_RejectsNonWaveRiff()
    {
        using var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write(Encoding.UTF8.GetBytes("RIFF"));
        writer.Write(4);
        writer.Write(Encoding.UTF8.GetBytes("JUNK"));
        writer.Flush();
        byte[] file = ms.ToArray();

        Assert.Throws<InvalidDataException>(() => At9Decoder.ReadFile(new MemoryStream(file)));
    }
}
