using System;
using System.Globalization;
using System.Reflection;

namespace UnityTranslator.Core;

/// <summary>
/// 按**名字**读取一个对象上的成员，不依赖编译期的强类型绑定。
///
/// 为什么必须有它（实测踩过的崩溃级坑）：
///   插件要用同一份 dll 支持 Unity 2021 / 2022 / 6000。而 Unity 会改自己的公开字段类型 ——
///   例如 <c>UnityEngine.TextCore.FaceInfo.pointSize</c> 在 2021/2022 是 <c>int</c>，
///   到 Unity 6000 变成 <c>float</c>。
///   只要代码里写了 <c>faceInfo.pointSize</c>，编译时就会把**那一个**访问器绑进 IL；
///   换一个 Unity 版本运行就抛
///   <c>MissingMethodException: Method not found: single FaceInfo.get_pointSize()</c>，
///   整个字体装配直接失败（实测：Mad Island 上装配 100% 失败）。
///   按名字反射读取就与版本无关：先找属性、再找字段，找不到返回 null 由调用方决定。
/// </summary>
public static class UnityMemberReader
{
    /// <summary>
    /// 读取 <paramref name="name"/> 成员并转成 <typeparamref name="T"/>。
    /// 成员不存在、读取抛异常、或转换失败都返回 null（调用方必须处理 null，
    /// 不允许用默认值蒙混过去 —— 那正是"看起来成功实际没生效"的来源）。
    /// </summary>
    public static T? Read<T>(object? target, string name) where T : struct
    {
        var raw = ReadRaw(target, name);
        if (raw is null) return null;

        if (raw is T typed) return typed;

        try
        {
            // int → float 这类跨版本类型差异就靠这里抹平
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>读取字符串成员；不存在返回 null。</summary>
    public static string? ReadString(object? target, string name) => ReadRaw(target, name) as string;

    /// <summary>
    /// 按名字取原始值：属性优先（Unity 的公开 API 多为属性），其次字段。
    /// </summary>
    public static object? ReadRaw(object? target, string name)
    {
        if (target is null || string.IsNullOrEmpty(name)) return null;
        if (target is string) return null;

        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        try
        {
            var property = type.GetProperty(name, flags);
            if (property is not null && property.CanRead && property.GetIndexParameters().Length == 0)
                // 必须传两个参数：单参的 GetValue(object) 重载是 .NET 4.5 才有的，
                // 精简变体跑在 CLR 2.0 上找不到它会编译失败。两参版本所有框架都有。
                return property.GetValue(target, null);
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or TargetException)
        {
            // 属性存在但取值失败：继续尝试同名字段
        }

        try
        {
            var field = type.GetField(name, flags);
            if (field is not null) return field.GetValue(target);
        }
        catch (Exception ex) when (ex is TargetException or ArgumentException or FieldAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// 成员是否存在（属性或字段都算）。用于在装配前判断某个版本的 API 面。
    /// </summary>
    public static bool HasMember(object? target, string name) =>
        HasMember(target?.GetType(), name);

    /// <summary>成员是否存在（按类型判断）。</summary>
    public static bool HasMember(Type? type, string name)
    {
        if (type is null || string.IsNullOrEmpty(name)) return false;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            if (type.GetProperty(name, flags) is not null) return true;
            if (type.GetField(name, flags) is not null) return true;
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException)
        {
        }
        return false;
    }
}
