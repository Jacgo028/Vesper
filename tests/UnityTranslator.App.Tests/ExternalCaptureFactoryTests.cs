using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

public sealed class ExternalCaptureFactoryTests
{
    [Fact]
    public void Unsupported_platform_never_enters_api_probe_or_backend()
    {
        var apiCalls = 0;
        var backendCalls = 0;
        var factory = new ExternalCaptureFactory(() => false, () => { apiCalls++; return true; },
            () => { backendCalls++; throw new Exception("device"); });
        var availability = factory.GetAvailability();
        Assert.False(availability.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(availability.Reason));
        Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Equal(0, apiCalls);
        Assert.Equal(0, backendCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_throwing_api_is_unavailable_without_constructing_backend(bool throws)
    {
        var backendCalls = 0;
        var factory = new ExternalCaptureFactory(() => true,
            () => throws ? throw new TypeLoadException("projection unavailable") : false,
            () => { backendCalls++; throw new Exception("device"); });
        var availability = factory.GetAvailability();
        Assert.False(availability.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(availability.Reason));
        Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Equal(0, backendCalls);
    }

    [Fact]
    public void Device_initialization_is_lazy_and_failure_has_reason_and_inner_exception()
    {
        var calls = 0;
        var cause = new Exception("D3D device unavailable");
        var factory = new ExternalCaptureFactory(() => true, () => true,
            () => { calls++; throw cause; });
        Assert.True(factory.GetAvailability().IsAvailable);
        Assert.True(factory.GetAvailability().IsAvailable);
        Assert.Equal(0, calls);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Create());
        Assert.Contains("D3D device unavailable", error.Message);
        Assert.Same(cause, error.InnerException);
        Assert.Equal(1, calls);
    }
}
