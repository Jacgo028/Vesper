using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Cheats;

public sealed class CheatTableEntry
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Description { get; set; } = "未命名";
    public string Address { get; set; } = "";
    public NumericKind Kind { get; set; }
    public long[] Offsets { get; set; } = [];
    public bool IsGroup { get; set; }
    public string Value { get; set; } = "??";
    public string ResolvedAddress { get; set; } = "";
    public bool Frozen { get; set; }
    public int Depth { get; set; }
    public string DisplayDescription => new string(' ', Math.Min(48,Depth) * 2) + (IsGroup ? "▸ " : "") + Description;
    public string KindText => IsGroup ? "分组" : UiText.KindName(Kind);
    public string AddressText => IsGroup ? "" : Address + (Offsets.Length == 0 ? "" : " → " + string.Join(" → ", Offsets.Select(x => x.ToString("X"))));
    public string StateText => IsGroup ? "分组" : Frozen ? "已锁定" : "未锁定";
}

/// <summary>Only numeric addresses and groups cross the .CT boundary. No imported Lua, AA, hotkeys or forms execute.</summary>
public sealed class CheatTableDocument
{
    private static readonly Regex AddressPattern = new(
        "\\A(?:\"(?<module>[\\p{L}\\p{N}_ .+\\-\\[\\]]+)\"|(?<hex>(?:0[xX])?[0-9A-Fa-f]+))(?<offsets>(?: *[+-] *(?:0[xX])?[0-9A-Fa-f]+)*)\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex OffsetPattern = new("[+-] *(?<hex>(?:0[xX])?[0-9A-Fa-f]+)", RegexOptions.CultureInvariant);
    public List<CheatTableEntry> Entries { get; } = [];
    public int SkippedScripts { get; private set; }
    public int SkippedUnsupported { get; private set; }
    public bool HasLuaScript { get; private set; }

    public static void ValidateAddress(string address) => _ = ParseAddressExpression(address);

    /// <summary>The import boundary and live table share exactly the same supported grammar.</summary>
    internal static Match ParseAddressExpression(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 512 || address.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("地址只支持单行十六进制地址或带引号的模块名及加减偏移，例如 7FF01234 或 \"Game.exe\"+1234。");
        var match = AddressPattern.Match(address.Trim());
        if (!match.Success)
            throw new ArgumentException("地址只支持十六进制地址或带引号的模块名及十六进制加减偏移；指针请单独填写偏移。");
        if (match.Groups["hex"].Success) ValidateHexRange(match.Groups["hex"].Value);
        foreach (Match offset in OffsetPattern.Matches(match.Groups["offsets"].Value))
            ValidateHexRange(offset.Groups["hex"].Value);
        return match;
    }

    private static void ValidateHexRange(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("十六进制地址和加减偏移不能超出 64 位范围。");
    }

    public static CheatTableDocument Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "CheatTable") throw new InvalidDataException("这不是有效的 Cheat Engine .CT 地址表。");
        var result = new CheatTableDocument { HasLuaScript = root.Descendants("LuaScript").Any() || root.Descendants("LuaScriptEntry").Any() };
        var nextId = 0;
        void Read(XElement container, int? parent, int depth)
        {
            if (depth > 48) throw new InvalidDataException("地址表的分组层数过多。");
            foreach (var node in container.Elements("CheatEntry"))
            {
                if (++nextId > 10000) throw new InvalidDataException("地址表超过 10000 项，请拆分后导入。");
                var type = (string?)node.Element("VariableType") ?? "";
                if (type.Contains("Auto Assembler", StringComparison.OrdinalIgnoreCase) || node.Element("AssemblerScript") is not null)
                { result.SkippedScripts++; continue; }
                var group = (string?)node.Element("GroupHeader") == "1";
                NumericKind kind = type switch { "4 Bytes" => NumericKind.Int32, "8 Bytes" => NumericKind.Int64,
                    "Float" => NumericKind.Float32, "Double" => NumericKind.Float64, _ => (NumericKind)(-1) };
                var children = node.Element("CheatEntries");
                if (!group && (int)kind < 0)
                { result.SkippedUnsupported++; if (children is not null) Read(children, parent, depth + 1); continue; }
                var address = (string?)node.Element("Address") ?? "";
                if (!group) ValidateAddress(address);
                var offsets = node.Element("Offsets")?.Elements("Offset").Select(x => ParseOffset(x.Value)).ToArray() ?? [];
                if (offsets.Length > 32) throw new InvalidDataException("单条地址的指针层数不能超过 32。");
                var entry = new CheatTableEntry { Id = nextId, ParentId = parent, Description = ((string?)node.Element("Description") ?? "未命名").Trim('"'),
                    Address = address, Kind = group ? NumericKind.Int32 : kind, IsGroup = group, Offsets = offsets, Depth = depth };
                result.Entries.Add(entry);
                if (children is not null) Read(children, entry.Id, depth + 1);
            }
        }
        if (root.Element("CheatEntries") is { } entries) Read(entries, null, 0);
        return result;
    }

    public static long ParseOffset(string input)
    {
        input = input.Trim();
        var negative = input.StartsWith('-');
        if (negative) input = input[1..];
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) input = input[2..];
        if (!ulong.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || (negative && value > 0x8000000000000000UL))
            throw new ArgumentException("指针偏移必须是十六进制整数，例如 10、20 或 -8。");
        if (!negative) return unchecked((long)value);
        return value == 0x8000000000000000UL ? long.MinValue : -(long)value;
    }

    public string ToXml()
    {
        var root = new XElement("CheatTable", new XAttribute("CheatEngineTableVersion", "45"));
        var top = new XElement("CheatEntries"); root.Add(top);
        var nodes = new Dictionary<int, XElement>();
        foreach (var entry in Entries)
        {
            var node = new XElement("CheatEntry", new XElement("ID", entry.Id), new XElement("Description", "\"" + entry.Description + "\""));
            if (entry.IsGroup) node.Add(new XElement("GroupHeader", "1"));
            else
            {
                ValidateAddress(entry.Address);
                node.Add(new XElement("VariableType", entry.Kind switch { NumericKind.Int64 => "8 Bytes", NumericKind.Float32 => "Float", NumericKind.Float64 => "Double", _ => "4 Bytes" }), new XElement("Address", entry.Address));
                if (entry.Offsets.Length > 0) node.Add(new XElement("Offsets", entry.Offsets.Select(x => new XElement("Offset", x < 0 ? "-" + ((ulong)(-(x + 1)) + 1).ToString("X") : x.ToString("X")))));
            }
            if (entry.ParentId is int parent && nodes.TryGetValue(parent, out var parentNode))
            { var children = parentNode.Element("CheatEntries"); if (children is null) { children = new XElement("CheatEntries"); parentNode.Add(children); } children.Add(node); }
            else top.Add(node);
            nodes.Add(entry.Id, node);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }
}
