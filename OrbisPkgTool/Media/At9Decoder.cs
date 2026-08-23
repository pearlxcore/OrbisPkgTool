using System;
using System.IO;
using System.Linq;
using OrbisPkgTool.Media.LibAtrac9;

namespace OrbisPkgTool.Media;

/// <summary>
/// Result of parsing an .at9 file (port of PS4_Tools.Media.Atrac9.At9Structure).
/// </summary>
public sealed class At9File
{
    public Atrac9Config Config { get; set; }

    public byte[][] AudioData { get; set; }

    public int SampleCount { get; set; }

    public int Version { get; set; }

    public int EncoderDelay { get; set; }

    public int SuperframeCount { get; set; }

    public bool Looping { get; set; }

    public int LoopStart { get; set; }

    public int LoopEnd { get; set; }
}

/// <summary>
/// ATRAC9 decoder facade: reads an .at9 file and decodes it to a 16-bit PCM WAV,
/// replicating PS4_Tools.Media.Atrac9.LoadAt9.
/// </summary>
public static class At9Decoder
{
    private static readonly Guid MediaSubtypeAtrac9 = new Guid("47E142D2-36BA-4d8d-88FC-61654F8C836C");

    private static readonly Guid MediaSubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>Decodes an .at9 file from disk to 16-bit PCM WAV bytes.</summary>
    public static byte[] DecodeToWav(string at9Path)
    {
        using Stream stream = new FileStream(at9Path, FileMode.Open, FileAccess.Read);
        return DecodeToWav(stream);
    }

    /// <summary>Decodes .at9 file bytes to 16-bit PCM WAV bytes.</summary>
    public static byte[] DecodeToWav(byte[] at9File)
    {
        using MemoryStream stream = new MemoryStream(at9File);
        return DecodeToWav(stream);
    }

    /// <summary>Decodes an .at9 stream to 16-bit PCM WAV bytes.</summary>
    public static byte[] DecodeToWav(Stream stream)
    {
        At9File at9 = ReadFile(stream);
        short[][] channels = Decode(at9);
        return WriteWave(channels, at9.Config.SampleRate, at9.Looping, at9.LoopStart, at9.LoopEnd);
    }

    /// <summary>Parses the .at9 container without decoding (port of At9Reader.ReadFile).</summary>
    public static At9File ReadFile(Stream stream)
    {
        At9File at9Structure = new At9File();
        RiffParser riffParser = new RiffParser();
        riffParser.ParseRiff(stream);
        ValidateAt9File(riffParser);
        WaveFmtChunk fmt = riffParser.GetSubChunk<WaveFmtChunk>("fmt ");
        At9FactChunk fact = riffParser.GetSubChunk<At9FactChunk>("fact");
        At9DataChunk data = riffParser.GetSubChunk<At9DataChunk>("data");
        WaveSmplChunk smpl = riffParser.GetSubChunk<WaveSmplChunk>("smpl");
        at9Structure.Config = new Atrac9Config(fmt.Ext.ConfigData);
        at9Structure.SampleCount = fact.SampleCount;
        at9Structure.EncoderDelay = fact.EncoderDelaySamples;
        at9Structure.Version = fmt.Ext.VersionInfo;
        at9Structure.AudioData = data.AudioData;
        at9Structure.SuperframeCount = data.FrameCount;
        if (smpl?.Loops?.FirstOrDefault() != null)
        {
            at9Structure.LoopStart = smpl.Loops[0].Start - at9Structure.EncoderDelay;
            at9Structure.LoopEnd = smpl.Loops[0].End - at9Structure.EncoderDelay;
            at9Structure.Looping = at9Structure.LoopEnd > at9Structure.LoopStart;
        }
        return at9Structure;
    }

    private static void ValidateAt9File(RiffParser parser)
    {
        if (parser.RiffChunk.Type != "WAVE")
        {
            throw new InvalidDataException("Not a valid WAVE file");
        }
        WaveFmtChunk obj = parser.GetSubChunk<WaveFmtChunk>("fmt ") ?? throw new InvalidDataException("File must have a valid fmt chunk");
        if (obj.Ext == null)
        {
            throw new InvalidDataException("File must have a format chunk extension");
        }
        if (parser.GetSubChunk<At9FactChunk>("fact") == null)
        {
            throw new InvalidDataException("File must have a valid fact chunk");
        }
        if (parser.GetSubChunk<At9DataChunk>("data") == null)
        {
            throw new InvalidDataException("File must have a valid data chunk");
        }
        if (obj.ChannelCount == 0)
        {
            throw new InvalidDataException("Channel count must not be zero");
        }
        if (obj.Ext.SubFormat != MediaSubtypeAtrac9)
        {
            throw new InvalidDataException($"Must contain ATRAC9 data. Has unsupported SubFormat {obj.Ext.SubFormat}");
        }
    }

    /// <summary>Port of PS4_Tools.Atrac9Format.Decode + CopyBuffer.</summary>
    private static short[][] Decode(At9File at9)
    {
        ValidateLoopPoints(at9);
        Atrac9Decoder atrac9Decoder = new Atrac9Decoder();
        atrac9Decoder.Initialize(at9.Config.ConfigData);
        Atrac9Config config = atrac9Decoder.Config;
        short[][] array = CreateJaggedArray(config.ChannelCount, at9.SampleCount);
        short[][] array2 = CreateJaggedArray(config.ChannelCount, config.SuperframeSamples);
        for (int i = 0; i < at9.AudioData.Length; i++)
        {
            atrac9Decoder.Decode(at9.AudioData[i], array2);
            CopyBuffer(array2, array, at9.EncoderDelay, i);
        }
        return array;
    }

    /// <summary>Port of AudioFormatBaseBuilder.WithLoop(bool, int, int) validation.</summary>
    private static void ValidateLoopPoints(At9File at9)
    {
        if (!at9.Looping)
        {
            return;
        }
        if (at9.LoopStart < 0 || at9.LoopStart > at9.SampleCount)
        {
            throw new ArgumentOutOfRangeException("loopStart", at9.LoopStart, "Loop points must be less than the number of samples and non-negative.");
        }
        if (at9.LoopEnd < 0 || at9.LoopEnd > at9.SampleCount)
        {
            throw new ArgumentOutOfRangeException("loopEnd", at9.LoopEnd, "Loop points must be less than the number of samples and non-negative.");
        }
        if (at9.LoopEnd < at9.LoopStart)
        {
            throw new ArgumentOutOfRangeException("loopEnd", at9.LoopEnd, "The loop end must be greater than the loop start");
        }
    }

    private static short[][] CreateJaggedArray(int outer, int inner)
    {
        short[][] array = new short[outer][];
        for (int i = 0; i < outer; i++)
        {
            array[i] = new short[inner];
        }
        return array;
    }

    /// <summary>Port of PS4_Tools.Atrac9Format.CopyBuffer.</summary>
    private static void CopyBuffer(short[][] bufferIn, short[][] bufferOut, int startIndex, int bufferIndex)
    {
        if (bufferIn == null || bufferOut == null || bufferIn.Length == 0 || bufferOut.Length == 0)
        {
            throw new ArgumentException("bufferIn and bufferOut must be non-null with a length greater than 0");
        }
        int num = bufferIn[0].Length;
        int num2 = bufferOut[0].Length;
        int num3 = bufferIndex * num - startIndex;
        int val = Math.Min(num2 - num3, num2);
        int num4 = Clamp(-num3, 0, num);
        int destinationIndex = Math.Max(num3, 0);
        int num5 = Math.Min(num - num4, val);
        if (num5 > 0)
        {
            for (int i = 0; i < bufferOut.Length; i++)
            {
                Array.Copy(bufferIn[i], num4, bufferOut[i], destinationIndex, num5);
            }
        }
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }

    /// <summary>Port of PS4_Tools.WaveWriter (Pcm16Bit codec).</summary>
    private static byte[] WriteWave(short[][] channels, int sampleRate, bool looping, int loopStart, int loopEnd)
    {
        int channelCount = channels.Length;
        int sampleCount = (channelCount > 0) ? channels[0].Length : 0;
        int fmtChunkSize = (channelCount <= 2) ? 16 : 40;
        int dataChunkSize = channelCount * sampleCount * 2;
        int smplChunkSize = 60;
        int riffChunkSize = 12 + fmtChunkSize + 8 + dataChunkSize + (looping ? 8 + smplChunkSize : 0);

        using MemoryStream memoryStream = new MemoryStream();
        using (BinaryWriter writer = new BinaryWriter(memoryStream))
        {
            WriteRiffHeader(writer, riffChunkSize);
            WriteFmtChunk(writer, channelCount, sampleRate, fmtChunkSize);
            WriteDataChunk(writer, channels, channelCount, sampleCount);
            if (looping)
            {
                WriteSmplChunk(writer, loopStart, loopEnd);
            }
        }
        return memoryStream.ToArray();
    }

    private static void WriteRiffHeader(BinaryWriter writer, int riffChunkSize)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));
        writer.Write(riffChunkSize);
        writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"));
    }

    private static void WriteFmtChunk(BinaryWriter writer, int channelCount, int sampleRate, int fmtChunkSize)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes("fmt "));
        writer.Write(fmtChunkSize);
        writer.Write((short)((channelCount > 2) ? 0xFFFE : 1));
        writer.Write((short)channelCount);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2 * channelCount);
        writer.Write((short)(2 * channelCount));
        writer.Write((short)16);
        if (channelCount > 2)
        {
            writer.Write((short)22);
            writer.Write((short)16);
            writer.Write(GetChannelMask(channelCount));
            writer.Write(MediaSubtypePcm.ToByteArray());
        }
    }

    private static void WriteDataChunk(BinaryWriter writer, short[][] channels, int channelCount, int sampleCount)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));
        writer.Write(channelCount * sampleCount * 2);
        for (int i = 0; i < sampleCount; i++)
        {
            for (int j = 0; j < channelCount; j++)
            {
                writer.Write(channels[j][i]);
            }
        }
    }

    private static void WriteSmplChunk(BinaryWriter writer, int loopStart, int loopEnd)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes("smpl"));
        writer.Write(60);
        for (int i = 0; i < 7; i++)
        {
            writer.Write(0);
        }
        writer.Write(1);
        for (int j = 0; j < 3; j++)
        {
            writer.Write(0);
        }
        writer.Write(loopStart);
        writer.Write(loopEnd);
        writer.Write(0);
        writer.Write(0);
    }

    private static int GetChannelMask(int channelCount)
    {
        return channelCount switch
        {
            4 => 51,
            5 => 307,
            6 => 1587,
            7 => 499,
            8 => 1779,
            _ => (1 << channelCount) - 1,
        };
    }
}
