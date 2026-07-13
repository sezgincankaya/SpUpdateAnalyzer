# ?? SP Update Kolon Analizörü (SpUpdateAnalyzer)

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 12](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![Tests](https://img.shields.io/badge/tests-50%2B-brightgreen)](ConsoleApp3.Tests/)
[![License](https://img.shields.io/badge/license-internal-lightgrey)](#)

SQL Server sunucusundaki **tüm veritabanlarýný ve stored procedure'leri tarayarak**, hedef tablolara yapýlan `UPDATE` ifadelerini tespit eden ve bu UPDATE'lerde adýnda **"update" geçen kolonlarýn** (örn. `UpdateSystemDate`, `UpdatedDate`, `LastUpdateTime`) SET edilip edilmediðini denetleyen bir analiz aracýdýr. Sonuçlar **Excel raporu** olarak üretilir.

> ???? English summary available [below](#-english-summary).

---

## ?? Ekran Görüntüleri

<!-- TODO: Ekran görüntülerini docs/images/ klasörüne ekleyip yollarý güncelleyin -->

| Konsol taramasý | Excel raporu |
|---|---|
| ![Konsol çýktýsý](docs/images/console-scan.png) | ![Excel raporu](docs/images/excel-report.png) |

---

## ?? Kullaným

```bash
dotnet run --project ConsoleApp3 -- ^
    --connection "Server=.;Integrated Security=true;TrustServerCertificate=true" ^
    --tables tables.json ^
    --output rapor.xlsx ^
    --threads 8 ^
    --verbose
```

### Argümanlar

| Argüman | Açýklama | Varsayýlan |
|---|---|---|
| `--connection` | SQL Server baðlantý string'i | `appsettings.json` ? `ConnectionStrings:Boa`, yoksa konsoldan sorulur |
| `--tables` | Hedef tablo listesi (JSON) | `tables.json` — yoksa DB'den otomatik üretilir |
| `--output` | Excel çýktý dosyasý | `result_yyyyMMdd_HHmmss.xlsx` |
| `--threads` | Paralel analiz thread sayýsý | `Ýþlemci sayýsý × 2` |
| `--verbose` | Adým adým analiz logu | Kapalý |

### `tables.json` formatý

```json
{
  "tables": [
    "BOA.COR.Account",
    "BOA.LNS.Project",
    "BOA.CUS.Customer"
  ]
}
```

> ?? Tablolar tam nitelikli (`DB.Schema.Tablo`), kýsmi (`Schema.Tablo`) veya sadece base ad (`Tablo`) olarak verilebilir. Ne kadar nitelikli verilirse eþleþtirme o kadar hassas olur.

---

## ??? Mimari Bileþenler

- ??? **`Program.cs`** — CLI giriþ noktasý: argüman parse, `appsettings.json` desteði, `tables.json` otomatik üretimi, Ctrl+C ile temiz iptal.
- ?? **`UpdateStatementAnalyzer`** — Çekirdek analiz motoru: UPDATE tespiti, statement-local alias çözümleme, parantez-derinlikli SET kolonu çýkarýmý, DB baðlamlý hedef eþleþtirme.
- ?? **`SqlScriptParser`** — Yorum temizleme, string literal maskeleme, obje adý normalizasyonu, dinamik SQL tespiti.
- ??? **`SqlServerReader` + `DatabaseScanner`** — Tüm kullanýcý DB'lerini listeler, SP definition'larýný **prefetch pipeline** ile indirir (I/O ? CPU örtüþür), paralel analiz eder.
- ? **`ParallelSpAnalyzer`** — SP'leri belirlenen thread sayýsýyla paralel iþler.
- ?? **`ProgressReporter`** — Renkli konsol çýktýsý: DB özeti, ilerleme çubuðu, süre ölçümü, eksik kolon / dinamik SQL uyarýlarý.
- ?? **`ExcelReporter`** — Bulgularý (eksik kolon, dinamik SQL uyarýsý, satýr numarasý, snippet) Excel'e yazar.

---

## ?? Analiz Akýþý

1. ? **Hýzlý ön-eleme** — body'de `UPDATE` / `EXEC` / `sp_executesql` yoksa pahalý adýmlara hiç girilmez.
2. ?? **Yorum temizleme** — `--` ve `/* */` yorumlarý, satýr numaralarý korunarak silinir.
3. ?? **String maskeleme** — tek týrnak içi literaller `__STR__` olur; dinamik SQL izi (`sp_executesql`, `EXEC(@var)`) uyarý üretir.
4. ?? **UPDATE tespiti** — regex ile tüm UPDATE ifadeleri bulunur (TOP, temp, linked server varyasyonlarý dahil).
5. ??? **Alias çözümleme** — yalnýzca ilgili statement'ýn FROM/JOIN bloðundan (statement-local).
6. ?? **Hedef eþleþtirme** — saðdan hizalý parça karþýlaþtýrmasý + SP'nin DB baðlamý.
7. ?? **SET kolonu çýkarýmý** — parantez derinliði takip eden parser (subquery'ler bloðu erken kesmez).
8. ?? **Denetim** — SET bloðunda adýnda "update" geçen kolon yoksa **eksik** olarak raporlanýr.

---

## ??? Yakalanan Edge Case'ler

| # | Kategori | Edge Case | Örnek | Davranýþ |
|---|---|---|---|---|
| 1 | ?? Yorum | Tek satýr yorumdaki UPDATE | `-- UPDATE Orders SET x=1` | ? Yoksayýlýr |
| 2 | ?? Yorum | Çok satýrlý yorumdaki UPDATE | `/* UPDATE Orders ... */` | ? Yoksayýlýr |
| 3 | ?? String | Literal içindeki UPDATE | `@msg = 'UPDATE Orders SET x=1'` | ? Yoksayýlýr |
| 4 | ? Dinamik SQL | `sp_executesql` / `EXEC(@var)` | `EXEC sp_executesql @sql` | ?? Uyarý üretilir |
| 5 | ??? Temp | Local temp tablo | `UPDATE #tmpRiskDetail SET ...` | ? Dahil edilmez |
| 6 | ??? Temp | Global temp tablo | `UPDATE ##globalTmp SET ...` | ? Dahil edilmez |
| 7 | ??? Temp | tempdb çift-nokta gösterimi | `UPDATE tempdb..#tmp` | ? Dahil edilmez |
| 8 | ??? Temp | Tablo deðiþkeni | `UPDATE @Orders SET ...` | ? Dahil edilmez |
| 9 | ??? Alias | Alias'lý UPDATE | `UPDATE o ... FROM dbo.Orders o` | ? Gerçek tabloya çözümlenir |
| 10 | ??? Alias | `AS` ile alias | `FROM dbo.Orders AS o` | ? Çözümlenir |
| 11 | ??? Alias | Ayný alias farklý statement'larda farklý tablolar | `UPDATE a FROM #tmp a` + baþka yerde `COR.Account AS a` | ? Statement-local çözüm — karýþmaz |
| 12 | ??? Alias | Alias temp tabloya çözümleniyor | `UPDATE a ... FROM #tmpRiskDetail a` | ? Dahil edilmez |
| 13 | ?? Eþleþtirme | Ayný tablo adý, farklý schema/DB | `BOADWH.LGD.Account` ? hedef `BOA.COR.Account` | ? Eþleþmez (saðdan hizalý parça karþýlaþtýrmasý) |
| 14 | ?? Eþleþtirme | DB baðlamý: hedef DB dýþýndaki SP'de nitelenmemiþ referans | BOADWH'deki SP'de `COR.Account` | ? `BOA.COR.Account` ile eþleþmez |
| 15 | ?? Eþleþtirme | Farklý DB'den tam nitelikli cross-DB update | BOADWH'deki SP'de `UPDATE BOA.COR.Account` | ? Eþleþir |
| 16 | ?? Eþleþtirme | Kýsmi niteleme varyasyonlarý | `COR.Account`, `Account`, `BOA..Account` | ? Belirtilmeyen parça joker |
| 17 | ?? Sözdizimi | Köþeli parantez kombinasyonlarý | `[BOA].[COR].[Account]`, `[COR].Account` | ? Normalize edilir |
| 18 | ?? Sözdizimi | Çift týrnaklý tanýmlayýcý (QUOTED_IDENTIFIER) | `"COR"."Account"` | ? Desteklenir |
| 19 | ?? Sözdizimi | Nokta etrafýnda boþluk | `BOA . COR . Account` | ? Yakalanýr |
| 20 | ?? Sözdizimi | 4 parçalý linked server adý | `srvkkdb.BOA.COR.Account` | ? Server parçasý atýlýr, DB.Schema.Table karþýlaþtýrýlýr |
| 21 | ?? Sözdizimi | `UPDATE TOP (n) [PERCENT]` | `UPDATE TOP (10) dbo.Orders` | ? Tablo doðru yakalanýr |
| 22 | ?? Yanlýþ pozitif | MERGE içi UPDATE | `WHEN MATCHED THEN UPDATE SET ...` | ? `SET` tablo sanýlmaz |
| 23 | ?? Yanlýþ pozitif | `UPDATE STATISTICS` | `UPDATE STATISTICS dbo.Orders` | ? Veri UPDATE'i deðil, atlanýr |
| 24 | ?? Yanlýþ pozitif | Trigger fonksiyonu | `IF UPDATE(Status)` | ? UPDATE sayýlmaz |
| 25 | ?? SET parse | SET içinde subquery (FROM/WHERE'li) | `SET x = ISNULL((SELECT ... FROM ... WHERE ...), 0), UpdateSystemDate = GETDATE()` | ? Parantez derinliði takibi — sonraki kolonlar kaçmaz |
| 26 | ?? SET parse | CASE içi karþýlaþtýrmalar | `SET x = CASE WHEN FEC = 0 THEN ...` | ? `FEC` kolon sanýlmaz |
| 27 | ?? SET parse | `;` ile biten statement | `SET Status = 1;` sonra `SELECT Amount = 5` | ? Blok sonraki statement'a taþmaz |
| 28 | ?? SET parse | Çoklu UPDATE ayný SP'de | Orders + Products ayrý UPDATE'ler | ? Her biri baðýmsýz analiz edilir |

---

## ?? Excel Rapor Ýçeriði

Her bulgu için:

- ??? Veritabaný, þema ve SP adý (tam nitelikli)
- ?? Eþleþen hedef tablo (hedef listesindeki tam ad)
- ?? UPDATE ifadesinin **satýr numarasý** ve ilk 120 karakterlik **snippet**'i
- ?? SET edilen kolon listesi
- ?? Eksik "update" kolonu bilgisi
- ? Dinamik SQL uyarýsý

---

## ?? Test Kapsamý

```bash
dotnet test ConsoleApp3.Tests\ConsoleApp3.Tests.csproj
```

- ? **50 test** (`UpdateStatementAnalyzerTests.cs`) — yukarýdaki tüm edge case'ler + `SqlScriptParser` birim testleri (SP parse, normalizasyon, yorum/literal temizleme).
- ? `DatabaseScannerTests` ve `ParallelSpAnalyzerTests` — tarama ve paralellik testleri.
- ?? Regresyon testleri gerçek üretim SP kalýplarýndan türetilmiþtir (`tsk_KLRTRiskCentralization`, `tsk_UsableAmount` senaryolarý).

---

## ?? Bilinen Sýnýrlar

- ? **Dinamik SQL içeriði analiz edilmez** — sadece uyarý raporlanýr (bilinçli tasarým kararý).
- ?? Tablo **metadata'sý kullanýlmadýðý** için "update kolonu" kontrolü isim kalýbýna dayanýr: adýnda `update` geçen bir kolon SET edilmiþ mi?
- ?? 4 parçalý adlarda linked server kimliði doðrulanmaz (server parçasý yok sayýlýr).
- ?? `ProgressReporter` çýktýlarý konsol geniþliðine duyarlýdýr; çok dar terminallerde ilerleme çubuðu kýsaltýlýr.

---

## ??? Gereksinimler

- .NET 8 SDK
- SQL Server eriþimi (`VIEW DEFINITION` yetkisi ile SP definition'larýný okuyabilmelidir)

---

## ???? English Summary

**SpUpdateAnalyzer** scans every user database and stored procedure on a SQL Server instance, detects `UPDATE` statements targeting a configurable list of tables (`tables.json`), and verifies whether columns whose names contain **"update"** (e.g. `UpdateSystemDate`, `UpdatedDate`) are included in the SET clause. Findings are exported to an **Excel report** with line numbers and snippets.

**Key capabilities:**

- ?? Strips comments and masks string literals before analysis; flags dynamic SQL (`sp_executesql`, `EXEC(@var)`) with warnings.
- ??? Excludes temp tables (`#tmp`, `##global`, `tempdb..#tmp`) and table variables (`@var`), including alias-resolved ones.
- ??? **Statement-local alias resolution** — the same alias used for different tables in different statements never gets mixed up.
- ?? **Schema/DB-aware target matching** — `BOADWH.LGD.Account` does *not* match target `BOA.COR.Account`; unqualified references are qualified with the SP's **database context** so `COR.Account` inside a non-BOA database won't falsely match.
- ?? Handles bracketed (`[COR].[Account]`), double-quoted (`"COR"."Account"`), spaced-dot (`BOA . COR . Account`), four-part linked-server names, and `UPDATE TOP (n)`.
- ?? **Parenthesis-depth SET parser** — subqueries inside SET expressions don't truncate the block, and comparisons inside `CASE`/subqueries aren't mistaken for column assignments.
- ?? Avoids false positives from `MERGE ... UPDATE SET`, `UPDATE STATISTICS`, and the trigger function `IF UPDATE(col)`.
- ? Parallel scanning with a prefetch pipeline (download of the next DB's SP definitions overlaps with analysis of the current one).

**Usage:** `dotnet run --project ConsoleApp3 -- --connection "<conn>" [--tables tables.json] [--output report.xlsx] [--threads N] [--verbose]`

**Requirements:** .NET 8 SDK and SQL Server access with `VIEW DEFINITION` permission.
