using UnityTranslator.Core;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 字体载入重载选择。**这是为了让"装配成功但显示方框"这类隐蔽故障变得可测。**
///
/// 背景（实测踩过的坑）：`FontEngine.LoadFontFace` 同时有 (string,int,int) 与 (byte[],int,int)
/// 两个参数个数相同的重载。按"参数个数"挑会挑到 string 版，
/// 调用抛 ArgumentException（Byte[] 不能转 String），字体根本没载入，
/// 但后续 SetFaceSize / GetFaceInfo 仍然成功，引擎里留着的是游戏自己的字体 ——
/// 表现就是"装配日志一切正常、游戏里却显示一串方框"。
/// 诊断探针当时报出 `family=Noto Sans JP`（而我们要载入的是 msyh.ttc）才定位到。
/// </summary>
public sealed class LoadFaceOverloadSelectorTests
{
    private static LoadFaceOverloadSelector.Signature Sig(string parameters) =>
        new("LoadFontFace", parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Unity 2022.3 实测签名集合（含 string 与 byte[] 两族）。</summary>
    private static readonly string[] Unity2022Signatures =
    {
        "String", "Byte[]", "Font",
        "String,Int32", "Byte[],Int32", "Font,Int32",
        "String,String",
        "String,Single,Int32", "Byte[],Single,Int32", "Font,Single,Int32",
        "String,String,Single"
    };

    /// <summary>Unity 2021 实测签名集合。</summary>
    private static readonly string[] Unity2021Signatures =
    {
        "String", "Byte[]", "Font",
        "String,Int32", "Byte[],Int32", "Font,Int32",
        "String,String", "String,String,Int32"
    };

    [Fact]
    public void PicksByteArrayOverloadNotStringOverload()
    {
        var candidates = Unity2022Signatures.Select(Sig);

        var selected = LoadFaceOverloadSelector.Select(candidates);

        Assert.NotNull(selected);
        // 第一个参数必须是 byte[] —— 挑到 String 就会抛 "Byte[] 不能转 String"
        Assert.Equal("Byte[]", selected!.ParameterTypes[0]);
    }

    [Fact]
    public void PicksThreeParameterByteArrayOverloadWhenAvailable()
    {
        var selected = LoadFaceOverloadSelector.Select(Unity2022Signatures.Select(Sig));

        Assert.NotNull(selected);
        Assert.Equal(3, selected!.ParameterCount);
        Assert.True(selected.StartsWithByteArray);
        Assert.True(selected.HasFaceIndexParameter);
        // Unity 2022 上点大小是 float
        Assert.Equal("Single", selected.ParameterTypes[1]);
    }

    [Fact]
    public void FallsBackToTwoParameterOverloadOnUnity2021()
    {
        // Unity 2021 没有三参数 byte[] 版，应退回 (Byte[], Int32)
        var selected = LoadFaceOverloadSelector.Select(Unity2021Signatures.Select(Sig));

        Assert.NotNull(selected);
        Assert.Equal(2, selected!.ParameterCount);
        Assert.Equal("Byte[]", selected.ParameterTypes[0]);
        Assert.Equal("Int32", selected.ParameterTypes[1]);
    }

    /// <summary>
    /// 关键回归：**绝不能**在同时存在 string 与 byte[] 重载时挑到 string 那个。
    /// 这是"装配日志正常但游戏里显示方框"的直接原因。
    /// </summary>
    [Fact]
    public void NeverSelectsStringOrFontOverload()
    {
        foreach (var signatures in new[] { Unity2022Signatures, Unity2021Signatures })
        {
            var selected = LoadFaceOverloadSelector.Select(signatures.Select(Sig));
            Assert.NotNull(selected);
            Assert.NotEqual("String", selected!.ParameterTypes[0]);
            Assert.NotEqual("Font", selected.ParameterTypes[0]);
        }
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoByteArrayOverload()
    {
        var onlyStrings = new[] { Sig("String"), Sig("String,Int32"), Sig("Font"), Sig("String,String") };

        Assert.Null(LoadFaceOverloadSelector.Select(onlyStrings));
    }

    [Fact]
    public void ReturnsNullForEmptyInput()
    {
        Assert.Null(LoadFaceOverloadSelector.Select(Array.Empty<LoadFaceOverloadSelector.Signature>()));
        Assert.Null(LoadFaceOverloadSelector.Select(null!));
    }

    [Fact]
    public void IgnoresMethodsWithOtherNames()
    {
        var candidates = new[]
        {
            new LoadFaceOverloadSelector.Signature("SetFaceSize", "Byte[]", "Int32", "Int32"),
            new LoadFaceOverloadSelector.Signature("LoadFontFace", "Byte[]", "Int32", "Int32")
        };

        var selected = LoadFaceOverloadSelector.Select(candidates);

        Assert.NotNull(selected);
        Assert.Equal("LoadFontFace", selected!.Name);
    }

    [Fact]
    public void HandlesByteArraySpellingVariants()
    {
        foreach (var spelling in new[] { "Byte[]", "byte[]", "System.Byte[]" })
        {
            var candidates = new[] { new LoadFaceOverloadSelector.Signature("LoadFontFace", spelling, "Int32") };
            var selected = LoadFaceOverloadSelector.Select(candidates);
            Assert.NotNull(selected);
        }
    }

    // ---------------- 参数构造 ----------------

    [Fact]
    public void BuildsArgumentsForThreeParameterSignature()
    {
        var signature = LoadFaceOverloadSelector.Select(Unity2022Signatures.Select(Sig));
        var bytes = new byte[] { 1, 2, 3 };

        var arguments = LoadFaceOverloadSelector.BuildArguments(signature, bytes, pointSize: 24, faceIndex: 0);

        Assert.NotNull(arguments);
        Assert.Equal(3, arguments!.Length);
        Assert.Same(bytes, arguments[0]);                        // 必须是同一个字节数组
        Assert.Equal(24d, Convert.ToDouble(arguments[1]));       // 点大小（Unity 2022 是 float）
        Assert.Equal(0d, Convert.ToDouble(arguments[2]));        // faceIndex
    }

    [Fact]
    public void BuildsArgumentsForTwoParameterSignature()
    {
        var signature = LoadFaceOverloadSelector.Select(Unity2021Signatures.Select(Sig));
        var bytes = new byte[] { 9 };

        var arguments = LoadFaceOverloadSelector.BuildArguments(signature, bytes, pointSize: 24, faceIndex: 3);

        Assert.NotNull(arguments);
        Assert.Equal(2, arguments!.Length);
        Assert.Same(bytes, arguments[0]);
        Assert.Equal(24d, Convert.ToDouble(arguments[1]));   // 两参数版没有 faceIndex，不能把它塞进去
    }

    [Fact]
    public void BuildsArgumentsKeepsFaceIndexForTtcCollections()
    {
        var candidates = new[]
        {
            new LoadFaceOverloadSelector.Signature("LoadFontFace", "Byte[]", "Int32", "Int32")
        };
        var signature = LoadFaceOverloadSelector.Select(candidates);

        var arguments = LoadFaceOverloadSelector.BuildArguments(signature, new byte[] { 1 }, 24, faceIndex: 1);

        Assert.NotNull(arguments);
        Assert.Equal(24d, Convert.ToDouble(arguments![1]));   // 点大小
        Assert.Equal(1d, Convert.ToDouble(arguments[2]));     // faceIndex 必须原样传进去（.ttc 要靠它选 face）
    }

    [Fact]
    public void BuildArgumentsReturnsNullForUnusableSignature()
    {
        Assert.Null(LoadFaceOverloadSelector.BuildArguments(null, new byte[] { 1 }, 24, 0));

        var stringSignature = new LoadFaceOverloadSelector.Signature("LoadFontFace", "String", "Int32", "Int32");
        Assert.Null(LoadFaceOverloadSelector.BuildArguments(stringSignature, new byte[] { 1 }, 24, 0));
    }

    [Theory]
    [InlineData("Int32", true)]
    [InlineData("int", true)]
    [InlineData("Single", false)]
    [InlineData("String", false)]
    public void IntegerDetectionIsCorrect(string typeName, bool expected)
    {
        Assert.Equal(expected, LoadFaceOverloadSelector.Signature.IsInteger(typeName));
    }
}
