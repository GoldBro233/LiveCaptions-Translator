using System.Text.Json;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.apis
{
    public sealed class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");

            var configs = new Dictionary<string, List<TranslateAPIConfig>>();
            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString() ?? string.Empty;
                reader.Read();
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException("Expected a StartArray token.");

                var list = new List<TranslateAPIConfig>();
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndArray)
                {
                    var config = configType != null &&
                        typeof(TranslateAPIConfig).IsAssignableFrom(configType)
                        ? (TranslateAPIConfig?)JsonSerializer.Deserialize(ref reader, configType, options)
                        : JsonSerializer.Deserialize<TranslateAPIConfig>(ref reader, options);
                    if (config == null)
                        throw new JsonException($"Unable to deserialize config '{key}'.");
                    list.Add(config);
                    reader.Read();
                }

                configs[key] = list;
                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<string, List<TranslateAPIConfig>> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value)
            {
                writer.WritePropertyName(pair.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{pair.Key}Config");
                writer.WriteStartArray();
                foreach (var config in pair.Value)
                {
                    if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                        JsonSerializer.Serialize(writer, config, configType, options);
                    else
                        JsonSerializer.Serialize(writer, config, typeof(TranslateAPIConfig), options);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }
}
