using System.Collections.ObjectModel;
using System.Text;

namespace LiveCaptionsTranslator.models
{
    public enum OpenAIProtocol
    {
        ChatCompletions,
        Responses
    }

    public enum TranslationUpdateKind
    {
        Text,
        Completed,
        Failed,
        Incomplete
    }

    public sealed record TranslationUpdate(
        TranslationUpdateKind Kind,
        string Text = "",
        string? Error = null);

    public sealed record TranslationContext(string SourceText, string TranslatedText);

    public sealed record TranslationSettingsSnapshot(
        string ApiName,
        int ConfigIndex,
        string TargetLanguage,
        string Prompt,
        bool ContextAware,
        string ModelName,
        double Temperature,
        string ApiKey,
        string ApiUrl,
        int KeepAlive,
        OpenAIProtocol Protocol,
        bool Stream,
        long ConfigurationVersion,
        IReadOnlyDictionary<string, string> Parameters)
    {
        public string Parameter(string name) =>
            Parameters.TryGetValue(name, out var value) ? value : string.Empty;
    }

    public sealed record TranslationRequest(
        Guid SessionId,
        long SentenceId,
        int Revision,
        string Text,
        bool IsFinal,
        IReadOnlyList<TranslationContext> Contexts,
        TranslationSettingsSnapshot Settings)
    {
        public string CacheKey
        {
            get
            {
                var builder = new StringBuilder();
                Append(builder, Settings.ApiName);
                Append(builder, Settings.ConfigIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(builder, Settings.ConfigurationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(builder, Settings.Protocol.ToString());
                Append(builder, Settings.ApiUrl);
                Append(builder, Settings.ModelName);
                Append(builder, Settings.TargetLanguage);
                Append(builder, Settings.Prompt);
                Append(builder, Settings.ContextAware.ToString());
                Append(builder, Settings.Temperature.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                Append(builder, Settings.KeepAlive.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(builder, Settings.Stream.ToString());
                foreach (var parameter in Settings.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    Append(builder, parameter.Key);
                    Append(builder, parameter.Value);
                }
                Append(builder, Text);
                foreach (var context in Contexts)
                {
                    Append(builder, context.SourceText);
                    Append(builder, context.TranslatedText);
                }
                return builder.ToString();
            }
        }

        private static void Append(StringBuilder builder, string value)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }
    }

    public sealed record CaptionSentence(
        long Id,
        int Revision,
        string Text,
        bool IsFinal,
        bool Changed);

    public sealed record CaptionSnapshot(
        IReadOnlyList<CaptionSentence> Sentences,
        bool SourceReset);
}
