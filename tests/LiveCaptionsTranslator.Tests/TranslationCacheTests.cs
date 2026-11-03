using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public sealed class TranslationCacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedEntryAtCapacity()
    {
        var cache = new TranslationCache();
        cache.Set("first", "one");

        for (int i = 1; i < TranslationCache.Capacity + 1; i++)
            cache.Set($"key-{i}", $"value-{i}");

        Assert.False(cache.TryGet("first", out _));
        Assert.True(cache.TryGet($"key-{TranslationCache.Capacity}", out var newest));
        Assert.Equal($"value-{TranslationCache.Capacity}", newest);
    }
}
