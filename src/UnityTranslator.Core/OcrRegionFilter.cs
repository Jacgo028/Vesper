namespace UnityTranslator.Core;

public record OcrRect(int X, int Y, int Width, int Height)
{
    public bool Overlaps(OcrRect other) => X < other.X + other.Width && X + Width > other.X &&
                                           Y < other.Y + other.Height && Y + Height > other.Y;
}

public sealed record OcrBox(int X, int Y, int Width, int Height, string Text, float Confidence)
    : OcrRect(X, Y, Width, Height);

public static class OcrRegionFilter
{
    public static IReadOnlyList<OcrBox> Select(IEnumerable<OcrBox> boxes,
        IEnumerable<OcrRect> knownUiBoxes, float minConfidence)
    {
        var known = knownUiBoxes.ToArray();
        return boxes.Where(box => box.Confidence >= minConfidence &&
                                  box.Width > 0 && box.Height > 0 &&
                                  !string.IsNullOrWhiteSpace(box.Text) &&
                                  !known.Any(box.Overlaps)).ToArray();
    }
}

public sealed class OcrStabilityTracker
{
    private Dictionary<string, int> _previous = [];

    public IReadOnlyList<OcrBox> Observe(IEnumerable<OcrBox> boxes)
    {
        var current = new Dictionary<string, int>();
        var stable = new List<OcrBox>();
        foreach (var box in boxes)
        {
            var key = $"{box.X / 20}:{box.Y / 20}:{box.Text.Trim()}";
            var count = _previous.GetValueOrDefault(key) + 1;
            current[key] = count;
            if (count >= 2) stable.Add(box);
        }
        _previous = current;
        return stable;
    }
}
