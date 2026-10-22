using UnityTranslator.Core.Cheats;

namespace UnityTranslator.Core.Tests.Cheats;

/// <summary>
/// 预设的存取与失效判定。重点：
/// - 中文、日文游戏名与快捷项名称必须能原样往返；
/// - 字段预设只有在“恰好一个匹配 + 游戏版本一致”时才可以直接写入；
/// - 内存搜索预设永远不跨启动复用地址。
/// </summary>
public sealed class CheatPresetStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ut-presets-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        foreach (var candidate in new[] { _path, _path + ".tmp" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private CheatPresetStore NewStore() => new CheatPresetStore(_path);

    private static FieldCandidate Candidate(string objectPath, string componentType, string fieldName,
        NumericKind kind, string value) => new FieldCandidate(objectPath, componentType, fieldName, kind, value);

    [Fact]
    public void UnicodeGameAndPresetNamesRoundTrip()
    {
        var preset = CheatPreset.Field("クレア・ストラグル", "6000.0.58f2", "金钱（金币）", "Main/Player/UI",
            "PlayerStats", "gold", NumericKind.Int64);

        NewStore().Save(preset);
        var loaded = Assert.Single(NewStore().List("クレア・ストラグル"));

        Assert.Equal("金钱（金币）", loaded.Name);
        Assert.Equal("Main/Player/UI", loaded.ObjectPath);
        Assert.Equal("PlayerStats", loaded.ComponentType);
        Assert.Equal("gold", loaded.FieldName);
        Assert.Equal(NumericKind.Int64, loaded.Kind);
        Assert.Equal(CheatPresetSource.UnityField, loaded.Source);
    }

    [Fact]
    public void SavingTheSameNameOverwritesInsteadOfDuplicating()
    {
        var store = NewStore();
        store.Save(CheatPreset.Field("g1", "1", "金钱", "A", "B", "gold", NumericKind.Int32));
        store.Save(CheatPreset.Field("g1", "1", "金钱", "A2", "B2", "gold2", NumericKind.Int64));

        var only = Assert.Single(store.List("g1"));
        Assert.Equal("A2", only.ObjectPath);
        Assert.Equal(NumericKind.Int64, only.Kind);
    }

    [Fact]
    public void PresetsArePartitionedByGameId()
    {
        var store = NewStore();
        store.Save(CheatPreset.Field("game-a", "1", "金钱", "A", "B", "gold", NumericKind.Int32));
        store.Save(CheatPreset.Field("game-b", "1", "金钱", "A", "B", "gold", NumericKind.Int32));

        Assert.Single(store.List("game-a"));
        Assert.Single(store.List("game-b"));
        Assert.Equal(2, store.List(string.Empty).Count);
    }

    [Fact]
    public void RemovingAPresetDeletesOnlyThatOne()
    {
        var store = NewStore();
        store.Save(CheatPreset.Field("g1", "1", "金钱", "A", "B", "gold", NumericKind.Int32));
        store.Save(CheatPreset.Field("g1", "1", "生命", "A", "B", "hp", NumericKind.Int32));

        Assert.True(store.Remove("g1", "金钱", CheatPresetSource.UnityField));

        var remaining = Assert.Single(store.List("g1"));
        Assert.Equal("生命", remaining.Name);
        Assert.False(store.Remove("g1", "不存在", CheatPresetSource.UnityField));
    }

    [Fact]
    public void UniqueMatchWithSameVersionIsReady()
    {
        var preset = CheatPreset.Field("g1", "6000.0.58f2", "金钱", "Main/Player", "PlayerStats", "gold",
            NumericKind.Int32);
        var fields = new[] { Candidate("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120") };

        var resolution = NewStore().Resolve(preset, fields, "6000.0.58f2");

        Assert.Equal(PresetStatus.Ready, resolution.Status);
        Assert.NotNull(resolution.Field);
        Assert.Equal("120", resolution.Field!.DisplayValue);
        Assert.True(resolution.IsReady);
    }

    [Fact]
    public void DuplicateMatchesNeedConfirmation()
    {
        var preset = CheatPreset.Field("g1", "1", "金钱", "Main/Player", "PlayerStats", "gold", NumericKind.Int32);
        var fields = new[]
        {
            Candidate("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "120"),
            Candidate("Main/Enemy", "PlayerStats", "gold", NumericKind.Int32, "999")
        };

        var resolution = NewStore().Resolve(preset, fields, "1");

        Assert.Equal(PresetStatus.NeedsConfirmation, resolution.Status);
        Assert.Null(resolution.Field);
        Assert.Contains("无法确定", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFieldNeedsConfirmation()
    {
        var preset = CheatPreset.Field("g1", "1", "金钱", "Main/Player", "PlayerStats", "gold", NumericKind.Int32);

        var resolution = NewStore().Resolve(preset, Array.Empty<FieldCandidate>(), "1");

        Assert.Equal(PresetStatus.NeedsConfirmation, resolution.Status);
        Assert.Null(resolution.Field);
        Assert.Contains("没有找到", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedFieldTypeDoesNotMatch()
    {
        var preset = CheatPreset.Field("g1", "1", "金钱", "Main/Player", "PlayerStats", "gold", NumericKind.Int32);
        var fields = new[] { Candidate("Main/Player", "PlayerStats", "gold", NumericKind.Int64, "120") };

        var resolution = NewStore().Resolve(preset, fields, "1");

        Assert.Equal(PresetStatus.NeedsConfirmation, resolution.Status);
        Assert.Null(resolution.Field);
    }

    [Fact]
    public void ChangedUnityVersionNeedsConfirmationEvenWhenUnique()
    {
        var preset = CheatPreset.Field("g1", "2022.3.10f1", "生命", "Main/Player", "PlayerStats", "hp",
            NumericKind.Float32);
        var fields = new[] { Candidate("Main/Player", "PlayerStats", "hp", NumericKind.Float32, "100") };

        var resolution = NewStore().Resolve(preset, fields, "6000.0.58f2");

        Assert.Equal(PresetStatus.NeedsConfirmation, resolution.Status);
        Assert.Contains("版本", resolution.Reason, StringComparison.Ordinal);
        // 仍然给出候选，但必须由用户确认，不能直接写。
        Assert.NotNull(resolution.Field);
        Assert.False(resolution.IsReady);
    }

    [Fact]
    public void MemorySearchPresetAlwaysRequiresARescan()
    {
        var store = NewStore();
        var preset = CheatPreset.Memory("g1", "1", "金钱扫描", 12345, "第一次扫描 12345");
        store.Save(preset);

        var loaded = Assert.Single(store.List("g1"));
        Assert.Equal(CheatPresetSource.MemorySearch, loaded.Source);
        Assert.Equal(12345, loaded.ScanValue);
        Assert.Equal("第一次扫描 12345", loaded.Note);

        // 即使游戏里恰好有同名同类型的字段，内存预设也绝不复用地址。
        var fields = new[] { Candidate("Main/Player", "PlayerStats", "gold", NumericKind.Int32, "12345") };
        var resolution = store.Resolve(loaded, fields, "1");

        Assert.Equal(PresetStatus.RescanRequired, resolution.Status);
        Assert.Null(resolution.Field);
    }

    [Fact]
    public void PresetFileNeverContainsAMemoryAddress()
    {
        var store = NewStore();
        store.Save(CheatPreset.Memory("g1", "1", "金钱扫描", 999, "注释"));

        var text = File.ReadAllText(_path);

        Assert.DoesNotContain("Address", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("999", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptedFileIsTreatedAsEmptyAndDoesNotThrow()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = NewStore();

        Assert.Empty(store.List("g1"));
        // 仍然可以正常写入，坏文件被整体替换。
        store.Save(CheatPreset.Field("g1", "1", "金钱", "A", "B", "gold", NumericKind.Int32));
        Assert.Single(store.List("g1"));
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        var store = NewStore();
        store.Save(CheatPreset.Field("g1", "1", "金钱", "A", "B", "gold", NumericKind.Int32));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void EmptyFilePathIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CheatPresetStore("   "));
    }

    [Fact]
    public void PresetWithoutNameIsRejected()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Save(
            CheatPreset.Field("g1", "1", "  ", "A", "B", "gold", NumericKind.Int32)));
    }
}
