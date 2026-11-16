using System.Text;
using EPTradeBot.Core.Streaming.Feeds;
using Xunit;

namespace EPTradeBot.Contract.Tests;

public sealed class GrowwNkeysVectorTests
{
    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "UA5WUJ54Z23KILLCUOUNAKTPBVZWKMQVO4O6EQ5GHLAERIMLLHNCTYM5", "JPpewWZmmHWCmq/R3C0j29Zfd1kTsNw+q3x8UiC4AnxiIg3IDw50t9ofGJkF4bryiG4xMvSDSefkcr2NHambDA==")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "UB3KCWJAISTOJ5IREZN4U45GATMQWBJJ2HPWAK7DBIM2SJLWMDI7LRYD", "sWaXyWvkuMHrbH5wQ4n/aVJpWoFlcru8sqtUdgHoiZqjv6Jh2oZwae5D2f8V0R4emccO2fJe7LXo0iNMT/RqDg==")]
    [InlineData("c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7", "UAZLKPUIFY62VQMA26S7MISNMG3EAG2D7EA5WCPQ4TMEZZHEUJYYWVP3", "1NhkG6ZTqzWZyWfIUlD+0UnA4b1Y/WVKeR3qDA1iLqyIRYGj9x4vUBSOG3p60LoXaMc3GK5WIvpblujDsTmHAw==")]
    [InlineData("0707070707070707070707070707070707070707070707070707070707070707", "UDVEU3DD4KOFECV66VIHWEZOYX4ZKR3WV27L464SIIPOU2IUI3JCYSJF", "y+KsmiuewC+qz8PNu40JlC3Nc4/XuVxaClkr2UcvjnrN5tAXPB6bhnWOWIy4NFTK1SYWY777vgDxhEf9IrVDBQ==")]
    [InlineData("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20", "UB43KVROR7TFJ6KAPCYRF2FJROTZAH4FHLTJLPWX4DRZCC5NASLGJBFE", "VceuTaMJwoteDN26ietOW4SeK6qmFXEhnJFOP2LDgQ/DNIpUtcveua9EaCNqyrTZoCqB2Ih2TDU60bFZ2Yr3DQ==")]
    [InlineData("0b30557a9fc4e90e33587da2c7ec11365b80a5caef14395e83a8cdf2173c6186", "UASMHJEU7IRJSYTGIS4WLKITFWTZZ7THCUP5YQW6MAGPBEEX3OBEPN24", "URuiEfrLMgEx7yLHDPfWEzFL7lROkGw8DUyliMjcdRcUIG6Ht2I0YhY+mOiICUR0aoMh37+50gzA76mSZ7DkAw==")]
    [InlineData("0560bb1671cc2782dd3893ee49a4ff5ab5106bc6217cd7328de8439ef954af0a", "UBOQP7HETCL2344P3FAS6YADWMVSU74AUHKDXZJGF6BNOCOA4N2N5ENE", "EpjJNzuEpFXsRTWrWicBDkJ0oeax1QnO3PUBcYPR7Hr4aAeHlnJXwYZrQe9K6+vPuAvqOfO711k2Vmy4g5eABg==")]
    public void The_public_key_and_the_nonce_signature_are_the_ones_the_python_client_makes(string seedHex, string publicKey, string signature)
    {
        var seed = Convert.FromHexString(seedHex);
        Assert.Equal(publicKey, GrowwNkeys.EncodePublicKey(seed));
        var message = Encoding.ASCII.GetBytes("nonce-").Concat(seed.Take(4)).ToArray();
        Assert.Equal(signature, Convert.ToBase64String(GrowwNkeys.Sign(seed, message)));
    }
}
