using System.Text.RegularExpressions;

namespace UnityTranslator.Core;

public sealed record TextObservation(long ComponentId, int Generation, string Source, string Language);

public sealed class TextTracker
{
    private readonly Dictionary<long, State> _states = [];

    public TextObservation? Observe(long componentId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (_states.TryGetValue(componentId, out var prior) && (text == prior.Source || text == prior.Translated)) return null;
        var generation = prior is null ? 1 : prior.Generation + 1;
        _states[componentId] = new State(generation, text, null);
        if (Regex.IsMatch(text, @"^[\s\d\p{P}\p{S}]+$") || Regex.IsMatch(text, @"[\u4e00-\u9fff]")) return null;
        var language = Regex.IsMatch(text, @"[\u3040-\u30ff\u31f0-\u31ff]") ? "ja" : "en";
        return new TextObservation(componentId, generation, text, language);
    }

    public void SetTranslated(long componentId, int generation, string translated)
    {
        if (_states.TryGetValue(componentId, out var prior) && prior.Generation == generation)
            _states[componentId] = prior with { Translated = translated };
    }

    public string? Original(long componentId) => _states.TryGetValue(componentId, out var state) ? state.Source : null;

    private sealed record State(int Generation, string Source, string? Translated);
}
