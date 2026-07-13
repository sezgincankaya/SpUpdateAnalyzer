using ConsoleApp3.Parsing;
using Xunit;

namespace ConsoleApp3.Tests;

public class UpdateStatementAnalyzerTests
{
    private static UpdateStatementAnalyzer CreateAnalyzer(params string[] tables)
        => new(tables, verbose: false);

    // -------------------------------------------------------------------------
    // 1. Alias ile UPDATE — UpdatedDate set edilmemiş → EKSİK
    // -------------------------------------------------------------------------
    [Fact]
    public void Alias_Update_MissingUpdatedDate_ShouldFlag()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_UpdateOrder AS BEGIN
    UPDATE t
    SET t.Status = 'A', t.Amount = 100
    FROM [dbo].[Orders] t
    WHERE t.Id = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_UpdateOrder", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.True(result.MissingColumnCount > 0, "UpdatedDate/update-içeren kolon eksik olmalı");
    }

    // -------------------------------------------------------------------------
    // 2. Doğrudan tablo adı — UpdatedDate SET edilmiş → OK
    // -------------------------------------------------------------------------
    [Fact]
    public void Direct_Update_WithUpdatedDate_ShouldNotFlag()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_UpdateProduct AS BEGIN
    UPDATE [MyDB].[dbo].[Products]
    SET Price = 99, UpdatedDate = GETDATE()
    WHERE Id = 5
END";
        var analyzer = CreateAnalyzer("Products");
        var result = analyzer.Analyze("usp_UpdateProduct", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 3. Köşeli parantezli tablo adı eşleşmesi
    // -------------------------------------------------------------------------
    [Fact]
    public void BracketedTableName_ShouldMatchTarget()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_UpdateCustomer AS BEGIN
    UPDATE [dbo].[Customers]
    SET Name = 'Test'
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Customers");
        var result = analyzer.Analyze("usp_UpdateCustomer", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.True(result.MissingColumnCount > 0, "update içeren kolon set edilmemiş olmalı");
    }

    // -------------------------------------------------------------------------
    // 4. Yorum satırı içindeki UPDATE — yoksayılmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void CommentedOutUpdate_ShouldBeIgnored()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_CommentTest AS BEGIN
    -- UPDATE Orders SET Status = 1
    SELECT 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_CommentTest", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 5. Çok satırlı yorum içindeki UPDATE — yoksayılmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void MultiLineCommentedUpdate_ShouldBeIgnored()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_MultiCommentTest AS BEGIN
    /*
        UPDATE Orders SET Status = 1 WHERE Id = 2
    */
    SELECT 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_MultiCommentTest", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 6. String literal içindeki UPDATE — yoksayılmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void StringLiteralUpdate_ShouldBeIgnored()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_StringTest AS BEGIN
    DECLARE @msg NVARCHAR(200) = 'UPDATE Orders SET Status = 1'
    PRINT @msg
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_StringTest", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 7. "update" içeren kolon SET edilmiş → eksik sayılmamalı
    // -------------------------------------------------------------------------
    [Fact]
    public void UpdateColumnIsSet_ShouldNotFlag()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_OkUpdate AS BEGIN
    UPDATE dbo.Orders
    SET Status = 2,
        LastUpdateTime = GETDATE(),
        UpdatedBy = 'system'
    WHERE Id = 99
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_OkUpdate", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 8. "update" içeren kolon SET edilmemiş → eksik sayılmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void UpdateColumnNotSet_ShouldFlag()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_MissingUpdate AS BEGIN
    UPDATE dbo.Orders
    SET Status = 3,
        Amount = 500
    WHERE Id = 10
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_MissingUpdate", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.True(result.MissingColumnCount > 0);
    }

    // -------------------------------------------------------------------------
    // 9. DB.Schema.Tablo formatı — normalize edilip eşleşmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void ThreePartTableName_ShouldNormalizeAndMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_ThreePart AS BEGIN
    UPDATE [MyDB].[dbo].[Products]
    SET Price = 10
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Products");
        var result = analyzer.Analyze("usp_ThreePart", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 10. Dinamik SQL uyarısı — sp_executesql içerdiğinde uyarı verilmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void DynamicSql_ShouldRaiseWarning()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Dynamic AS BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'UPDATE Orders SET Status = 1'
    EXEC sp_executesql @sql
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_Dynamic", sp);

        Assert.True(result.HasDynamicSqlWarning);
    }

    // -------------------------------------------------------------------------
    // 11. Hedef dışı tablo — sayılmamalı
    // -------------------------------------------------------------------------
    [Fact]
    public void NonTargetTable_ShouldNotBeIncluded()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_OtherTable AS BEGIN
    UPDATE dbo.SomeOtherTable
    SET Col1 = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Orders", "Products");
        var result = analyzer.Analyze("usp_OtherTable", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 12. AS alias ile FROM — alias çözümlenmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void AsAlias_ShouldResolveToTargetTable()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_AsAlias AS BEGIN
    UPDATE o
    SET o.Status = 5
    FROM dbo.Orders AS o
    WHERE o.Id = 7
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_AsAlias", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 13. Çoklu UPDATE — aynı SP içinde farklı tablolar
    // -------------------------------------------------------------------------
    [Fact]
    public void MultipleUpdates_ShouldAnalyzeAll()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_MultiUpdate AS BEGIN
    UPDATE dbo.Orders
    SET Status = 1
    WHERE Id = 1

    UPDATE dbo.Products
    SET Price = 50, UpdatedDate = GETDATE()
    WHERE Id = 2
END";
        var analyzer = CreateAnalyzer("Orders", "Products");
        var result = analyzer.Analyze("usp_MultiUpdate", sp);

        // Orders'ta update içeren kolon yok → eksik
        // Products'ta UpdatedDate set edilmiş → OK
        Assert.Equal(2, result.TotalTargetUpdateCount);
        Assert.Equal(1, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 14. SqlScriptParser — birden fazla SP parse edilmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void ScriptParser_ShouldParseMultipleSPs()
    {
        const string script = @"
CREATE PROCEDURE dbo.usp_First AS BEGIN SELECT 1 END
GO
CREATE OR ALTER PROCEDURE dbo.usp_Second AS BEGIN SELECT 2 END
GO
CREATE PROCEDURE dbo.usp_Third AS BEGIN SELECT 3 END
GO";
        var result = SqlScriptParser.ParseStoredProceduresFromText(script);

        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("usp_First"));
        Assert.True(result.ContainsKey("usp_Second"));
        Assert.True(result.ContainsKey("usp_Third"));
    }

    // -------------------------------------------------------------------------
    // 15. NormalizeObjectName — tüm format varyasyonları
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("[dbo].[MyTable]", "MyTable")]
    [InlineData("dbo.MyTable", "MyTable")]
    [InlineData("[MyDB].[dbo].[MyTable]", "MyTable")]
    [InlineData("MyDB.dbo.MyTable", "MyTable")]
    [InlineData("MyTable", "MyTable")]
    [InlineData("[MyTable]", "MyTable")]
    public void NormalizeObjectName_ShouldReturnBaseTableName(string input, string expected)
    {
        var result = SqlScriptParser.NormalizeObjectName(input);
        Assert.Equal(expected, result, StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 16. StripComments — yorumlar temizlenmeli, kod kalmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void StripComments_ShouldRemoveAllComments()
    {
        const string sql = @"
SELECT 1 -- bu bir yorum
/* çok
   satırlı yorum */
UPDATE Orders SET Status = 1";

        var result = SqlScriptParser.StripComments(sql);

        Assert.DoesNotContain("bu bir yorum", result);
        Assert.DoesNotContain("çok", result);
        Assert.Contains("UPDATE Orders", result);
    }

    // -------------------------------------------------------------------------
    // 17. MaskStringLiterals — string içindeki UPDATE maskelenmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void MaskStringLiterals_ShouldMaskSingleQuoteContent()
    {
        const string sql = "DECLARE @s = 'UPDATE Orders SET x=1'";
        var (masked, _) = SqlScriptParser.MaskStringLiterals(sql);

        Assert.DoesNotContain("UPDATE Orders", masked);
        Assert.Contains("__STR__", masked);
    }

    // -------------------------------------------------------------------------
    // 18. EXEC(@var) formatındaki dinamik SQL — uyarı verilmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void ExecVariable_ShouldRaiseDynamicSqlWarning()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_ExecVar AS BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'UPDATE Orders SET Status = 1'
    EXEC (@sql)
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_ExecVar", sp);

        Assert.True(result.HasDynamicSqlWarning);
    }

    // -------------------------------------------------------------------------
    // 19. Dinamik SQL uyarısı UPDATE ifadelerine de yansımalı
    // -------------------------------------------------------------------------
    [Fact]
    public void DynamicSql_ShouldPropagateToUpdateStatements()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Mixed AS BEGIN
    UPDATE dbo.Orders
    SET Status = 1, UpdatedDate = GETDATE()
    WHERE Id = 1

    DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'
    EXEC sp_executesql @sql
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_Mixed", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.All(result.UpdateStatements, u => Assert.True(u.HasDynamicSqlWarning));
    }

    // -------------------------------------------------------------------------
    // 20. UPDATE ifadesinin satır numarası ve snippet dolu olmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void UpdateStatement_ShouldHaveLineNumberAndSnippet()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_LineTest AS BEGIN
    SELECT 1

    UPDATE dbo.Orders
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_LineTest", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.True(stmt.LineNumber > 1, "Satır numarası 1'den büyük olmalı");
        Assert.Contains("UPDATE", stmt.UpdateSnippet, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 21. SET kolonları doğru çıkarılmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void SetColumns_ShouldBeExtractedCorrectly()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_SetCols AS BEGIN
    UPDATE dbo.Orders
    SET Status = 1,
        Amount = 250,
        UpdatedDate = GETDATE()
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_SetCols", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.Contains("Status", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Amount", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("UpdatedDate", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 22. Temp tabloya doğrudan UPDATE — dahil edilmemeli
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("#tmpOrders")]
    [InlineData("##globalTmpOrders")]
    [InlineData("[#tmpOrders]")]
    [InlineData("tempdb..#tmpOrders")]
    public void TempTableUpdate_ShouldBeExcluded(string tempRef)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_TempUpdate AS BEGIN
    UPDATE {tempRef}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Orders", "tmpOrders", "globalTmpOrders");
        var result = analyzer.Analyze("usp_TempUpdate", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 23. Tablo değişkenine UPDATE — dahil edilmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void TableVariableUpdate_ShouldBeExcluded()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_TableVar AS BEGIN
    DECLARE @Orders TABLE (Id INT, Status INT)
    UPDATE @Orders
    SET Status = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_TableVar", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 24. Alias temp tabloya çözümleniyor — dahil edilmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void AliasResolvingToTempTable_ShouldBeExcluded()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_TempAlias AS BEGIN
    UPDATE a SET
        a.Risk = 0
    FROM #tmpRiskDetail a
    WHERE a.RiskCode = '106'
END";
        var analyzer = CreateAnalyzer("Account", "tmpRiskDetail");
        var result = analyzer.Analyze("usp_TempAlias", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 25. Aynı alias farklı statement'larda farklı tablolara — karışmamalı
    //     (global alias haritası hatası regresyon testi)
    // -------------------------------------------------------------------------
    [Fact]
    public void SameAliasDifferentStatements_ShouldResolveLocally()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_SameAlias AS BEGIN
    -- 'a' burada temp tabloya işaret ediyor
    UPDATE a SET
        a.Risk = 0
    FROM #tmpRiskDetail a
    WHERE a.RiskCode = '106'

    -- 'a' burada başka bir SELECT'te gerçek tabloya işaret ediyor
    SELECT 1
    FROM COR.Account AS a WITH (NOLOCK)
    WHERE a.AccountNumber = 1
END";
        var analyzer = CreateAnalyzer("Account");
        var result = analyzer.Analyze("usp_SameAlias", sp);

        // Temp tabloya yapılan UPDATE, Account'a yapılmış gibi raporlanmamalı
        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 26. Alias gerçek tabloya çözümleniyor, aynı SP'de temp tablolar da var
    // -------------------------------------------------------------------------
    [Fact]
    public void AliasResolvingToRealTable_WithTempTablesAround_ShouldBeIncluded()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_MixedAlias AS BEGIN
    UPDATE t SET
        t.Status = 0
    FROM #tmpSomething t

    UPDATE o SET
        o.Status = 1, o.UpdatedDate = GETDATE()
    FROM dbo.Orders AS o
    INNER JOIN #tmpSomething t ON t.Id = o.Id
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_MixedAlias", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.Equal("Orders", stmt.NormalizedTableName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 27. UPDATE TOP (n) tablo — tablo doğru yakalanmalı
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE TOP (10) dbo.Orders")]
    [InlineData("UPDATE TOP(100) PERCENT dbo.Orders")]
    public void UpdateTop_ShouldCaptureTable(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_Top AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_Top", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 28. MERGE ... WHEN MATCHED THEN UPDATE SET — yanlış pozitif üretmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void MergeUpdateSet_ShouldNotProduceFalsePositive()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Merge AS BEGIN
    MERGE dbo.Orders AS target
    USING dbo.Staging AS source ON target.Id = source.Id
    WHEN MATCHED THEN UPDATE SET target.Status = source.Status;
END";
        var analyzer = CreateAnalyzer("Set", "Orders");
        var result = analyzer.Analyze("usp_Merge", sp);

        // 'SET' bir tablo adı gibi raporlanmamalı
        Assert.DoesNotContain(result.UpdateStatements,
            u => u.NormalizedTableName.Equals("SET", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // 29. UPDATE STATISTICS tablo — veri UPDATE'i değildir, dahil edilmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void UpdateStatistics_ShouldBeIgnored()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Stats AS BEGIN
    UPDATE STATISTICS dbo.Orders
END";
        var analyzer = CreateAnalyzer("Orders", "Statistics");
        var result = analyzer.Analyze("usp_Stats", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 30. UPDATE(kolon) fonksiyonu (trigger'larda) — tablo UPDATE'i sayılmamalı
    // -------------------------------------------------------------------------
    [Fact]
    public void UpdateFunctionInTrigger_ShouldNotMatchTargets()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_FnLike AS BEGIN
    IF UPDATE(Status)
        SELECT 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_FnLike", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 31. Schema/DB farklı ama tablo adı aynı → EŞLEŞMEMELİ
    //     (BOADWH.LGD.Account vs BOA.COR.Account regresyon testi)
    // -------------------------------------------------------------------------
    [Fact]
    public void SameTableNameDifferentSchema_ShouldNotMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_LgdAccount AS BEGIN
    UPDATE lgd
    SET lgd.ClosureDate = lgd.DefaultDate
    FROM BOADWH.LGD.Account lgd
    WHERE lgd.ClosureDate < lgd.DefaultDate
    AND ImportSource = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_LgdAccount", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 32. Tam nitelikli hedef ↔ tam nitelikli referans → eşleşmeli
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE BOA.COR.Account")]
    [InlineData("UPDATE [BOA].[COR].[Account]")]
    [InlineData("UPDATE COR.Account")]
    [InlineData("UPDATE [COR].[Account]")]
    [InlineData("UPDATE Account")]
    [InlineData("UPDATE [Account]")]
    [InlineData("UPDATE BOA..Account")]
    public void QualifiedTarget_MatchingReferences_ShouldMatch(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_Acc AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_Acc", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 33. Tam nitelikli hedef ↔ farklı schema/db referansları → EŞLEŞMEMELİ
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE BOADWH.LGD.Account")]
    [InlineData("UPDATE [BOADWH].[LGD].[Account]")]
    [InlineData("UPDATE LGD.Account")]
    [InlineData("UPDATE OtherDb.COR.Account")]
    public void QualifiedTarget_DifferentSchemaOrDb_ShouldNotMatch(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_AccOther AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_AccOther", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 34. Alias, schema'sı farklı tabloya çözümleniyor → EŞLEŞMEMELİ
    // -------------------------------------------------------------------------
    [Fact]
    public void AliasResolvingToDifferentSchemaTable_ShouldNotMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_AliasSchema AS BEGIN
    UPDATE a SET
        a.Status = 1
    FROM [BOADWH].[LGD].[Account] AS a
    WHERE a.Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_AliasSchema", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 35. Alias, doğru schema'lı tabloya çözümleniyor → eşleşmeli
    //     ve rapor edilen ad, hedef listesindeki tam ad olmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void AliasResolvingToMatchingSchemaTable_ShouldMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_AliasSchemaOk AS BEGIN
    UPDATE a SET
        a.Status = 1, a.UpdatedDate = GETDATE()
    FROM COR.Account AS a WITH (NOLOCK)
    WHERE a.Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_AliasSchemaOk", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.Equal("BOA.COR.Account", stmt.NormalizedTableName);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 36. Kısa hedef (base ad) verilirse tüm schema'larla eşleşebilmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void BareTargetName_ShouldMatchAnySchema()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_BareTarget AS BEGIN
    UPDATE BOADWH.LGD.Account
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("Account");
        var result = analyzer.Analyze("usp_BareTarget", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 37. Köşeli parantezli / boşluklu hedef tanımı normalize edilmeli
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("[BOA].[COR].[Account]")]
    [InlineData(" BOA . COR . Account ")]
    public void BracketedOrSpacedTargetDefinition_ShouldNormalize(string target)
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_NormTarget AS BEGIN
    UPDATE BOA.COR.Account
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer(target);
        var result = analyzer.Analyze("usp_NormTarget", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 38. DB bağlamı: SP hedef DB'de (BOA) → DB niteliği olmayan referans eşleşmeli
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE COR.Account")]
    [InlineData("UPDATE [COR].Account")]
    [InlineData("UPDATE COR.[Account]")]
    [InlineData("UPDATE [COR].[Account]")]
    [InlineData("UPDATE Account")]
    public void DbContextMatchesTarget_UnqualifiedRef_ShouldMatch(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_DbCtx AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_DbCtx", sp, currentDatabase: "BOA");

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 39. DB bağlamı: SP FARKLI DB'de → DB niteliği olmayan referans EŞLEŞMEMELİ
    //     (BOADWH içindeki "COR.Account" aslında BOADWH.COR.Account'tur)
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE COR.Account")]
    [InlineData("UPDATE [COR].[Account]")]
    [InlineData("UPDATE Account")]
    public void DbContextDiffersFromTarget_UnqualifiedRef_ShouldNotMatch(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_DbCtxOther AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_DbCtxOther", sp, currentDatabase: "BOADWH");

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 40. DB bağlamı: farklı DB'deki SP tam nitelikli BOA.COR.Account yazarsa → eşleşmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void DbContextDiffers_FullyQualifiedRef_ShouldStillMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_CrossDb AS BEGIN
    UPDATE BOA.COR.Account
    SET Status = 1, UpdatedDate = GETDATE()
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_CrossDb", sp, currentDatabase: "BOADWH");

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 41. DB bağlamı verilmezse eski davranış korunur (geriye uyumluluk)
    // -------------------------------------------------------------------------
    [Fact]
    public void NoDbContext_UnqualifiedRef_ShouldMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_NoCtx AS BEGIN
    UPDATE COR.Account
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_NoCtx", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 42. Nokta etrafında boşluklu referanslar (geçerli T-SQL) yakalanmalı
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("UPDATE BOA . COR . Account")]
    [InlineData("UPDATE [BOA] . [COR] . [Account]")]
    public void SpacedDotsInReference_ShouldMatch(string updateClause)
    {
        var sp = $@"
CREATE PROCEDURE dbo.usp_SpacedDots AS BEGIN
    {updateClause}
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_SpacedDots", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 43. 4 parçalı (linked server) referans — server parçası yok sayılıp eşleşmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void FourPartLinkedServerReference_ShouldMatchIgnoringServer()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_LinkedSrv AS BEGIN
    UPDATE srvkkdb.BOA.COR.Account
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_LinkedSrv", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 44. 4 parçalı referans farklı DB'ye işaret ediyorsa → eşleşmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void FourPartReference_DifferentDb_ShouldNotMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_LinkedSrvOther AS BEGIN
    UPDATE srvkkdb.kredikuveyt.dbo.Account
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_LinkedSrvOther", sp);

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 45. Çift tırnaklı tanımlayıcılar (QUOTED_IDENTIFIER ON) eşleşmeli
    // -------------------------------------------------------------------------
    [Fact]
    public void DoubleQuotedIdentifiers_ShouldMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Quoted AS BEGIN
    UPDATE ""COR"".""Account""
    SET Status = 1
    WHERE Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_Quoted", sp, currentDatabase: "BOA");

        Assert.Equal(1, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 46. DB bağlamı + alias: farklı DB'deki SP'de alias'lı COR.Account → eşleşmemeli
    // -------------------------------------------------------------------------
    [Fact]
    public void DbContext_AliasedUnqualifiedRef_InOtherDb_ShouldNotMatch()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_AliasCtx AS BEGIN
    UPDATE a SET
        a.Status = 1
    FROM COR.Account AS a WITH (NOLOCK)
    WHERE a.Id = 1
END";
        var analyzer = CreateAnalyzer("BOA.COR.Account");
        var result = analyzer.Analyze("usp_AliasCtx", sp, currentDatabase: "BOADWH");

        Assert.Equal(0, result.TotalTargetUpdateCount);
    }

    // -------------------------------------------------------------------------
    // 47. SET içinde subquery (FROM/WHERE içeren) — sonraki kolonlar kaçmamalı
    //     (CLT.tsk_UsableAmount regresyon testi: UpdateSystemDate raporlanmıştı)
    // -------------------------------------------------------------------------
    [Fact]
    public void SetWithSubquery_ColumnsAfterSubquery_ShouldBeDetected()
    {
        const string sp = @"
CREATE PROCEDURE CLT.tsk_Usable AS BEGIN
    UPDATE C SET
        C.UsableAmountUSD = ISNULL(
            ( SELECT SUM(d.InstallmentAmount)
              FROM CLT.DebtEndorsementDetail AS d WITH(NOLOCK)
              WHERE d.CollateralId = c.CollateralId AND
                    d.IsActive = 1
              GROUP BY d.CollateralId ), 0),
        C.UpdateSystemDate = GetDate()
    FROM CLT.Collateral C
    INNER JOIN CLT.DebtEndorsement d WITH(NOLOCK) ON d.CollateralId = C.CollateralId
    WHERE C.State NOT IN ( 2,5 )
END";
        var analyzer = CreateAnalyzer("Collateral");
        var result = analyzer.Analyze("tsk_Usable", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.Contains("UsableAmountUSD", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("UpdateSystemDate", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 48. SET içinde CASE ve subquery karışımı — subquery'deki '=' karşılaştırmaları
    //     kolon sanılmamalı
    // -------------------------------------------------------------------------
    [Fact]
    public void SetWithCaseAndSubquery_ComparisonsInside_ShouldNotBeColumns()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_CaseSub AS BEGIN
    UPDATE C SET
        C.UsableAmountUSD = (CASE
                                WHEN C.FEC = 0 THEN C.CollateralAmount / @Rate
                                ELSE C.CollateralAmount * fx.Parity
                             END),
        C.UpdateSystemDate = GetDate()
    FROM CLT.Collateral C
    LEFT JOIN @FxRate fx ON c.FEC = fx.FEC
    WHERE C.State NOT IN ( 2,5 )
END";
        var analyzer = CreateAnalyzer("Collateral");
        var result = analyzer.Analyze("usp_CaseSub", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        // Sadece gerçek atamalar kolon olmalı
        Assert.Equal(2, stmt.SetColumns.Count);
        Assert.Contains("UsableAmountUSD", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("UpdateSystemDate", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        // CASE içindeki FEC = 0 karşılaştırması kolon sanılmamalı
        Assert.DoesNotContain("FEC", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.MissingColumnCount);
    }

    // -------------------------------------------------------------------------
    // 49. Subquery FROM'undan SONRA update kolonu yoksa yine eksik raporlanmalı
    //     (yanlış negatife düşmediğimizi doğrula)
    // -------------------------------------------------------------------------
    [Fact]
    public void SetWithSubquery_NoUpdateColumn_ShouldStillFlag()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_SubNoUpd AS BEGIN
    UPDATE C SET
        C.Amount = ISNULL(
            ( SELECT SUM(d.Amount)
              FROM dbo.Detail AS d
              WHERE d.Id = c.Id ), 0)
    FROM dbo.Orders C
    WHERE C.State = 1
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_SubNoUpd", sp);

        Assert.Equal(1, result.TotalTargetUpdateCount);
        Assert.True(result.MissingColumnCount > 0);
    }

    // -------------------------------------------------------------------------
    // 50. Noktalı virgülle biten SET bloğu doğru sınırlanmalı
    // -------------------------------------------------------------------------
    [Fact]
    public void SetBlockEndingWithSemicolon_ShouldBeBounded()
    {
        const string sp = @"
CREATE PROCEDURE dbo.usp_Semi AS BEGIN
    UPDATE dbo.Orders
    SET Status = 1, UpdatedDate = GETDATE();
    SELECT Amount = 5 FROM dbo.Other
END";
        var analyzer = CreateAnalyzer("Orders");
        var result = analyzer.Analyze("usp_Semi", sp);

        var stmt = Assert.Single(result.UpdateStatements);
        Assert.Equal(2, stmt.SetColumns.Count);
        Assert.DoesNotContain("Amount", stmt.SetColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.MissingColumnCount);
    }
}