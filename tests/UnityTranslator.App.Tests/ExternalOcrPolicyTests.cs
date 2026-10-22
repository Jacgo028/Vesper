using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class ExternalOcrPolicyTests
{
    [Theory]
    [InlineData("unity6-mono", true)]
    [InlineData("unity6-mono", false)]
    [InlineData("unity2022-mono", true)]
    [InlineData("unity2022-mono", false)]
    [InlineData("unity2022-mono-bepinex5", true)]
    [InlineData("unity2022-mono-bepinex5", false)]
    [InlineData("unity2021-mono", true)]
    [InlineData("unity2021-mono", false)]
    [InlineData("unity2020-mono", true)]
    [InlineData("unity2020-mono", false)]
    public void Full_Mono_always_uses_one_embedded_channel(string package, bool available) =>
        Assert.Equal(OcrPresentationMode.Embedded, OcrModeSelector.Select(package, new(available, "fixture")));

    [Theory]
    [InlineData("il2cpp-bepinex6", true, true)]
    [InlineData("il2cpp-bepinex6", false, false)]
    [InlineData("legacy-mono", true, true)]
    [InlineData("legacy-mono", false, false)]
    public void External_only_packages_require_capture_availability(string package, bool available, bool external) =>
        Assert.Equal(external ? OcrPresentationMode.External : OcrPresentationMode.Unavailable,
            OcrModeSelector.Select(package, new(available, "fixture")));

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    [InlineData("", true)]
    [InlineData("UNITY6-MONO", true)]
    public void Missing_or_unknown_packages_do_not_advertise_OCR(string? package, bool available) =>
        Assert.Equal(OcrPresentationMode.Unavailable, OcrModeSelector.Select(package, new(available, "fixture")));

    [Theory]
    [InlineData(LoaderFamily.Auto)]
    [InlineData(LoaderFamily.BepInEx5)]
    [InlineData(LoaderFamily.BepInEx6)]
    public void External_route_does_not_redefine_IL2CPP_embedded_capability(LoaderFamily family)
    {
        var game = new GameProfile("game", "C:\\synthetic", "C:\\synthetic\\game.exe", "2022.3", "IL2CPP", "x64", true, "fixture");
        Assert.False(PackageCatalog.SupportsEmbeddedOcr(game, family));
        Assert.Equal(OcrPresentationMode.External,
            OcrModeSelector.Select(PackageCatalog.ResolvePackageName(game, family), new(true, "fixture")));
    }
}
