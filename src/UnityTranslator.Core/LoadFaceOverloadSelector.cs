using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityTranslator.Core;

/// <summary>
/// 从一组方法签名里挑出"把字体文件字节载入字体引擎"该用的那个重载。**纯逻辑，可离线单元测试。**
///
/// 为什么必须单独拆出来测：
///   这段逻辑原先写在插件里用反射现场挑重载，而反射挑错的后果非常隐蔽 ——
///   `FontEngine.LoadFontFace` 同时存在 (string,int,int) 与 (byte[],int,int) 两个
///   **参数个数相同**的重载。按"参数个数"挑会挑到 string 版，
///   调用时抛 ArgumentException（Byte[] 不能转 String），
///   于是**字体根本没被载入**，但后续 SetFaceSize / GetFaceInfo 仍然成功，
///   引擎里留着的是游戏自己的字体 —— 表现就是"装配成功、游戏里却显示方框"。
///   实测踩过这个坑（诊断探针报出 `family=Noto Sans JP`，而我们要载入的是 msyh.ttc）。
///
/// 所以这里把"该挑哪个"变成可枚举输入 → 可断言输出，用测试钉死：
///   1. 第一个参数**必须是 byte[]**（不能是 string / Font）；
///   2. 能用带 faceIndex 的三参数版就用它（.ttc 字体集合要能指定 face）；
///   3. 没有三参数版时退回两参数版（Unity 6000 的 pointSize 是 float，仍算匹配）；
///   4. 一个都不匹配时明确返回 null，由调用方报错，而不是硬塞参数。
/// </summary>
public static class LoadFaceOverloadSelector
{
    /// <summary>
    /// 一个方法签名的描述：各参数的类型名。
    /// 用**简单的类型名**（如 "Byte[]"、"String"、"Int32"、"Single"）而不是 Type，
    /// 这样 Core 不需要引用 UnityEngine，测试也能直接构造。
    /// </summary>
    public sealed class Signature
    {
        public Signature(string name, params string[] parameterTypes)
        {
            Name = name ?? string.Empty;
            ParameterTypes = parameterTypes ?? Array.Empty<string>();
        }

        public string Name { get; }
        public IReadOnlyList<string> ParameterTypes { get; }

        public int ParameterCount => ParameterTypes.Count;

        /// <summary>第一个参数是不是 byte[]（大小写与写法都容忍）。</summary>
        public bool StartsWithByteArray =>
            ParameterCount > 0 && Normalize(ParameterTypes[0]) is "byte[]" or "Byte[]" or "system.byte[]";

        /// <summary>参数里有没有一个"看起来是 faceIndex"的整数参数。</summary>
        public bool HasFaceIndexParameter =>
            ParameterCount >= 3
            && IsInteger(ParameterTypes[ParameterCount - 1]);

        public static bool IsInteger(string typeName) =>
            Normalize(typeName) is "int32" or "int" or "system.int32";

        public static bool IsFloatingPoint(string typeName) =>
            Normalize(typeName) is "single" or "float" or "system.single" or "double" or "system.double";

        private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

        public override string ToString() =>
            Name + "(" + string.Join(",", ParameterTypes) + ")";
    }

    /// <summary>
    /// 挑出该用的重载；没有合适的返回 null。
    /// </summary>
    public static Signature? Select(IEnumerable<Signature> candidates)
    {
        var list = (candidates ?? Enumerable.Empty<Signature>())
            .Where(candidate => candidate is not null
                                && candidate.Name == "LoadFontFace"
                                && candidate.StartsWithByteArray)
            .ToList();
        if (list.Count == 0) return null;

        // 带 faceIndex 的三参数版优先（.ttc 字体集合靠它指定第几个 face）
        var withFaceIndex = list.FirstOrDefault(candidate => candidate.HasFaceIndexParameter);
        if (withFaceIndex is not null) return withFaceIndex;

        // 退回两参数版：只有 (byte[], int) 这种
        var twoParameter = list.FirstOrDefault(candidate => candidate.ParameterCount == 2
                                                            && Signature.IsInteger(candidate.ParameterTypes[1]));
        if (twoParameter is not null) return twoParameter;

        // 再退：带 float 点大小的两参数版（Unity 6000 的签名形态）
        var floatVersion = list.FirstOrDefault(candidate => candidate.ParameterCount == 2
                                                            && Signature.IsFloatingPoint(candidate.ParameterTypes[1]));
        return floatVersion;
    }

    /// <summary>
    /// 按选中的签名算出各参数该传什么。返回 null 表示签名不可用。
    /// 参数顺序按"最后一个是 faceIndex、其余整数是点大小"解释 —— 这是 Unity 这三个重载的实际形态。
    /// </summary>
    public static object?[]? BuildArguments(Signature? signature, byte[] bytes, int pointSize, int faceIndex)
    {
        if (signature is null || !signature.StartsWithByteArray) return null;

        var arguments = new object?[signature.ParameterCount];
        arguments[0] = bytes;
        for (var index = 1; index < signature.ParameterCount; index++)
        {
            var typeName = signature.ParameterTypes[index];
            var isLast = index == signature.ParameterCount - 1;
            // 三参数版的最后一个参数是 faceIndex；两参数版没有 faceIndex
            var isFaceIndex = isLast && signature.ParameterCount >= 3;

            if (Signature.IsInteger(typeName)) arguments[index] = isFaceIndex ? faceIndex : pointSize;
            else if (Signature.IsFloatingPoint(typeName))
            {
                // Unity 6000 的 pointSize 是 float
                arguments[index] = isFaceIndex ? (float)faceIndex : (float)pointSize;
            }
            else arguments[index] = null;
        }
        return arguments;
    }
}
