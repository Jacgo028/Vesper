using System.Reflection;

namespace UnityTranslator.Core.Tests;

public sealed class UnityReflectionTests
{
    /// <summary>
    /// 用定义在本程序集里的假组件当替身，覆盖读写行为。
    /// 真实 IL2CPP 类型只在装了 BepInEx 的机器上才能测，见 UnityReflectionInteropTests。
    /// </summary>
    private sealed class FakeLookup : IAssemblyLookup
    {
        private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.Ordinal);

        public FakeLookup Add(string simpleName, Assembly assembly)
        {
            _assemblies[simpleName] = assembly;
            return this;
        }

        public Assembly? Find(string simpleName) =>
            _assemblies.TryGetValue(simpleName, out var assembly) ? assembly : null;
    }

    /// <summary>替身：有可读写的 text 与 font。非密封，供派生形状复用。</summary>
    public class FakeText
    {
        public string? text { get; set; } = "はい";
        public object? font { get; set; }
    }

    /// <summary>替身：text 只读（模拟某些组件）。</summary>
    public sealed class ReadOnlyText
    {
        private readonly string _value = "読み取り専用";
        public string? text => _value;
    }

    /// <summary>替身：派生组件，text 属性声明在基类 FakeText 上（和 TMP 的继承结构一致）。</summary>
    public sealed class FakeDerivedText : FakeText
    {
    }

    /// <summary>替身：Pointer 是 System.IntPtr。</summary>
    public sealed class FakePointerText
    {
        public global::System.IntPtr Pointer { get; set; }
        public string text { get; set; } = string.Empty;
    }

    /// <summary>
    /// 模拟 <c>Il2CppSystem.IntPtr</c>：完全不是 <c>System.IntPtr</c> 的值类型，
    /// 只靠"有返回 long 的 ToInt64()"这个形状被识别。
    /// 名字不能叫 IntPtr —— 那是 <c>nint</c> 的 C# 别名，同名类型当属性类型会永远解析成 nint。
    /// </summary>
    public readonly struct ForeignIntPtr
    {
        private readonly long _value;
        public ForeignIntPtr(long value) => _value = value;
        public long ToInt64() => _value;
    }

    /// <summary>替身：Pointer 是"外来 IntPtr"（模拟 Il2CppSystem.IntPtr）。</summary>
    public sealed class FakeForeignPointerText
    {
        public ForeignIntPtr Pointer { get; set; }
        public string text { get; set; } = string.Empty;
    }

    /// <summary>每个测试都用独立实例，避免缓存与解析器互相污染。</summary>
    private static UnityReflection CreateFake() => new(new FakeLookup().Add(FakeAssemblyName, typeof(FakeText).Assembly));

    /// <summary>
    /// 故意用一个**不属于真实 Unity 的名字**：真实互操作程序集一旦被加载就永久留在
    /// AppDomain 里，而 UnityReflection.LoadAssembly 会优先复用已加载的程序集
    /// （生产环境必需，因为 BepInEx 会预加载）。假类型若冒用 "Unity.TextMeshPro"
    /// 这个名字，就会命中真实程序集，导致断言拿到 typeof(TMPro.TMP_Text)（实测踩过）。
    /// </summary>
    private const string FakeAssemblyName = "UnityTranslator.Tests.FakeUnityText";

    [Fact]
    public void ResolvesTheTextComponentTypeByConvention()
    {
        var reflection = CreateFake();

        // 假程序集里没有 TMPro.TextMeshProUGUI / TMP_Text，所以解析结果为 null ——
        // 不能因为找不到就抛异常
        Assert.Null(reflection.TextComponentType);
        Assert.Null(reflection.ResolveType(FakeAssemblyName, "TMPro.TMP_Text"));
    }

    [Fact]
    public void ReadsAndWritesTextOnTheGivenInstance()
    {
        var reflection = CreateFake();
        var component = new FakeText { text = "本当にゲームを終了しますか?" };

        Assert.Equal("本当にゲームを終了しますか?", reflection.ReadText(component));

        Assert.True(reflection.WriteText(component, "真的要退出游戏吗?"));
        Assert.Equal("真的要退出游戏吗?", component.text);
    }

    [Fact]
    public void WriteFailsGracefullyWhenThePropertyIsReadOnly()
    {
        var reflection = CreateFake();
        var component = new ReadOnlyText();

        Assert.Equal("読み取り専用", reflection.ReadText(component));
        // 只读时不抛异常，而是明确返回 false，让调用方保持原样
        Assert.False(reflection.WriteText(component, "改不了"));
        Assert.Equal("読み取り専用", component.text);
    }

    [Fact]
    public void ReadsFontAsObject()
    {
        var reflection = CreateFake();
        var font = new object();

        Assert.Same(font, reflection.ReadFont(new FakeText { font = font }));
        Assert.Null(reflection.ReadFont(new FakeText()));
    }

    [Fact]
    public void TwoInstancesDoNotShareCachedTypes()
    {
        // 这条用例是回归保护：早先用静态缓存时，真实 interop 的类型会泄漏到
        // 使用假类型的用例里，导致断言拿到 typeof(TextMeshProUGUI)
        var first = CreateFake();
        var second = new UnityReflection(new FakeLookup());

        Assert.Null(first.TextComponentType);
        Assert.Null(second.TextComponentType);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void SelectsNonGenericFromSystemTypeFactory()
    {
        var resolver = typeof(UnityReflection).GetMethod(
            "ResolveIl2CppTypeFromSystemType",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(resolver);

        var method = (MethodInfo?)resolver!.Invoke(null, new object[] { typeof(FakeIl2CppTypeFactory) });
        Assert.NotNull(method);
        Assert.Equal("From", method!.Name);
        Assert.False(method.IsGenericMethod);
        Assert.Equal(typeof(Type), Assert.Single(method.GetParameters()).ParameterType);
    }

    [Fact]
    public void EnumeratesPlainArrays()
    {
        var items = new object[] { new FakeText(), new FakeText() };

        var result = UnityReflection.EnumerateObjects(items);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EnumeratesArrayLikeObjectsAndSkipsNulls()
    {
        // 模拟 Il2CppArrayBase：只有非泛型 IEnumerable 与 Count/索引器
        var arrayLike = new ArrayLike(new object?[] { new FakeText(), null, new FakeText() });

        var result = UnityReflection.EnumerateObjects(arrayLike);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EnumeratesViaCountAndIndexerWhenNotEnumerable()
    {
        // 模拟只暴露 Count + 索引器的形状（没有 IEnumerable）
        var shaped = new IndexerOnly(new object?[] { new FakeText(), new FakeText() });

        var result = UnityReflection.EnumerateObjects(shaped);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HandlesNullAndUnknownShapesWithoutThrowing()
    {
        Assert.Empty(UnityReflection.EnumerateObjects(null));
        // 字符串也是 IEnumerable，但它不是对象集合，必须按"取不到"处理
        Assert.Empty(UnityReflection.EnumerateObjects("不是数组"));
    }

    [Fact]
    public void EnumeratesGenericOnlyShapes()
    {
        // 模拟 Il2CppArrayBase<T>：没有非泛型 IEnumerable，只有公开泛型 GetEnumerator()
        var generic = new GenericOnlyArray(new object[] { new FakeText(), new FakeText() });

        var result = UnityReflection.EnumerateObjects(generic);

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// 回归保护：早期版本扫描结果恒为"组件=0"的直接原因是
    /// <c>Il2CppArrayBase&lt;T&gt;</c> 的非泛型枚举产不出元素。这个替身复现了那个形状，
    /// 修好后必须能枚举出全部元素。
    /// </summary>
    [Fact]
    public void EnumeratesWhenTheNonGenericEnumeratorYieldsNothing()
    {
        var array = new ExplicitGenericArray(new object?[] { new FakeText(), null, new FakeText() });

        var result = UnityReflection.EnumerateObjects(array);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ReadsPointerFromSystemIntPtr()
    {
        var reflection = CreateFake();
        // 必须写 global::System.IntPtr：本文件没有 using System，裸 IntPtr 会解析成测试替身
        var component = new FakePointerText { Pointer = new global::System.IntPtr(0x2A0000000L) };

        Assert.Equal(0x2A0000000L, reflection.ReadPointer(component));
    }

    /// <summary>
    /// 回归保护：Il2CppInterop 的 Pointer 返回 Il2CppSystem.IntPtr，不是 System.IntPtr。
    /// 早期版本因此取不到指针，退化成 GetHashCode()，同一个组件每轮扫描都被当成新的。
    /// </summary>
    [Fact]
    public void ReadsPointerFromForeignIntPtrType()
    {
        var reflection = CreateFake();
        var component = new FakeForeignPointerText { Pointer = new ForeignIntPtr(0x1F0000000) };

        Assert.Equal(0x1F0000000L, reflection.ReadPointer(component));
    }

    [Fact]
    public void ReturnsNullPointerWhenTheComponentHasNone()
    {
        var reflection = CreateFake();

        Assert.Null(reflection.ReadPointer(new FakeText()));
    }

    [Fact]
    public void ReadsTextFromDerivedComponentsWithTheirOwnPropertyInfo()
    {
        // 回归保护：基类的 PropertyInfo 不能拿去读派生实例
        var reflection = CreateFake();

        Assert.Equal("親", reflection.ReadText(new FakeDerivedText { text = "親" }));
        Assert.True(reflection.WriteText(new FakeDerivedText(), "父母"));
    }

    [Fact]
    public void ReturnsNullWhenTheAssemblyIsMissing()
    {
        var reflection = new UnityReflection(new FakeLookup());

        Assert.Null(reflection.ResolveType("UnityTranslator.Tests.NoSuchAssembly", "TMPro.TMP_Text"));
        Assert.Null(reflection.TextComponentType);
        Assert.Null(reflection.LoadAssembly("UnityTranslator.Tests.NoSuchAssembly"));
    }

    /// <summary>模拟 IL2CPP 数组：实现非泛型 IEnumerable，另有 Count 与索引器。</summary>
    private sealed class ArrayLike : System.Collections.IEnumerable
    {
        private readonly object?[] _items;
        public ArrayLike(object?[] items) => _items = items;
        public int Count => _items.Length;
        public object? this[int index] => _items[index];
        public System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
    }

    /// <summary>
    /// 模拟 <c>Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase&lt;T&gt;</c> 的真实形状：
    /// 有**公开的** <c>GetEnumerator()</c> 返回自定义枚举器，而 <c>IEnumerable.GetEnumerator()</c>
    /// 是显式接口实现 —— 于是 <c>is IEnumerable</c> 判断为真，但非泛型枚举一个元素都产不出来。
    /// 这正是早期版本扫描结果恒为"组件=0"的原因。
    /// </summary>
    private sealed class ExplicitGenericArray : IEnumerable<object>
    {
        private readonly object?[] _items;
        public ExplicitGenericArray(object?[] items) => _items = items;

        public ItemEnumerator GetEnumerator() => new(_items);

        IEnumerator<object> IEnumerable<object>.GetEnumerator()
        {
            foreach (var item in _items)
                if (item != null) yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        internal sealed class ItemEnumerator : IEnumerator<object>
        {
            private readonly object?[] _items;
            private int _index = -1;
            public ItemEnumerator(object?[] items) => _items = items;
            public object Current => _items[_index]!;
            public bool MoveNext() => ++_index < _items.Length;
            public void Reset() => _index = -1;
            public void Dispose() { }
        }
    }

    /// <summary>
    /// 模拟"没有非泛型 IEnumerable，只有公开泛型 GetEnumerator()"的形状，
    /// 覆盖 ResolveGenericEnumerator 里"公开方法"这条分支。
    /// </summary>
    private sealed class GenericOnlyArray
    {
        private readonly object[] _items;
        public GenericOnlyArray(object[] items) => _items = items;
        public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)_items).GetEnumerator();
    }

    /// <summary>只有 Count 与索引器，没有 IEnumerable。</summary>
    private sealed class IndexerOnly
    {
        private readonly object?[] _items;
        public IndexerOnly(object?[] items) => _items = items;
        public int Count => _items.Length;
        public object? this[int index] => _items[index];
    }

    private static class FakeIl2CppTypeFactory
    {
        public static object From(Type type) => type;
        public static object From(Type type, bool throwOnFailure) => type;
        public static object Of<T>() => typeof(T);
    }
}
