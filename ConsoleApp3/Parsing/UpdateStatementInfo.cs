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
    // Tek bir ad parçası: [köşeli], "çift tırnaklı" veya düz ad (temp/değişken önekleriyle)
    private const string NamePartPattern =
        @"(?:\[[\w\s#@]+\]|""[\w\s#@]+""|(?:##?|@)?[\w]+)";

    // Parça ayırıcı: nokta(lar), etrafında boşluk olabilir (BOA . COR . Account geçerlidir)
    private const string SeparatorPattern = @"\s*\.(?:\s*\.)?\s*";

    // UPDATE ifadesinin başlangıcını yakalar: UPDATE [TOP (n)] [alias_or_table]
    // Temp tablolar (#tmp, ##global), tablo değişkenleri (@var),
    // tempdb..#tmp çift-nokta gösterimleri ve 4 parçalı (linked server) adlar da yakalanır.
    private const string UpdateKeywordPattern =
        @"\bUPDATE\s+(?:TOP\s*\(\s*\d+\s*\)\s*(?:PERCENT\s+)?)?(" + NamePartPattern + @"(?:" + SeparatorPattern + NamePartPattern + @"){0,3})";

    // FROM bloğu içinde alias tanımını yakalar: tablo_adı alias veya tablo_adı AS alias
    private const string AliasPattern =
        @"(?:FROM|JOIN)\s+(" + NamePartPattern + @"(?:" + SeparatorPattern + NamePartPattern + @"){0,3})\s+(?:AS\s+)?([\w]+)";

    // NOT: SET bloğu artık regex ile değil, parantez derinliği takip eden
    // ExtractSetColumns tarayıcısıyla çıkarılıyor (subquery içindeki FROM/WHERE
    // bloğu erken kesmesin diye).

    private static readonly Regex UpdateRegex =
        new(UpdateKeywordPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AliasRegex =
        new(AliasPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    // Hedef tablolar: (görünen tam ad, normalize edilmiş parçalar [db, schema, tablo])
    private readonly List<(string DisplayName, string[] Parts)> _targetTables;
    private readonly bool _verbose;

    /// <summary>
    /// Hedef tablo listesi ve verbose modu ile başlatır.
    /// </summary>
    /// <param name="targetTables">İzlenecek tablo adları (tam nitelikli olabilir: DB.Schema.Tablo).</param>
    /// <param name="verbose">Konsola adım adım log yazar.</param>
    public UpdateStatementAnalyzer(IEnumerable<string> targetTables, bool verbose = false)
    {
        _targetTables = targetTables
            .Select(t => (t, SplitAndNormalizeParts(t)))
            .Where(t => t.Item2.Length > 0)
            .ToList();
        _verbose = verbose;
    }

    /// <summary>
    /// SP body'sini analiz eder ve sonuçları döner.
    /// </summary>
    /// <param name="spName">SP adı (loglama için).</param>
    /// <param name="spBody">Ham SP CREATE script metni.</param>
    /// <param name="currentDatabase">
    /// SP'nin bulunduğu veritabanı (opsiyonel). Verilirse, DB niteliği olmayan referanslar
    /// (örn. "COR.Account") bu DB ile nitelenir ve hedeflerdeki DB parçasıyla karşılaştırılır.
    /// Böylece BOA dışı bir DB'deki SP'de geçen "COR.Account", "BOA.COR.Account" hedefiyle eşleşmez.
    /// </param>
    public SpAnalysisResult Analyze(string spName, string spBody, string? currentDatabase = null)
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

        // 3. UPDATE ifadelerini bul
        var updateMatches = UpdateRegex.Matches(masked);
        Log($"  Bulunan UPDATE sayısı: {updateMatches.Count}");

        for (var i = 0; i < updateMatches.Count; i++)
        {
            var updateMatch = updateMatches[i];
            var rawRef = updateMatch.Groups[1].Value.Trim();

            // Temp tablo veya tablo değişkenine yapılan UPDATE'ler dahil edilmez.
            if (IsTempOrVariableReference(rawRef))
            {
                Log($"  Temp tablo / tablo değişkeni UPDATE'i atlandı: {rawRef}");
                continue;
            }

            var normalizedRef = SqlScriptParser.NormalizeObjectName(rawRef);

            // MERGE ... WHEN MATCHED THEN UPDATE SET → "SET" tablo adı sanılmasın.
            // UPDATE STATISTICS tablo → gerçek bir veri UPDATE'i değildir.
            if (IsSqlKeyword(normalizedRef) || normalizedRef.Equals("STATISTICS", StringComparison.OrdinalIgnoreCase))
            {
                Log($"  SQL anahtar kelimesi / UPDATE STATISTICS, atlandı: {rawRef}");
                continue;
            }

            // Statement sınırlarını belirle: bu UPDATE'ten bir sonraki UPDATE'e (veya metin sonuna) kadar.
            var statementEnd = i + 1 < updateMatches.Count ? updateMatches[i + 1].Index : masked.Length;
            var statementText = masked.Substring(updateMatch.Index, statementEnd - updateMatch.Index);

            // Alias ise SADECE bu statement'ın FROM/JOIN bloğundan gerçek tablo adını çöz.
            // (Global alias haritası farklı statement'lardaki aynı adlı alias'ları karıştırabiliyordu.)
            var aliasMap = BuildAliasMap(statementText);
            string resolvedTable;
            string rawResolvedRef = rawRef;
            if (aliasMap.TryGetValue(normalizedRef, out var aliasResolved))
            {
                rawResolvedRef = aliasResolved;
                resolvedTable = SqlScriptParser.NormalizeObjectName(aliasResolved);
                Log($"  Alias çözümlendi: {normalizedRef} → {resolvedTable}");

                // Alias temp tabloya çözümleniyorsa dahil etme.
                if (IsTempOrVariableReference(aliasResolved))
                {
                    Log($"  Alias temp tabloya çözümlendi, atlandı: {normalizedRef} → {aliasResolved}");
                    continue;
                }
            }
            else
            {
                resolvedTable = normalizedRef;
            }

            // Hedef tablo mu? (schema/db duyarlı, sağdan hizalı parça karşılaştırması + DB bağlamı)
            var matchedTarget = MatchTarget(rawResolvedRef, currentDatabase);
            if (matchedTarget is null)
            {
                Log($"  Hedef dışı tablo, atlandı: {rawResolvedRef}");
                continue;
            }

            Log($"  Hedef tablo bulundu: {matchedTarget} (raw: {rawRef})");

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
                NormalizedTableName = matchedTarget,
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
    /// FROM ve JOIN ifadelerinden alias → ham tablo referansı haritası oluşturur.
    /// (İlk eşleşme kazanır: statement-local kullanımda UPDATE'in kendi FROM'u önceliklidir.)
    /// </summary>
    private static Dictionary<string, string> BuildAliasMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = AliasRegex.Matches(sql);
        foreach (Match m in matches)
        {
            var tableRef = m.Groups[1].Value.Trim();
            var alias = m.Groups[2].Value.Trim();
            // alias SQL anahtar kelimesi değilse ve daha önce eklenmemişse ekle
            if (!IsSqlKeyword(alias) && !map.ContainsKey(alias))
                map[alias] = tableRef;
        }
        return map;
    }

    /// <summary>
    /// Referansın temp tablo (#tmp, ##global) veya tablo değişkeni (@var) olup olmadığını kontrol eder.
    /// </summary>
    private static bool IsTempOrVariableReference(string reference)
    {
        var clean = reference.Replace("[", "").Replace("]", "").Trim();
        // Çok parçalı adlarda son parçaya bak (örn. tempdb..#tmp)
        var last = clean.Split('.')[^1].Trim();
        return last.StartsWith("#") || last.StartsWith("@");
    }

    /// <summary>
    /// Obje adını normalize edilmiş parçalara ayırır:
    /// köşeli parantez ve çift tırnaklar kaldırılır, boşluklar kırpılır, nokta ile bölünür.
    /// "db..tablo" gösteriminde ortadaki boş parça (default schema) korunur (joker sayılır).
    /// </summary>
    private static string[] SplitAndNormalizeParts(string name)
    {
        return name
            .Split('.')
            .Select(p => p.Replace("[", "").Replace("]", "").Replace("\"", "").Trim())
            .ToArray();
    }

    /// <summary>
    /// Ham tablo referansını hedef listesiyle sağdan hizalı parça karşılaştırmasıyla eşler.
    /// Referans hangi parçaları belirtiyorsa (schema, db) onlar da eşleşmek zorundadır.
    /// Örn: "BOADWH.LGD.Account" → hedef "BOA.COR.Account" ile EŞLEŞMEZ (LGD ≠ COR).
    ///      "COR.Account" veya "Account" → "BOA.COR.Account" ile eşleşir
    ///      (ancak currentDatabase verilmiş ve BOA değilse EŞLEŞMEZ).
    /// 4 parçalı (LinkedServer.DB.Schema.Table) referanslarda server parçası yok sayılır.
    /// Boş parça (db..tablo) joker kabul edilir.
    /// Eşleşen hedefin tam adını, eşleşme yoksa null döner.
    /// </summary>
    private string? MatchTarget(string rawReference, string? currentDatabase = null)
    {
        var refParts = SplitAndNormalizeParts(rawReference);
        if (refParts.Length == 0) return null;

        // 4 parçalı ad: linked server parçasını at, karşılaştırma DB.Schema.Table üzerinden yapılır.
        if (refParts.Length == 4)
            refParts = refParts[1..];

        // DB bağlamı: referans DB belirtmiyorsa (en fazla schema.tablo) SP'nin DB'si ile nitele.
        // Böylece hedefteki DB parçası da karşılaştırmaya girer.
        if (!string.IsNullOrWhiteSpace(currentDatabase) && refParts.Length <= 2)
        {
            refParts = refParts.Length == 1
                ? new[] { currentDatabase.Trim(), "", refParts[0] }   // schema bilinmiyor → joker
                : new[] { currentDatabase.Trim(), refParts[0], refParts[1] };
        }

        foreach (var (displayName, targetParts) in _targetTables)
        {
            if (MatchesRightAligned(refParts, targetParts))
                return displayName;
        }
        return null;
    }

    /// <summary>
    /// İki parça dizisini sağdan hizalayarak karşılaştırır.
    /// Karşılaştırılan ortak parça sayısı kadar tüm parçalar eşleşmelidir;
    /// boş parça joker kabul edilir. En az tablo adı (son parça) dolu olmalıdır.
    /// </summary>
    private static bool MatchesRightAligned(string[] refParts, string[] targetParts)
    {
        var common = Math.Min(refParts.Length, targetParts.Length);
        if (common == 0) return false;

        for (var i = 1; i <= common; i++)
        {
            var rp = refParts[^i];
            var tp = targetParts[^i];

            // Tablo adı (son parça) joker olamaz, kesin eşleşmeli.
            if (i == 1)
            {
                if (!rp.Equals(tp, StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }

            // Boş parça (db..tablo veya tanımsız schema) joker sayılır.
            if (rp.Length == 0 || tp.Length == 0)
                continue;

            if (!rp.Equals(tp, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// UPDATE ifadesinin hemen ardındaki SET bloğunu bulur, kolon adlarını çıkarır.
    /// Parantez derinliği takip edilir: SET bloğu yalnızca derinlik 0'daki
    /// FROM / WHERE / ; ile sonlanır. Böylece SET içindeki subquery'lerin
    /// FROM/WHERE'leri bloğu erken kesmez ve subquery içindeki '=' karşılaştırmaları
    /// kolon ataması sanılmaz.
    /// </summary>
    private static List<string> ExtractSetColumns(string sqlFromUpdate, out string setBlock)
    {
        setBlock = string.Empty;

        // 1. Derinlik 0'daki ilk SET anahtar kelimesini bul
        var setStart = FindKeywordAtDepthZero(sqlFromUpdate, "SET", 0);
        if (setStart < 0)
            return new List<string>();

        var bodyStart = setStart + 3; // "SET" uzunluğu

        // 2. SET bloğunun sonunu bul: derinlik 0'da FROM/WHERE/; veya metin sonu
        var depth = 0;
        var end = sqlFromUpdate.Length;
        for (var i = bodyStart; i < sqlFromUpdate.Length; i++)
        {
            var ch = sqlFromUpdate[i];
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }

            if (depth > 0) continue;

            if (ch == ';') { end = i; break; }

            if ((ch == 'F' || ch == 'f' || ch == 'W' || ch == 'w')
                && IsWordBoundary(sqlFromUpdate, i)
                && (MatchesWord(sqlFromUpdate, i, "FROM") || MatchesWord(sqlFromUpdate, i, "WHERE")))
            {
                end = i;
                break;
            }
        }

        setBlock = sqlFromUpdate[bodyStart..end];

        // 3. Derinlik 0'daki virgüllerden atamalara böl,
        //    her atamanın '=' öncesindeki son tanımlayıcıyı kolon adı olarak al
        var columns = new List<string>();
        foreach (var assignment in SplitTopLevel(setBlock, ','))
        {
            var eqIndex = FindCharAtDepthZero(assignment, '=');
            if (eqIndex <= 0) continue;

            var lhs = assignment[..eqIndex].Trim();
            // alias.kolon → son parça; köşeli parantez/tırnak temizle
            var col = lhs.Split('.')[^1]
                .Replace("[", "").Replace("]", "").Replace("\"", "").Trim();

            if (!string.IsNullOrWhiteSpace(col) && !IsSqlKeyword(col)
                && col.All(c => char.IsLetterOrDigit(c) || c == '_'))
                columns.Add(col);
        }
        return columns;
    }

    /// <summary>Derinlik 0'da, kelime sınırlarıyla eşleşen ilk anahtar kelimenin index'ini döner.</summary>
    private static int FindKeywordAtDepthZero(string text, string keyword, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i <= text.Length - keyword.Length; i++)
        {
            var ch = text[i];
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth > 0) continue;

            if (IsWordBoundary(text, i) && MatchesWord(text, i, keyword))
                return i;
        }
        return -1;
    }

    /// <summary>Derinlik 0'daki ilk verilen karakterin index'ini döner, yoksa -1.</summary>
    private static int FindCharAtDepthZero(string text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && ch == target) return i;
        }
        return -1;
    }

    /// <summary>Metni derinlik 0'daki ayırıcı karakterden parçalara böler.</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && ch == separator)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>Pozisyonun bir kelimenin başlangıcı olup olmadığını kontrol eder.</summary>
    private static bool IsWordBoundary(string text, int index)
        => index == 0 || !(char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');

    /// <summary>Pozisyondan itibaren verilen kelimenin (case-insensitive, tam kelime) geçip geçmediğini kontrol eder.</summary>
    private static bool MatchesWord(string text, int index, string word)
    {
        if (index + word.Length > text.Length) return false;
        if (string.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = index + word.Length;
        return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
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