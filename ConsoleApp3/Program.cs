using ConsoleApp3.Parsing;
using ConsoleApp3.Reporting;
using System.Text.Json;

// ---- Argüman parse ----
string? inputFile = null;
string? outputFile = null;
string? tablesFile = null;
var verbose = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input" when i + 1 < args.Length:
            inputFile = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            outputFile = args[++i];
            break;
        case "--tables" when i + 1 < args.Length:
            tablesFile = args[++i];
            break;
        case "--verbose":
            verbose = true;
            break;
    }
}

inputFile ??= "scripts.sql";
outputFile ??= "result.xlsx";
tablesFile ??= "tables.json";

// ---- Dosya kontrolleri ----
if (!File.Exists(inputFile))
{
    Console.Error.WriteLine($"[HATA] Girdi dosyası bulunamadı: {inputFile}");
    Environment.Exit(1);
}

if (!File.Exists(tablesFile))
{
    Console.Error.WriteLine($"[HATA] Tablo listesi dosyası bulunamadı: {tablesFile}");
    Environment.Exit(1);
}

// ---- Tablo listesini oku ----
List<string> targetTables;
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

Console.WriteLine($"Hedef tablo sayısı   : {targetTables.Count}");
Console.WriteLine($"Girdi dosyası        : {inputFile}");
Console.WriteLine($"Çıktı dosyası        : {outputFile}");
Console.WriteLine($"Verbose modu         : {(verbose ? "Açık" : "Kapalı")}");
Console.WriteLine(new string('-', 50));

// ---- SP'leri parse et ----
Dictionary<string, string> spMap;
try
{
    spMap = SqlScriptParser.ParseStoredProcedures(inputFile);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HATA] Script dosyası okunamadı: {ex.Message}");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"Toplam SP sayısı     : {spMap.Count}");

// ---- Her SP'yi analiz et ----
var analyzer = new UpdateStatementAnalyzer(targetTables, verbose);
var allResults = spMap
    .Select(kv => analyzer.Analyze(kv.Key, kv.Value))
    .ToList();

// ---- Özet konsola yaz ----
Console.WriteLine("\n--- Analiz Özeti ---");
foreach (var r in allResults)
{
    if (r.TotalTargetUpdateCount == 0 && !r.HasDynamicSqlWarning) continue;
    var status = r.MissingColumnCount > 0 ? "EKSİK KOLON" : "OK";
    if (r.HasDynamicSqlWarning) status += " [DİNAMİK SQL UYARISI]";
    Console.WriteLine($"  {r.SpName,-50} → {status}");
}

// ---- Excel raporunu oluştur ----
try
{
    ExcelReporter.WriteReport(allResults, outputFile);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HATA] Excel dosyası yazılamadı: {ex.Message}");
    Environment.Exit(1);
}