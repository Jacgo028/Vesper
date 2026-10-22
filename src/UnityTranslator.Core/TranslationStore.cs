using Microsoft.Data.Sqlite;

namespace UnityTranslator.Core;

/// <summary>译文库里的一条记录。<paramref name="UserCorrected"/> 为真表示是用户手工改过的。</summary>
public sealed record StoredTranslation(string Translated, bool UserCorrected);

public sealed class TranslationStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public TranslationStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS translations (
                language TEXT NOT NULL,
                source TEXT NOT NULL,
                translated TEXT NOT NULL,
                user_corrected INTEGER NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (language, source)
            );
            CREATE TABLE IF NOT EXISTS provider_translations (
                provider TEXT NOT NULL,
                language TEXT NOT NULL,
                source TEXT NOT NULL,
                translated TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (provider, language, source)
            );
            """;
        command.ExecuteNonQuery();
    }

    public string? Find(string language, string source) => FindEntry(language, source)?.Translated;

    /// <summary>
    /// 连"是不是用户手工改过的"一起取出来。
    /// 用户改过的译文必须无条相信；自动翻出来的则要允许调用方重新校验 ——
    /// 旧版本把模型的对话式回复也当译文存了进来，缓存命中这条路以前完全不校验。
    /// </summary>
    public StoredTranslation? FindEntry(string language, string source)
    {
        lock (_gate)
        {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT translated, user_corrected FROM translations WHERE language=$language AND source=$source";
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$source", Normalize(source));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new StoredTranslation(reader.GetString(0), reader.GetInt32(1) != 0);
        }
    }

    /// <summary>自动译文按服务商分开；用户手工校正仍优先用于所有服务商。</summary>
    public StoredTranslation? FindEntry(string language, string source, string? providerScope)
    {
        if (providerScope is null) return FindEntry(language, source);
        var scope = RequireScope(providerScope);
        lock (_gate)
        {
            var existing = FindEntry(language, source);
            if (existing?.UserCorrected == true) return existing;
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT translated FROM provider_translations WHERE provider=$provider AND language=$language AND source=$source";
            command.Parameters.AddWithValue("$provider", scope);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$source", Normalize(source));
            var value = command.ExecuteScalar() as string;
            return value is null ? null : new StoredTranslation(value, false);
        }
    }

    /// <summary>
    /// 删掉一条译文。译文库里存着旧版本漏进来的垃圾译文时，用它清掉再重新翻译。
    /// </summary>
    public bool Delete(string language, string source)
    {
        lock (_gate)
        {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM translations WHERE language=$language AND source=$source";
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$source", Normalize(source));
        return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(string language, string source, string? providerScope)
    {
        if (providerScope is null) return Delete(language, source);
        var scope = RequireScope(providerScope);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM provider_translations WHERE provider=$provider AND language=$language AND source=$source";
            command.Parameters.AddWithValue("$provider", scope);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$source", Normalize(source));
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void SaveCorrection(string language, string source, string translated) => Save(language, source, translated, true);

    public void SaveGenerated(string language, string source, string translated) => Save(language, source, translated, false);

    public void SaveGenerated(string language, string source, string translated, string? providerScope)
    {
        if (providerScope is null) { SaveGenerated(language, source, translated); return; }
        var scope = RequireScope(providerScope);
        if (!TextSafety.IsSafe(source, translated)) throw new ArgumentException("译文未保留原文的格式或占位符", nameof(translated));
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO provider_translations (provider, language, source, translated, updated_utc)
                VALUES ($provider, $language, $source, $translated, $updated)
                ON CONFLICT(provider, language, source) DO UPDATE SET
                    translated=excluded.translated,
                    updated_utc=excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$provider", scope);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$source", Normalize(source));
            command.Parameters.AddWithValue("$translated", translated.Trim());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private void Save(string language, string source, string translated, bool corrected)
    {
        if (!TextSafety.IsSafe(source, translated)) throw new ArgumentException("译文未保留原文的格式或占位符", nameof(translated));
        lock (_gate)
        {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translations (language, source, translated, user_corrected, updated_utc)
            VALUES ($language, $source, $translated, $corrected, $updated)
            ON CONFLICT(language, source) DO UPDATE SET
                translated=excluded.translated,
                user_corrected=excluded.user_corrected,
                updated_utc=excluded.updated_utc
            WHERE translations.user_corrected=0 OR excluded.user_corrected=1;
            """;
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$source", Normalize(source));
        command.Parameters.AddWithValue("$translated", translated.Trim());
        command.Parameters.AddWithValue("$corrected", corrected ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        }
    }

    private static string Normalize(string value) => value.Trim().Normalize(System.Text.NormalizationForm.FormC);

    private static string RequireScope(string providerScope) => !string.IsNullOrWhiteSpace(providerScope)
        ? providerScope.Trim()
        : throw new ArgumentException("翻译服务缓存标识不能为空", nameof(providerScope));

    public void Dispose() { lock (_gate) _connection.Dispose(); }
}
