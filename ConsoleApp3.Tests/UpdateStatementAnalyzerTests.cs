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
}