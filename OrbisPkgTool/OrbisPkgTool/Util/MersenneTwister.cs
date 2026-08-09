namespace OrbisPkgTool.Util;

/// <summary>
/// MT19937 Mersenne Twister with array seeding — used by the PS4 PKG
/// RSA padding generator (Sony's RSA2048EncryptKey scheme).
/// </summary>
public sealed class MersenneTwister
{
    public const int N = 624;
    private const uint M = 397;
    private const uint DefaultSeed = 0x12BD6AA;
    private const uint MatrixA = 0x9908b0df;
    private const uint UpperMask = 0x80000000;
    private const uint LowerMask = 0x7fffffff;
    private const uint Constant1 = 0x6C078965;
    private const uint Constant2 = 0x19660D;
    private const uint Constant3 = 0x5D588B65;
    private const uint Constant4 = 0x9d2c5680;
    private const uint Constant5 = 0xefc60000;

    private readonly uint[] _mt = new uint[N];
    private uint _mti;

    private static uint Mask(int val) => ~(~0u << val);

    public MersenneTwister(uint seed = DefaultSeed)
    {
        _mt[0] = seed;
        for (_mti = 1; _mti < N; _mti++)
            _mt[_mti] = _mti + Constant1 * (_mt[_mti - 1] ^ (_mt[_mti - 1] >> 30));
    }

    public MersenneTwister(uint[] seed) : this(DefaultSeed)
    {
        uint stateIdx = 1, seedIdx = 0;
        for (int length = Math.Max(N, seed.Length); length > 0; length--)
        {
            _mt[stateIdx] = (_mt[stateIdx] ^ ((_mt[stateIdx - 1] ^ (_mt[stateIdx - 1] >> 30)) * Constant2)) + seed[seedIdx] + seedIdx;
            stateIdx++;
            seedIdx++;
            if (stateIdx >= N) { _mt[0] = _mt[N - 1]; stateIdx = 1; }
            if (seedIdx >= seed.Length) seedIdx = 0;
        }
        for (int length = 0; length < N - 1; length++)
        {
            _mt[stateIdx] = (_mt[stateIdx] ^ ((_mt[stateIdx - 1] ^ (_mt[stateIdx - 1] >> 30)) * Constant3)) - stateIdx;
            stateIdx++;
            if (stateIdx >= N) { _mt[0] = _mt[N - 1]; stateIdx = 1; }
        }
        _mt[0] = 1u << 31; // MSB is 1; assuring non-zero initial array
    }

    public uint Int32()
    {
        var mag01 = new[] { 0u, MatrixA };
        uint y;
        if (_mti >= N)
        {
            uint kk;
            for (kk = 0; kk < N - M; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + M] ^ ((y >> 1) & Mask(31)) ^ mag01[y & 1];
            }
            for (; kk < N - 1; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + M - N] ^ ((y >> 1) & Mask(31)) ^ mag01[y & 1];
            }
            y = (_mt[N - 1] & UpperMask) | (_mt[0] & LowerMask);
            _mt[N - 1] = _mt[M - 1] ^ ((y >> 1) & Mask(31)) ^ mag01[y & 1];
            _mti = 0;
        }

        y = _mt[_mti++];
        y ^= (y >> 11) & Mask(21);
        y ^= (y << 7) & Constant4;
        y ^= (y << 15) & Constant5;
        y ^= (y >> 18) & Mask(14);
        return y;
    }
}
