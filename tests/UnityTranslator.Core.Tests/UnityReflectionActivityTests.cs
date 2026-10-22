using System.Reflection;

namespace UnityTranslator.Core.Tests;

public sealed class UnityReflectionActivityTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void LegacyUiTextRequiresAnActiveGameObjectAndEnabledBehaviour(
        bool active, bool enabled, bool expected)
    {
        var component = new LegacyUiText { enabled = enabled };
        component.gameObject.activeInHierarchy = active;

        Assert.Equal(expected, IsActiveAndEnabled(new UnityReflection(), component));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void TmpTextRequiresAnActiveGameObjectAndEnabledBehaviour(
        bool active, bool enabled, bool expected)
    {
        var component = new TmpText { enabled = enabled };
        component.gameObject.activeInHierarchy = active;

        Assert.Equal(expected, IsActiveAndEnabled(new UnityReflection(), component));
    }

    [Fact]
    public void ActivationIsReadAgainAfterAnInactiveScan()
    {
        var reflection = new UnityReflection();
        var component = new TmpText();
        component.gameObject.activeInHierarchy = false;

        Assert.False(IsActiveAndEnabled(reflection, component));
        component.gameObject.activeInHierarchy = true;
        Assert.True(IsActiveAndEnabled(reflection, component));
        component.enabled = false;
        Assert.False(IsActiveAndEnabled(reflection, component));
        component.enabled = true;
        Assert.True(IsActiveAndEnabled(reflection, component));
    }

    [Fact]
    public void MissingActivityMembersPreserveCompatibility()
    {
        var reflection = new UnityReflection();

        Assert.True(IsActiveAndEnabled(reflection, new TextWithoutActivityMembers()));
        Assert.True(IsActiveAndEnabled(reflection, new ComponentWithUnknownGameObject()));
    }

    [Fact]
    public void NullOrDestroyedUnityObjectsAreRejected()
    {
        var reflection = new UnityReflection();

        Assert.False(IsActiveAndEnabled(reflection, null));
        Assert.False(IsActiveAndEnabled(reflection, new TmpText { Destroyed = true }));
        Assert.False(IsActiveAndEnabled(reflection, new ComponentWithDestroyedGameObject()));
        Assert.False(IsActiveAndEnabled(reflection, new ComponentWithMissingGameObject()));
    }

    [Fact]
    public void ActivityCheckOnlyReadsComponentState()
    {
        var component = new LegacyUiText();
        var font = component.font;

        Assert.True(IsActiveAndEnabled(new UnityReflection(), component));
        Assert.Equal("Start game", component.text);
        Assert.Same(font, component.font);
        Assert.Equal(18, component.fontSize);
        Assert.Equal(200, component.width);
    }

    // Reflecting the new API lets the first RED run fail with a missing behavior assertion,
    // while every case still exercises the production guard against actual component shapes.
    private static bool IsActiveAndEnabled(UnityReflection reflection, object? component)
    {
        var method = typeof(UnityReflection).GetMethod("IsActiveAndEnabled", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(reflection, new[] { component }));
    }

    public class UnityObject
    {
        public bool Destroyed { get; set; }
        public static implicit operator bool(UnityObject component) => component is not null && !component.Destroyed;
    }

    public sealed class GameObject
    {
        public bool activeInHierarchy { get; set; } = true;
    }

    public class Behaviour : UnityObject
    {
        public bool enabled { get; set; } = true;
        public GameObject gameObject { get; } = new();
    }

    public sealed class LegacyUiText : Behaviour
    {
        public string text { get; set; } = "Start game";
        public object font { get; set; } = new();
        public int fontSize { get; set; } = 18;
        public int width { get; set; } = 200;
    }

    public sealed class TmpText : Behaviour
    {
        public string text { get; set; } = "Start game";
    }

    public sealed class TextWithoutActivityMembers
    {
        public string text { get; set; } = "Start game";
    }

    public sealed class ComponentWithUnknownGameObject
    {
        public object gameObject { get; } = new();
    }

    public sealed class ComponentWithDestroyedGameObject
    {
        public GameObject gameObject => throw new ObjectDisposedException("gameObject");
    }

    public sealed class ComponentWithMissingGameObject
    {
        public GameObject? gameObject => null;
    }
}
