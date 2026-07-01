namespace ConsoleApp3.Models
{
    /// <summary>
    /// Tek bir Stored Procedure'ün analiz sonucunu tutar.
    /// </summary>
    public class SpAnalysisResult
    {
        /// <summary>Stored Procedure adı.</summary>
        public string SpName { get; set; } = string.Empty;

        /// <summary>SP içinde hedef tablolara yönelik bulunan UPDATE ifadeleri.</summary>
        public List<UpdateStatementInfo> UpdateStatements { get; set; } = new();

        /// <summary>Genel uyarı mesajları.</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Eksik kolon içeren UPDATE sayısı.</summary>
        public int MissingColumnCount => UpdateStatements.Sum(u => u.MissingUpdateColumns.Count);

        /// <summary>Dinamik SQL uyarısı var mı?</summary>
        public bool HasDynamicSqlWarning =>
            Warnings.Any(w => w.Contains("Dinamik SQL", StringComparison.OrdinalIgnoreCase)) ||
            UpdateStatements.Any(u => u.HasDynamicSqlWarning);

        /// <summary>Hedef tablolara yönelik toplam UPDATE sayısı.</summary>
        public int TotalTargetUpdateCount => UpdateStatements.Count;
    }
}
