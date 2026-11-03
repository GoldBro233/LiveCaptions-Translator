using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public sealed class TranslationRequestTests
{
    [Fact]
    public void CacheKeySeparatesEffectiveContextWithoutEmbeddingCredentials()
    {
        var first = CreateRequest(new TranslationContext("A", "甲"), apiKey: "secret-token");
        var second = CreateRequest(new TranslationContext("B", "乙"), apiKey: "secret-token");

        Assert.NotEqual(first.CacheKey, second.CacheKey);
        Assert.DoesNotContain("secret-token", first.CacheKey, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialChangesInvalidateTheConfigurationVersion()
    {
        var first = CreateRequest(new TranslationContext("A", "甲"), configurationVersion: 1);
        var changed = CreateRequest(new TranslationContext("A", "甲"), configurationVersion: 2);

        Assert.NotEqual(first.CacheKey, changed.CacheKey);
    }

    private static TranslationRequest CreateRequest(
        TranslationContext context,
        string apiKey = "",
        long configurationVersion = 1)
    {
        var settings = new TranslationSettingsSnapshot(
            "OpenAI",
            0,
            "zh-CN",
            "Translate to {0}",
            true,
            "model",
            0.2,
            apiKey,
            "https://example.test/v1/chat/completions",
            0,
            OpenAIProtocol.ChatCompletions,
            true,
            configurationVersion,
            new Dictionary<string, string>());
        return new TranslationRequest(
            Guid.NewGuid(),
            1,
            1,
            "Current",
            true,
            new[] { context },
            settings);
    }
}
