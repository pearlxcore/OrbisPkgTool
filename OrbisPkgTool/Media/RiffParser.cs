using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OrbisPkgTool.Media.LibAtrac9;

namespace OrbisPkgTool.Media;

/// <summary>
/// Minimal RIFF/WAVE container parser ported from PS4_Tools.RiffParser, specialized
/// for ATRAC9 .at9 files. Walks top-level sub chunks and stores them by ID.
/// </summary>
internal sealed class RiffParser
{
    public RiffChunk RiffChunk { get; set; }

    private Dictionary<string, RiffSubChunk> SubChunks { get; } = new Dictionary<string, RiffSubChunk>();

    public void ParseRiff(Stream file)
    {
        using BinaryReader binaryReader = new BinaryReader(file);
        RiffChunk = RiffChunk.Parse(binaryReader);
        SubChunks.Clear();
        long num = binaryReader.BaseStream.Position - 4 + RiffChunk.Size;
        while (binaryReader.BaseStream.Position + 8 < num)
        {
            RiffSubChunk riffSubChunk = ParseSubChunk(binaryReader);
            SubChunks[riffSubChunk.SubChunkId] = riffSubChunk;
        }
    }

    public List<RiffSubChunk> GetAllSubChunks()
    {
        return new List<RiffSubChunk>(SubChunks.Values);
    }

    public T GetSubChunk<T>(string id) where T : RiffSubChunk
    {
        SubChunks.TryGetValue(id, out var value);
        return value as T;
    }

    private RiffSubChunk ParseSubChunk(BinaryReader reader)
    {
        string key = ReadFourCc(reader);
        reader.BaseStream.Position -= 4L;
        long num = reader.BaseStream.Position + 8;
        RiffSubChunk riffSubChunk = CreateSubChunk(key, reader);
        long num2 = num + riffSubChunk.SubChunkSize;
        int count = (int)Math.Max(num2 - reader.BaseStream.Position, 0L);
        riffSubChunk.Extra = reader.ReadBytes(count);
        reader.BaseStream.Position = num2 + (num2 & 1);
        return riffSubChunk;
    }

    private RiffSubChunk CreateSubChunk(string id, BinaryReader reader)
    {
        switch (id)
        {
            case "fmt ":
                return new WaveFmtChunk(reader);
            case "smpl":
                return new WaveSmplChunk(reader);
            case "fact":
                return new At9FactChunk(reader);
            case "data":
                return new At9DataChunk(this, reader);
            default:
                return new RiffSubChunk(reader);
        }
    }

    internal static string ReadFourCc(BinaryReader reader)
    {
        byte[] array = reader.ReadBytes(4);
        return Encoding.UTF8.GetString(array);
    }
}

internal class RiffChunk
{
    public string ChunkId { get; set; }

    public int Size { get; set; }

    public string Type { get; set; }

    public static RiffChunk Parse(BinaryReader reader)
    {
        return new RiffChunk
        {
            ChunkId = RiffParser.ReadFourCc(reader),
            Size = reader.ReadInt32(),
            Type = RiffParser.ReadFourCc(reader)
        };
    }
}

internal class RiffSubChunk
{
    public string SubChunkId { get; set; }

    public int SubChunkSize { get; set; }

    public byte[] Extra { get; set; }

    public RiffSubChunk(BinaryReader reader)
    {
        SubChunkId = RiffParser.ReadFourCc(reader);
        SubChunkSize = reader.ReadInt32();
    }
}

internal class WaveFmtChunk : RiffSubChunk
{
    public const int WaveFormatExtensible = 0xFFFE;

    public int FormatTag { get; set; }

    public int ChannelCount { get; set; }

    public int SampleRate { get; set; }

    public int AvgBytesPerSec { get; set; }

    public int BlockAlign { get; set; }

    public int BitsPerSample { get; set; }

    public At9WaveExtensible Ext { get; set; }

    public WaveFmtChunk(BinaryReader reader)
        : base(reader)
    {
        FormatTag = reader.ReadUInt16();
        ChannelCount = reader.ReadInt16();
        SampleRate = reader.ReadInt32();
        AvgBytesPerSec = reader.ReadInt32();
        BlockAlign = reader.ReadInt16();
        BitsPerSample = reader.ReadInt16();
        if (FormatTag == WaveFormatExtensible)
        {
            long num = reader.BaseStream.Position + 2;
            Ext = new At9WaveExtensible(reader);
            int count = (int)Math.Max(num + Ext.Size - reader.BaseStream.Position, 0L);
            Ext.Extra = reader.ReadBytes(count);
        }
    }
}

internal class At9WaveExtensible
{
    public int Size { get; set; }

    public int ValidBitsPerSample { get; set; }

    public uint ChannelMask { get; set; }

    public Guid SubFormat { get; set; }

    public byte[] Extra { get; set; }

    public int VersionInfo { get; set; }

    public byte[] ConfigData { get; set; }

    public int Reserved { get; set; }

    public At9WaveExtensible(BinaryReader reader)
    {
        Size = reader.ReadInt16();
        ValidBitsPerSample = reader.ReadInt16();
        ChannelMask = reader.ReadUInt32();
        SubFormat = new Guid(reader.ReadBytes(16));
        VersionInfo = reader.ReadInt32();
        ConfigData = reader.ReadBytes(4);
        Reserved = reader.ReadInt32();
    }
}

internal class WaveSmplChunk : RiffSubChunk
{
    public int Manufacturer { get; set; }

    public int Product { get; set; }

    public int SamplePeriod { get; set; }

    public int MidiUnityNote { get; set; }

    public int MidiPitchFraction { get; set; }

    public int SmpteFormat { get; set; }

    public int SmpteOffset { get; set; }

    public int SampleLoops { get; set; }

    public int SamplerData { get; set; }

    public SampleLoop[] Loops { get; set; }

    public WaveSmplChunk(BinaryReader reader)
        : base(reader)
    {
        Manufacturer = reader.ReadInt32();
        Product = reader.ReadInt32();
        SamplePeriod = reader.ReadInt32();
        MidiUnityNote = reader.ReadInt32();
        MidiPitchFraction = reader.ReadInt32();
        SmpteFormat = reader.ReadInt32();
        SmpteOffset = reader.ReadInt32();
        SampleLoops = reader.ReadInt32();
        SamplerData = reader.ReadInt32();
        Loops = new SampleLoop[SampleLoops];
        for (int i = 0; i < SampleLoops; i++)
        {
            Loops[i] = new SampleLoop
            {
                CuePointId = reader.ReadInt32(),
                Type = reader.ReadInt32(),
                Start = reader.ReadInt32(),
                End = reader.ReadInt32(),
                Fraction = reader.ReadInt32(),
                PlayCount = reader.ReadInt32()
            };
        }
    }
}

internal class SampleLoop
{
    public int CuePointId { get; set; }

    public int Type { get; set; }

    public int Start { get; set; }

    public int End { get; set; }

    public int Fraction { get; set; }

    public int PlayCount { get; set; }
}

internal class At9FactChunk : RiffSubChunk
{
    public int SampleCount { get; set; }

    public int InputOverlapDelaySamples { get; set; }

    public int EncoderDelaySamples { get; set; }

    public At9FactChunk(BinaryReader reader)
        : base(reader)
    {
        SampleCount = reader.ReadInt32();
        InputOverlapDelaySamples = reader.ReadInt32();
        EncoderDelaySamples = reader.ReadInt32();
    }
}

internal class At9DataChunk : RiffSubChunk
{
    public int FrameCount { get; set; }

    public byte[][] AudioData { get; set; }

    public At9DataChunk(RiffParser parser, BinaryReader reader)
        : base(reader)
    {
        WaveFmtChunk obj = parser.GetSubChunk<WaveFmtChunk>("fmt ") ?? throw new InvalidDataException("fmt chunk must come before data chunk");
        if (obj.Ext == null)
        {
            throw new InvalidDataException("fmt chunk must come before data chunk");
        }
        At9FactChunk subChunk = parser.GetSubChunk<At9FactChunk>("fact");
        if (subChunk == null)
        {
            throw new InvalidDataException("fact chunk must come before data chunk");
        }
        Atrac9Config atrac9Config = new Atrac9Config(obj.Ext.ConfigData);
        FrameCount = DivideByRoundUp(subChunk.SampleCount + subChunk.EncoderDelaySamples, atrac9Config.SuperframeSamples);
        int num = FrameCount * atrac9Config.SuperframeBytes;
        if (num > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Required AT9 length is greater than the number of bytes remaining in the file.");
        }
        AudioData = DeInterleave(reader.BaseStream, num, atrac9Config.SuperframeBytes, FrameCount);
    }

    private static int DivideByRoundUp(int value, int divisor)
    {
        return (value + divisor - 1) / divisor;
    }

    /// <summary>Port of PS4_Tools.Util.HexBinTemp.DeInterleave(Stream, int, int, int, int).</summary>
    private static byte[][] DeInterleave(Stream input, int length, int interleaveSize, int outputCount)
    {
        if (input.CanSeek && input.Length - input.Position < length)
        {
            throw new ArgumentOutOfRangeException("length", length, "Specified length is greater than the number of bytes remaining in the Stream");
        }
        if (length % outputCount != 0)
        {
            throw new ArgumentOutOfRangeException("outputCount", outputCount, $"The input length ({length}) must be divisible by the number of outputs.");
        }
        int num = length / outputCount;
        int outputSize = num;
        int num2 = DivideByRoundUp(num, interleaveSize);
        int num3 = DivideByRoundUp(outputSize, interleaveSize);
        int num4 = num - (num2 - 1) * interleaveSize;
        int num5 = outputSize - (num3 - 1) * interleaveSize;
        int num6 = Math.Min(num2, num3);
        byte[][] array = new byte[outputCount][];
        for (int i = 0; i < outputCount; i++)
        {
            array[i] = new byte[outputSize];
        }
        for (int j = 0; j < num6; j++)
        {
            int num7 = ((j == num2 - 1) ? num4 : interleaveSize);
            int val = ((j == num3 - 1) ? num5 : interleaveSize);
            int num8 = Math.Min(num7, val);
            for (int k = 0; k < outputCount; k++)
            {
                input.Read(array[k], interleaveSize * j, num8);
                if (num8 < num7)
                {
                    input.Position += num7 - num8;
                }
            }
        }
        return array;
    }
}
