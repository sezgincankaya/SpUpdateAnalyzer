using ClosedXML.Excel;
using ConsoleApp3.Models;

namespace ConsoleApp3.Reporting;

/// <summary>
/// Analiz sonuçlarını Excel (.xlsx) formatında raporlar.
/// Sekme 1: Eksik update kolonları detayı.
/// Sekme 2: SP bazlı özet.
/// Sekme 3: DB bazlı özet.
/// </summary>
public static class ExcelReporter
{
    public static void WriteReport(IEnumerable<SpAnalysisResult> results, string outputPath)
    {
        using var wb = new XLWorkbook();
        var resultList = results.ToList();

        WriteDetailSheet(wb, resultList);
        WriteSummarySheet(wb, resultList);
        WriteDatabaseSummarySheet(wb, resultList);

        wb.SaveAs(outputPath);
        Console.WriteLine($"\nRapor oluşturuldu: {outputPath}");
    }

    // -------------------------------------------------------------------------
    // Sekme 1: Detay
    // -------------------------------------------------------------------------
    private static void WriteDetailSheet(XLWorkbook wb, List<SpAnalysisResult> results)
    {
        var ws = wb.Worksheets.Add("MissingUpdateColumns");

        var headers = new[]
        {
            "Database",
            "Schema",
            "SP Adı",
            "Tablo Adı (Script'teki)",
            "Normalized Tablo Adı",
            "Eksik Kolon",
            "UPDATE İfadesi (ilk 120 karakter)",
            "Satır No",
            "Dinamik SQL Uyarısı"
        };

        WriteHeaders(ws, headers, "#2F5496");

        var row = 2;
        foreach (var spResult in results)
        {
            foreach (var upd in spResult.UpdateStatements)
            {
                if (upd.MissingUpdateColumns.Count == 0) continue;

                foreach (var missingCol in upd.MissingUpdateColumns)
                {
                    ws.Cell(row, 1).Value = spResult.DatabaseName;
                    ws.Cell(row, 2).Value = spResult.SchemaName;
                    ws.Cell(row, 3).Value = spResult.SpName;
                    ws.Cell(row, 4).Value = upd.RawTableReference;
                    ws.Cell(row, 5).Value = upd.NormalizedTableName;
                    ws.Cell(row, 6).Value = missingCol;
                    ws.Cell(row, 7).Value = upd.UpdateSnippet;
                    ws.Cell(row, 8).Value = upd.LineNumber;
                    ws.Cell(row, 9).Value = upd.HasDynamicSqlWarning ? "EVET" : "Hayır";

                    ws.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE4D6");
                    row++;
                }
            }
        }

        FinalizeSheet(ws, headers.Length, row);
    }

    // -------------------------------------------------------------------------
    // Sekme 2: SP bazlı özet
    // -------------------------------------------------------------------------
    private static void WriteSummarySheet(XLWorkbook wb, List<SpAnalysisResult> results)
    {
        var ws = wb.Worksheets.Add("SP Summary");

        var headers = new[]
        {
            "Database",
            "Schema",
            "SP Adı",
            "Toplam Hedef UPDATE Sayısı",
            "Eksik Kolon Sayısı",
            "Durum"
        };

        WriteHeaders(ws, headers, "#2F5496");

        var row = 2;
        foreach (var spResult in results)
        {
            ws.Cell(row, 1).Value = spResult.DatabaseName;
            ws.Cell(row, 2).Value = spResult.SchemaName;
            ws.Cell(row, 3).Value = spResult.SpName;
            ws.Cell(row, 4).Value = spResult.TotalTargetUpdateCount;
            ws.Cell(row, 5).Value = spResult.MissingColumnCount;

            var (status, color) = GetStatusAndColor(spResult);
            ws.Cell(row, 6).Value = status;
            ws.Row(row).Style.Fill.BackgroundColor = color;
            row++;
        }

        FinalizeSheet(ws, headers.Length, row);
    }

    // -------------------------------------------------------------------------
    // Sekme 3: DB bazlı özet
    // -------------------------------------------------------------------------
    private static void WriteDatabaseSummarySheet(XLWorkbook wb, List<SpAnalysisResult> results)
    {
        var ws = wb.Worksheets.Add("DB Summary");

        var headers = new[]
        {
            "Database",
            "Taranan SP Sayısı",
            "Hedef Tablo UPDATE İçeren SP",
            "Eksik Kolon Bulunan SP",
            "Toplam Eksik Kolon",
            "Dinamik SQL Uyarısı Olan SP",
            "Durum"
        };

        WriteHeaders(ws, headers, "#1F3864");

        var grouped = results
            .GroupBy(r => r.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);

        var row = 2;
        foreach (var dbGroup in grouped)
        {
            var spList = dbGroup.ToList();
            var totalSp = spList.Count;
            var withTargetUpdate = spList.Count(s => s.TotalTargetUpdateCount > 0);
            var withMissing = spList.Count(s => s.MissingColumnCount > 0);
            var totalMissing = spList.Sum(s => s.MissingColumnCount);
            var withDynamic = spList.Count(s => s.HasDynamicSqlWarning);

            ws.Cell(row, 1).Value = dbGroup.Key;
            ws.Cell(row, 2).Value = totalSp;
            ws.Cell(row, 3).Value = withTargetUpdate;
            ws.Cell(row, 4).Value = withMissing;
            ws.Cell(row, 5).Value = totalMissing;
            ws.Cell(row, 6).Value = withDynamic;

            string status;
            XLColor color;
            if (totalMissing > 0 && withDynamic > 0)
            { status = "EKSİK + DİNAMİK SQL"; color = XLColor.FromHtml("#FF0000"); }
            else if (totalMissing > 0)
            { status = "EKSİK KOLON VAR"; color = XLColor.FromHtml("#FCE4D6"); }
            else if (withDynamic > 0)
            { status = "DİNAMİK SQL UYARISI"; color = XLColor.FromHtml("#FFEB9C"); }
            else
            { status = "OK"; color = XLColor.FromHtml("#C6EFCE"); }

            ws.Cell(row, 7).Value = status;
            ws.Row(row).Style.Fill.BackgroundColor = color;
            row++;
        }

        FinalizeSheet(ws, headers.Length, row);
    }

    // -------------------------------------------------------------------------
    // Yardımcılar
    // -------------------------------------------------------------------------

    private static void WriteHeaders(IXLWorksheet ws, string[] headers, string colorHex)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(colorHex);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void FinalizeSheet(IXLWorksheet ws, int colCount, int lastRow)
    {
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        if (lastRow > 2)
            ws.Range(1, 1, lastRow - 1, colCount).SetAutoFilter();
    }

    private static (string status, XLColor color) GetStatusAndColor(SpAnalysisResult r)
    {
        if (r.MissingColumnCount > 0 && r.HasDynamicSqlWarning)
            return ("EKSİK KOLON + DİNAMİK SQL", XLColor.FromHtml("#FF0000"));
        if (r.MissingColumnCount > 0)
            return ("EKSİK KOLON", XLColor.FromHtml("#FCE4D6"));
        if (r.HasDynamicSqlWarning)
            return ("UYARI: Dinamik SQL", XLColor.FromHtml("#FFEB9C"));
        if (r.TotalTargetUpdateCount == 0)
            return ("Hedef Tablo UPDATE'i Yok", XLColor.FromHtml("#EDEDED"));
        return ("OK", XLColor.FromHtml("#C6EFCE"));
    }
}