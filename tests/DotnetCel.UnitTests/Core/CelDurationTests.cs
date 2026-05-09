using DotnetCel.Values;

namespace DotnetCel.UnitTests.Core;

public sealed class CelDurationTests
{
    [Fact]
    public void ToString_Whole_Seconds_Has_No_Fraction()
    {
        Assert.Equal("0s", CelDuration.Zero.ToString());
        Assert.Equal("5s", CelDuration.FromSeconds(5).ToString());
        Assert.Equal("-3s", new CelDuration(-3 * CelDuration.NanosPerSecond).ToString());
    }

    [Fact]
    public void ToString_Trims_Trailing_Zeros_From_Fraction()
    {
        Assert.Equal("1.5s", new CelDuration(1_500_000_000).ToString());
        Assert.Equal("0.000000001s", new CelDuration(1).ToString());
        Assert.Equal("0.123456789s", new CelDuration(123_456_789).ToString());
    }

    [Fact]
    public void Roundtrip_Through_TimeSpan_Loses_Sub_100ns_Precision()
    {
        // Document the known precision-loss path so future changes don't regress silently.
        var ns1 = new CelDuration(1);
        Assert.Equal(TimeSpan.Zero, ns1.ToTimeSpan());

        var us1 = new CelDuration(1_000);
        Assert.Equal(CelDuration.FromTimeSpan(us1.ToTimeSpan()), us1);
    }
}
