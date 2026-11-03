namespace LiveCaptionsTranslator.models
{
    public sealed class TranslationCache
    {
        public const int Capacity = 64;
        public static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(10);

        private readonly object sync = new();
        private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> order = new();

        public bool TryGet(string key, out string value)
        {
            lock (sync)
            {
                if (!entries.TryGetValue(key, out var entry))
                {
                    value = string.Empty;
                    return false;
                }

                if (DateTimeOffset.UtcNow - entry.CreatedAt > EntryLifetime)
                {
                    Remove(key, entry);
                    value = string.Empty;
                    return false;
                }

                order.Remove(entry.Node);
                order.AddFirst(entry.Node);
                value = entry.Value;
                return true;
            }
        }

        public void Set(string key, string value)
        {
            lock (sync)
            {
                if (entries.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    existing.CreatedAt = DateTimeOffset.UtcNow;
                    order.Remove(existing.Node);
                    order.AddFirst(existing.Node);
                    return;
                }

                var node = order.AddFirst(key);
                entries[key] = new CacheEntry(value, DateTimeOffset.UtcNow, node);
                while (entries.Count > Capacity)
                {
                    var last = order.Last;
                    if (last == null)
                        break;
                    var lastKey = last.Value;
                    order.RemoveLast();
                    entries.Remove(lastKey);
                }
            }
        }

        private void Remove(string key, CacheEntry entry)
        {
            entries.Remove(key);
            order.Remove(entry.Node);
        }

        private sealed class CacheEntry
        {
            public string Value { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public LinkedListNode<string> Node { get; }

            public CacheEntry(string value, DateTimeOffset createdAt, LinkedListNode<string> node)
            {
                Value = value;
                CreatedAt = createdAt;
                Node = node;
            }
        }
    }
}
