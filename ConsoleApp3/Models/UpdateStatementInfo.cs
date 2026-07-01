namespace ConsoleApp3.Models;

/// <summary>
/// Bir UPDATE ifadesine ait parse edilmiş bilgileri tutar.
/// </summary>
public class UpdateStatementInfo
{
    /// <summary>Script'teki orijinal tablo adı (alias çözümlenmeden önce).</summary>
    public string RawTableReference { get; set; } = string.Empty;

    /// <summary>Köşeli parantez ve DB/schema prefix'i temizlenmiş base tablo adı.</summary>
    public string NormalizedTableName { get; set; } = string.Empty;

    /// <summary>SET bloğundaki tüm kolon adları.</summary>
    public List<string> SetColumns { get; set; } = new();

    /// <summary>SET edilmemiş, adında "update" geçen hedef kolonlar.</summary>
    public List<string> MissingUpdateColumns { get; set; } = new();

    /// <summary>UPDATE ifadesinin SP içindeki satır numarası.</summary>
    public int LineNumber { get; set; }

    /// <summary>UPDATE ifadesinin ilk 120 karakteri (raporlama için).</summary>
    public string UpdateSnippet { get; set; } = string.Empty;

    /// <summary>Dinamik SQL uyarısı içerip içermediği.</summary>
    public bool HasDynamicSqlWarning { get; set; }
}