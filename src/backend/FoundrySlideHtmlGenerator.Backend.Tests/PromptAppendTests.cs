using FoundrySlideHtmlGenerator.Backend.Orchestration;

namespace FoundrySlideHtmlGenerator.Backend.Tests;

public sealed class PromptAppendTests
{
    [Fact]
    public void ComposeEffectivePrompt_Appends16x9Constraints()
    {
        var prompt = "Hello";
        var effective = AspectPrompt.ComposeEffectivePrompt(prompt, "16:9");

        Assert.Contains("1920x1080", effective);
        Assert.Contains("64px", effective);
        Assert.Contains(prompt, effective);
    }

    [Fact]
    public void ComposeEffectivePrompt_Appends4x3Constraints()
    {
        var prompt = "Hello";
        var effective = AspectPrompt.ComposeEffectivePrompt(prompt, "4:3");

        Assert.Contains("1024x768", effective);
        Assert.Contains("48px", effective);
        Assert.Contains(prompt, effective);
    }
}

