using System.Globalization;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Core.Tests.Cheats;

public sealed class NumericValueTests
{
    [Fact]
    public void Int64RoundTripsWithoutDoublePrecisionLoss()
    {
        Assert.True(NumericValue.TryParse(long.MaxValue.ToString(CultureInfo.InvariantCulture), NumericKind.Int64, out var bytes));
        Assert.Equal(8, bytes.Length);
        Assert.Equal(0, NumericValue.Compare(bytes, BitConverter.GetBytes(long.MaxValue), NumericKind.Int64));
        Assert.False(NumericValue.TryParse("9223372036854775808", NumericKind.Int64, out _));
    }

    [Fact]
    public void RejectsNonFiniteFloatingPointNumbers()
    {
        Assert.False(NumericValue.TryParse("NaN", NumericKind.Float32, out _));
        Assert.False(NumericValue.TryParse("Infinity", NumericKind.Float64, out _));
        Assert.True(NumericValue.TryParse("1.25", NumericKind.Float32, out var bytes));
        Assert.Equal(0, NumericValue.Compare(bytes, BitConverter.GetBytes(1.25f), NumericKind.Float32));
    }

    [Fact]
    public void AcceptsCurrentCultureDecimalSeparator()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.True(NumericValue.TryParse("1,5", NumericKind.Float64, out var bytes));
            Assert.Equal(0, NumericValue.Compare(bytes, BitConverter.GetBytes(1.5d), NumericKind.Float64));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void ComparesIntegerAndFloatingValuesByTheirOwnTypes()
    {
        Assert.Equal(1, NumericValue.Compare(BitConverter.GetBytes(long.MaxValue), BitConverter.GetBytes(long.MaxValue - 1), NumericKind.Int64));
        Assert.Equal(-1, NumericValue.Compare(BitConverter.GetBytes(-0.5f), BitConverter.GetBytes(0.5f), NumericKind.Float32));
        Assert.Throws<ArgumentException>(() => NumericValue.Compare(new byte[2], new byte[4], NumericKind.Int32));
    }
}
