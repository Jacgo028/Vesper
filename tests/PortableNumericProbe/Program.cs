using System.Reflection;
using UnityTranslator.Core.Cheats;

// Older Unity must never require Span/System.Memory just to load its plugin.
var format = typeof(NumericValue).GetMethod("Format", new[] { typeof(byte[]), typeof(NumericKind) });
if (format is null) throw new Exception("Portable numeric codec must accept byte[] without Span.");
var cases = new[]
{
    (NumericKind.Int32, "-1234", new byte[] { 46, 251, 255, 255 }),
    (NumericKind.Int64, "2147483648", new byte[] { 0, 0, 0, 128, 0, 0, 0, 0 }),
    (NumericKind.Float32, "12.5", new byte[] { 0, 0, 72, 65 }),
    (NumericKind.Float64, "-0.5", new byte[] { 0, 0, 0, 0, 0, 0, 224, 191 }),
};
foreach (var (kind, text, expected) in cases)
{
    if (!NumericValue.TryParse(text, kind, out var actual) || !actual.SequenceEqual(expected))
        throw new Exception("Portable parsing differs from little-endian game memory: " + kind);
    if ((string)format.Invoke(null, new object[] { expected, kind })! != text)
        throw new Exception("Portable formatting changed a numeric value: " + kind);
    if (NumericValue.Compare(expected, actual, kind) != 0)
        throw new Exception("Portable comparison changed equal values.");
}
if (NumericValue.TryParse("NaN", NumericKind.Float32, out _) ||
    NumericValue.TryParse("Infinity", NumericKind.Float64, out _))
    throw new Exception("Non-finite values must be rejected.");
Console.WriteLine("PASS: portable numeric byte-array contract, four exact memory formats, round-trip and finite checks");
