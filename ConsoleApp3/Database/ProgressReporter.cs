namespace ConsoleApp3.Database;

/// <summary>
/// Konsola renkli, hiyerarşik ilerleme bilgisi yazar.
/// Thread-safe değildir; tek thread'den çağrılması beklenir.
/// </summary>
public static class ProgressReporter
{
    private static readonly object _lock = new();

    // ---- Genel bilgi ----
    public static void Info(string message)
        => Write(message, ConsoleColor.Gray);

    // ---- DB seviyesi ----
    public static void Database(string dbName, int current, int total)
        => Write($"\n[{current}/{total}] ▶ Database: {dbName}", ConsoleColor.Cyan);

    // ---- Şema seviyesi ----
    public static void Schema(string schemaName, int current, int total)
        => Write($"  [{current}/{total}] Schema: {schemaName}", ConsoleColor.Yellow);

    // ---- SP seviyesi ----
    public static void StoredProcedure(string spName, int current, int total)
    {
        lock (_lock)
        {
            Console.CursorLeft = 0;
            var text = $"    [{current}/{total}] SP: {spName}";
            // Konsol genişliğine sığdır, taşanı sil
            var maxWidth = Console.WindowWidth - 1;
            if (text.Length > maxWidth)
                text = text[..maxWidth];
            Console.Write(text.PadRight(maxWidth));
        }
    }

    // ---- SP sonucu ----
    public static void SpResult(string spName, bool hasMissing, bool hasWarning)
    {
        if (!hasMissing && !hasWarning) return;

        lock (_lock)
        {
            Console.WriteLine(); // SP satırının altına in
            if (hasMissing)
                Write($"      ⚠  EKSİK KOLON: {spName}", ConsoleColor.Red);
            if (hasWarning)
                Write($"      ⚡ DİNAMİK SQL : {spName}", ConsoleColor.DarkYellow);
        }
    }

    // ---- Şema tamamlandı ----
    public static void SchemaComplete(string schema, int spCount, int missingCount)
    {
        Console.WriteLine();
        var color = missingCount > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        Write($"    ✔ {schema} tamamlandı — {spCount} SP, {missingCount} eksik", color);
    }

    // ---- DB tamamlandı ----
    public static void DatabaseComplete(string db, int spCount, int missingCount)
    {
        var color = missingCount > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        Write($"  ✔ {db} tamamlandı — Toplam {spCount} SP, {missingCount} eksik", color);
    }

    // ---- Genel özet ----
    public static void Summary(int dbCount, int spCount, int missingCount, string outputPath)
    {
        Console.WriteLine();
        Write(new string('═', 60), ConsoleColor.White);
        Write($"  Taranan DB  : {dbCount}", ConsoleColor.White);
        Write($"  Taranan SP  : {spCount}", ConsoleColor.White);
        Write($"  Eksik kolon : {missingCount}", missingCount > 0 ? ConsoleColor.Red : ConsoleColor.Green);
        Write($"  Rapor       : {outputPath}", ConsoleColor.Cyan);
        Write(new string('═', 60), ConsoleColor.White);
    }

    // ---- Hata ----
    public static void Error(string message)
        => Write($"  [HATA] {message}", ConsoleColor.DarkRed);

    // ---- Uyarı ----
    public static void Warning(string message)
        => Write($"  [UYARI] {message}", ConsoleColor.DarkYellow);

    // ---- Yardımcı ----
    private static void Write(string message, ConsoleColor color)
    {
        lock (_lock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = prev;
        }
    }
}