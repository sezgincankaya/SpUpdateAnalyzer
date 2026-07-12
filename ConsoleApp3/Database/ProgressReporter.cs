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

    // ---- Başlangıç özeti: DB listesi ve SP sayıları ----
    public static void Overview(IReadOnlyList<(string Database, int SpCount)> databases)
    {
        lock (_lock)
        {
            Write("\n  Taranacak veritabanları:", ConsoleColor.White);
            Write($"  {new string('─', 44)}", ConsoleColor.DarkGray);
            foreach (var (db, spCount) in databases)
                Write($"    {db,-32} {spCount,8} SP", ConsoleColor.Gray);
            Write($"  {new string('─', 44)}", ConsoleColor.DarkGray);
            Write($"    {"TOPLAM",-32} {databases.Sum(d => d.SpCount),8} SP", ConsoleColor.White);
            Console.WriteLine();
        }
    }

    // ---- DB seviyesi ----
    public static void Database(string dbName, int current, int total)
        => Write($"\n[{current}/{total}] ▶ Database: {dbName}", ConsoleColor.Cyan);

    // ---- İlerleme çubuğu ----
    private static long _lastProgressTicks;

    public static void Progress(int current, int total)
    {
        if (total <= 0) return;

        // En fazla ~100 ms'de bir güncelle (son adım hariç)
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastProgressTicks);
        if (current != total && now - last < 100)
            return;
        if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last)
            return; // başka thread zaten yazdı

        lock (_lock)
        {
            var percent = (double)current / total;
            const int barWidth = 40;
            var filled = (int)(percent * barWidth);
            var bar = new string('█', filled) + new string('░', barWidth - filled);
            var text = $"  [{bar}] {percent,6:P1} ({current}/{total})";

            Console.CursorLeft = 0;
            var maxWidth = Console.WindowWidth - 1;
            if (text.Length > maxWidth)
                text = text[..maxWidth];

            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(text.PadRight(maxWidth));
            Console.ForegroundColor = prev;

            if (current == total)
                Console.WriteLine();
        }
    }

    // ---- SP sonucu ----
    public static void SpResult(string spName, bool hasMissing, bool hasWarning)
    {
        if (!hasMissing && !hasWarning) return;

        lock (_lock)
        {
            // Progress bar satırını temizle
            Console.CursorLeft = 0;
            Console.Write(new string(' ', Console.WindowWidth - 1));
            Console.CursorLeft = 0;
            if (hasMissing)
                Write($"      ⚠  EKSİK KOLON: {spName}", ConsoleColor.Red);
            if (hasWarning)
                Write($"      ⚡ DİNAMİK SQL : {spName}", ConsoleColor.DarkYellow);
        }
    }

    // ---- DB tamamlandı ----
    public static void DatabaseComplete(string db, int spCount, int missingCount, TimeSpan? elapsed = null)
    {
        var color = missingCount > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        var duration = elapsed is { } e ? $" ({FormatDuration(e)})" : string.Empty;
        Write($"  ✔ {db} tamamlandı — Toplam {spCount} SP, {missingCount} eksik{duration}", color);
    }

    // ---- Genel özet ----
    public static void Summary(int dbCount, int spCount, int missingCount, string outputPath, TimeSpan? elapsed = null)
    {
        Console.WriteLine();
        Write(new string('═', 60), ConsoleColor.White);
        Write($"  Taranan DB  : {dbCount}", ConsoleColor.White);
        Write($"  Taranan SP  : {spCount}", ConsoleColor.White);
        Write($"  Eksik kolon : {missingCount}", missingCount > 0 ? ConsoleColor.Red : ConsoleColor.Green);
        if (elapsed is { } e)
            Write($"  Toplam süre : {FormatDuration(e)}", ConsoleColor.White);
        Write($"  Rapor       : {outputPath}", ConsoleColor.Cyan);
        Write(new string('═', 60), ConsoleColor.White);
    }

    private static string FormatDuration(TimeSpan e)
        => e.TotalHours >= 1
            ? $"{(int)e.TotalHours}sa {e.Minutes}dk {e.Seconds}sn"
            : e.TotalMinutes >= 1
                ? $"{e.Minutes}dk {e.Seconds}sn"
                : $"{e.TotalSeconds:F1}sn";

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