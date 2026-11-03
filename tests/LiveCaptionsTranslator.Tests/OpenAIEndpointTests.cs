using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public sealed class OpenAIEndpointTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "ChatCompletions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "Responses", "https://api.openai.com/v1/responses")]
    [InlineData("https://localhost:1234/custom/v1", "Responses", "https://localhost:1234/custom/v1/responses")]
    public void AppendsEndpointForSelectedProtocol(
        string baseUrl,
        string protocol,
        string expected)
    {
        var settings = CreateSettings(baseUrl, protocol);

        Assert.Equal(expected, TranslateAPI.ResolveOpenAIEndpoint(settings));
    }

    [Fact]
    public void NormalizesExistingFullEndpointForCompatibility()
    {
        var settings = CreateSettings(
            "https://api.openai.com/v1/chat/completions",
            "Responses");

        Assert.Equal(
            "https://api.openai.com/v1/responses",
            TranslateAPI.ResolveOpenAIEndpoint(settings));
    }

    private static TranslationSettingsSnapshot CreateSettings(string url, string protocol)
    {
        return new TranslationSettingsSnapshot(
            "OpenAI",
            0,
            "zh-CN",
            "Translate to {0}",
            false,
            "model",
            0.2,
            "",
            url,
            0,
            Enum.Parse<OpenAIProtocol>(protocol),
            false,
            1,
            new Dictionary<string, string>());
    }
}
