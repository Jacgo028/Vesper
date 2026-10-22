using System;
using System.Collections.Generic;
using System.Reflection;
using UnityTranslator.Core.Cheats;
using UnityEngine;

namespace UnityTranslator.Plugin.Cheats;

/// <summary>
/// Unity 运行时数值字段的发现与安全写入。
///
/// 三条约束贯穿整份实现：
/// 1. 只在 Unity 主线程、按每帧预算分批扫描，不在主线程做大规模反射。
/// 2. 只读取当前可访问对象的实例字段，不调用属性 getter、不调用游戏方法。
/// 3. 写入前必须重新定位到唯一字段，并核对类型与用户确认过的当前值。
/// </summary>
public sealed class UnityFieldProbe
{
    private const int MaxCandidates = 400;
    private const int MaxQueryResults = 400;

    private readonly HashSet<int> _seen = new HashSet<int>();

    /// <summary>同一路径+组件类型+字段名只保留一条候选，避免结果里出现重复行。</summary>
    private readonly Dictionary<string, Discovered> _catalog = new Dictionary<string, Discovered>(StringComparer.Ordinal);

    /// <summary>已经完成反射的组件数量，用于界面显示进度。</summary>
    public int ScannedObjects { get; private set; }

    /// <summary>候选数量触到上限，界面需要提示用户用搜索缩小范围。</summary>
    public bool Truncated { get; private set; }

    public int CandidateCount => _catalog.Count;

    /// <summary>被跳过的组件数量，通常是反射抛异常的第三方脚本。</summary>
    public int SkippedComponents { get; private set; }

    /// <summary>
    /// 扫描一批新出现的组件。每帧调用，<paramref name="maxObjects"/> 是本次最多反射多少个组件。
    /// </summary>
    public void Tick(int maxObjects)
    {
        if (maxObjects < 1) return;
        MonoBehaviour[] components;
        try
        {
            components = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        }
        catch (Exception)
        {
            return;
        }

        var budget = maxObjects;
        foreach (var component in components)
        {
            if (budget <= 0) break;
            if (component == null) continue;
            int instanceId;
            try
            {
                instanceId = component.GetInstanceID();
                // 已经扫描过的对象直接跳过；这里放在反射之前，是每帧的主要开销来源。
                if (_seen.Contains(instanceId)) continue;
                if (!component.gameObject.activeInHierarchy) continue;
            }
            catch (Exception)
            {
                continue;
            }
            budget--;
            _seen.Add(instanceId);
            Scan(component);
        }
    }

    private void Scan(MonoBehaviour component)
    {
        Type type;
        string objectPath;
        string componentType;
        try
        {
            type = component.GetType();
            componentType = type.Name;
            objectPath = BuildPath(component);
        }
        catch (Exception)
        {
            SkippedComponents++;
            return;
        }

        var discovered = new List<Discovered>();
        for (var current = type; current != null && current != typeof(MonoBehaviour) &&
             current != typeof(Behaviour) && current != typeof(Component) && current != typeof(UnityEngine.Object);
             current = current.BaseType)
        {
            FieldInfo[] fields;
            try
            {
                fields = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                SkippedComponents++;
                return;
            }
            foreach (var field in fields)
            {
                if (!NumericValue.TryKindOf(field.FieldType, out var kind)) continue;
                if (field.IsInitOnly || field.IsLiteral) continue;   // readonly / const 不写入
                if (field.IsStatic) continue;                         // 静态字段不写入
                object? raw;
                try
                {
                    raw = field.GetValue(component);
                }
                catch (Exception)
                {
                    SkippedComponents++;
                    continue;
                }
                var display = FormatValue(raw, kind);
                if (display is null) continue;                        // NaN / Infinity 不进候选
                discovered.Add(new Discovered(component, field, objectPath, componentType, kind, display));
            }
        }

        ScannedObjects++;
        foreach (var item in discovered)
        {
            var key = KeyOf(item.ObjectPath, item.ComponentType, item.FieldName);
            if (_catalog.TryGetValue(key, out var existing))
            {
                // 同一路径 + 组件类型 + 字段名出现第二次：可能是同一个 GameObject 上挂了两个同类组件，
                // 也可能是两个不同对象被折叠成同一层级路径。两种情况下都无法确定用户要改哪一个，
                // 因此把该候选标记为有歧义，写入时一定会被拒绝。
                _catalog[key] = existing.AsAmbiguous();
                continue;
            }
            if (_catalog.Count >= MaxCandidates)
            {
                Truncated = true;
                break;
            }
            _catalog.Add(key, item);
        }
    }

    /// <summary>按名字过滤返回当前候选。<paramref name="nameFilter"/> 为空时返回全部候选。</summary>
    public List<FieldSnapshot> Query(string? nameFilter)
    {
        var filter = nameFilter ?? string.Empty;
        var result = new List<FieldSnapshot>();
        foreach (var item in _catalog.Values)
        {
            var display = CurrentDisplay(item);
            if (display is null) continue;
            if (filter.Length > 0 && !Matches(item, filter)) continue;
            result.Add(new FieldSnapshot(TokenOf(item), item.ObjectPath, item.ComponentType, item.FieldName,
                item.Kind, display));
            if (result.Count >= MaxQueryResults)
            {
                Truncated = true;
                break;
            }
        }
        result.Sort(CompareSnapshots);
        return result;
    }

    /// <summary>
    /// 按可核查描述写入字段。返回的结果里 <see cref="CheatFieldWriteResult.IsOk"/> 表示写入成功。
    /// </summary>
    public CheatFieldWriteResult Write(CheatFieldWriteRequest request)
    {
        if (request is null)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Unsupported, "写入请求为空");

        var target = request.Target;
        var kind = target.Kind;
        var candidates = DescribeFor(target);

        // 先做与平台无关的判定，保证界面与插件使用同一套规则。
        var resolution = FieldWriteGuard.Resolve(candidates, target);
        var precheck = FieldWriteGuard.Evaluate(resolution, kind,
            NumericValue.Format(request.ExpectedBytes, kind), NumericValue.Format(request.NewBytes, kind),
            isStatic: false, isReadonly: false, isProperty: false);
        if (!precheck.IsOk) return precheck;

        var key = KeyOf(target.ObjectPath, target.ComponentType, target.FieldName);
        if (!_catalog.TryGetValue(key, out var live))
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.NotFound,
                $"字段 {target.FieldName} 的对象已经不存在，请重新识别");
        if (live.Ambiguous || live.Component is null || live.Field is null)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Ambiguous,
                $"字段 {target.FieldName} 对应多个对象或已经消失，未执行写入");

        var field = live.Field;
        // 反射层面的最终核对：仍是实例字段、类型没变、当前值仍是用户确认过的值。
        if (field.IsStatic)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.NotWritable, "静态字段不修改");
        if (field.IsInitOnly || field.IsLiteral)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.NotWritable, "只读字段不修改");
        if (!NumericValue.TryKindOf(field.FieldType, out var liveKind) || liveKind != kind)
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Unsupported, "字段类型已变化，请重新识别");

        object? current;
        try
        {
            current = field.GetValue(live.Component);
        }
        catch (Exception ex)
        {
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.NotFound, "无法读取字段当前值：" + ex.Message);
        }
        if (!MatchesExpected(current, request.ExpectedBytes, kind))
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.ValueChanged,
                $"字段当前值已变成 {FormatValue(current, kind) ?? "无法读取"}，与确认时的数值不同");

        try
        {
            field.SetValue(live.Component, ToBoxed(request.NewBytes, kind));
        }
        catch (Exception ex)
        {
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Refused, "字段写入失败：" + ex.Message);
        }

        // 写入后回读核对，确认值真的落到了对象上。
        object? after;
        try
        {
            after = field.GetValue(live.Component);
        }
        catch (Exception ex)
        {
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Refused, "写入后无法回读：" + ex.Message);
        }
        if (!MatchesExpected(after, request.NewBytes, kind))
            return CheatFieldWriteResult.Fail(CheatFieldWriteReplyStatus.Refused, "写入后数值未能通过核对");

        var snapshot = new FieldSnapshot(TokenOf(live), live.ObjectPath, live.ComponentType, live.FieldName,
            kind, NumericValue.Format(request.NewBytes, kind));
        return CheatFieldWriteResult.Success(snapshot, request.NewBytes);
    }

    private List<FieldSnapshot> DescribeFor(CheatFieldWriteTarget target)
    {
        var result = new List<FieldSnapshot>();
        foreach (var item in _catalog.Values)
        {
            if (!string.Equals(item.FieldName, target.FieldName, StringComparison.Ordinal)) continue;
            var display = CurrentDisplay(item);
            result.Add(new FieldSnapshot(TokenOf(item), item.ObjectPath, item.ComponentType, item.FieldName,
                item.Kind, display ?? item.DisplayValue));
        }
        return result;
    }

    private static bool Matches(Discovered item, string filter) =>
        item.FieldName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
        item.ComponentType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
        item.ObjectPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? CurrentDisplay(Discovered item)
    {
        if (item.Ambiguous || item.Component == null || item.Field == null) return null;
        try
        {
            return FormatValue(item.Field.GetValue(item.Component), item.Kind);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool MatchesExpected(object? current, byte[] expected, NumericKind kind)
    {
        var display = FormatValue(current, kind);
        if (display is null || !NumericValue.TryParse(display, kind, out var bytes) || bytes.Length != expected.Length)
            return false;
        for (var index = 0; index < bytes.Length; index++)
            if (bytes[index] != expected[index]) return false;
        return true;
    }

    private static object? ToBoxed(byte[] bytes, NumericKind kind) => kind switch
    {
        NumericKind.Int32 => BitConverter.ToInt32(bytes, 0),
        NumericKind.Int64 => BitConverter.ToInt64(bytes, 0),
        NumericKind.Float32 => BitConverter.ToSingle(bytes, 0),
        NumericKind.Float64 => BitConverter.ToDouble(bytes, 0),
        _ => null
    };

    private static string? FormatValue(object? raw, NumericKind kind) => raw switch
    {
        int value when kind == NumericKind.Int32 => NumericValue.Format(value),
        long value when kind == NumericKind.Int64 => NumericValue.Format(value),
        float value when kind == NumericKind.Float32 && IsUsable(value) => NumericValue.Format(value),
        double value when kind == NumericKind.Float64 && IsUsable(value) => NumericValue.Format(value),
        _ => null
    };

    private static bool IsUsable(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string KeyOf(string objectPath, string componentType, string fieldName) =>
        objectPath + "|" + componentType + "|" + fieldName;

    private static string TokenOf(Discovered item) => KeyOf(item.ObjectPath, item.ComponentType, item.FieldName);

    private static int CompareSnapshots(FieldSnapshot left, FieldSnapshot right)
    {
        var byComponent = string.CompareOrdinal(left.ComponentType, right.ComponentType);
        if (byComponent != 0) return byComponent;
        var byField = string.CompareOrdinal(left.FieldName, right.FieldName);
        if (byField != 0) return byField;
        return string.CompareOrdinal(left.ObjectPath, right.ObjectPath);
    }

    /// <summary>对象路径用“场景层级 + 组件类型”构成，跨帧稳定，且不依赖实例编号。</summary>
    private static string BuildPath(MonoBehaviour component)
    {
        var names = new List<string>();
        var transform = component.transform;
        var guard = 0;
        while (transform != null && guard++ < 64)
        {
            names.Add(transform.name);
            transform = transform.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private sealed class Discovered
    {
        public Discovered(MonoBehaviour? component, FieldInfo? field, string objectPath, string componentType,
            NumericKind kind, string displayValue, bool ambiguous = false)
        {
            Component = component;
            Field = field;
            ObjectPath = objectPath;
            ComponentType = componentType;
            Kind = kind;
            DisplayValue = displayValue;
            Ambiguous = ambiguous;
        }

        public MonoBehaviour? Component { get; }
        public FieldInfo? Field { get; }
        public string FieldName => Field?.Name ?? string.Empty;
        public string ObjectPath { get; }
        public string ComponentType { get; }
        public NumericKind Kind { get; }
        public string DisplayValue { get; }
        public bool Ambiguous { get; }

        public Discovered AsAmbiguous() =>
            new Discovered(Component, Field, ObjectPath, ComponentType, Kind, DisplayValue, true);
    }
}
