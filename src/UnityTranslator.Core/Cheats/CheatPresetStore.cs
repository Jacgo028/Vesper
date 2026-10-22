using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityTranslator.Core.Cheats;

public enum CheatPresetSource
{
    /// <summary>Unity 字段预设：保存可核查的字段描述，下次启动重新定位。</summary>
    UnityField,

    /// <summary>内存搜索预设：只保存搜索条件，绝不保存地址。</summary>
    MemorySearch
}

/// <summary>
/// 一条快捷修改项。
///
/// 跨启动可复用的只有“描述”和“搜索条件”：
/// - UnityField 用对象路径 + 组件类型 + 字段名 + 数值类型重新定位；
/// - MemorySearch 只记录扫描类型与用户输入的数值，重启后必须重新扫描。
/// 任何情况下都不把内存地址写进预设文件。
/// </summary>
public sealed record CheatPreset(
    string GameId,
    string UnityVersion,
    string Name,
    CheatPresetSource Source,
    string ObjectPath,
    string ComponentType,
    string FieldName,
    NumericKind Kind,
    int ScanValue,
    string Note)
{
    public static CheatPreset Field(string gameId, string unityVersion, string name, string objectPath,
        string componentType, string fieldName, NumericKind kind) =>
        new CheatPreset(gameId, unityVersion, name, CheatPresetSource.UnityField, objectPath, componentType,
            fieldName, kind, 0, string.Empty);

    public static CheatPreset Memory(string gameId, string unityVersion, string name, int scanValue, string note) =>
        new CheatPreset(gameId, unityVersion, name, CheatPresetSource.MemorySearch, string.Empty, string.Empty,
            string.Empty, NumericKind.Int32, scanValue, note ?? string.Empty);

    /// <summary>持久化标识：同游戏同来源同名视为同一条，保存时覆盖。</summary>
    public string Key => GameId + "|" + Source + "|" + Name;
}

public enum PresetStatus
{
    /// <summary>唯一命中，可以按预设直接写入。</summary>
    Ready,

    /// <summary>需要用户重新确认：出现多个匹配、类型变化或游戏版本变化。</summary>
    NeedsConfirmation,

    /// <summary>内存搜索预设：重启后地址失效，必须重新扫描。</summary>
    RescanRequired
}

public sealed record PresetResolution(PresetStatus Status, FieldCandidate? Field, string Reason)
{
    public bool IsReady => Status == PresetStatus.Ready;
}

/// <summary>
/// 按游戏分区的预设存储。写文件使用“临时文件 + 替换”，避免中途失败留下半截 JSON。
/// </summary>
public sealed class CheatPresetStore
{
    private const int FormatVersion = 1;
    private readonly string _filePath;

    public CheatPresetStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("预设文件路径不能为空", nameof(filePath));
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public IReadOnlyList<CheatPreset> List(string gameId)
    {
        var all = Load();
        if (string.IsNullOrEmpty(gameId)) return all;
        var result = new List<CheatPreset>();
        foreach (var preset in all)
            if (string.Equals(preset.GameId, gameId, StringComparison.Ordinal)) result.Add(preset);
        return result;
    }

    /// <summary>保存或覆盖一条预设；游戏名、字段名等可以包含中文与日文。</summary>
    public void Save(CheatPreset preset)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (string.IsNullOrWhiteSpace(preset.GameId)) throw new ArgumentException("预设缺少游戏标识", nameof(preset));
        if (string.IsNullOrWhiteSpace(preset.Name)) throw new ArgumentException("预设缺少名称", nameof(preset));
        var all = new List<CheatPreset>(Load());
        var key = preset.Key;
        var replaced = false;
        for (var index = 0; index < all.Count; index++)
        {
            if (!string.Equals(all[index].Key, key, StringComparison.Ordinal)) continue;
            all[index] = preset;
            replaced = true;
            break;
        }
        if (!replaced) all.Add(preset);
        Persist(all);
    }

    public bool Remove(string gameId, string name, CheatPresetSource source)
    {
        var all = new List<CheatPreset>(Load());
        var key = gameId + "|" + source + "|" + name;
        var removed = false;
        for (var index = all.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(all[index].Key, key, StringComparison.Ordinal)) continue;
            all.RemoveAt(index);
            removed = true;
        }
        if (removed) Persist(all);
        return removed;
    }

    /// <summary>
    /// 用当前游戏里的字段候选重新定位预设。只有恰好一个匹配才返回 Ready。
    /// </summary>
    public PresetResolution Resolve(CheatPreset preset, IReadOnlyList<FieldCandidate>? fields,
        string currentUnityVersion)
    {
        if (preset is null) throw new ArgumentNullException(nameof(preset));
        if (preset.Source == CheatPresetSource.MemorySearch)
            return new PresetResolution(PresetStatus.RescanRequired, null,
                "内存地址在游戏重启后不再有效，请重新扫描并确认。");

        var candidates = new List<FieldCandidate>();
        if (fields is not null)
        {
            foreach (var field in fields)
            {
                if (!string.Equals(field.FieldName, preset.FieldName, StringComparison.Ordinal)) continue;
                if (field.Kind != preset.Kind) continue;
                candidates.Add(field);
            }
        }
        if (candidates.Count == 0)
            return new PresetResolution(PresetStatus.NeedsConfirmation, null,
                $"这一版游戏里没有找到字段 {preset.FieldName}，请重新识别。");
        if (candidates.Count > 1)
            return new PresetResolution(PresetStatus.NeedsConfirmation, null,
                $"字段 {preset.FieldName} 有 {candidates.Count} 处候选，无法确定要改哪一个，请重新选择。");
        if (!string.Equals(preset.UnityVersion, currentUnityVersion, StringComparison.Ordinal))
            return new PresetResolution(PresetStatus.NeedsConfirmation, candidates[0],
                $"游戏版本从 {preset.UnityVersion} 变为 {currentUnityVersion}，请确认后再修改。");
        return new PresetResolution(PresetStatus.Ready, candidates[0], "预设已重新定位。");
    }

    private List<CheatPreset> Load()
    {
        if (!File.Exists(_filePath)) return new List<CheatPreset>();
        string text;
        try
        {
            text = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<CheatPreset>();
        }
        if (!MiniJson.TryParse(text, out var root) || !root.IsObject) return new List<CheatPreset>();
        if (!root.TryGet("Presets", out var presets) || !presets.IsArray) return new List<CheatPreset>();
        var result = new List<CheatPreset>();
        foreach (var item in presets.Items)
        {
            var preset = ReadPreset(item);
            if (preset is not null) result.Add(preset);
        }
        return result;
    }

    private static CheatPreset? ReadPreset(MiniJsonValue item)
    {
        if (!item.IsObject) return null;
        if (!item.TryGetString("GameId", out var gameId) || string.IsNullOrEmpty(gameId)) return null;
        if (!item.TryGetString("Name", out var name) || string.IsNullOrEmpty(name)) return null;
        var unityVersion = item.TryGetString("UnityVersion", out var version) ? version : string.Empty;
        var sourceText = item.TryGetString("Source", out var rawSource) ? rawSource : string.Empty;
        var source = string.Equals(sourceText, nameof(CheatPresetSource.MemorySearch), StringComparison.Ordinal)
            ? CheatPresetSource.MemorySearch
            : CheatPresetSource.UnityField;
        var objectPath = item.TryGetString("ObjectPath", out var rawPath) ? rawPath : string.Empty;
        var componentType = item.TryGetString("ComponentType", out var rawComponent) ? rawComponent : string.Empty;
        var fieldName = item.TryGetString("FieldName", out var rawField) ? rawField : string.Empty;
        var kindText = item.TryGetString("Kind", out var rawKind) ? rawKind : string.Empty;
        var kind = NumericKind.Int32;
        foreach (var enumValue in Enum.GetValues(typeof(NumericKind)))
            if (enumValue.ToString() == kindText) kind = (NumericKind)enumValue;
        var scanValue = item.TryGetInt32("ScanValue", out var rawScan) ? rawScan : 0;
        var note = item.TryGetString("Note", out var rawNote) ? rawNote : string.Empty;
        return new CheatPreset(gameId, unityVersion, name, source, objectPath, componentType, fieldName, kind,
            scanValue, note);
    }

    private void Persist(List<CheatPreset> presets)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var builder = new StringBuilder(1024);
        builder.Append("{\"Version\":").Append(MiniJson.WriteInt(FormatVersion)).Append(",\"Presets\":[");
        for (var index = 0; index < presets.Count; index++)
        {
            if (index > 0) builder.Append(',');
            Append(builder, presets[index]);
        }
        builder.Append("]}");

        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
        // File.Move(overwrite) 在 Windows 上是原子替换，失败时原文件保持不变。
        File.Move(temporary, _filePath, true);
    }

    private static void Append(StringBuilder builder, CheatPreset preset)
    {
        builder.Append("{\"GameId\":").Append(MiniJson.Text(preset.GameId));
        builder.Append(",\"UnityVersion\":").Append(MiniJson.Text(preset.UnityVersion));
        builder.Append(",\"Name\":").Append(MiniJson.Text(preset.Name));
        builder.Append(",\"Source\":").Append(MiniJson.Text(preset.Source.ToString()));
        builder.Append(",\"ObjectPath\":").Append(MiniJson.Text(preset.ObjectPath));
        builder.Append(",\"ComponentType\":").Append(MiniJson.Text(preset.ComponentType));
        builder.Append(",\"FieldName\":").Append(MiniJson.Text(preset.FieldName));
        builder.Append(",\"Kind\":").Append(MiniJson.Text(preset.Kind.ToString()));
        builder.Append(",\"ScanValue\":").Append(MiniJson.WriteInt(preset.ScanValue));
        builder.Append(",\"Note\":").Append(MiniJson.Text(preset.Note));
        builder.Append('}');
    }
}
