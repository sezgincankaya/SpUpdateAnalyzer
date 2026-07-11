using ConsoleApp3.Parsing;
using Xunit;

namespace ConsoleApp3.Tests;

public class ParallelSpAnalyzerTests
{
    private static UpdateStatementAnalyzer CreateAnalyzer(params string[] tables)
        => new(tables, verbose: false);

    private static Dictionary<string, string> BuildSpSet(int count)
    {
        var sps = new Dictionary<string, string>();
        for (var i = 0; i < count; i++)
        {
            // Çift indeksliler UpdatedDate set eder (OK), tek indeksliler etmez (eksik)
            var setClause = i % 2 == 0
                ? "SET Status = 1, UpdatedDate = GETDATE()"
                : "SET Status = 1";

            sps[$"usp_Test_{i:D3}"] = $@"
CREATE PROCEDURE dbo.usp_Test_{i:D3} AS BEGIN
    UPDATE dbo.Orders
    {setClause}
    WHERE Id = {i}
END";
        }
        return sps;
    }

    // -------------------------------------------------------------------------
    // 1. Paralel analiz — tüm SP'ler iþlenmeli
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task AnalyzeAll_WithVariousThreadCounts_ShouldProcessAllSps(int threadCount)
    {
        var sps = BuildSpSet(50);
        var analyzer = CreateAnalyzer("Orders");

        var results = await ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, threadCount);

        Assert.Equal(50, results.Count);
        Assert.Equal(50, results.Select(r => r.SpName).Distinct().Count());
    }

    // -------------------------------------------------------------------------
    // 2. Paralel sonuçlar sýralý çalýþtýrmayla ayný olmalý (determinizm)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task AnalyzeAll_ParallelResults_ShouldMatchSequentialResults()
    {
        var sps = BuildSpSet(30);
        var analyzer = CreateAnalyzer("Orders");

        var sequential = await ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, 1);
        var parallel = await ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, 8);

        Assert.Equal(sequential.Count, parallel.Count);
        for (var i = 0; i < sequential.Count; i++)
        {
            Assert.Equal(sequential[i].SpName, parallel[i].SpName);
            Assert.Equal(sequential[i].TotalTargetUpdateCount, parallel[i].TotalTargetUpdateCount);
            Assert.Equal(sequential[i].MissingColumnCount, parallel[i].MissingColumnCount);
        }
    }

    // -------------------------------------------------------------------------
    // 3. Eksik kolon sayýsý doðru toplanmalý
    // -------------------------------------------------------------------------
    [Fact]
    public async Task AnalyzeAll_ShouldCountMissingColumnsCorrectly()
    {
        var sps = BuildSpSet(10); // 5 OK (çift), 5 eksik (tek)
        var analyzer = CreateAnalyzer("Orders");

        var results = await ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, 4);

        Assert.Equal(5, results.Count(r => r.MissingColumnCount > 0));
        Assert.Equal(5, results.Count(r => r.MissingColumnCount == 0));
    }

    // -------------------------------------------------------------------------
    // 4. Geçersiz thread sayýsý — hata fýrlatmalý
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnalyzeAll_InvalidThreadCount_ShouldThrow(int threadCount)
    {
        var analyzer = CreateAnalyzer("Orders");
        var sps = BuildSpSet(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, threadCount));
    }

    // -------------------------------------------------------------------------
    // 5. Boþ SP listesi — boþ sonuç dönmeli
    // -------------------------------------------------------------------------
    [Fact]
    public async Task AnalyzeAll_EmptyInput_ShouldReturnEmpty()
    {
        var analyzer = CreateAnalyzer("Orders");

        var results = await ParallelSpAnalyzer.AnalyzeAllAsync(
            analyzer, new Dictionary<string, string>(), 4);

        Assert.Empty(results);
    }

    // -------------------------------------------------------------------------
    // 6. Ýptal token'ý — OperationCanceledException fýrlatmalý
    // -------------------------------------------------------------------------
    [Fact]
    public async Task AnalyzeAll_CancelledToken_ShouldThrowOperationCanceled()
    {
        var analyzer = CreateAnalyzer("Orders");
        var sps = BuildSpSet(100);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, 4, cts.Token));
    }

    // -------------------------------------------------------------------------
    // 7. Sonuçlar SP adýna göre sýralý olmalý
    // -------------------------------------------------------------------------
    [Fact]
    public async Task AnalyzeAll_Results_ShouldBeOrderedBySpName()
    {
        var sps = BuildSpSet(20);
        var analyzer = CreateAnalyzer("Orders");

        var results = await ParallelSpAnalyzer.AnalyzeAllAsync(analyzer, sps, 8);

        var names = results.Select(r => r.SpName).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }
}
