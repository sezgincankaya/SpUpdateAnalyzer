using ConsoleApp3.Models;
using ConsoleApp3.Parsing;

namespace ConsoleApp3.Database;

/// <summary>
/// Sunucudaki tüm veritabanlarını ve şemalarını adım adım tarayarak
/// SP analizi yapar. İlerlemeyi ProgressReporter ile konsola yazar.
/// </summary>
public class DatabaseScanner
{
    private readonly SqlServerReader _reader;
    private readonly UpdateStatementAnalyzer _analyzer;
    private readonly bool _verbose;
    private readonly int _maxDegreeOfParallelism;

    public DatabaseScanner(
        SqlServerReader reader,
        UpdateStatementAnalyzer analyzer,
        bool verbose = false,
        int maxDegreeOfParallelism = 1)
    {
        if (maxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism),
                "Thread sayısı 1 veya daha büyük olmalıdır.");

        _reader = reader;
        _analyzer = analyzer;
        _verbose = verbose;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>
    /// Tüm sunucuyu tarar, sonuçları döner.
    /// </summary>
    public async Task<List<SpAnalysisResult>> ScanAllAsync(CancellationToken ct = default)
    {
        var allResults = new List<SpAnalysisResult>();

        // 1. Veritabanlarını al
        ProgressReporter.Info("Veritabanları listeleniyor...");
        List<string> databases;
        try
        {
            databases = await _reader.GetUserDatabasesAsync(ct);
        }
        catch (Exception ex)
        {
            ProgressReporter.Error($"Veritabanları listelenemedi: {ex.Message}");
            return allResults;
        }

        ProgressReporter.Info($"Bulunan kullanıcı DB sayısı: {databases.Count}");

        int totalDbMissing = 0, totalDbSp = 0;

        // 2. Her DB için
        for (var dbIdx = 0; dbIdx < databases.Count; dbIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var db = databases[dbIdx];
            ProgressReporter.Database(db, dbIdx + 1, databases.Count);

            int dbSpCount = 0, dbMissingCount = 0;

            // 3. Şemaları al
            List<string> schemas;
            try
            {
                schemas = await _reader.GetSchemasAsync(db, ct);
            }
            catch (Exception ex)
            {
                ProgressReporter.Error($"'{db}' şemaları alınamadı: {ex.Message}");
                continue;
            }

            if (schemas.Count == 0)
            {
                ProgressReporter.Warning($"'{db}' içinde SP bulunan şema yok, atlanıyor.");
                continue;
            }

            // 4. Her şema için
            for (var schemaIdx = 0; schemaIdx < schemas.Count; schemaIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var schema = schemas[schemaIdx];
                ProgressReporter.Schema(schema, schemaIdx + 1, schemas.Count);

                // 5. SP listesini al
                List<(string Schema, string SpName)> spList;
                try
                {
                    spList = await _reader.GetStoredProcedureNamesAsync(db, schema, ct);
                }
                catch (Exception ex)
                {
                    ProgressReporter.Error($"'{db}.{schema}' SP listesi alınamadı: {ex.Message}");
                    continue;
                }

                int schemaMissing = 0;

                // 6. SP'leri paralel işle (thread sayısı kullanıcı tarafından belirlenir)
                var schemaResults = new System.Collections.Concurrent.ConcurrentBag<SpAnalysisResult>();
                var processedCount = 0;

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(spList, parallelOptions, async (sp, token) =>
                {
                    var (spSchema, spName) = sp;
                    var current = Interlocked.Increment(ref processedCount);
                    ProgressReporter.StoredProcedure(spName, current, spList.Count);

                    // Definition'ı çek
                    string? definition;
                    try
                    {
                        definition = await _reader.GetSpDefinitionAsync(db, spSchema, spName, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ProgressReporter.Error($"'{spName}' definition alınamadı: {ex.Message}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(definition))
                    {
                        if (_verbose)
                            ProgressReporter.Warning($"'{spName}' definition boş, atlanıyor.");
                        return;
                    }

                    // Analiz et
                    SpAnalysisResult result;
                    try
                    {
                        // Tam niteliklendirme: DB.Schema.SpName
                        var fullName = $"{db}.{spSchema}.{spName}";
                        result = _analyzer.Analyze(fullName, definition);

                        // DB ve şema bilgisini result'a ekle (raporlama için)
                        result.DatabaseName = db;
                        result.SchemaName = spSchema;
                    }
                    catch (Exception ex)
                    {
                        ProgressReporter.Error($"'{spName}' analiz hatası: {ex.Message}");
                        return;
                    }

                    ProgressReporter.SpResult(
                        spName,
                        result.MissingColumnCount > 0,
                        result.HasDynamicSqlWarning);

                    schemaResults.Add(result);
                });

                foreach (var result in schemaResults.OrderBy(r => r.SpName, StringComparer.OrdinalIgnoreCase))
                {
                    allResults.Add(result);
                    schemaMissing += result.MissingColumnCount;
                    dbSpCount++;
                }

                dbMissingCount += schemaMissing;
                ProgressReporter.SchemaComplete(schema, spList.Count, schemaMissing);
            }

            totalDbSp += dbSpCount;
            totalDbMissing += dbMissingCount;
            ProgressReporter.DatabaseComplete(db, dbSpCount, dbMissingCount);
        }

        ProgressReporter.Summary(databases.Count, totalDbSp, totalDbMissing, string.Empty);
        return allResults;
    }
}