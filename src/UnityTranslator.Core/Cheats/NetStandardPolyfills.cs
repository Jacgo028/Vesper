// 这份文件被链接进 netstandard2.1 的游戏插件。
// netstandard2.1 没有 IsExternalInit，C# 9 的 record / init 访问器依赖它；
// net8.0 的 Core 已经自带该类型，因此只在缺少它的目标框架下补充定义。
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
