#if NETSTANDARD2_0 || NET46 || PORTABLE_NUMERIC
using System;
using System.Globalization;

namespace UnityTranslator.Core.Cheats;

// Unity 2020's CLR does not provide Span/BinaryPrimitives. Keep the game's
// protocol codec self-contained instead of loading another System.Memory.
public static class NumericValue
{
    public static int Width(NumericKind kind) => kind switch
    {
        NumericKind.Int32 or NumericKind.Float32 => 4,
        NumericKind.Int64 or NumericKind.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Format(byte[] bytes, NumericKind kind)
    {
        if (bytes.Length != Width(kind)) throw new ArgumentException("Numeric byte length does not match the selected type.");
        var native = Native(bytes);
        return kind switch
        {
            NumericKind.Int32 => Format(BitConverter.ToInt32(native, 0)),
            NumericKind.Int64 => Format(BitConverter.ToInt64(native, 0)),
            NumericKind.Float32 => Format(BitConverter.ToSingle(native, 0)),
            NumericKind.Float64 => Format(BitConverter.ToDouble(native, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static bool TryKindOf(Type type, out NumericKind kind)
    {
        if (type == typeof(int)) { kind = NumericKind.Int32; return true; }
        if (type == typeof(long)) { kind = NumericKind.Int64; return true; }
        if (type == typeof(float)) { kind = NumericKind.Float32; return true; }
        if (type == typeof(double)) { kind = NumericKind.Float64; return true; }
        kind = default;
        return false;
    }

    public static bool TryParse(string text, NumericKind kind, out byte[] bytes)
    {
        bytes = new byte[0];
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        switch (kind)
        {
            case NumericKind.Int32:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32) &&
                    !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out i32)) return false;
                bytes = BitConverter.GetBytes(i32); break;
            case NumericKind.Int64:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64) &&
                    !long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out i64)) return false;
                bytes = BitConverter.GetBytes(i64); break;
            case NumericKind.Float32:
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f32) &&
                    !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out f32)) return false;
                if (float.IsNaN(f32) || float.IsInfinity(f32)) return false;
                bytes = BitConverter.GetBytes(f32); break;
            case NumericKind.Float64:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f64) &&
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out f64)) return false;
                if (double.IsNaN(f64) || double.IsInfinity(f64)) return false;
                bytes = BitConverter.GetBytes(f64); break;
            default: return false;
        }
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return true;
    }

    public static int Compare(byte[] current, byte[] previous, NumericKind kind)
    {
        if (current.Length != Width(kind) || previous.Length != Width(kind)) throw new ArgumentException("Numeric byte length mismatch.");
        var first = Native(current);
        var second = Native(previous);
        return kind switch
        {
            NumericKind.Int32 => BitConverter.ToInt32(first, 0).CompareTo(BitConverter.ToInt32(second, 0)),
            NumericKind.Int64 => BitConverter.ToInt64(first, 0).CompareTo(BitConverter.ToInt64(second, 0)),
            NumericKind.Float32 => CompareFinite(BitConverter.ToSingle(first, 0), BitConverter.ToSingle(second, 0)),
            NumericKind.Float64 => CompareFinite(BitConverter.ToDouble(first, 0), BitConverter.ToDouble(second, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static int CompareFinite(double first, double second)
    {
        if (double.IsNaN(first) || double.IsInfinity(first) || double.IsNaN(second) || double.IsInfinity(second))
            throw new ArgumentException("Non-finite game value cannot be compared.");
        return Math.Sign(first.CompareTo(second));
    }

    private static byte[] Native(byte[] bytes)
    {
        if (BitConverter.IsLittleEndian) return bytes;
        var native = (byte[])bytes.Clone();
        Array.Reverse(native);
        return native;
    }
}
#else
using System;
using System.Buffers.Binary;
using System.Globalization;

namespace UnityTranslator.Core.Cheats;

public static class NumericValue
{
    public static int Width(NumericKind kind) => kind switch
    {
        NumericKind.Int32 or NumericKind.Float32 => 4,
        NumericKind.Int64 or NumericKind.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// 把字段当前值转成界面文本，保证 <see cref="TryParse"/> 能原样解析回同样的字节。
    /// 浮点使用往返格式，避免 0.1 显示成 0.1 却解析成另一个值。
    /// </summary>
    public static string Format(ReadOnlySpan<byte> bytes, NumericKind kind)
    {
        if (bytes.Length != Width(kind))
            throw new ArgumentException("Numeric byte length does not match the selected type.", nameof(bytes));
        return kind switch
        {
            NumericKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            NumericKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            NumericKind.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                .ToString("R", CultureInfo.InvariantCulture),
            NumericKind.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
                .ToString("R", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Unity 字段类型到数值类型的映射；不支持的类型返回 false，不做猜测。</summary>
    public static bool TryKindOf(Type type, out NumericKind kind)
    {
        if (type == typeof(int)) { kind = NumericKind.Int32; return true; }
        if (type == typeof(long)) { kind = NumericKind.Int64; return true; }
        if (type == typeof(float)) { kind = NumericKind.Float32; return true; }
        if (type == typeof(double)) { kind = NumericKind.Float64; return true; }
        kind = default;
        return false;
    }

    /// <summary>严格解析：只接受这一种数值类型，不做隐式窄化或加宽。</summary>
    public static bool TryParse(string text, NumericKind kind, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        switch (kind)
        {
            case NumericKind.Int32:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32) &&
                    !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out i32)) return false;
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, i32);
                return true;
            case NumericKind.Int64:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64) &&
                    !long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out i64)) return false;
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, i64);
                return true;
            case NumericKind.Float32:
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f32) &&
                    !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out f32)) return false;
                if (!float.IsFinite(f32)) return false;
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(f32));
                return true;
            case NumericKind.Float64:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f64) &&
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out f64)) return false;
                if (!double.IsFinite(f64)) return false;
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(f64));
                return true;
            default:
                return false;
        }
    }

    public static int Compare(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, NumericKind kind)
    {
        var width = Width(kind);
        if (current.Length != width || previous.Length != width)
            throw new ArgumentException("Numeric byte length does not match the selected type.");
        return kind switch
        {
            NumericKind.Int32 => Math.Sign(BinaryPrimitives.ReadInt32LittleEndian(current)
                .CompareTo(BinaryPrimitives.ReadInt32LittleEndian(previous))),
            NumericKind.Int64 => Math.Sign(BinaryPrimitives.ReadInt64LittleEndian(current)
                .CompareTo(BinaryPrimitives.ReadInt64LittleEndian(previous))),
            NumericKind.Float32 => CompareFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(current)),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(previous))),
            NumericKind.Float64 => CompareFinite(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(current)),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(previous))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static int CompareFinite(float current, float previous)
    {
        if (!float.IsFinite(current) || !float.IsFinite(previous))
            throw new ArgumentException("Non-finite game value cannot be compared.");
        return Math.Sign(current.CompareTo(previous));
    }

    private static int CompareFinite(double current, double previous)
    {
        if (!double.IsFinite(current) || !double.IsFinite(previous))
            throw new ArgumentException("Non-finite game value cannot be compared.");
        return Math.Sign(current.CompareTo(previous));
    }
}
#endif
