using CsvHelper.Configuration.Attributes;

namespace LiveCaptionsTranslator.models
{
    public class TranslationHistoryEntry
    {
        [Ignore]
        public long Id { get; set; }
        [Ignore]
        public Guid? SessionId { get; set; }
        [Ignore]
        public long SentenceId { get; set; }
        [Ignore]
        public int Revision { get; set; }
        public required string Timestamp { get; set; }
        [Ignore]
        public required string TimestampFull { get; set; }
        public required string SourceText { get; set; }
        public required string TranslatedText { get; set; }
        public required string TargetLanguage { get; set; }
        public required string ApiUsed { get; set; }
    }
}
