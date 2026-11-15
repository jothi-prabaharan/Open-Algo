using System.Numerics;
using System.Security.Cryptography;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>
/// NATS user keys as Groww's feed uses them (broker/groww/streaming/groww_nkeys.py): a 32-byte seed, Ed25519 (RFC 8032) signatures, and the seed and the public key written in the nkeys text form (a prefix, a CRC16 and base32 without padding). The signing is done
/// here, with big integers, because the platform's cryptography has no Ed25519; it is not constant-time, which does not matter for a key made for one connection and thrown away.
/// </summary>
public static class GrowwNkeys
{
    private const int PrefixSeed = 18 << 3;
    private const int PrefixUser = 20 << 3;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) BasePoint = Base();

    private static BigInteger Mod(BigInteger value) => ((value % P) + P) % P;

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Base()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0)!.Value;
        return (x, y, 1, Mod(x * y));
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        var xx = Mod((y * y - 1) * Inverse(D * y * y + 1));
        var x = BigInteger.ModPow(xx, (P + 3) / 8, P);
        if (Mod(x * x - xx) != 0) x = Mod(x * SqrtMinusOne);
        if (Mod(x * x - xx) != 0) return null;
        if ((int)(x & 1) != sign) x = Mod(-x);
        return x;
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Add((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var (e, f, g, h) = (b - a, d - c, d + c, b + a);
        return (Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Multiply(BigInteger scalar, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) point)
    {
        var result = (X: BigInteger.Zero, Y: BigInteger.One, Z: BigInteger.One, T: BigInteger.Zero);
        var addend = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static byte[] Encode((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) point)
    {
        var zInverse = Inverse(point.Z);
        var x = Mod(point.X * zInverse);
        var y = Mod(point.Y * zInverse);
        var bytes = new byte[32];
        var raw = (y | ((x & 1) << 255)).ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(raw, bytes, Math.Min(raw.Length, 32));
        return bytes;
    }

    private static BigInteger Little(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static (BigInteger Scalar, byte[] Prefix) Expand(byte[] seed)
    {
        var h = SHA512.HashData(seed);
        var a = h.AsSpan(0, 32).ToArray();
        a[0] &= 248;
        a[31] &= 127;
        a[31] |= 64;
        return (Little(a), h[32..]);
    }

    /// <summary>The raw 32-byte public key of a seed.</summary>
    public static byte[] PublicKey(byte[] seed) => Encode(Multiply(Expand(seed).Scalar, BasePoint));

    /// <summary>The Ed25519 signature (64 bytes) of a message with a seed (RFC 8032).</summary>
    public static byte[] Sign(byte[] seed, byte[] message)
    {
        var (a, prefix) = Expand(seed);
        var publicKey = Encode(Multiply(a, BasePoint));
        var r = Little(SHA512.HashData([.. prefix, .. message])) % L;
        var rPoint = Encode(Multiply(r, BasePoint));
        var k = Little(SHA512.HashData([.. rPoint, .. publicKey, .. message])) % L;
        var s = (r + k * a) % L;
        var sBytes = new byte[32];
        var raw = s.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(raw, sBytes, Math.Min(raw.Length, 32));
        return [.. rPoint, .. sBytes];
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }

        return crc;
    }

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            var value = alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) throw new FormatException("not base32");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 255));
                bits -= 8;
            }
        }

        return [.. output];
    }

    /// <summary>A seed in the nkeys text form: S, then the user prefix, the 32 bytes and a CRC16, in base32.</summary>
    public static string EncodeSeed(byte[] seed)
    {
        if (seed.Length != 32) throw new ArgumentException("Seed must be 32 bytes");
        byte[] header = [(byte)(PrefixSeed | (PrefixUser >> 5)), (byte)((31 & PrefixUser) << 3)];
        var body = (byte[])[.. header, .. seed];
        var crc = Crc16(body);
        return Base32([.. body, (byte)(crc & 0xff), (byte)(crc >> 8)]);
    }

    /// <summary>The 32 bytes of a seed in the nkeys text form; a seed that is not one is refused.</summary>
    public static byte[] DecodeSeed(string encoded)
    {
        var raw = FromBase32(encoded);
        if (raw.Length < 36) throw new FormatException("Invalid seed length");
        var body = raw[..^2];
        if ((raw[0] & 248) != PrefixSeed) throw new FormatException("Invalid seed prefix");
        return body[2..];
    }

    /// <summary>The public key in the nkeys text form: the user prefix, the 32 bytes and a CRC16, in base32.</summary>
    public static string EncodePublicKey(byte[] seed)
    {
        byte[] body = [(byte)PrefixUser, .. PublicKey(seed)];
        var crc = Crc16(body);
        return Base32([.. body, (byte)(crc & 0xff), (byte)(crc >> 8)]);
    }
}
