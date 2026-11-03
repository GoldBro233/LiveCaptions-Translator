using System.Globalization;
using System.Text;

using CsvHelper;
using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = "Data Source=translation_history.db;";

        private static SqliteConnection? sharedConnection;
        private static readonly object connectionLock = new();
        private static readonly SemaphoreSlim writeLock = new(1, 1);

        static SQLiteHistoryLogger()
        {
            InitializeDatabase();
        }

        private static void InitializeDatabase()
        {
            using var command = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );", GetConnection());
            command.ExecuteNonQuery();

            EnsureColumn("SessionId", "TEXT");
            EnsureColumn("SentenceId", "INTEGER");
            EnsureColumn("Revision", "INTEGER");
        }

        private static void EnsureColumn(string name, string type)
        {
            try
            {
                using var command = new SqliteCommand(
                    $"ALTER TABLE TranslationHistory ADD COLUMN {name} {type};", GetConnection());
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
            }
        }

        private static SqliteConnection GetConnection()
        {
            lock (connectionLock)
            {
                if (sharedConnection == null)
                {
                    sharedConnection = new SqliteConnection(CONNECTION_STRING);
                    sharedConnection.Open();
                }
                else if (sharedConnection.State != System.Data.ConnectionState.Open)
                {
                    sharedConnection.Dispose();
                    sharedConnection = new SqliteConnection(CONNECTION_STRING);
                    sharedConnection.Open();
                }
                return sharedConnection;
            }
        }

        public static async Task<TranslationHistoryEntry> UpsertTranslation(
            TranslationRequest request,
            string translatedText,
            CancellationToken token = default)
        {
            await writeLock.WaitAsync(token);
            try
            {
                var connection = GetConnection();
                long? existingId = null;
                using (var select = new SqliteCommand(@"
                    SELECT Id FROM TranslationHistory
                    WHERE SessionId = @SessionId AND SentenceId = @SentenceId
                    LIMIT 1", connection))
                {
                    select.Parameters.AddWithValue("@SessionId", request.SessionId.ToString());
                    select.Parameters.AddWithValue("@SentenceId", request.SentenceId);
                    var value = await select.ExecuteScalarAsync(token);
                    if (value != null && value != DBNull.Value)
                        existingId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }

                long id;
                if (existingId.HasValue)
                {
                    using var update = new SqliteCommand(@"
                        UPDATE TranslationHistory
                        SET Timestamp = @Timestamp,
                            SourceText = @SourceText,
                            TranslatedText = @TranslatedText,
                            TargetLanguage = @TargetLanguage,
                            ApiUsed = @ApiUsed,
                            Revision = @Revision
                        WHERE Id = @Id", connection);
                    AddTranslationParameters(update, request, translatedText);
                    update.Parameters.AddWithValue("@Id", existingId.Value);
                    await update.ExecuteNonQueryAsync(token);
                    id = existingId.Value;
                }
                else
                {
                    using var insert = new SqliteCommand(@"
                        INSERT INTO TranslationHistory
                            (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                             SessionId, SentenceId, Revision)
                        VALUES
                            (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed,
                             @SessionId, @SentenceId, @Revision);
                        SELECT last_insert_rowid();", connection);
                    AddTranslationParameters(insert, request, translatedText);
                    id = Convert.ToInt64(await insert.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                return CreateEntry(
                    id,
                    request.SessionId,
                    request.SentenceId,
                    request.Revision,
                    request.Text,
                    translatedText,
                    request.Settings.TargetLanguage,
                    request.Settings.ApiName,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task LogTranslation(
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            CancellationToken token = default)
        {
            await writeLock.WaitAsync(token);
            try
            {
                using var command = new SqliteCommand(@"
                    INSERT INTO TranslationHistory
                        (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                    VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)", GetConnection());
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@SourceText", sourceText);
                command.Parameters.AddWithValue("@TranslatedText", translatedText);
                command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
                command.Parameters.AddWithValue("@ApiUsed", apiUsed);
                await command.ExecuteNonQueryAsync(token);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page,
            int maxRow,
            string searchText,
            CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            int totalCount;
            using (var count = new SqliteCommand(@"
                SELECT COUNT(*) FROM TranslationHistory
                WHERE SourceText LIKE @Search OR TranslatedText LIKE @Search", GetConnection()))
            {
                count.Parameters.AddWithValue("@Search", $"%{searchText}%");
                totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }

            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            int offset = Math.Max(0, (page - 1) * maxRow);
            using var command = new SqliteCommand(@"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                       SessionId, SentenceId, Revision
                FROM TranslationHistory
                WHERE SourceText LIKE @Search OR TranslatedText LIKE @Search
                ORDER BY Id DESC
                LIMIT @MaxRow OFFSET @Offset", GetConnection());
            command.Parameters.AddWithValue("@Search", $"%{searchText}%");
            command.Parameters.AddWithValue("@MaxRow", maxRow);
            command.Parameters.AddWithValue("@Offset", offset);

            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                history.Add(ReadEntry(reader));
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            await writeLock.WaitAsync(token);
            try
            {
                using var command = new SqliteCommand(
                    "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'",
                    GetConnection());
                await command.ExecuteNonQueryAsync(token);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task<string> LoadLastSourceText(CancellationToken token = default)
        {
            using var command = new SqliteCommand(
                "SELECT SourceText FROM TranslationHistory ORDER BY Id DESC LIMIT 1", GetConnection());
            return (await command.ExecuteScalarAsync(token))?.ToString() ?? string.Empty;
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            using var command = new SqliteCommand(@"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                       SessionId, SentenceId, Revision
                FROM TranslationHistory ORDER BY Id DESC LIMIT 1", GetConnection());
            using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? ReadEntry(reader) : null;
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            using (var command = new SqliteCommand(@"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed,
                       SessionId, SentenceId, Revision
                FROM TranslationHistory ORDER BY Id DESC", GetConnection()))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                    history.Add(ReadEntry(reader));
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        private static void AddTranslationParameters(
            SqliteCommand command,
            TranslationRequest request,
            string translatedText)
        {
            command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@SourceText", request.Text);
            command.Parameters.AddWithValue("@TranslatedText", translatedText);
            command.Parameters.AddWithValue("@TargetLanguage", request.Settings.TargetLanguage);
            command.Parameters.AddWithValue("@ApiUsed", request.Settings.ApiName);
            command.Parameters.AddWithValue("@SessionId", request.SessionId.ToString());
            command.Parameters.AddWithValue("@SentenceId", request.SentenceId);
            command.Parameters.AddWithValue("@Revision", request.Revision);
        }

        private static TranslationHistoryEntry ReadEntry(SqliteDataReader reader)
        {
            DateTimeOffset timestamp = ParseTimestamp(reader["Timestamp"]?.ToString());
            Guid? sessionId = Guid.TryParse(reader["SessionId"]?.ToString(), out var parsed)
                ? parsed
                : null;
            return CreateEntry(
                Convert.ToInt64(reader["Id"], CultureInfo.InvariantCulture),
                sessionId,
                Convert.ToInt64(reader["SentenceId"] == DBNull.Value ? 0 : reader["SentenceId"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["Revision"] == DBNull.Value ? 0 : reader["Revision"], CultureInfo.InvariantCulture),
                reader["SourceText"]?.ToString() ?? string.Empty,
                reader["TranslatedText"]?.ToString() ?? string.Empty,
                reader["TargetLanguage"]?.ToString() ?? string.Empty,
                reader["ApiUsed"]?.ToString() ?? string.Empty,
                timestamp);
        }

        private static TranslationHistoryEntry CreateEntry(
            long id,
            Guid? sessionId,
            long sentenceId,
            int revision,
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            DateTimeOffset timestamp)
        {
            var localTime = timestamp.LocalDateTime;
            return new TranslationHistoryEntry
            {
                Id = id,
                SessionId = sessionId,
                SentenceId = sentenceId,
                Revision = revision,
                Timestamp = localTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                SourceText = sourceText,
                TranslatedText = translatedText,
                TargetLanguage = targetLanguage,
                ApiUsed = apiUsed
            };
        }

        private static DateTimeOffset ParseTimestamp(string? value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            return DateTimeOffset.UtcNow;
        }
    }
}
