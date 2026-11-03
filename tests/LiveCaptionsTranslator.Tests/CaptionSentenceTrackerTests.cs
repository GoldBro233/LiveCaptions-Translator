using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSentenceTrackerTests
{
    [Fact]
    public void KeepsSentenceIdentityWhenCaptionIsRevised()
    {
        var tracker = new CaptionSentenceTracker();

        var first = tracker.Update("Hello.");
        var firstId = first.Sentences[0].Id;

        var appended = tracker.Update("Hello. world");
        var revised = tracker.Update("Hello! world");

        Assert.Equal(firstId, appended.Sentences[0].Id);
        Assert.Equal(firstId, revised.Sentences[0].Id);
        Assert.Equal(2, revised.Sentences[0].Revision);
        Assert.Equal(appended.Sentences[1].Id, revised.Sentences[1].Id);
        Assert.True(revised.Sentences[0].Changed);
        Assert.False(revised.Sentences[1].IsFinal);
    }

    [Fact]
    public void GivesRepeatedSentencesDifferentIdentitiesByPosition()
    {
        var tracker = new CaptionSentenceTracker();

        var snapshot = tracker.Update("Again. Again.");

        Assert.Equal(2, snapshot.Sentences.Count);
        Assert.NotEqual(snapshot.Sentences[0].Id, snapshot.Sentences[1].Id);
    }
}
