namespace OrbisPkgTool.Media.LibAtrac9;

internal class Atrac9Rng
{
    private ushort _stateA;

    private ushort _stateB;

    private ushort _stateC;

    private ushort _stateD;

    public Atrac9Rng(ushort seed)
    {
        int num = 19859 * (seed ^ (seed >> 14));
        _stateA = (ushort)(3 - num);
        _stateB = (ushort)(2 - num);
        _stateC = (ushort)(1 - num);
        _stateD = (ushort)(-num);
    }

    public ushort Next()
    {
        ushort num = (ushort)(_stateD ^ (_stateD << 5));
        _stateD = _stateC;
        _stateC = _stateB;
        _stateB = _stateA;
        _stateA = (ushort)(num ^ _stateA ^ ((num ^ (_stateA >> 5)) >> 4));
        return _stateA;
    }
}
