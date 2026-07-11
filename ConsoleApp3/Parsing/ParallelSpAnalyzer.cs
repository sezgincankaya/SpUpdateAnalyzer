using ConsoleApp3.Models;
using System.Collections.Concurrent;

namespace ConsoleApp3.Parsing;

/// <summary>
/// Birden fazla SP body'sini belirtilen thread sayýsýyla paralel analiz eder.
/// </summary>
public static class ParallelSpAnalyzer
{
    /// <summary>
    /// Verilen SP'leri (ad ? body) paralel olarak analiz eder.
    /// </summary>
    /// <param name="analyzer">Kullanýlacak analizör (thread-safe olmalýdýr).</param>
    /// <param name="storedProcedures">SP adý ? SP body eþlemesi.</param>
    /// <param name="maxDegreeOfParallelism">Ayný anda çalýþacak maksimum thread sayýsý.</param>
    /// <param name="ct">Ýptal token'ý.</param>
    /// <returns>SP adýna göre sýralanmýþ analiz sonuçlarý.</returns>
    public static async Task<List<SpAnalysisResult>> AnalyzeAllAsync(
        UpdateStatementAnalyzer analyzer,
        IReadOnlyDictionary<string, string> storedProcedures,
        int maxDegreeOfParallelism,
        CancellationToken ct = default)
    {
        if (analyzer is null)
            throw new ArgumentNullException(nameof(analyzer));
        if (storedProcedures is null)
            throw new ArgumentNullException(nameof(storedProcedures));
        if (maxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism),
                "Thread sayýsý 1 veya daha büyük olmalýdýr.");

        var results = new ConcurrentBag<SpAnalysisResult>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(storedProcedures, options, (kvp, token) =>
        {
            token.ThrowIfCancellationRequested();
            var result = analyzer.Analyze(kvp.Key, kvp.Value);
            results.Add(result);
            return ValueTask.CompletedTask;
        });

        return results
            .OrderBy(r => r.SpName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
