using System.Reflection;

namespace UnityTranslator.Core.Tests;

/// <summary>
/// 针对**真实 IL2CPP 互操作程序集**的验证。这些程序集由 BepInEx 针对每款游戏生成，
/// 不可能随源码分发，所以用环境变量指向它们；没设置时整组测试自动跳过。
///
///   setx BEPINEX_IL2CPP_INTEROP "D:\path\to\Game\BepInEx\interop"
///   setx BEPINEX_IL2CPP_CORE    "D:\path\to\Game\BepInEx\core"
///
/// 注意：加载互操作程序集时，运行时会去解析它自己的依赖（Il2CppInterop.Runtime 等）。
/// 体外环境没有 BepInEx 预加载，所以这里临时挂一个解析钩子，用完立刻摘掉 ——
/// 不这么做的话，钩子会污染同进程里的其它测试。
/// </summary>
public sealed class UnityReflectionInteropTests
{
    private static string? InteropDirectory => Environment.GetEnvironmentVariable("BEPINEX_IL2CPP_INTEROP");
    private static string? CoreDirectory => Environment.GetEnvironmentVariable("BEPINEX_IL2CPP_CORE");

    private static bool InteropAvailable =>
        !string.IsNullOrWhiteSpace(InteropDirectory) &&
        File.Exists(Path.Combine(InteropDirectory!, "Unity.TextMeshPro.dll")) &&
        !string.IsNullOrWhiteSpace(CoreDirectory) &&
        File.Exists(Path.Combine(CoreDirectory!, "Il2CppInterop.Runtime.dll"));

    private sealed class DirectoryLookup : IAssemblyLookup
    {
        private readonly string[] _directories;
        public DirectoryLookup(params string[] directories) => _directories = directories;

        public Assembly? Find(string simpleName)
        {
            foreach (var directory in _directories)
            {
                var candidate = Path.Combine(directory, simpleName + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }
    }

    private sealed class EmptyLookup : IAssemblyLookup
    {
        public Assembly? Find(string simpleName) => null;
    }

    /// <summary>临时挂上解析钩子跑一段代码，结束后摘掉。</summary>
    private static void WithResolver(IAssemblyLookup lookup, Action<UnityReflection> body)
    {
        ResolveEventHandler handler = (_, args) =>
        {
            var requested = new AssemblyName(args.Name).Name;
            return string.IsNullOrEmpty(requested) ? null : lookup.Find(requested);
        };
        AppDomain.CurrentDomain.AssemblyResolve += handler;
        try
        {
            body(new UnityReflection(lookup));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= handler;
        }
    }

    [Fact]
    public void ResolvesTheRealTmpTextTypeFromGeneratedInteropAssemblies()
    {
        if (!InteropAvailable) return;   // 没有互操作程序集的机器上跳过

        WithResolver(new DirectoryLookup(CoreDirectory!, InteropDirectory!), reflection =>
        {
            var textType = reflection.TextComponentType;
            Assert.NotNull(textType);
            Assert.Equal("TMPro", textType!.Namespace);

            // 必须能读写 text、读取 font —— 这是插件全部功能的基础
            var textProperty = textType.GetProperty("text");
            Assert.NotNull(textProperty);
            Assert.True(textProperty!.CanRead);
            Assert.True(textProperty.CanWrite);
            Assert.NotNull(textType.GetProperty("font"));

            // 必须是 Il2CppObjectBase 的后代，否则 Il2CppType.From 那类调用用不了
            var runtime = reflection.LoadAssembly("Il2CppInterop.Runtime");
            Assert.NotNull(runtime);
            var objectBase = runtime!.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");
            Assert.NotNull(objectBase);
            Assert.True(objectBase!.IsAssignableFrom(textType),
                "TMP 文字组件应当是 Il2CppObjectBase 的后代");
        });
    }

    [Fact]
    public void RealIl2CppArrayTypeIsEnumerableWithCount()
    {
        if (!InteropAvailable) return;

        WithResolver(new DirectoryLookup(CoreDirectory!, InteropDirectory!), reflection =>
        {
            var runtime = reflection.LoadAssembly("Il2CppInterop.Runtime");
            Assert.NotNull(runtime);
            var referenceArray = runtime!.GetType(
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1");
            Assert.NotNull(referenceArray);

            // 枚举逻辑依赖这两个事实：非泛型 IEnumerable + Count 属性
            Assert.True(typeof(System.Collections.IEnumerable).IsAssignableFrom(referenceArray!));
            Assert.NotNull(referenceArray!.GetProperty("Count"));
        });
    }

    [Fact]
    public void RealResourcesTypeExposesTheTypeOverloadUsedToFindTextComponents()
    {
        if (!InteropAvailable) return;

        WithResolver(new DirectoryLookup(CoreDirectory!, InteropDirectory!), reflection =>
        {
            var resources = reflection.ResolveType("UnityEngine.CoreModule", "UnityEngine.Resources");
            Assert.NotNull(resources);

            // 插件靠 FindObjectsOfTypeAll(<Type>) 找文字组件
            var overloads = resources!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "FindObjectsOfTypeAll")
                .ToArray();
            Assert.NotEmpty(overloads);

            var singleParameter = overloads.Where(m => m.GetParameters().Length == 1).ToArray();
            Assert.NotEmpty(singleParameter);

            // 重要事实：参数类型是 Il2CppSystem.Type，不是 System.Type。
            // 反射查找时若按 typeof(System.Type) 精确匹配会找不到 —— 实测踩过。
            var parameter = singleParameter[0].GetParameters()[0].ParameterType;
            Assert.Equal("Type", parameter.Name);
            Assert.NotEqual(typeof(Type), parameter);
            Assert.Contains("Il2Cpp", parameter.Namespace ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RealIl2CppTypeExposesTheNonGenericSystemTypeFactory()
    {
        if (!InteropAvailable) return;

        WithResolver(new DirectoryLookup(CoreDirectory!, InteropDirectory!), reflection =>
        {
            var factory = reflection.ResolveType("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Il2CppType");
            Assert.NotNull(factory);

            var method = UnityReflection.ResolveIl2CppTypeFromSystemType(factory!);
            Assert.NotNull(method);
            Assert.False(method!.IsGenericMethod);
            Assert.Equal("From", method.Name);
            Assert.Equal(typeof(Type), Assert.Single(method.GetParameters()).ParameterType);
            Assert.Equal("Il2CppSystem.Type", method.ReturnType.FullName);
        });
    }

    [Fact]
    public void InjectedAssemblyLookupDoesNotFallBackToUnrelatedLoadedAssemblies()
    {
        if (!InteropAvailable) return;

        WithResolver(new DirectoryLookup(CoreDirectory!, InteropDirectory!), reflection =>
        {
            Assert.NotNull(reflection.ResolveType("Unity.TextMeshPro", "TMPro.TextMeshProUGUI"));

            var isolated = new UnityReflection(new EmptyLookup());
            Assert.Null(isolated.ResolveType("Unity.TextMeshPro", "TMPro.TextMeshProUGUI"));
        });
    }
}
