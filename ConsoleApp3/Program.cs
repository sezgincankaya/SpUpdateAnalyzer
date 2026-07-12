using ConsoleApp3.Database;
using ConsoleApp3.Models;
using ConsoleApp3.Parsing;
using ConsoleApp3.Reporting;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

// ─────────────────────────────────────────────
// Argüman parse
// ─────────────────────────────────────────────
string? connectionString = null;
string? outputFile = null;
string? tablesFile = null;
var verbose = false;
var threadCount = Environment.ProcessorCount*2;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--connection" when i + 1 < args.Length: connectionString = args[++i]; break;
        case "--output"     when i + 1 < args.Length: outputFile       = args[++i]; break;
        case "--tables"     when i + 1 < args.Length: tablesFile       = args[++i]; break;
        case "--threads"    when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out threadCount) || threadCount <= 0)
            {
                Console.Error.WriteLine("[HATA] --threads değeri pozitif bir tam sayı olmalıdır.");
                Environment.Exit(1);
            }
            break;
        case "--verbose": verbose = true; break;
    }
}

outputFile ??= $"result_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
tablesFile ??= "tables.json";

// ─────────────────────────────────────────────
// Bağlantı string'i — önce appsettings, sonra argüman, en son prompt
// ─────────────────────────────────────────────
if (string.IsNullOrWhiteSpace(connectionString))
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .Build();

    connectionString = config.GetConnectionString("Boa");
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Write("SQL Server bağlantı string'i: ");
    connectionString = Console.ReadLine()?.Trim();
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("[HATA] Bağlantı string'i boş olamaz.");
    Environment.Exit(1);
}

// ─────────────────────────────────────────────
// Tablo listesi — yoksa DB'den otomatik üret
// ─────────────────────────────────────────────
List<string> targetTables;

if (!File.Exists(tablesFile))
{
    Console.WriteLine($"[BİLGİ] '{tablesFile}' bulunamadı. Veritabanından tablo listesi alınıyor...");
    try
    {
        var tempReader = new SqlServerReader(connectionString!);
        // Initial Catalog varsa doğrudan o DB; yoksa ilk kullanıcı DB'si
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        var targetDb = !string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? builder.InitialCatalog
            : (await tempReader.GetUserDatabasesAsync()).FirstOrDefault();

        if (targetDb is null)
        {
            Console.Error.WriteLine("[HATA] Hedef veritabanı bulunamadı.");
            Environment.Exit(1);
            return;
        }

        targetTables = await tempReader.GetAllTablesAsync(targetDb);

        if (targetTables.Count == 0)
        {
            Console.Error.WriteLine($"[HATA] '{targetDb}' veritabanında tablo bulunamadı.");
            Environment.Exit(1);
            return;
        }

        var jsonContent = JsonSerializer.Serialize(
            new { tables = targetTables },
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(tablesFile, jsonContent);
        Console.WriteLine($"[BİLGİ] {targetTables.Count} tablo '{tablesFile}' dosyasına yazıldı (DB: {targetDb}).");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HATA] Tablo listesi oluşturulamadı: {ex.Message}");
        Environment.Exit(1);
        return;
    }
}
else
{
    try
    {
        var json = File.ReadAllText(tablesFile);
        var doc = JsonDocument.Parse(json);
        targetTables = doc.RootElement
            .GetProperty("tables")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HATA] Tablo listesi okunamadı: {ex.Message}");
        Environment.Exit(1);
        return;
    }
}

// ─────────────────────────────────────────────
// Başlangıç bilgisi
// ─────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine(new string('═', 60));
Console.WriteLine("  SP Update Kolon Analizörü");
Console.WriteLine(new string('═', 60));
Console.ResetColor();
Console.WriteLine($"  Hedef tablo sayısı : {targetTables.Count}");
Console.WriteLine($"  Çıktı dosyası      : {outputFile}");
Console.WriteLine($"  Verbose            : {(verbose ? "Açık" : "Kapalı")}");
Console.WriteLine($"  Thread sayısı      : {threadCount}");
Console.WriteLine();

// ─────────────────────────────────────────────
// Tarama
// ─────────────────────────────────────────────
var reader = new SqlServerReader(connectionString);
var analyzer = new UpdateStatementAnalyzer(targetTables, verbose);
var scanner = new DatabaseScanner(reader, analyzer, verbose, threadCount);

using var cts = new CancellationTokenSource();

// Ctrl+C ile temiz iptal
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[İPTAL] Kullanıcı tarafından durduruldu...");
    cts.Cancel();
};

List<SpAnalysisResult> allResults;
try
{
    allResults = await scanner.ScanAllAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Tarama iptal edildi. Mevcut sonuçlar raporlanıyor...");
    allResults = new();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HATA] Beklenmeyen hata: {ex.Message}");
    Environment.Exit(1);
    return;
}

// ─────────────────────────────────────────────
// Excel raporu
// ─────────────────────────────────────────────
if (allResults.Count == 0)
{
    Console.WriteLine("Analiz edilecek sonuç bulunamadı.");
    return;
}

try
{
    ExcelReporter.WriteReport(allResults, outputFile);
    ProgressReporter.Summary(
        allResults.Select(r => r.DatabaseName).Distinct().Count(),
        allResults.Count,
        allResults.Sum(r => r.MissingColumnCount),
        outputFile,
        scanner.Elapsed);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HATA] Excel yazılamadı: {ex.Message}");
    Environment.Exit(1);
}