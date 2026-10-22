using System.IO;
using System.Text.Json;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public sealed class GameLibrary
{
    private readonly string _filePath;
    private readonly List<GameProfile> _games;

    /// <summary>载入时是否对某些条目重新探测过（用于决定要不要回写）。</summary>
    private bool _refreshedOnLoad;

    public GameLibrary(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        _games = Load(_filePath);
        // 重判之后结论可能变了，落盘一次，避免每次启动都重算。
        if (_refreshedOnLoad) Save(_games);
    }

    public GameProfile Add(string path)
    {
        var profile = GameDetector.Detect(path);
        var index = _games.FindIndex(game => game.GameId == profile.GameId);
        var candidate = new List<GameProfile>(_games);
        if (index >= 0)
        {
            // ★ 保留之前**实测出来**的加载器结论 —— 重新探测只更新引擎信息，
            //   不该把"这款游戏 BepInEx 6 起不来"这个实机发现冲掉。
            profile = profile with
            {
                PreferredLoader = _games[index].PreferredLoader,
                CustomName = _games[index].CustomName
            };
            candidate[index] = profile;
        }
        else candidate.Add(profile);
        Commit(candidate);
        return profile;
    }

    /// <summary>修改卡片别名，空白恢复程序名。只有保存成功后才更新内存。</summary>
    public bool Rename(string gameId, string? name)
    {
        if (name?.Any(char.IsControl) == true)
            throw new ArgumentException("名称不能包含控制字符。", nameof(name));
        var alias = name?.Trim() ?? string.Empty;
        if (alias.Length > 80)
            throw new ArgumentException("名称最多 80 个字符。", nameof(name));
        var index = _games.FindIndex(game => game.GameId == gameId);
        if (index < 0 || string.Equals(_games[index].CustomName, alias, StringComparison.Ordinal)) return false;
        var candidate = new List<GameProfile>(_games);
        candidate[index] = candidate[index] with { CustomName = alias };
        Commit(candidate);
        return true;
    }

    /// <summary>只移除游戏列表记录；不删除游戏文件或翻译数据库。</summary>
    public bool Remove(string gameId)
    {
        var index = _games.FindIndex(game => game.GameId == gameId);
        if (index < 0) return false;
        var candidate = new List<GameProfile>(_games);
        candidate.RemoveAt(index);
        Commit(candidate);
        return true;
    }

    /// <summary>
    /// 记住"这款游戏实测该用哪一档加载器"。返回是否真的发生了变化
    /// （没变化就不重复落盘，也避免反复提示用户）。
    /// </summary>
    public bool RememberPreferredLoader(string gameId, string loader)
    {
        var index = _games.FindIndex(game => game.GameId == gameId);
        if (index < 0) return false;
        if (string.Equals(_games[index].PreferredLoader, loader, StringComparison.Ordinal)) return false;
        var candidate = new List<GameProfile>(_games);
        candidate[index] = candidate[index] with { PreferredLoader = loader };
        Commit(candidate);
        return true;
    }

    /// <summary>
    /// 取某款游戏"实测能跑"的加载器；没记录过返回空串（= 按 Unity 版本自动判断）。
    ///
    /// 为什么用 GameId 现查、而不是直接用传进来的 <see cref="GameProfile"/>：
    /// 界面上的游戏卡片是列表的**快照**，回退写进列表之后卡片对象还是旧的，
    /// 用卡片上的字段会稳稳地取到回退**之前**的值（就是刚修掉的那个 bug）。
    /// </summary>
    public string PreferredLoaderFor(string gameId) =>
        _games.Find(game => game.GameId == gameId)?.PreferredLoader ?? string.Empty;

    public IReadOnlyList<GameProfile> List() => _games.ToArray();

    private void Commit(List<GameProfile> candidate)
    {
        Save(candidate);
        _games.Clear();
        _games.AddRange(candidate);
    }

    private void Save(IReadOnlyList<GameProfile> games)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(games));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private List<GameProfile> Load(string filePath)
    {
        if (!File.Exists(filePath)) return [];
        List<GameProfile> saved;
        try
        {
            saved = JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(filePath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        // ★ 存档里存的是"上次探测的结论"，而**探测逻辑会随版本改进** —— 不能直接信它。
        //
        //   实测（第 44 轮）：把 IL2CPP 从"不支持"改成"可以翻译"之后，
        //   已经添加过 DECOY 的用户界面**仍然显示"当前版本尚未支持 IL2CPP 游戏"**，
        //   因为 App 启动时直接用 games.json 里的旧结论，从不重新探测。
        //   这不只是显示问题：卡片上连"安装翻译组件"都不给，用户根本装不上。
        //
        //   所以这里按存下来的路径**重判一次**。判不出来（游戏被删/移动）就保留原样，
        //   让界面照旧提示"找不到游戏文件夹"。
        var refreshed = new List<GameProfile>(saved.Count);
        foreach (var game in saved)
        {
            var path = !string.IsNullOrWhiteSpace(game.ExePath) ? game.ExePath : game.RootPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                refreshed.Add(game);
                continue;
            }

            try
            {
                var detected = GameDetector.Detect(path);
                // 只有 GameId 一致才替换，避免路径解析差异把两条记录混在一起。
                if (detected.GameId != game.GameId) { refreshed.Add(game); continue; }
                if (detected.IsCompatible != game.IsCompatible ||
                    detected.CompatibilityReason != game.CompatibilityReason ||
                    detected.Backend != game.Backend ||
                    detected.Architecture != game.Architecture ||
                    detected.UnityVersion != game.UnityVersion)
                {
                    _refreshedOnLoad = true;
                }
                // ★★ 重新探测**只更新引擎信息**，必须把"实测出来的加载器结论"带过去。
                //   漏了这一句，`PreferredLoader` 会在每次启动 App 时被清空 ——
                //   于是"这款游戏 BepInEx 6 起不来"这个实机发现永远存不住，
                //   用户每次都得再撞一次同样的崩溃。这一条是我加完字段才意识到的坑。
                refreshed.Add(detected with { PreferredLoader = game.PreferredLoader, CustomName = game.CustomName });
            }
            catch (Exception)
            {
                refreshed.Add(game);
            }
        }
        return refreshed;
    }
}
