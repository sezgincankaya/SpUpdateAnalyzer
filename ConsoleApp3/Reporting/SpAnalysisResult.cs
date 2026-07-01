using ClosedXML.Excel;
using ConsoleApp3.Models;

namespace ConsoleApp3.Reporting;

/// <summary>
/// Analiz sonuçlarını Excel (.xlsx) formatında raporlar.
/// Sekme 1: Eksik update kolonları detayı.
/// Sekme 2: SP bazlı özet.
/// </summary>
public static class ExcelReporter
{
    /// <summary>
    /// Sonuçları belirtilen dosya yoluna Excel olarak yazar.
    /// </summary>
    public static void WriteReport(IEnumerable<SpAnalysisResult> results, string outputPath)
    {
        using var wb = new XLWorkbook();
        var resultList = results.ToList();

        WriteDetailSheet(wb, resultList);
        WriteSummarySheet(wb, resultList);

        wb.SaveAs(outputPath);
        Console.WriteLine($"\nRapor oluşturuldu: {outputPath}");
    }

    private static void WriteDetailSheet(XLWorkbook wb, List<SpAnalysisResult> results)
    {
        var ws = wb.Worksheets.Add("MissingUpdateColumns");

        // Başlıklar
        var headers = new[]
        {
            "SP Adı",
            "Tablo Adı (Script'teki)",
            "Normalized Tablo Adı",
            "Eksik Kolon",
            "UPDATE İfadesi (ilk 120 karakter)",
            "Satır No",
            "Dinamik SQL Uyarısı"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2F5496");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = 2;
        foreach (var spResult in results)
        {
            foreach (var upd in spResult.UpdateStatements)
            {
                if (upd.MissingUpdateColumns.Count == 0) continue;

                foreach (var missingCol in upd.MissingUpdateColumns)
                {
                    ws.Cell(row, 1).Value = spResult.SpName;
                    ws.Cell(row, 2).Value = upd.RawTableReference;
                    ws.Cell(row, 3).Value = upd.NormalizedTableName;
                    ws.Cell(row, 4).Value = missingCol;
                    ws.Cell(row, 5).Value = upd.UpdateSnippet;
                    ws.Cell(row, 6).Value = upd.LineNumber;
                    ws.Cell(row, 7).Value = upd.HasDynamicSqlWarning ? "EVET" : "Hayır";

                    // Eksik satırları kırmızıyla vurgula
                    ws.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE4D6");
                    row++;
                }
            }
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        // Filtre ekle
        if (row > 2)
            ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
    }

    private static void WriteSummarySheet(XLWorkbook wb, List<SpAnalysisResult> results)
    {
        var ws = wb.Worksheets.Add("Summary");

        var headers = new[]
        {
            "SP Adı",
            "Toplam Hedef Tablo UPDATE Sayısı",
            "Eksik Kolon Sayısı",
            "Durum"
        };

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2F5496");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = 2;
        foreach (var spResult in results)
        {
            ws.Cell(row, 1).Value = spResult.SpName;
            ws.Cell(row, 2).Value = spResult.TotalTargetUpdateCount;
            ws.Cell(row, 3).Value = spResult.MissingColumnCount;

            string status;
            XLColor rowColor;

            if (spResult.HasDynamicSqlWarning && spResult.MissingColumnCount > 0)
            {
                status = "EKSİK KOLON + DİNAMİK SQL UYARISI";
                rowColor = XLColor.FromHtml("#FF0000");
            }
            else if (spResult.HasDynamicSqlWarning)
            {
                status = "UYARI: Dinamik SQL";
                rowColor = XLColor.FromHtml("#FFEB9C");
            }
            else if (spResult.MissingColumnCount > 0)
            {
                status = "EKSİK KOLON";
                rowColor = XLColor.FromHtml("#FCE4D6");
            }
            else if (spResult.TotalTargetUpdateCount == 0)
            {
                status = "Hedef Tablo UPDATE'i Yok";
                rowColor = XLColor.FromHtml("#EDEDED");
            }
            else
            {
                status = "OK";
                rowColor = XLColor.FromHtml("#C6EFCE");
            }

            ws.Cell(row, 4).Value = status;
            ws.Row(row).Style.Fill.BackgroundColor = rowColor;
            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        if (row > 2)
            ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
    }
}