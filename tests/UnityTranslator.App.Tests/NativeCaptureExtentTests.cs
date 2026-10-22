using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

public sealed class NativeCaptureExtentTests
{
    // Break caught: treating over-reported ContentSize as the actual GPU texture
    // size, or accepting undefined pixels / a differently-sized capture surface.
    // Literal restore observation: DWM/item/texture964x654, ContentSize982x663.
    [Theory]
    [InlineData(964, 654, 982, 663, 964u, 654u, true)]
    [InlineData(964, 654, 964, 654, 964u, 654u, true)]
    [InlineData(964, 654, 963, 654, 964u, 654u, false)]
    [InlineData(964, 654, 964, 653, 964u, 654u, false)]
    [InlineData(964, 654, 982, 663, 982u, 663u, false)]
    [InlineData(964, 654, 964, 654, 963u, 654u, false)]
    [InlineData(964, 654, 964, 654, 964u, 653u, false)]
    [InlineData(0, 654, 982, 663, 0u, 654u, false)]
    [InlineData(964, -1, 982, 663, 964u, 654u, false)]
    [InlineData(964, 654, 0, 663, 964u, 654u, false)]
    [InlineData(964, 654, 982, -1, 964u, 654u, false)]
    [InlineData(964, 654, int.MaxValue, int.MaxValue, uint.MaxValue, 654u, false)]
    public void Only_a_fully_defined_surface_matching_the_verified_window_extent_is_accepted(
        int expectedWidth, int expectedHeight, int contentWidth, int contentHeight,
        uint textureWidth, uint textureHeight, bool accepted) =>
        Assert.Equal(accepted, ExternalFrameRules.HasDefinedCaptureExtent(expectedWidth, expectedHeight,
            contentWidth, contentHeight, textureWidth, textureHeight));
}
