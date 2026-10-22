namespace UnityTranslator.App.ExternalOcr;

internal static class OcrModeSelector
{
    internal static OcrPresentationMode Select(string? packageName, ExternalOcrAvailability availability) =>
        packageName switch
        {
            "unity6-mono" or "unity2022-mono" or "unity2022-mono-bepinex5" or
                "unity2021-mono" or "unity2020-mono" => OcrPresentationMode.Embedded,
            "il2cpp-bepinex6" or "legacy-mono" when availability.IsAvailable => OcrPresentationMode.External,
            _ => OcrPresentationMode.Unavailable
        };
}
