using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public sealed class CaptionSentenceTracker
    {
        private readonly List<TrackedSentence> previous = new();
        private long nextId = 1;
        public void Reset()
        {
            previous.Clear();
            nextId = 1;
        }

        public CaptionSnapshot Update(string text)
        {
            var current = Split(text);
            if (current.Count == 0)
            {
                previous.Clear();
                return new CaptionSnapshot(Array.Empty<CaptionSentence>(), false);
            }

            bool sourceReset = previous.Count > 0 && !HasAnchor(current);
            var result = new List<CaptionSentence>(current.Count);
            var next = new List<TrackedSentence>(current.Count);

            int shift = sourceReset ? int.MinValue : FindAlignmentShift(current);
            for (int currentIndex = 0; currentIndex < current.Count; currentIndex++)
            {
                string currentText = current[currentIndex];
                TrackedSentence? old = null;
                if (!sourceReset)
                {
                    int oldIndex = shift == int.MinValue ? currentIndex : currentIndex + shift;
                    if (oldIndex >= 0 && oldIndex < previous.Count)
                        old = previous[oldIndex];
                }

                long id = old?.Id ?? nextId++;
                int revision = old == null ? 1 :
                    string.Equals(old.Text, currentText, StringComparison.Ordinal) ? old.Revision : old.Revision + 1;
                bool changed = old == null || !string.Equals(old.Text, currentText, StringComparison.Ordinal);
                bool isFinal = currentIndex < current.Count - 1 ||
                    currentText.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, currentText[^1]) >= 0;

                var sentence = new CaptionSentence(id, revision, currentText, isFinal, changed);
                result.Add(sentence);
                next.Add(new TrackedSentence(id, revision, currentText));
            }

            previous.Clear();
            previous.AddRange(next);
            return new CaptionSnapshot(result, sourceReset);
        }

        private bool HasAnchor(IReadOnlyList<string> current)
        {
            return current.Any(currentText => previous.Any(old =>
                string.Equals(old.Text, currentText, StringComparison.Ordinal)));
        }

        private int FindAlignmentShift(IReadOnlyList<string> current)
        {
            int bestShift = 0;
            int bestMatches = -1;
            for (int shift = -previous.Count; shift < current.Count; shift++)
            {
                int matches = 0;
                for (int currentIndex = 0; currentIndex < current.Count; currentIndex++)
                {
                    int oldIndex = currentIndex + shift;
                    if (oldIndex >= 0 && oldIndex < previous.Count &&
                        string.Equals(previous[oldIndex].Text, current[currentIndex], StringComparison.Ordinal))
                        matches++;
                }

                if (matches > bestMatches)
                {
                    bestMatches = matches;
                    bestShift = shift;
                }
            }

            return bestMatches > 0 ? bestShift : 0;
        }

        private static List<string> Split(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (Array.IndexOf(TextUtil.PUNC_EOS, text[i]) < 0)
                    continue;

                var sentence = text[start..(i + 1)].Trim();
                if (sentence.Length > 0)
                    result.Add(sentence);
                start = i + 1;
            }

            var remainder = text[start..].Trim();
            if (remainder.Length > 0)
                result.Add(remainder);
            return result;
        }

        private sealed record TrackedSentence(long Id, int Revision, string Text);
    }
}
