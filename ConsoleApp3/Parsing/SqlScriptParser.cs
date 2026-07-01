using System.Text.RegularExpressions;

namespace ConsoleApp3.Parsing;

/// <summary>
/// SQL script dosyasını okuyarak her SP'yi ayrıştırır.
/// Yorum satırlarını temizler, string literalleri maskeler.
/// </summary>
public static class SqlScriptParser
{
    // SP başlangıcını tanıyan pattern (CREATE PROCEDURE veya CREATE OR ALTER PROCEDURE)
    private const string SpStartPattern =
        @"CREATE\s+(?:OR\s+ALTER\s+)?PROCEDURE\s+(?:[\w\[\].]+)";

    // GO bloğu ayırıcısı
    private const string GoSeparatorPattern =
        @"^\s*GO\s*$";

    private static readonly Regex SpStartRegex =
        new(SpStartPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GoRegex =
        new(GoSeparatorPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SpNameRegex =
        new(@"CREATE\s+(?:OR\s+ALTER\s+)?PROCEDURE\s+([\w\[\].]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Dosyayı okuyup SP adı → SP body sözlüğü döner.
    /// </summary>
    public static Dictionary<string, string> ParseStoredProcedures(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return ParseStoredProceduresFromText(content);
    }

    /// <summary>
    /// Metin içeriğinden SP adı → SP body sözlüğü döner (testler için).
    /// </summary>
    public static Dictionary<string, string> ParseStoredProceduresFromText(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // GO ile blokları ayır
        var blocks = GoRegex.Split(content);

        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (!SpStartRegex.IsMatch(trimmed)) continue;

            var nameMatch = SpNameRegex.Match(trimmed);
            if (!nameMatch.Success) continue;

            var spName = NormalizeObjectName(nameMatch.Groups[1].Value);
            result[spName] = trimmed;
        }

        return result;
    }

    /// <summary>
    /// Obje adından köşeli parantezleri kaldırır, son parçayı (base adı) döner.
    /// Örn: [MyDB].[dbo].[MyTable] → MyTable
    /// </summary>
    public static string NormalizeObjectName(string name)
    {
        // Köşeli parantezleri kaldır
        var clean = name.Replace("[", "").Replace("]", "");
        // Nokta ile ayrılmışsa son parçayı al
        var parts = clean.Split('.');
        return parts[^1].Trim();
    }

    /// <summary>
    /// SQL metnindeki -- ve /* */ yorumlarını boşlukla değiştirir.
    /// Satır numaraları korunur (--yorumlar newline ile değiştirilmez).
    /// </summary>
    public static string StripComments(string sql)
    {
        // Önce /* */ çok satırlı yorumları temizle (newline'ları koru)
        var result = Regex.Replace(sql, @"/\*.*?\*/",
            m => new string('\n', m.Value.Count(c => c == '\n')),
            RegexOptions.Singleline);

        // Sonra -- tek satır yorumları temizle
        result = Regex.Replace(result, @"--[^\r\n]*", "");

        return result;
    }

    /// <summary>
    /// SQL metnindeki string literalleri (tek tırnak içi) placeholder ile maskeler.
    /// Dinamik SQL tespiti için orijinal değer ayrıca döndürülür.
    /// </summary>
    public static (string maskedSql, bool hasDynamicSql) MaskStringLiterals(string sql)
    {
        var hasDynamic = false;

        // EXEC sp_executesql veya EXEC(@var) kontrolü (yorum temizlenmişte)
        if (Regex.IsMatch(sql, @"\bEXEC\b.*?sp_executesql\b|\bEXEC\s*\(@", RegexOptions.IgnoreCase))
            hasDynamic = true;

        // Tek tırnak içindeki içerikleri __STR__ ile değiştir
        var masked = Regex.Replace(sql, @"'(?:''|[^'])*'", "__STR__");
        return (masked, hasDynamic);
    }
}