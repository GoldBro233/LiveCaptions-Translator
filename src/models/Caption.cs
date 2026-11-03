using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance;
        private readonly object contextsLock = new();
        private readonly Queue<TranslationHistoryEntry> contexts = new(MAX_CONTEXTS);

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;
        public IReadOnlyCollection<TranslationHistoryEntry> Contexts
        {
            get
            {
                lock (contextsLock)
                    return contexts.ToArray();
            }
        }

        public IEnumerable<TranslationHistoryEntry> AwareContexts =>
            GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);
        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged();
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged();
            }
        }
        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged();
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged();
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged();
            }
        }
        public string OverlayPreviousTranslation =>
            GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation);

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            return instance ??= new Caption();
        }

        public IReadOnlyList<TranslationContext> GetAwareContextSnapshot(int count)
        {
            return GetPreviousContexts(count)
                .Select(entry => new TranslationContext(entry.SourceText, entry.TranslatedText))
                .ToArray();
        }

        public void UpsertContext(TranslationHistoryEntry entry)
        {
            lock (contextsLock)
            {
                var items = contexts.ToList();
                int index = entry.SessionId.HasValue
                    ? items.FindIndex(item => item.SessionId == entry.SessionId && item.SentenceId == entry.SentenceId)
                    : -1;
                if (index >= 0)
                    items[index] = entry;
                else
                    items.Add(entry);

                while (items.Count > MAX_CONTEXTS)
                    items.RemoveAt(0);
                contexts.Clear();
                foreach (var item in items)
                    contexts.Enqueue(item);
            }

            OnPropertyChanged(nameof(DisplayLogCards));
            OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        public void ClearContexts()
        {
            lock (contextsLock)
                contexts.Clear();
            OnPropertyChanged(nameof(DisplayLogCards));
            OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        public string GetPreviousText(int count, TextType textType)
        {
            var entries = GetPreviousContexts(count).ToList();
            if (entries.Count == 0)
                return string.Empty;

            string previous = string.Empty;
            foreach (var entry in entries)
            {
                string current = textType == TextType.Caption ? entry.SourceText : entry.TranslatedText;
                current = RegexPatterns.NoticePrefix().Replace(current, "");
                if (string.IsNullOrEmpty(current))
                    continue;
                if (!string.IsNullOrEmpty(previous))
                {
                    if (Array.IndexOf(TextUtil.PUNC_EOS, previous[^1]) == -1)
                        previous += TextUtil.isCJChar(previous[^1]) ? "。" : ". ";
                    else if (!TextUtil.isCJChar(previous[^1]))
                        previous += " ";
                }
                previous += current;
            }

            if (textType == TextType.Translation)
                previous = RegexPatterns.NoticePrefix().Replace(previous, "");
            if (!string.IsNullOrEmpty(previous) && Array.IndexOf(TextUtil.PUNC_EOS, previous[^1]) == -1)
                previous += TextUtil.isCJChar(previous[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(previous) && Encoding.UTF8.GetByteCount(previous[^1].ToString()) < 2)
                previous += " ";
            return previous;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            if (count <= 0)
                return [];
            lock (contextsLock)
            {
                return contexts
                    .Reverse()
                    .Take(count)
                    .Reverse()
                    .Where(entry => !string.Equals(entry.TranslatedText, "N/A", StringComparison.Ordinal) &&
                                   !entry.TranslatedText.Contains("[ERROR]", StringComparison.Ordinal) &&
                                   !entry.TranslatedText.Contains("[WARNING]", StringComparison.Ordinal))
                    .ToArray();
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
