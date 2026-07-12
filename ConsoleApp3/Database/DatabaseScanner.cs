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
    /// Son taramanın toplam süresi.
    /// </summary>
    public TimeSpan Elapsed { get; private set; }

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

        // DB başına SP sayılarını al ve başlangıçta özet göster
        var dbOverview = new List<(string Database, int SpCount)>(databases.Count);
        foreach (var db in databases)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dbOverview.Add((db, await _reader.GetStoredProcedureCountAsync(db, ct)));
            }
            catch (Exception ex)
            {
                ProgressReporter.Warning($"'{db}' SP sayısı alınamadı: {ex.Message}");
                dbOverview.Add((db, 0));
            }
        }
        ProgressReporter.Overview(dbOverview);

        int totalDbMissing = 0, totalDbSp = 0;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 2. Her DB için
        for (var dbIdx = 0; dbIdx < databases.Count; dbIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var db = databases[dbIdx];
            var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ProgressReporter.Database(db, dbIdx + 1, databases.Count);

            int dbSpCount = 0, dbMissingCount = 0;
            var dbTotalSp = dbOverview[dbIdx].SpCount;
            var dbProcessedSp = 0;

            // 3. Tüm SP definition'larını TEK sorguda çek (SP başına bağlantı açılmaz)
            List<(string Schema, string SpName, string Definition)> spDefinitions;
            try
            {
                spDefinitions = await _reader.GetAllSpDefinitionsAsync(db, ct);
            }
            catch (Exception ex)
            {
                ProgressReporter.Error($"'{db}' SP definition'ları alınamadı: {ex.Message}");
                continue;
            }

            if (spDefinitions.Count == 0)
            {
                ProgressReporter.Warning($"'{db}' içinde SP yok, atlanıyor.");
                continue;
            }

            // 4. Analizi tamamen CPU-bound paralel çalıştır
            var dbResults = new System.Collections.Concurrent.ConcurrentBag<SpAnalysisResult>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxDegreeOfParallelism,
                CancellationToken = ct
            };

            await Task.Run(() =>
                Parallel.ForEach(spDefinitions, parallelOptions, sp =>
                {
                    var (spSchema, spName, definition) = sp;
                    var currentDb = Interlocked.Increment(ref dbProcessedSp);
                    ProgressReporter.Progress(currentDb, dbTotalSp);

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

                    // SP bazlı detaylar sadece verbose modda konsola yazılır;
                    // eksik kolon / dinamik SQL detayları Excel raporunda yer alır.
                    if (_verbose)
                    {
                        ProgressReporter.SpResult(
                            spName,
                            result.MissingColumnCount > 0,
                            result.HasDynamicSqlWarning);
                    }

                    dbResults.Add(result);
                }), ct);

            foreach (var result in dbResults
                         .OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.SpName, StringComparer.OrdinalIgnoreCase))
            {
                allResults.Add(result);
                dbMissingCount += result.MissingColumnCount;
                dbSpCount++;
            }

            totalDbSp += dbSpCount;
            totalDbMissing += dbMissingCount;
            dbStopwatch.Stop();
            ProgressReporter.DatabaseComplete(db, dbSpCount, dbMissingCount, dbStopwatch.Elapsed);
        }

        totalStopwatch.Stop();
        Elapsed = totalStopwatch.Elapsed;
        ProgressReporter.Summary(databases.Count, totalDbSp, totalDbMissing, string.Empty, totalStopwatch.Elapsed);
        return allResults;
    }
}