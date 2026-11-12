namespace EPTradeBot.Core.Portfolio;

/// <summary>
/// numpy's default_rng(seed) for an integer seed: SeedSequence expansion into a PCG64 (XSL-RR 128/64) and Generator.integers for a range under 2^32 (Lemire's bounded method
/// on the buffered 32-bit halves). It exists so the Monte Carlo draws the same blocks OpenAlgo does for the same seed.
/// </summary>
public sealed class NumpyRandom
{
    private const uint InitA = 0x43b0d7e5, MultA = 0x931e8875, InitB = 0x8b51f9dd, MultB = 0x58f38ded, MixMultL = 0xca01f9dd, MixMultR = 0x4973f715;
    private static readonly UInt128 Multiplier = ((UInt128)0x2360ED051FC65DA4 << 64) | 0x4385DF649FCCF645UL;

    private UInt128 _state;
    private readonly UInt128 _inc;
    private bool _hasUint32;
    private uint _buffered;

    public NumpyRandom(uint seed)
    {
        var words = GenerateState(seed);
        var initState = ((UInt128)words[0] << 64) | words[1];
        var initSeq = ((UInt128)words[2] << 64) | words[3];
        _inc = (initSeq << 1) | 1;
        _state = 0;
        Step();
        _state += initState;
        Step();
    }

    private static uint HashMix(uint value, ref uint hashConst)
    {
        value ^= hashConst;
        hashConst *= MultA;
        value *= hashConst;
        return value ^ (value >> 16);
    }

    private static uint Mix(uint x, uint y)
    {
        var result = unchecked(MixMultL * x - MixMultR * y);
        return result ^ (result >> 16);
    }

    private static ulong[] GenerateState(uint seed)
    {
        var pool = new uint[4];
        var hashConst = InitA;
        uint[] entropy = [seed, 0, 0, 0];
        for (var i = 0; i < 4; i++) pool[i] = HashMix(entropy[i], ref hashConst);
        for (var src = 0; src < 4; src++)
            for (var dst = 0; dst < 4; dst++)
                if (src != dst) pool[dst] = Mix(pool[dst], HashMix(pool[src], ref hashConst));

        var state32 = new uint[8];
        var h = InitB;
        for (var i = 0; i < 8; i++)
        {
            var data = pool[i % 4];
            data ^= h;
            h *= MultB;
            data *= h;
            state32[i] = data ^ (data >> 16);
        }

        return [.. Enumerable.Range(0, 4).Select(i => state32[2 * i] | ((ulong)state32[2 * i + 1] << 32))];
    }

    private void Step() => _state = unchecked(_state * Multiplier + _inc);

    public ulong NextUInt64()
    {
        Step();
        var hi = (ulong)(_state >> 64);
        var lo = (ulong)_state;
        var rot = (int)(_state >> 122);
        var x = hi ^ lo;
        return (x >> rot) | (x << ((-rot) & 63));
    }

    private uint NextUInt32()
    {
        if (_hasUint32)
        {
            _hasUint32 = false;
            return _buffered;
        }

        var next = NextUInt64();
        _hasUint32 = true;
        _buffered = (uint)(next >> 32);
        return (uint)(next & 0xFFFFFFFF);
    }

    /// <summary>Integers in [low, high), as Generator.integers(low, high) for a span under 2^32.</summary>
    public long Integer(long low, long high)
    {
        var rng = (uint)(high - 1 - low);
        if (rng == 0) return low;
        if (rng == 0xFFFFFFFF) return low + NextUInt32();
        var exclusive = (ulong)rng + 1;
        var m = (ulong)NextUInt32() * exclusive;
        var leftover = (uint)(m & 0xFFFFFFFF);
        if (leftover < exclusive)
        {
            var threshold = (uint)((0xFFFFFFFFUL - rng) % exclusive);
            while (leftover < threshold)
            {
                m = (ulong)NextUInt32() * exclusive;
                leftover = (uint)(m & 0xFFFFFFFF);
            }
        }

        return low + (long)(m >> 32);
    }
}
