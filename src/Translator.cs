using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static readonly object stateLock = new();
        private static readonly CaptionSentenceTracker sentenceTracker = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly SemaphoreSlim historyLock = new(1, 1);
        private static readonly CancellationTokenSource lifetime = new();
        private static readonly Dictionary<long, PendingCaption> pending = new();
        private static readonly Dictionary<long, int> latestRevisions = new();

        private static AutomationElement? window;
        private static Caption? caption;
        private static Setting? setting;
        private static Guid sessionId = Guid.NewGuid();
        private static long? currentSentenceId;
        private static TranslationRequest? activeRequest;
        private static bool logOnlyFlag;
        private static bool initialized;

        private static readonly TimeSpan TranslationThrottle = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan TranslationMaxWait = TimeSpan.FromMilliseconds(1200);

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }

        public static Caption Caption => caption!;
        public static Setting Setting => setting!;

        public static bool LogOnlyFlag
        {
            get => logOnlyFlag;
            set
            {
                if (logOnlyFlag == value)
                    return;
                logOnlyFlag = value;
                BeginNewSession(clearContexts: true, resetTracker: true);
            }
        }

        public static bool FirstUseFlag { get; set; }
        public static event Action? TranslationLogged;

        static Translator()
        {
            Window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
            translationTaskQueue.Updated += OnTranslationUpdated;
            initialized = true;
        }

        public static void SyncLoop()
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (Window == null)
                {
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.FixLiveCaptions(Window);
                    LiveCaptionsHandler.HideLiveCaptions(Window);
                    BeginNewSession(clearContexts: true, resetTracker: true);
                    Wait(2000);
                    continue;
                }

                string fullText;
                try
                {
                    _ = Window.Current.Name;
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    BeginNewSession(clearContexts: true, resetTracker: true);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fullText))
                {
                    Wait(25);
                    continue;
                }

                fullText = NormalizeCaption(fullText);
                var snapshot = sentenceTracker.Update(fullText);
                if (snapshot.SourceReset)
                {
                    BeginNewSession(clearContexts: true, resetTracker: true);
                    snapshot = sentenceTracker.Update(fullText);
                }

                if (snapshot.Sentences.Count == 0)
                {
                    Wait(25);
                    continue;
                }

                var latest = snapshot.Sentences[^1];
                bool sentenceChanged;
                lock (stateLock)
                {
                    sentenceChanged = currentSentenceId != latest.Id;
                    currentSentenceId = latest.Id;
                }
                UpdateOriginalDisplay(snapshot, latest, sentenceChanged);

                foreach (var sentence in snapshot.Sentences)
                {
                    if (!sentence.Changed)
                        continue;
                    ScheduleSentence(sentence);
                }

                TrySubmitNext();
                Wait(25);
            }
        }

        public static void ConfigurationChanged()
        {
            if (!initialized)
                return;
            BeginNewSession(clearContexts: true, resetTracker: true);
        }

        public static void ClearContexts() => Caption.ClearContexts();

        public static void Shutdown()
        {
            lifetime.Cancel();
            translationTaskQueue.CancelAll();
            BeginNewSession(clearContexts: false, resetTracker: true);
        }

        private static void ScheduleSentence(CaptionSentence sentence)
        {
            lock (stateLock)
            {
                latestRevisions[sentence.Id] = sentence.Revision;
                pending[sentence.Id] = new PendingCaption(
                    sentence.Id,
                    sentence.Revision,
                    sentence.Text,
                    sentence.IsFinal,
                    DateTimeOffset.UtcNow,
                    pending.TryGetValue(sentence.Id, out var previous) ? previous.LastSubmitted : null);
            }

            TrySubmitNext();
        }

        private static void TrySubmitNext()
        {
            TranslationRequest? request = null;
            lock (stateLock)
            {
                if (activeRequest != null)
                {
                    if (pending.TryGetValue(activeRequest.SentenceId, out var revision) &&
                        revision.Revision > activeRequest.Revision)
                    {
                        activeRequest = null;
                    }
                    else
                    {
                        return;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var candidate = pending.Values
                    .Where(item => item.IsFinal ||
                        now - item.LastChanged >= TranslationMaxWait ||
                        item.LastSubmitted == null || now - item.LastSubmitted >= TranslationThrottle)
                    .OrderBy(item => item.SentenceId)
                    .FirstOrDefault();
                if (candidate == null)
                    return;

                request = CreateRequest(candidate);
                candidate.LastSubmitted = now;
                activeRequest = request;
            }

            if (request == null)
                return;
            if (LogOnlyFlag)
                _ = CommitLogOnlyAsync(request);
            else
                translationTaskQueue.Enqueue(request);
        }

        private static TranslationRequest CreateRequest(PendingCaption candidate)
        {
            var contexts = Setting.ContextAware
                ? Caption.GetAwareContextSnapshot(Setting.NumContexts)
                : Array.Empty<TranslationContext>();
            return new TranslationRequest(
                sessionId,
                candidate.SentenceId,
                candidate.Revision,
                candidate.Text,
                candidate.IsFinal,
                contexts,
                Setting.CreateTranslationSnapshot());
        }

        private static async void OnTranslationUpdated(TranslationTaskUpdate update)
        {
            if (!IsCurrentRequest(update.Request))
                return;

            if (update.Kind == TranslationUpdateKind.Text || update.Kind == TranslationUpdateKind.Completed)
            {
                if (IsCurrentDisplayedSentence(update.Request))
                    await UpdateTranslationDisplayAsync(update.Text);
            }
            else if (update.Kind is TranslationUpdateKind.Failed or TranslationUpdateKind.Incomplete)
            {
                if (IsCurrentDisplayedSentence(update.Request))
                {
                    string prefix = update.Kind == TranslationUpdateKind.Failed ? "[ERROR]" : "[WARNING]";
                    await UpdateTranslationDisplayAsync($"{prefix} {update.Error ?? "Translation did not complete."}");
                }
            }

            if (update.Kind == TranslationUpdateKind.Completed)
            {
                if (update.Request.IsFinal && !LogOnlyFlag && !string.IsNullOrWhiteSpace(update.Text))
                    await CommitTranslationAsync(update.Request, update.Text);
                FinishRequest(update.Request);
            }
            else if (update.Kind is TranslationUpdateKind.Failed or TranslationUpdateKind.Incomplete)
            {
                FinishRequest(update.Request);
            }
        }

        private static async Task CommitTranslationAsync(TranslationRequest request, string translatedText)
        {
            bool entered = false;
            try
            {
                await historyLock.WaitAsync(lifetime.Token);
                entered = true;
                if (!IsCurrentRequest(request))
                    return;
                var entry = await SQLiteHistoryLogger.UpsertTranslation(request, translatedText, lifetime.Token);
                await RunOnUiAsync(() =>
                {
                    if (IsCurrentRequest(request))
                    {
                        Caption.UpsertContext(entry);
                        TranslationLogged?.Invoke();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => SnackbarHost.Show(
                    "[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true));
            }
            finally
            {
                if (entered)
                    historyLock.Release();
            }
        }

        private static async Task CommitLogOnlyAsync(TranslationRequest request)
        {
            bool entered = false;
            try
            {
                await historyLock.WaitAsync(lifetime.Token);
                entered = true;
                if (request.IsFinal && IsCurrentRequest(request))
                {
                    var entry = await SQLiteHistoryLogger.UpsertTranslation(request, "N/A", lifetime.Token);
                    await RunOnUiAsync(() =>
                    {
                        if (IsCurrentRequest(request))
                        {
                            Caption.UpsertContext(entry);
                            TranslationLogged?.Invoke();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (entered)
                    historyLock.Release();
                FinishRequest(request);
            }
        }

        private static void FinishRequest(TranslationRequest request)
        {
            lock (stateLock)
            {
                if (activeRequest?.SessionId != request.SessionId ||
                    activeRequest.SentenceId != request.SentenceId ||
                    activeRequest.Revision != request.Revision)
                    return;
                activeRequest = null;
                if (request.IsFinal && pending.TryGetValue(request.SentenceId, out var item) &&
                    item.Revision == request.Revision)
                    pending.Remove(request.SentenceId);
            }
            TrySubmitNext();
        }

        private static bool IsCurrentRequest(TranslationRequest request)
        {
            lock (stateLock)
            {
                return request.SessionId == sessionId &&
                    latestRevisions.TryGetValue(request.SentenceId, out var revision) &&
                    revision == request.Revision;
            }
        }

        private static bool IsCurrentDisplayedSentence(TranslationRequest request)
        {
            lock (stateLock)
                return currentSentenceId == request.SentenceId;
        }

        private static void UpdateOriginalDisplay(CaptionSnapshot snapshot, CaptionSentence latest, bool sentenceChanged)
        {
            string overlay = string.Join(" ", snapshot.Sentences
                .TakeLast(Math.Max(1, Setting.DisplaySentences + 1))
                .Select(sentence => sentence.Text));
            UpdateCaption(() =>
            {
                Caption.OverlayOriginalCaption = overlay;
                Caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(
                    latest.Text, TextUtil.VERYLONG_THRESHOLD);
                Caption.OriginalCaption = latest.Text;
                if (sentenceChanged)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = string.Empty;
                    Caption.OverlayCurrentTranslation = string.Empty;
                    Caption.OverlayNoticePrefix = string.Empty;
                }
                if (LogOnlyFlag)
                {
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                }
            });
        }

        private static async Task UpdateTranslationDisplayAsync(string translatedText)
        {
            await RunOnUiAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(translatedText))
                    return;
                Caption.TranslatedCaption = translatedText;
                Caption.DisplayTranslatedCaption = TextUtil.ShortenDisplaySentence(
                    translatedText, TextUtil.VERYLONG_THRESHOLD);
                if (translatedText.Contains("[ERROR]", StringComparison.Ordinal) ||
                    translatedText.Contains("[WARNING]", StringComparison.Ordinal))
                {
                    Caption.OverlayNoticePrefix = string.Empty;
                    Caption.OverlayCurrentTranslation = translatedText;
                    return;
                }

                var match = RegexPatterns.NoticePrefixAndTranslation().Match(translatedText);
                Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
            });
        }

        private static void BeginNewSession(bool clearContexts, bool resetTracker)
        {
            lock (stateLock)
            {
                sessionId = Guid.NewGuid();
                currentSentenceId = null;
                activeRequest = null;
                pending.Clear();
                latestRevisions.Clear();
                if (resetTracker)
                    sentenceTracker.Reset();
            }
            translationTaskQueue.CancelAll();
            if (clearContexts)
                UpdateCaption(Caption.ClearContexts);
            UpdateCaption(() =>
            {
                Caption.OriginalCaption = string.Empty;
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayOriginalCaption = string.Empty;
                Caption.DisplayTranslatedCaption = string.Empty;
                Caption.OverlayOriginalCaption = " ";
                Caption.OverlayCurrentTranslation = string.Empty;
                Caption.OverlayNoticePrefix = string.Empty;
            });
        }

        private static string NormalizeCaption(string text)
        {
            text = RegexPatterns.Acronym().Replace(text, "$1$2");
            text = RegexPatterns.AcronymWithWords().Replace(text, "$1 $2");
            text = RegexPatterns.PunctuationSpace().Replace(text, "$1 ");
            text = RegexPatterns.CJPunctuationSpace().Replace(text, "$1");
            return TextUtil.ReplaceNewlines(text, TextUtil.MEDIUM_THRESHOLD);
        }

        private static void UpdateCaption(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        private static Task RunOnUiAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }

        private static void Wait(int milliseconds)
        {
            lifetime.Token.WaitHandle.WaitOne(milliseconds);
        }

        private sealed class PendingCaption
        {
            public long SentenceId { get; }
            public int Revision { get; }
            public string Text { get; }
            public bool IsFinal { get; }
            public DateTimeOffset LastChanged { get; }
            public DateTimeOffset? LastSubmitted { get; set; }

            public PendingCaption(
                long sentenceId,
                int revision,
                string text,
                bool isFinal,
                DateTimeOffset lastChanged,
                DateTimeOffset? lastSubmitted)
            {
                SentenceId = sentenceId;
                Revision = revision;
                Text = text;
                IsFinal = isFinal;
                LastChanged = lastChanged;
                LastSubmitted = lastSubmitted;
            }
        }
    }
}
