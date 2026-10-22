using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Core.Tests.Cheats;

public sealed class CandidateRankerTests
{
    [Fact]
    public void MoneyNameAndMatchingDisplayedValueRaiseCandidate()
    {
        var money = CandidateRanker.Rank(new FieldCandidate("Player", "Stats", "gold", NumericKind.Int32, "50"), "50");
        var unrelated = CandidateRanker.Rank(new FieldCandidate("Player", "Stats", "frame", NumericKind.Int32, "50"), "50");
        Assert.True(money.Score > unrelated.Score);
        Assert.Contains(money.Reasons, reason => reason.Contains("gold", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(money.Reasons, reason => reason.Contains("50", StringComparison.Ordinal));
    }

    [Fact]
    public void MismatchedVisibleValueDoesNotGetMatchCredit()
    {
        var field = new FieldCandidate("Player", "Health", "hp", NumericKind.Int32, "25");
        var matching = CandidateRanker.Rank(field, "25");
        var mismatched = CandidateRanker.Rank(field, "100");
        Assert.True(matching.Score > mismatched.Score);
    }

    [Fact]
    public void FieldNameTakesPriorityOverEarlierObjectNameKeyword()
    {
        var field = new FieldCandidate("GoldPanel", "CashDisplay", "experience", NumericKind.Int32, "10");
        var ranked = CandidateRanker.Rank(field, null);
        Assert.Contains(ranked.Reasons, reason => reason.Contains("字段名命中 exp", StringComparison.Ordinal));
        Assert.Equal(100, ranked.Score);
    }
}
