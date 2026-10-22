using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// <see cref="UnityMemberReader"/> 的离线回归测试。
///
/// 守的是**跨 Unity 版本**这一类崩溃：Unity 会改自己公开成员的类型
/// （`FaceInfo.pointSize` 在 2021/2022 是 int、6000 起是 float），
/// 代码里一旦写强类型访问，换版本运行就抛
/// <c>MissingMethodException: Method not found: single FaceInfo.get_pointSize()</c>。
/// 实测后果是 Mad Island 上字体装配 100% 失败。
/// </summary>
public sealed class UnityMemberReaderTests
{
    /// <summary>模拟 Unity 2021/2022 的 FaceInfo：pointSize 是 int。</summary>
    private struct LegacyFaceInfo
    {
        public int pointSize;
        public string familyName;
    }

    /// <summary>模拟 Unity 6000 的 FaceInfo：pointSize 是 float，且是属性。</summary>
    private struct ModernFaceInfo
    {
        private readonly float _size;
        public ModernFaceInfo(float size) => _size = size;
        public float pointSize => _size;
        public string familyName => "Microsoft YaHei";
    }

    /// <summary>
    /// 回归保护：同一个读取器必须**同时**能读 int 版与 float 版，
    /// 并且在两种版本上都给出同一个 float 结果。
    /// </summary>
    [Fact]
    public void ReadsPointSizeFromBothTheIntVersionAndTheFloatVersion()
    {
        var legacy = new LegacyFaceInfo { pointSize = 36, familyName = "SimHei" };
        var modern = new ModernFaceInfo(36f);

        Assert.Equal(36f, UnityMemberReader.Read<float>(legacy, "pointSize"));
        Assert.Equal(36f, UnityMemberReader.Read<float>(modern, "pointSize"));
    }

    /// <summary>
    /// 属性优先于字段：派生类用属性遮蔽基类同名字段时，读到的应该是属性的值。
    /// （Unity 的公开 API 大多是属性，而内部字段是同名的 m_Xxx，这里只是把优先级钉死。）
    /// </summary>
    [Fact]
    public void PrefersAPropertyOverAFieldOfTheSameName()
    {
        var target = new ShadowingHolder();

        Assert.Equal(20, UnityMemberReader.Read<int>(target, "size"));
    }

    private class FieldBase
    {
        public int size = 10;
    }

    private sealed class ShadowingHolder : FieldBase
    {
        public new int size => 20;
    }

    [Fact]
    public void ReadsFamilyNameAsString()
    {
        Assert.Equal("SimHei", UnityMemberReader.ReadString(new LegacyFaceInfo { familyName = "SimHei" }, "familyName"));
        Assert.Equal("Microsoft YaHei", UnityMemberReader.ReadString(new ModernFaceInfo(36f), "familyName"));
    }

    /// <summary>
    /// 成员不存在时必须返回 null，让调用方明确失败 —— 不能悄悄给个默认值，
    /// 那正是"看起来装配成功实际没生效"的来源。
    /// </summary>
    [Fact]
    public void ReturnsNullForMissingMembersInsteadOfGuessing()
    {
        var legacy = new LegacyFaceInfo { pointSize = 36 };

        Assert.Null(UnityMemberReader.Read<float>(legacy, "noSuchMember"));
        Assert.Null(UnityMemberReader.ReadString(legacy, "noSuchMember"));
        Assert.Null(UnityMemberReader.ReadRaw(legacy, "noSuchMember"));
    }

    [Fact]
    public void HandlesNullAndNonObjectTargetsWithoutThrowing()
    {
        Assert.Null(UnityMemberReader.Read<float>(null, "pointSize"));
        Assert.Null(UnityMemberReader.ReadString(null, "familyName"));
        Assert.Null(UnityMemberReader.ReadRaw(null, "pointSize"));
        // 字符串也是对象，但不该被当成成员宿主
        Assert.Null(UnityMemberReader.ReadRaw("不是宿主", "Length"));
        Assert.Null(UnityMemberReader.ReadRaw(new LegacyFaceInfo(), string.Empty));
    }

    /// <summary>类型不兼容的成员（例如拿 float 读一个字符串）要给 null，不能抛。</summary>
    [Fact]
    public void ReturnsNullWhenTheValueCannotBeConverted()
    {
        Assert.Null(UnityMemberReader.Read<float>(new { name = "文字" }, "name"));
        Assert.Null(UnityMemberReader.Read<int>(new { value = "不是数字" }, "value"));
    }

    [Fact]
    public void ReadsThroughPropertiesAndFieldsEqually()
    {
        Assert.Equal(7, UnityMemberReader.Read<int>(new { value = 7 }, "value"));
        Assert.Equal(9, UnityMemberReader.Read<int>(new FieldHolder { value = 9 }, "value"));
    }

    [Fact]
    public void DetectsWhetherAMemberExists()
    {
        var legacy = new LegacyFaceInfo { pointSize = 36 };

        Assert.True(UnityMemberReader.HasMember(legacy, "pointSize"));
        Assert.True(UnityMemberReader.HasMember(new ModernFaceInfo(36f), "pointSize"));
        Assert.False(UnityMemberReader.HasMember(legacy, "unitsPerEM"));
        Assert.False(UnityMemberReader.HasMember((object?)null, "pointSize"));
    }

    private sealed class FieldHolder
    {
        public int value;
    }
}
