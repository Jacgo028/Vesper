// 这个文件与 IL2CPP 插件（net6.0）和 Mono 插件（netstandard2.1）共享编译，
// 两者都关闭了 ImplicitUsings，所以这里必须显式列出所有 using，
// 且只能用 netstandard2.1 / net6.0 都有的 API。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UnityTranslator.Core;

/// <summary>
/// 程序集解析钩子。IL2CPP 插件必须能在运行时找到 BepInEx 为游戏生成的互操作程序集，
/// 而插件自身不引用它们（这样一份插件才能适配所有 IL2CPP 游戏）。
/// 体外测试时也用它来模拟运行时环境。
/// </summary>
public interface IAssemblyLookup
{
    Assembly? Find(string simpleName);
}

/// <summary>
/// 用反射访问 IL2CPP 生成的 Unity 类型。
///
/// 为什么用反射而不是直接引用：互操作程序集是 BepInEx 针对**每一款游戏**从
/// GameAssembly.dll 生成的，各不相同。若编译期直接引用，插件就得为每款游戏
/// 单独编译一次、无法量产。改成按名字解析 + 反射调用后，一份插件适用于所有
/// IL2CPP 游戏（已实测：BepInEx 官方 IL2CPP 插件就是这么做的）。
///
/// 设计成实例而不是静态：解析结果要缓存，而缓存与解析器是"一次配置、到处使用"的状态。
/// 用静态字段会让多个使用者互相污染（写测试时实测踩到：真实 interop 的缓存泄漏到
/// 假类型的用例里，导致断言拿到 typeof(TextMeshProUGUI)）。每个使用者持有一个实例即可。
/// </summary>
public sealed class UnityReflection
{
    private readonly IAssemblyLookup? _lookup;
    private Type? _textType;

    // 缓存必须按"实例的具体类型"分桶，不能只留一个字段。
    //
    // 实测踩过的坑：TMP_Text.text 声明在基类 TMP_Text 上，而扫描拿到的是
    // TextMeshProUGUI / TextMeshPro（甚至第三方子类）的实例。Il2CppInterop 为每个
    // 类型生成独立的互操作类，拿基类的 PropertyInfo 去 GetValue 派生实例，
    // PropertyInfo.GetValue 会抛 ArgumentException（"对象与目标类型不匹配"），
    // 于是所有读取都变成 null、扫描结果恒为"组件=0"。按类型分桶后每个具体类型
    // 都拿到自己的 PropertyInfo。
    private readonly Dictionary<Type, PropertyInfo?> _textProperties = new();
    private readonly Dictionary<Type, PropertyInfo?> _fontProperties = new();
    private readonly Dictionary<Type, MemberInfo?> _pointerMembers = new();
    private readonly Dictionary<(Type Type, string Name), PropertyInfo?> _activityProperties = new();
    private readonly Dictionary<Type, MethodInfo?> _aliveOperators = new();

    public UnityReflection(IAssemblyLookup? lookup = null) => _lookup = lookup;

    /// <summary>
    /// 解析 TMP 文字组件类型。优先 TextMeshProUGUI（UGUI 场景最常见），
    /// 退回基类 TMP_Text（也覆盖 TextMeshPro 的 3D 用法）。
    /// </summary>
    public Type? TextComponentType => _textType ??= ResolveFirst(
        ("Unity.TextMeshPro", "TMPro.TextMeshProUGUI"),
        ("Unity.TextMeshPro", "TMPro.TMP_Text"));

    /// <summary>按程序集名 + 类型全名解析类型；解析不到返回 null。</summary>
    public Type? ResolveType(string assemblyName, string typeName)
    {
        var assembly = LoadAssembly(assemblyName);
        if (assembly == null) return null;
        try { return assembly.GetType(typeName, throwOnError: false); }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or TypeInitializationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Selects Il2CppInterop's non-generic bridge from System.Type to Il2CppSystem.Type.
    /// The generic Of&lt;T&gt; path initializes Il2CppClassPointerStore&lt;T&gt; through a reflected
    /// generic method and fails for runtime-discovered component types.
    /// </summary>
    public static MethodInfo? ResolveIl2CppTypeFromSystemType(Type factoryType)
    {
        foreach (var method in factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "From" || method.IsGenericMethod) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Type))
                return method;
        }
        return null;
    }

    /// <summary>
    /// 加载程序集。
    ///
    /// **必须先查 AppDomain 里已加载的程序集**，再退回解析器。
    /// 实测踩过的坑（IL2CPP 插件扫描恒为"组件=0"的真正原因）：
    /// 解析器用 <c>Assembly.LoadFrom(路径)</c> 把 BepInEx 生成的互操作程序集
    /// **又加载了一份**。同一份 dll 于是存在两个 <c>Assembly</c> 实例，
    /// 从副本里取出的 <c>PropertyInfo</c> 与默认上下文里的组件实例类型标识不一致，
    /// 于是 <c>PropertyInfo.GetValue</c> 抛
    /// <c>TargetException: Object does not match target type</c>，扫描整个被异常打断。
    /// 这也解释了为什么源码几乎相同的只读探针能扫到组件、正式插件扫不到 ——
    /// 探针用的是无解析器的构造，走的正是这条 AppDomain 路径。
    ///
    /// 测试替身不受影响：假类型所在的程序集本来就已加载，也是走这条路径拿到它。
    /// </summary>
    public Assembly? LoadAssembly(string simpleName)
    {
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = loaded.GetName().Name;
            if (string.Equals(name, simpleName, StringComparison.Ordinal)) return loaded;
        }

        if (_lookup == null) return null;

        try { return _lookup.Find(simpleName); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private Type? ResolveFirst(params (string Assembly, string Type)[] candidates)
    {
        foreach (var (assembly, type) in candidates)
        {
            var resolved = ResolveType(assembly, type);
            if (resolved != null) return resolved;
        }
        return null;
    }

    /// <summary>
    /// 把 <c>Resources.FindObjectsOfTypeAll</c> 的返回值转成可枚举的列表。
    ///
    /// IL2CPP 下它返回 <c>Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase&lt;T&gt;</c>。
    /// 实测的关键点：这个类型只实现**泛型** <c>IEnumerable&lt;T&gt;</c>，不实现非泛型
    /// <c>IEnumerable</c>。早期版本只判断 <c>is IEnumerable</c>，于是永远走进
    /// "Count + 索引器"兜底分支；而 Il2CppInterop 的索引器属性不是普通托管属性，
    /// 反射取值为空 —— 两个分支同时失效，结果恒为 0 个组件。所以泛型枚举必须显式处理。
    /// </summary>
    public static IReadOnlyList<object> EnumerateObjects(object? arrayLike)
    {
        if (arrayLike == null) return Array.Empty<object>();

        // 普通托管数组（Mono 下或测试替身）
        if (arrayLike is object[] plain) return plain;

        // 字符串也实现 IEnumerable，但它不可能是 Unity 对象集合，按"取不到"处理
        if (arrayLike is string) return Array.Empty<object>();

        var results = new List<object>();

        // 1) 非泛型 IEnumerable（托管数组、List<object> 等）
        if (arrayLike is IEnumerable nonGeneric) Drain(nonGeneric.GetEnumerator(), results);
        if (results.Count > 0) return results;

        // 2) 泛型枚举。Il2CppArrayBase<T> 有公开的 GetEnumerator() 返回自定义枚举器；
        //    它的非泛型 IEnumerable.GetEnumerator() 是显式实现，从托管侧枚举不出任何元素，
        //    所以第 1 步会"成功但产出 0 个元素"，必须靠这一步兜住。
        //    （一般会走 GetInterfaceMap 拿显式实现；公开方法这条留给自定义集合用。）
        var genericEnumerator = ResolveGenericEnumerator(arrayLike.GetType());
        if (genericEnumerator != null)
        {
            IEnumerator? typed = null;
            try { typed = genericEnumerator.Invoke(arrayLike, null) as IEnumerator; }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException) { }
            if (typed != null) Drain(typed, results);
            if (results.Count > 0) return results;
        }

        // 兜底：用 Count + 索引器
        var type = arrayLike.GetType();
        var countProperty = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProperty?.GetValue(arrayLike) is not int count) return results;

        var indexer = FindIntIndexer(type);
        if (indexer == null) return results;

        var indexArguments = new object[1];
        for (var i = 0; i < count && i <= 10000; i++)
        {
            object? item = null;
            indexArguments[0] = i;
            try { item = indexer.GetValue(arrayLike, indexArguments); }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or IndexOutOfRangeException) { break; }
            if (item != null) results.Add(item);
        }
        return results;
    }

    /// <summary>取出全部元素，顺便挡住异常与病态长度，避免一个坏元素炸掉整轮扫描。</summary>
    private static IReadOnlyList<object> Drain(IEnumerator enumerator, List<object> results)
    {
        var guard = 0;
        try
        {
            while (enumerator.MoveNext())
            {
                var item = enumerator.Current;
                if (item != null) results.Add(item);
                if (++guard > 10000) break;
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException or NotSupportedException or TargetException)
        {
            // 已经取到的元素仍然有效，保留。
            // TargetException 必须在这里：Il2CppInterop 的枚举器在类型上下文不一致时抛的正是它，
            // 早期版本没兜住，一个元素的异常会直接炸掉整轮扫描（现象：扫描恒为"组件=0"）。
        }
        return results;
    }

    /// <summary>
    /// 找返回 <c>IEnumerator&lt;T&gt;</c> 的 <c>GetEnumerator</c>。
    ///
    /// 两种形状都要覆盖：公开方法（自定义集合）与显式接口实现（<c>Il2CppArrayBase&lt;T&gt;</c>
    /// 就是这种）。显式实现必须通过接口映射拿目标方法 —— <c>GetMethod("GetEnumerator")</c>
    /// 找不到显式实现。非泛型 <c>IEnumerable</c> 已被排除，否则会绕回同一个坑。
    /// </summary>
    private static MethodInfo? ResolveGenericEnumerator(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "GetEnumerator" || method.IsGenericMethod || method.GetParameters().Length != 0)
                continue;
            if (ReturnsGenericEnumerator(method.ReturnType)) return method;
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            var declaration = candidate.GetMethod("GetEnumerator");
            if (declaration == null) continue;

            try
            {
                var map = type.GetInterfaceMap(candidate);
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i] == declaration) return map.TargetMethods[i];
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException) { }
        }

        return null;
    }

    private static bool ReturnsGenericEnumerator(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IEnumerator<>);

    /// <summary>
    /// 找接收单个 int 的索引器。用 GetIndexParameters 判断而不是写死参数个数，
    /// 免得把接收其他类型参数的"Item"属性（例如字典式索引器）当成数组下标。
    /// </summary>
    private static PropertyInfo? FindIntIndexer(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name != "Item") continue;
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int)) return property;
        }
        return null;
    }

    /// <summary>
    /// 用原生指针把对象**重新包装**成指定的互操作类型。
    ///
    /// ★★★ 为什么必须有它（本轮定位到的 IL2CPP 致命问题）：
    ///   <c>Resources.FindObjectsOfTypeAll(Type)</c> 在互操作层声明的返回类型是
    ///   <c>UnityEngine.Object[]</c>，而 Il2CppInterop 包装数组元素时用的是
    ///   **数组的静态元素类型** —— 于是每个 TMP 组件在托管侧都只是
    ///   <c>UnityEngine.Object</c>（实测诊断：`类型=UnityEngine.Object 基类=Object`），
    ///   反射找 <c>text</c> 属性自然找不到，扫描结果永远"8 个组件、0 个可翻译"。
    ///
    ///   互操作类型都有 <c>(IntPtr)</c> 构造函数，拿指针重新构造一次就能得到
    ///   类型正确、属性能正常读写的包装对象。
    /// </summary>
    public object? RewrapAs(object component, Type? targetType)
    {
        if (targetType is null) return null;
        var pointer = ReadPointer(component);
        if (pointer is null) return null;

        try
        {
            var constructor = targetType.GetConstructor(new[] { typeof(IntPtr) });
            if (constructor is null) return null;
            return constructor.Invoke(new object[] { new IntPtr(pointer.Value) });
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException
                                       or MemberAccessException or TargetException)
        {
            return null;
        }
    }

    /// <summary>读取某个对象的 text 属性；取不到返回 null。</summary>
    public string? ReadText(object component)
    {
        var direct = GetObject(component, ResolveTextProperty(component));
        if (direct is string text) return text;

        // 缓存的那一个失配时（Il2CppInterop 会抛 TargetException），逐实例再试一次
        return ReadPropertyUncached(component, "text") as string;
    }

    /// <summary>
    /// Resources.FindObjectsOfTypeAll also returns inactive and disabled text components.
    /// Read their current Unity activity before requesting translation. Missing members stay
    /// compatible with other component shapes; a known Unity member that cannot be read is unsafe.
    /// </summary>
    public bool IsActiveAndEnabled(object? component)
    {
        if (component is null) return false;
        try
        {
            // A destroyed Unity object can retain its managed wrapper and native pointer.
            var alive = ResolveAliveOperator(component.GetType());
            if (alive?.Invoke(null, new[] { component }) is bool exists && !exists) return false;

            var enabled = ResolveActivityProperty(component.GetType(), "enabled");
            if (enabled?.GetValue(component) is bool isEnabled && !isEnabled) return false;

            var gameObjectProperty = ResolveActivityProperty(component.GetType(), "gameObject");
            if (gameObjectProperty is null) return true;
            var gameObject = gameObjectProperty.GetValue(component);
            if (gameObject is null) return false;
            var active = ResolveActivityProperty(gameObject.GetType(), "activeInHierarchy");
            return active?.GetValue(gameObject) is not bool isActive || isActive;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or TargetException
                                       or MemberAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private PropertyInfo? ResolveActivityProperty(Type concrete, string name)
    {
        var key = (concrete, name);
        if (_activityProperties.TryGetValue(key, out var cached)) return cached;
        var property = concrete.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanRead != true || property.GetIndexParameters().Length != 0) property = null;
        _activityProperties[key] = property;
        return property;
    }

    private MethodInfo? ResolveAliveOperator(Type concrete)
    {
        if (_aliveOperators.TryGetValue(concrete, out var cached)) return cached;
        MethodInfo? resolved = null;
        for (var type = concrete; type is not null && resolved is null; type = type.BaseType)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "op_Implicit" || method.ReturnType != typeof(bool)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(concrete)) continue;
                resolved = method;
                break;
            }
        _aliveOperators[concrete] = resolved;
        return resolved;
    }

    /// <summary>写入某个对象的 text 属性；成功返回 true。只读或调用失败时返回 false，由调用方保持原样。</summary>
    public bool WriteText(object component, string text)
    {
        var property = ResolveTextProperty(component);
        if (property?.CanWrite != true) return false;
        try
        {
            property.SetValue(component, text);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>读取 font 属性；取不到返回 null。</summary>
    public object? ReadFont(object component) => GetObject(component, ResolveFontProperty(component));

    /// <summary>
    /// 读取某个对象在原生侧的对象指针；取不到返回 null。
    /// IL2CPP 下对象的托管包装实例会随 GC 变化，指针才是稳定身份，
    /// 扫描用它做键，否则每轮扫描都会被当成"新组件"重新送翻译。
    /// </summary>
    public long? ReadPointer(object component)
    {
        var member = ResolvePointerMember(component.GetType());
        if (member == null) return null;

        object? raw;
        try
        {
            raw = member switch
            {
                PropertyInfo property when property.CanRead => property.GetValue(component),
                FieldInfo field => field.GetValue(component),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException) { return null; }

        return CoerceToInt64(raw);
    }

    /// <summary>
    /// 把指针值统一成 long。
    ///
    /// 必须同时接受 <c>Il2CppSystem.IntPtr</c>：Il2CppInterop 的 <c>Pointer</c> 属性
    /// 返回的是它自己的 IntPtr 结构，不是 <c>System.IntPtr</c>。早期版本写
    /// <c>is IntPtr</c> 匹配不上，直接掉进 GetHashCode() 兜底 —— 包装实例哈希每轮都可能变，
    /// 扫描就会把同一个组件反复当新的送翻译。
    ///
    /// 外来指针按形状识别（有返回 long 的 <c>ToInt64()</c>）而不是按类型名：
    /// <c>IntPtr</c> 这个名字在 C# 里是 <c>nint</c> 的别名，同名类型没法可靠地当属性类型用。
    /// </summary>
    private static long? CoerceToInt64(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case IntPtr systemPointer:
                return systemPointer.ToInt64();
            case long asLong:
                return asLong;
            case int asInt:
                return asInt;
        }

        var type = raw.GetType();
        if (!type.IsValueType) return null;

        try
        {
            var toInt64 = type.GetMethod("ToInt64", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (toInt64?.ReturnType == typeof(long) && toInt64.Invoke(raw, null) is long value) return value;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException) { }

        return null;
    }

    private PropertyInfo? ResolveTextProperty(object component) =>
        ResolveCached(_textProperties, component.GetType(), "text");

    private PropertyInfo? ResolveFontProperty(object component) =>
        ResolveCached(_fontProperties, component.GetType(), "font");

    /// <summary>
    /// 先按实例的具体类型找，找不到再退回 TMP 基类声明（并缓存"没找到"这个结论，
    /// 免得每轮扫描都为同一个类型反复反射）。
    /// </summary>
    private PropertyInfo? ResolveCached(Dictionary<Type, PropertyInfo?> cache, Type concrete, string name)
    {
        if (cache.TryGetValue(concrete, out var cached)) return cached;

        var resolved = concrete.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? TextComponentType?.GetProperty(name);
        cache[concrete] = resolved;
        return resolved;
    }

    private MemberInfo? ResolvePointerMember(Type concrete)
    {
        if (_pointerMembers.TryGetValue(concrete, out var cached)) return cached;

        // 先看属性（Il2CppObjectBase.Pointer），再看字段（部分版本暴露 m_CachedPtr）
        MemberInfo? member = concrete.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
        member ??= concrete.GetField("Pointer", BindingFlags.Public | BindingFlags.Instance);
        member ??= FindPointerField(concrete);

        _pointerMembers[concrete] = member;
        return member;
    }

    /// <summary>派生类型不会把基类的私有字段列进 GetField，所以要自己向上找。</summary>
    private static FieldInfo? FindPointerField(Type concrete)
    {
        for (var type = concrete; type != null; type = type.BaseType)
        {
            var field = type.GetField("Pointer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field;
        }
        return null;
    }

    private static object? GetObject(object target, PropertyInfo? property)
    {
        if (property?.CanRead != true) return null;
        try
        {
            return property.GetValue(target);
        }
        // ★ 必须捕获 TargetException：Il2CppInterop 下"PropertyInfo 与实例类型不匹配"抛的
        //   正是它，而它**不是** ArgumentException 的子类（它是 System.Exception 的直接派生）。
        //   实测漏掉它会让整个扫描被异常打断，日志里只看到一句
        //   `Object does not match target type`，完全看不出问题在哪一行。
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or TargetException)
        {
            return null;
        }
    }

    /// <summary>
    /// 逐实例取属性（**不走缓存**），作为缓存失配时的兜底。
    ///
    /// 为什么需要：Il2CppInterop 为每个类型生成独立的互操作类，
    /// 从基类拿到的 PropertyInfo 用在派生实例上可能抛 TargetException。
    /// 兜底路径直接用实例自己的类型查一次，命中就用。
    /// </summary>
    private static object? ReadPropertyUncached(object target, string name)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        try
        {
            // 先只在本类型上找（DeclaredOnly），避免拿到基类声明的那一个
            var declared = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (declared?.CanRead == true) return declared.GetValue(target);

            var inherited = type.GetProperty(name, flags);
            if (inherited?.CanRead == true) return inherited.GetValue(target);
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or TargetException)
        {
        }
        return null;
    }
}
