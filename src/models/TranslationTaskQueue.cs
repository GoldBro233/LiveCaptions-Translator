using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.models
{
    public sealed record TranslationTaskUpdate(
        TranslationRequest Request,
        TranslationUpdateKind Kind,
        string Text,
        string? Error = null);

    public sealed class TranslationTaskQueue
    {
        private readonly object sync = new();
        private readonly Dictionary<(Guid SessionId, long SentenceId), TranslationWork> latest = new();
        private readonly Dictionary<string, SharedTranslation> inFlight = new(StringComparer.Ordinal);
        private readonly TranslationCache cache = new();
        private readonly SemaphoreSlim activeRequests = new(1, 1);

        public event Action<TranslationTaskUpdate>? Updated;

        public void Enqueue(TranslationRequest request)
        {
            TranslationWork work;
            SharedTranslation? sharedToStart = null;
            string? cachedText = null;
            lock (sync)
            {
                var identity = (request.SessionId, request.SentenceId);
                if (latest.TryGetValue(identity, out var previous))
                    previous.Cancel();

                work = new TranslationWork(request);
                latest[identity] = work;

                if (cache.TryGet(request.CacheKey, out var value))
                {
                    cachedText = value;
                }
                else if (inFlight.TryGetValue(request.CacheKey, out var shared))
                {
                    shared.Add(work);
                }
                else
                {
                    shared = new SharedTranslation(request);
                    shared.Add(work);
                    inFlight.Add(request.CacheKey, shared);
                    sharedToStart = shared;
                }
            }

            if (cachedText != null)
            {
                work.Append(cachedText);
                Publish(work, TranslationUpdateKind.Completed, null);
            }

            if (sharedToStart != null)
                _ = RunSharedAsync(sharedToStart);
        }

        public void CancelAll()
        {
            lock (sync)
            {
                foreach (var work in latest.Values)
                    work.Cancel();
                latest.Clear();

                foreach (var shared in inFlight.Values)
                    shared.Cancel();
                inFlight.Clear();
            }
        }

        private async Task RunSharedAsync(SharedTranslation shared)
        {
            bool entered = false;
            try
            {
                await activeRequests.WaitAsync(shared.Token);
                entered = true;
            }
            catch (OperationCanceledException) when (shared.Token.IsCancellationRequested)
            {
                RemoveShared(shared);
                return;
            }

            try
            {
                var finalText = new StringBuilder();
                bool completed = false;
                bool terminal = false;
                try
                {
                    await foreach (var update in TranslateAPI.TranslateAsync(shared.Request, shared.Token)
                        .WithCancellation(shared.Token))
                    {
                        if (update.Kind == TranslationUpdateKind.Text)
                        {
                            finalText.Append(update.Text);
                            shared.Text.Append(update.Text);
                            shared.LastDelta = update.Text;
                        }
                        else if (update.Kind == TranslationUpdateKind.Completed)
                        {
                            completed = true;
                            terminal = true;
                        }
                        else if (update.Kind is TranslationUpdateKind.Failed or TranslationUpdateKind.Incomplete)
                        {
                            terminal = true;
                        }

                        Publish(shared, update.Kind, update.Error);
                    }

                    if (!terminal)
                        Publish(shared, TranslationUpdateKind.Incomplete, "The translation stream ended before completion.");
                    else if (completed)
                        cache.Set(shared.Request.CacheKey, finalText.ToString());
                }
                catch (OperationCanceledException) when (shared.Token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Publish(shared, TranslationUpdateKind.Failed, ex.Message);
                }
            }
            finally
            {
                if (entered)
                    activeRequests.Release();
                RemoveShared(shared);
            }
        }

        private void RemoveShared(SharedTranslation shared)
        {
            lock (sync)
            {
                if (inFlight.TryGetValue(shared.Request.CacheKey, out var current) &&
                    ReferenceEquals(current, shared))
                    inFlight.Remove(shared.Request.CacheKey);
            }
        }

        private void Publish(SharedTranslation shared, TranslationUpdateKind kind, string? error)
        {
            List<TranslationWork> subscribers;
            lock (sync)
            {
                subscribers = shared.Subscribers.ToList();
            }

            foreach (var work in subscribers)
            {
                if (kind == TranslationUpdateKind.Text)
                    work.Append(shared.LastDelta);
                Publish(work, kind, error);
            }
        }

        private void Publish(TranslationWork work, TranslationUpdateKind kind, string? error)
        {
            if (!IsCurrent(work))
                return;

            Updated?.Invoke(new TranslationTaskUpdate(
                work.Request,
                kind,
                work.Text.ToString(),
                error));
        }

        private bool IsCurrent(TranslationWork work)
        {
            lock (sync)
            {
                return !work.IsCanceled && latest.TryGetValue(
                    (work.Request.SessionId, work.Request.SentenceId), out var current) &&
                    ReferenceEquals(current, work);
            }
        }

        private sealed class SharedTranslation
        {
            public TranslationRequest Request { get; }
            public CancellationTokenSource Cancellation { get; } = new();
            public CancellationToken Token => Cancellation.Token;
            public List<TranslationWork> Subscribers { get; } = new();
            public StringBuilder Text { get; } = new();
            public string LastDelta { get; set; } = string.Empty;

            public SharedTranslation(TranslationRequest request)
            {
                Request = request;
            }

            public void Add(TranslationWork work)
            {
                Subscribers.Add(work);
                if (Text.Length > 0)
                    work.Append(Text.ToString());
            }

            public void Cancel() => Cancellation.Cancel();
        }

        private sealed class TranslationWork
        {
            private readonly StringBuilder text = new();
            public TranslationRequest Request { get; }
            public bool IsCanceled { get; private set; }
            public string Text => text.ToString();

            public TranslationWork(TranslationRequest request)
            {
                Request = request;
            }

            public void Append(string value)
            {
                if (value.Length > 0)
                    text.Append(value);
            }

            public void Cancel() => IsCanceled = true;
        }
    }
}
