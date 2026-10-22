namespace UnityTranslator.Core;

public sealed record TranslateRequest(
    string RequestId,
    string Source,
    string SourceLanguage,
    long ComponentId,
    int Generation);

public sealed record TranslateReply(
    string RequestId,
    string Translated,
    long ComponentId,
    int Generation);
