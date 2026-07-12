using ConsoleApp3.Database;
using ConsoleApp3.Parsing;
using Xunit;

namespace ConsoleApp3.Tests;

public class DatabaseScannerTests
{
    private static UpdateStatementAnalyzer CreateAnalyzer()
        => new(new[] { "Orders" }, verbose: false);

    // -------------------------------------------------------------------------
    // 1. Gecersiz thread sayisi ile olusturma - ArgumentOutOfRangeException
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_InvalidThreadCount_ShouldThrow(int threadCount)
    {
        var reader = new SqlServerReader("Server=localhost;Integrated Security=true;TrustServerCertificate=true");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DatabaseScanner(reader, CreateAnalyzer(), verbose: false, threadCount));
    }

    // -------------------------------------------------------------------------
    // 2. Gecerli thread sayisi ile olusturma - hata firlatmamali
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(64)]
    public void Constructor_ValidThreadCount_ShouldNotThrow(int threadCount)
    {
        var reader = new SqlServerReader("Server=localhost;Integrated Security=true;TrustServerCertificate=true");

        var scanner = new DatabaseScanner(reader, CreateAnalyzer(), verbose: false, threadCount);

        Assert.NotNull(scanner);
    }

    // -------------------------------------------------------------------------
    // 3. Tarama yapilmadan Elapsed sifir olmali
    // -------------------------------------------------------------------------
    [Fact]
    public void Elapsed_BeforeScan_ShouldBeZero()
    {
        var reader = new SqlServerReader("Server=localhost;Integrated Security=true;TrustServerCertificate=true");

        var scanner = new DatabaseScanner(reader, CreateAnalyzer());

        Assert.Equal(TimeSpan.Zero, scanner.Elapsed);
    }
}
