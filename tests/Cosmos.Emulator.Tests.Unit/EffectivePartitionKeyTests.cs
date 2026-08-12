using Cosmos.Emulator.Core.Helpers;
using Shouldly;

namespace Cosmos.Emulator.Tests.Unit;

/// <summary>
/// Golden vectors captured from the Cosmos SDK's own implementation
/// (Microsoft.Azure.Cosmos.Direct — ThinClientTransportSerializer.GetEffectivePartitionKeyHash,
/// SDK 3.59.0-preview.0). The emulator must agree bit-for-bit: the SDK hashes a
/// prefix partition key client-side and sends only the resulting EPK range, so any
/// divergence silently routes prefix-scoped reads to the wrong rows.
/// </summary>
public class EffectivePartitionKeyTests
{
    [Theory]
    // Strings
    [InlineData("[\"t1\"]", "3D48BB14DA9D090D22112C96695028B6")]
    [InlineData("[\"t2\"]", "0B0359C653EC76879A1A108D8D37B40C")]
    [InlineData("[\"t10\"]", "26F9715D00B5EF6DAC84EE93DA7FE88B")]
    [InlineData("[\"\"]", "32E9366E637A71B4E710384B2F4970A0")]
    [InlineData("[\"ünïcode\"]", "2F659D3306868DADA79E432F48A53236")]
    // Numbers
    [InlineData("[1]", "20CD98B339BA78A5D0CF6953B87070B0")]
    [InlineData("[12]", "19FD5D8D0EE06FCB7FA8BDD69193DFC3")]
    [InlineData("[0]", "155B95BEDAC4B1E9EC1CDC9BB0DDDE58")]
    [InlineData("[1.5]", "35C5DDEB6C795D16A9963C73C54E97BC")]
    [InlineData("[-3]", "30478B3D0E0AD5DBC95C665464636106")]
    // Literals, and the '{}' the extractor writes for an absent path
    [InlineData("[true]", "0E711127C5B5A8E4726AC6DD306A3E59")]
    [InlineData("[false]", "2FE1BE91E90A3439635E0E9E37361EF2")]
    [InlineData("[null]", "378867E4430E67857ACE5C908374FE16")]
    [InlineData("[{}]", "11622DAA78F835834610ABE56EFF5CB5")]
    public void Compute_ShouldMatchSdkHash(string partitionKey, string expected)
    {
        EffectivePartitionKey.Compute(partitionKey).ShouldBe(expected);
    }

    [Theory]
    [InlineData("[\"t1\",\"u1\"]",
        "3D48BB14DA9D090D22112C96695028B62A947C94F6E254B945CEBC3196C9897C")]
    [InlineData("[\"t1\",\"u2\"]",
        "3D48BB14DA9D090D22112C96695028B60D9BEA828BEFF90C9273C04B6C3A8002")]
    [InlineData("[\"t1\",{}]",
        "3D48BB14DA9D090D22112C96695028B611622DAA78F835834610ABE56EFF5CB5")]
    [InlineData("[\"t1\",\"u1\",\"s1\"]",
        "3D48BB14DA9D090D22112C96695028B62A947C94F6E254B945CEBC3196C9897C2924A879AFFF52A411F93F1A83532751")]
    public void Compute_MultipleComponents_ShouldConcatenatePerComponentHashes(
        string partitionKey, string expected)
    {
        EffectivePartitionKey.Compute(partitionKey).ShouldBe(expected);
    }

    [Fact]
    public void Compute_ShouldMakePrefixKeyAPrefixOfFullKey()
    {
        // This property is what makes prefix routing a contiguous range scan.
        var prefix = EffectivePartitionKey.Compute("[\"t1\"]");
        var full = EffectivePartitionKey.Compute("[\"t1\",\"u1\"]");

        full.ShouldStartWith(prefix);
    }

    [Fact]
    public void Compute_ShouldClearTopTwoBits_SoEveryKeySortsBelowMax()
    {
        foreach (var key in new[] { "[\"t1\"]", "[\"t2\"]", "[12]", "[true]", "[null]" })
        {
            var epk = EffectivePartitionKey.Compute(key);
            string.CompareOrdinal(epk, EffectivePartitionKey.MaxExclusive).ShouldBeLessThan(0);
        }
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "FF", true)]
    [InlineData("", null, true)]
    [InlineData("3D48BB14DA9D090D22112C96695028B6", "3D48BB14DA9D090D22112C96695028B6FF", false)]
    public void IsFullRange_ShouldRecogniseTheUnboundedRange(string? start, string? end, bool expected)
    {
        EffectivePartitionKey.IsFullRange(start, end).ShouldBe(expected);
    }

    [Fact]
    public void Compute_ShouldRejectNonArray()
    {
        Should.Throw<ArgumentException>(() => EffectivePartitionKey.Compute("\"t1\""));
    }
}
