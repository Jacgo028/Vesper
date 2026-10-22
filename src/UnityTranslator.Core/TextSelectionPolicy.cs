// 注意：这个文件与插件项目共享编译（Mono 插件 netstandard2.1、IL2CPP 插件 net6.0），
// 它们都关闭了 ImplicitUsings，所以这里必须显式写 using。
using System;

namespace UnityTranslator.Core;

/// <summary>
/// 决定一段游戏文字该不该送去翻译。平台无关 —— Mono 与 IL2CPP 插件共用同一份判定，
/// 避免两边行为不一致。
///
/// 判定规则（V0.4 修订）：
///   * 含假名 → 日文，送翻译；
///   * 含汉字但无假名 → **送翻译**（这是 V0.4 的关键改动，见下）；
///   * 只有数字/标点/符号 → 没有可翻译内容，不送；
///   * 只有拉丁字母 → 送翻译。
///
/// 为什么"含汉字但无假名"要改成送翻译：
///   V0.3 及以前把它当成"已经是中文"而直接跳过。理由是怕把中文界面二次翻译。
///   但真实的日语游戏里，**纯汉字、不带假名**的字符串极其常见：
///   設定 / 装備 / 名前 / 体力 / 攻撃力 / 経験値 / 道具 / 魔法 / 確認 / 戻る …
///   这类词在 V0.3 里全部被跳过，菜单和界面看不到任何翻译 ——
///   这正是"翻译功能几乎不可用"最主要的来源。
///
///   而"二次翻译中文"这个担心是**下游已经被拦住的**，不需要在这里拦：
///   桌面端 <c>TranslationCoordinator</c> 拿到译文后会走 <c>TextSafety.IsSafe</c>，
///   其中一条就是"原文与译文完全相同 → 判为无效、不回给游戏"。
///   也就是说中文原文送过去、翻译服务原样返回时，游戏侧什么都不会发生。
///   代价只是一次多余的请求，换来的是日语游戏界面真正能被翻译。
/// </summary>
public static class TextSelectionPolicy
{
    /// <summary>这段文字是否值得送去翻译。</summary>
    public static bool IsTranslatable(string? text, int minLength = 1, int maxLength = 400)
    {
        // 不用 string.IsNullOrWhiteSpace：那是 .NET 4.0 才有的，
        // 精简变体跑在 CLR 2.0 上会直接编译失败。见进展记录 B83。
        if (text == null || text.Trim().Length == 0) return false;
        if (text.Length < minLength || text.Length > maxLength) return false;

        var hasLatinLetter = false;
        var hasCjk = false;

        foreach (var c in text)
        {
            if (IsKana(c) || IsHan(c)) hasCjk = true;                    // 日文/中文都可能
            else if (c >= 'A' && c <= 'Z') hasLatinLetter = true;
            else if (c >= 'a' && c <= 'z') hasLatinLetter = true;
        }

        // 汉字或假名：一律送翻译。
        // 中文原文送过去只会被原样翻回来，随后被 TextSafety 判为无效丢弃，不会造成误翻。
        if (hasCjk) return true;

        // 剩下的只有拉丁字母才值得翻（纯数字/标点会被 hasLatinLetter 排除）
        return hasLatinLetter;
    }

    /// <summary>
    /// 推断源语言：有假名按日语，否则按英语。
    ///
    /// 注意：纯汉字文本这里仍然返回 "en"。这是**故意的**：
    /// 真正决定目标语言的是桌面端给模型的提示词，而模型对"把日文翻成中文"和
    /// "把英文翻成中文"都能正确处理纯汉字输入；而唯一会被这个返回值影响的是
    /// 缓存分桶（language 字段）。把纯汉字固定放进 en 桶，可以避免"同一句汉字
    /// 一会儿被当日语、一会儿被当英语"造成缓存分裂或反复请求。
    /// 真正需要按语言区分时，应该由用户在界面上显式指定源语言，而不是靠猜。
    /// </summary>
    public static string DetectLanguage(string text)
    {
        if (string.IsNullOrEmpty(text)) return "en";
        foreach (var c in text)
            if (IsKana(c)) return "ja";
        return "en";
    }

    /// <summary>平假名、片假名与片假名扩展。</summary>
    private static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u30ff') || (c >= '\u31f0' && c <= '\u31ff');

    /// <summary>
    /// 汉字。除了基本区，还包含扩展 A 区与兼容汉字 ——
    /// 日语游戏里偶尔会出现这些区的生僻字，漏掉会让整句被当成"没有可翻译内容"。
    /// </summary>
    private static bool IsHan(char c) =>
        (c >= '\u4e00' && c <= '\u9fff') ||     // CJK 统一表意文字
        (c >= '\u3400' && c <= '\u4dbf') ||     // 扩展 A
        (c >= '\uf900' && c <= '\ufaff');       // 兼容表意文字

    /// <summary>
    /// 该不该重试发送。指数退避，上限 60 秒 —— 与旧实现保持一致，
    /// 避免因为插件重写而改变重试节奏。
    /// </summary>
    public static bool RetryDue(int attempts, float lastSentAt, float now) =>
        attempts > 0 && now - lastSentAt >= Math.Min(60f, 10f * (float)Math.Pow(2, Math.Min(attempts - 1, 3)));
}
