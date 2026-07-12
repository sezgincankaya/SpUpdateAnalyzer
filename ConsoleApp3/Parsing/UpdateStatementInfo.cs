using ConsoleApp3.Models;
using System.Text.RegularExpressions;

namespace ConsoleApp3.Parsing;

/// <summary>
/// Bir SP body'si içindeki UPDATE ifadelerini analiz eder.
/// Hedef tablolara yönelik UPDATE'leri bulur, SET kolonlarını çıkarır,
/// "update" içeren kolonların set edilip edilmediğini kontrol eder.
/// </summary>
public class UpdateStatementAnalyzer
{
    // UPDATE ifadesinin başlangıcını yakalar: UPDATE [alias_or_table]
    private const string UpdateKeywordPattern =
        @"\bUPDATE\s+((?:\[[\w\s]+\]|[\w]+)(?:\.(?:\[[\w\s]+\]|[\w]+)){0,2})";

    // FROM bloğu içinde alias tanımını yakalar: tablo_adı alias veya tablo_adı AS alias
    private const string AliasPattern =
        @"(?:FROM|JOIN)\s+((?:\[[\w\s]+\]|[\w]+)(?:\.(?:\[[\w\s]+\]|[\w]+)){0,2})\s+(?:AS\s+)?([\w]+)";

    // SET bloğunu yakalar (UPDATE...SET...WHERE/FROM/; arasındaki kısım)
    private const string SetBlockPattern =
        @"\bSET\b(.*?)(?:\bWHERE\b|\bFROM\b|;|$)";

    // SET bloğundaki her bir atamayı yakalar: [alias.]kolon = ...
    private const string ColumnAssignPattern =
        @"(?:[\w\[\]]+\.)?([\[\w\]]+)\s*=";

    private static readonly Regex UpdateRegex =
        new(UpdateKeywordPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AliasRegex =
        new(AliasPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SetBlockRegex =
        new(SetBlockPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ColumnAssignRegex =
        new(ColumnAssignPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HashSet<string> _targetTables;
    private readonly bool _verbose;

    /// <summary>
    /// Hedef tablo listesi ve verbose modu ile başlatır.
    /// </summary>
    /// <param name="targetTables">İzlenecek tablo adları (normalize edilmiş, base adlar).</param>
    /// <param name="verbose">Konsola adım adım log yazar.</param>
    public UpdateStatementAnalyzer(IEnumerable<string> targetTables, bool verbose = false)
    {
        _targetTables = new HashSet<string>(
            targetTables.Select(SqlScriptParser.NormalizeObjectName),
            StringComparer.OrdinalIgnoreCase);
        _verbose = verbose;
    }

    /// <summary>
    /// SP body'sini analiz eder ve sonuçları döner.
    /// </summary>
    /// <param name="spName">SP adı (loglama için).</param>
    /// <param name="spBody">Ham SP CREATE script metni.</param>
    public SpAnalysisResult Analyze(string spName, string spBody)
    {
        var result = new SpAnalysisResult { SpName = spName };

        Log($"\n=== Analiz başlıyor: {spName} ===");

        // Hızlı ön-eleme: body'de hiç "UPDATE" ve dinamik SQL izi yoksa
        // pahalı regex/strip/mask adımlarına hiç girme.
        if (!spBody.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && !spBody.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase)
            && !spBody.Contains("EXEC", StringComparison.OrdinalIgnoreCase))
        {
            Log("  UPDATE/EXEC yok, hızlı atlama.");
            return result;
        }

        // 1. Yorumları temizle (satır numarası bilgisi için orijinali sakla)
        var originalLines = spBody.Split('\n');
        var stripped = SqlScriptParser.StripComments(spBody);

        // 2. String literalleri maskele
        var (masked, hasDynamic) = SqlScriptParser.MaskStringLiterals(stripped);

        if (hasDynamic)
        {
            result.Warnings.Add("Dinamik SQL tespit edildi (EXEC sp_executesql veya EXEC(@var)).");
            Log("  [UYARI] Dinamik SQL tespit edildi.");
        }

        // 3. Alias haritasını çıkar: alias → normalized tablo adı
        var aliasMap = BuildAliasMap(masked);
        Log($"  Alias haritası: {string.Join(", ", aliasMap.Select(kv => $"{kv.Key}→{kv.Value}"))}");

        // 4. UPDATE ifadelerini bul
        var updateMatches = UpdateRegex.Matches(masked);
        Log($"  Bulunan UPDATE sayısı: {updateMatches.Count}");

        foreach (Match updateMatch in updateMatches)
        {
            var rawRef = updateMatch.Groups[1].Value.Trim();
            var normalizedRef = SqlScriptParser.NormalizeObjectName(rawRef);

            // Alias ise gerçek tablo adını çöz
            string resolvedTable;
            if (aliasMap.TryGetValue(normalizedRef, out var aliasResolved))
            {
                resolvedTable = aliasResolved;
                Log($"  Alias çözümlendi: {normalizedRef} → {resolvedTable}");
            }
            else
            {
                resolvedTable = normalizedRef;
            }

            // Hedef tablo mu?
            if (!_targetTables.Contains(resolvedTable))
            {
                Log($"  Hedef dışı tablo, atlandı: {resolvedTable}");
                continue;
            }

            Log($"  Hedef tablo bulundu: {resolvedTable} (raw: {rawRef})");

            // Satır numarasını bul
            var lineNumber = FindLineNumber(originalLines, updateMatch.Index, spBody);

            // UPDATE ifadesinin bulunduğu pozisyondan itibaren SET bloğunu çıkar
            var fromUpdatePos = masked.Substring(updateMatch.Index);
            var setColumns = ExtractSetColumns(fromUpdatePos, out var setBlock);

            Log($"  SET kolonları: {string.Join(", ", setColumns)}");

            // "update" içeren kolonları kontrol et
            var missingCols = FindMissingUpdateColumns(setColumns, spName, resolvedTable);

            var info = new UpdateStatementInfo
            {
                RawTableReference = rawRef,
                NormalizedTableName = resolvedTable,
                SetColumns = setColumns,
                MissingUpdateColumns = missingCols,
                LineNumber = lineNumber,
                UpdateSnippet = BuildSnippet(spBody, updateMatch.Index),
                HasDynamicSqlWarning = hasDynamic
            };

            result.UpdateStatements.Add(info);
        }

        Log($"  Eksik kolon toplamı: {result.MissingColumnCount}");
        return result;
    }

    // -------------------------------------------------------------------------
    // Yardımcı metodlar
    // -------------------------------------------------------------------------

    /// <summary>
    /// FROM ve JOIN ifadelerinden alias → tablo adı haritası oluşturur.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = AliasRegex.Matches(sql);
        foreach (Match m in matches)
        {
            var tableName = SqlScriptParser.NormalizeObjectName(m.Groups[1].Value.Trim());
            var alias = m.Groups[2].Value.Trim();
            // alias SQL anahtar kelimesi değilse ekle
            if (!IsSqlKeyword(alias))
                map[alias] = tableName;
        }
        return map;
    }

    /// <summary>
    /// UPDATE ifadesinin hemen ardındaki SET bloğunu bulur, kolon adlarını çıkarır.
    /// </summary>
    private static List<string> ExtractSetColumns(string sqlFromUpdate, out string setBlock)
    {
        setBlock = string.Empty;
        var setMatch = SetBlockRegex.Match(sqlFromUpdate);
        if (!setMatch.Success)
            return new List<string>();

        setBlock = setMatch.Groups[1].Value;
        var columns = new List<string>();

        var assignMatches = ColumnAssignRegex.Matches(setBlock);
        foreach (Match m in assignMatches)
        {
            var col = m.Groups[1].Value
                .Replace("[", "")
                .Replace("]", "")
                .Trim();
            if (!string.IsNullOrWhiteSpace(col) && !IsSqlKeyword(col))
                columns.Add(col);
        }
        return columns;
    }

    /// <summary>
    /// Adında "update" geçen kolonlardan SET edilmeyenleri döner.
    /// </summary>
    private List<string> FindMissingUpdateColumns(List<string> setColumns, string spName, string tableName)
    {
        // Adında "update" geçen SET kolonları
        var updateColumnsInSet = setColumns
            .Where(c => c.Contains("update", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Log($"    SET'te 'update' içeren kolonlar: {string.Join(", ", updateColumnsInSet)}");

        // NOT: Bu metot şu an SET'teki update-içeren kolonları bulur.
        // Tablonun metadata'sı olmadığından "set edilmesi gereken ama edilmeyen" kolonları
        // sadece SP'deki diğer UPDATE ifadelerinde geçen kolonlarla çapraz karşılaştırarak
        // ya da kullanıcı config'inden alarak tespit edebiliriz.
        // Mevcut yaklaşım: SET bloğunda "update" içeren kolon HİÇ YOK ise eksik say.
        if (updateColumnsInSet.Count == 0)
        {
            Log($"    [EKSİK] SET bloğunda 'update' içeren kolon yok.");
            return new List<string> { "(update içeren kolon set edilmemiş)" };
        }

        return new List<string>(); // Tümü set edilmiş
    }

    /// <summary>
    /// Orijinal SQL'de bir karakterin bulunduğu yaklaşık satır numarasını döner.
    /// </summary>
    private static int FindLineNumber(string[] originalLines, int charIndex, string originalSql)
    {
        var count = 0;
        var line = 1;
        foreach (var l in originalLines)
        {
            count += l.Length + 1; // +1 for \n
            if (count >= charIndex) break;
            line++;
        }
        return line;
    }

    /// <summary>
    /// Belirtilen pozisyondan itibaren ilk 120 karakterlik snippet döner.
    /// </summary>
    private static string BuildSnippet(string sql, int index)
    {
        var available = sql.Length - index;
        var length = Math.Min(120, available);
        return sql.Substring(index, length).Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>
    /// Verilen kelimenin bilinen bir SQL anahtar kelimesi olup olmadığını kontrol eder.
    /// </summary>
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","SET","ON","AS","AND","OR","NOT","IN","IS",
        "NULL","JOIN","LEFT","RIGHT","INNER","OUTER","FULL","CROSS","HAVING",
        "GROUP","ORDER","BY","INTO","VALUES","INSERT","UPDATE","DELETE","MERGE",
        "WITH","TOP","DISTINCT","CASE","WHEN","THEN","ELSE","END","EXEC",
        "EXECUTE","BEGIN","TRANSACTION","COMMIT","ROLLBACK","GO","DECLARE",
        "PROCEDURE","PROC","FUNCTION","TRIGGER","VIEW","TABLE","INDEX","IF",
        "EXISTS","BETWEEN","LIKE"
    };

    private static bool IsSqlKeyword(string word) => SqlKeywords.Contains(word);

    private void Log(string message)
    {
        if (_verbose) Console.WriteLine(message);
    }
}