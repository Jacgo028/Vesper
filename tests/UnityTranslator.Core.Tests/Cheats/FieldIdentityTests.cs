using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Core.Tests.Cheats;

/// <summary>
/// Unity 字段识别的纯规则测试：唯一命中才能写，歧义、只读、静态、类型变化、数值不符都必须拒绝。
/// UnityEngine 类型只有游戏里才有，所以这里验证不依赖引擎的同一套规则。
/// </summary>
public sealed class FieldIdentityTests
{
    private static FieldSnapshot Field(string objectPath, string componentType, string fieldName,
        NumericKind kind, string displayValue) =>
        new FieldSnapshot(objectPath + "|" + componentType + "|" + fieldName, objectPath, componentType, fieldName,
            kind, displayValue);

    private static CheatFieldWriteTarget Target(string objectPath, string componentType, string fieldName,
        NumericKind kind = NumericKind.Int32) =>
        new CheatFieldWriteTarget(objectPath, componentType, fieldName, kind);

    [Fact]
    public void SingleMatchingFieldResolvesToUnique()
    {
        var fields = new[]
        {
            Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120"),
            Field("Main/Player", "PlayerStats", "level", NumericKind.Int32, "7")
        };

        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        Assert.Equal(FieldResolutionStatus.Unique, resolution.Status);
        Assert.Equal("gold", resolution.Field!.FieldName);
    }

    [Fact]
    public void DuplicateObjectPathsAreAmbiguousAndNeverGuessed()
    {
        // 两个不同对象被折叠成同一层级路径时，字段名与类型完全相同。
        var fields = new[]
        {
            Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120"),
            Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "999")
        };

        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        Assert.Equal(FieldResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Field);
        Assert.Contains("无法确定", resolution.Reason, StringComparison.Ordinal);
        Assert.False(resolution.IsUnique);
    }

    [Fact]
    public void MissingFieldResolvesToNotFound()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "level", NumericKind.Int32, "7") };

        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        Assert.Equal(FieldResolutionStatus.NotFound, resolution.Status);
        Assert.Null(resolution.Field);
    }

    [Fact]
    public void ChangedFieldTypeResolvesToInvalid()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "gold", NumericKind.Int64, "120") };

        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold", NumericKind.Int32));

        Assert.Equal(FieldResolutionStatus.Invalid, resolution.Status);
        Assert.Contains("类型", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPathOrComponentTypeWidensInsteadOfFailing()
    {
        // 预设允许只按组件类型+字段名重新定位，因为实例路径可能变化。
        var fields = new[] { Field("", "GlobalState", "money", NumericKind.Int64, "50") };

        var resolution = FieldWriteGuard.Resolve(fields, Target(string.Empty, "GlobalState", "money", NumericKind.Int64));

        Assert.Equal(FieldResolutionStatus.Unique, resolution.Status);
    }

    [Theory]
    [InlineData(true, false, false, true, false)]   // 静态字段
    [InlineData(false, true, false, true, false)]   // 只读字段
    [InlineData(false, false, true, true, false)]   // 属性
    [InlineData(false, false, false, false, false)]  // 当前值与确认值不同
    [InlineData(false, false, false, true, true)]   // 唯一允许的组合
    public void WritePermissionsFollowTheDocumentedRules(bool isStatic, bool isReadonly, bool isProperty,
        bool matchesExpected, bool expected)
    {
        Assert.Equal(expected, FieldWriteGuard.CanWrite(isStatic, isReadonly, isProperty, matchesExpected));
    }

    [Fact]
    public void MismatchedExpectedValueIsRejectedEvenWhenTheFieldIsUnique()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120") };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        var result = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32,
            expectedText: "50", newText: "9999", isStatic: false, isReadonly: false, isProperty: false);

        Assert.False(result.IsOk);
        Assert.Equal(CheatFieldWriteReplyStatus.NotWritable, result.Status);
        Assert.Contains("120", result.Message, StringComparison.Ordinal);
        Assert.Null(result.NewBytes);
    }

    [Fact]
    public void MatchingExpectedValueProducesTheEncodedReplacement()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120") };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        var result = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32,
            expectedText: "120", newText: "9999", isStatic: false, isReadonly: false, isProperty: false);

        Assert.True(result.IsOk);
        Assert.Equal(CheatFieldWriteReplyStatus.Ok, result.Status);
        Assert.NotNull(result.NewBytes);
        Assert.Equal(9999, BitConverter.ToInt32(result.NewBytes!, 0));
        Assert.Equal("gold", result.Field!.FieldName);
    }

    [Fact]
    public void StaticAndReadonlyFieldsAreRejectedByTheFullEvaluation()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120") };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        var byStatic = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32, "120", "5",
            isStatic: true, isReadonly: false, isProperty: false);
        var byReadonly = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32, "120", "5",
            isStatic: false, isReadonly: true, isProperty: false);
        var byProperty = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32, "120", "5",
            isStatic: false, isReadonly: false, isProperty: true);

        Assert.Equal(CheatFieldWriteReplyStatus.NotWritable, byStatic.Status);
        Assert.Equal(CheatFieldWriteReplyStatus.NotWritable, byReadonly.Status);
        Assert.Equal(CheatFieldWriteReplyStatus.NotWritable, byProperty.Status);
        Assert.All(new[] { byStatic, byReadonly, byProperty }, result => Assert.False(result.IsOk));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    [InlineData("abc")]
    public void UnsupportedNewValuesAreRejectedWithoutTouchingTheField(string newText)
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "hp", NumericKind.Float32, "10") };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "hp", NumericKind.Float32));

        var result = FieldWriteGuard.Evaluate(resolution, NumericKind.Float32, "10", newText,
            isStatic: false, isReadonly: false, isProperty: false);

        Assert.False(result.IsOk);
        Assert.Equal(CheatFieldWriteReplyStatus.Unsupported, result.Status);
    }

    [Fact]
    public void FieldWithUnparsableCurrentValueIsRejected()
    {
        var fields = new[] { Field("Main/Player", "PlayerStats", "hp", NumericKind.Float32, "NaN") };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "hp", NumericKind.Float32));

        var result = FieldWriteGuard.Evaluate(resolution, NumericKind.Float32, "NaN", "5",
            isStatic: false, isReadonly: false, isProperty: false);

        Assert.False(result.IsOk);
        Assert.Equal(CheatFieldWriteReplyStatus.Unsupported, result.Status);
    }

    [Fact]
    public void AmbiguousResolutionCannotReachAWrite()
    {
        var fields = new[]
        {
            Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120"),
            Field("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120")
        };
        var resolution = FieldWriteGuard.Resolve(fields, Target("Main/Player", "PlayerStats", "gold"));

        var result = FieldWriteGuard.Evaluate(resolution, NumericKind.Int32, "120", "5",
            isStatic: false, isReadonly: false, isProperty: false);

        Assert.Equal(CheatFieldWriteReplyStatus.Ambiguous, result.Status);
        Assert.Null(result.NewBytes);
    }
}
