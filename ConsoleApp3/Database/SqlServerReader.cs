using Microsoft.Data.SqlClient;

namespace ConsoleApp3.Database;

/// <summary>
/// SQL Server'a bağlanarak veritabanı, şema ve stored procedure
/// bilgilerini çeken sınıf.
/// </summary>
public class SqlServerReader
{
    private readonly string _masterConnectionString;

    // Sistem veritabanları — tarama dışı bırakılır
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "tempdb", "model", "msdb"
    };

    /// <param name="masterConnectionString">
    /// master DB'ye bağlantı string'i.
    /// Örn: "Server=.;Integrated Security=true;TrustServerCertificate=true;"
    /// </param>
    public SqlServerReader(string masterConnectionString)
    {
        _masterConnectionString = masterConnectionString;
    }

    // -------------------------------------------------------------------------
    // Veritabanı listesi
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sunucudaki kullanıcı veritabanlarını döner (sistem DB'leri hariç).
    /// </summary>
    public async Task<List<string>> GetUserDatabasesAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT name
            FROM   sys.databases
            WHERE  state_desc = 'ONLINE'
              AND  is_read_only = 0
            ORDER BY name";

        var databases = new List<string>();

        await using var conn = new SqlConnection(_masterConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
        {
            var dbName = rdr.GetString(0);
            if (!SystemDatabases.Contains(dbName))
                databases.Add(dbName);
        }

        return databases;
    }

    // -------------------------------------------------------------------------
    // Tablo listesi
    // -------------------------------------------------------------------------

    /// <summary>
    /// Belirtilen veritabanındaki tüm kullanıcı tablolarının adlarını döner.
    /// </summary>
    public async Task<List<string>> GetAllTablesAsync(string database, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT t.name
            FROM   sys.tables  t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE  t.is_ms_shipped = 0
            ORDER BY t.name";

        var tables = new List<string>();

        await using var conn = new SqlConnection(BuildConnectionString(database));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));

        return tables;
    }

    // -------------------------------------------------------------------------
    // Şema listesi
    // -------------------------------------------------------------------------

    /// <summary>
    /// Belirtilen veritabanındaki kullanıcı şemalarını döner.
    /// </summary>
    public async Task<List<string>> GetSchemasAsync(string database, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT s.name
            FROM   sys.schemas s
            INNER JOIN sys.procedures p ON p.schema_id = s.schema_id
            ORDER BY s.name";

        var schemas = new List<string>();

        await using var conn = new SqlConnection(BuildConnectionString(database));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
            schemas.Add(rdr.GetString(0));

        return schemas;
    }

    // -------------------------------------------------------------------------
    // SP listesi
    // -------------------------------------------------------------------------

    /// <summary>
    /// Belirtilen veritabanı ve şemadaki tüm SP adlarını döner.
    /// </summary>
    public async Task<List<(string Schema, string SpName)>> GetStoredProcedureNamesAsync(
        string database,
        string schema,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT s.name   AS SchemaName,
                   p.name   AS ProcName
            FROM   sys.procedures p
            INNER JOIN sys.schemas s ON s.schema_id = p.schema_id
            WHERE  s.name = @schema
              AND  p.type = 'P'
            ORDER BY p.name";

        var result = new List<(string, string)>();

        await using var conn = new SqlConnection(BuildConnectionString(database));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        while (await rdr.ReadAsync(ct))
            result.Add((rdr.GetString(0), rdr.GetString(1)));

        return result;
    }

    /// <summary>
    /// Belirtilen veritabanındaki toplam SP sayısını döner.
    /// </summary>
    public async Task<int> GetStoredProcedureCountAsync(string database, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT COUNT(*)
            FROM   sys.procedures p
            WHERE  p.type = 'P'";

        await using var conn = new SqlConnection(BuildConnectionString(database));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int count ? count : 0;
    }

    // -------------------------------------------------------------------------
    // SP definition
    // -------------------------------------------------------------------------

    /// <summary>
    /// Belirtilen SP'nin CREATE script'ini döner.
    /// OBJECT_DEFINITION yerine sys.sql_modules kullanılır — daha güvenilir.
    /// </summary>
    public async Task<string?> GetSpDefinitionAsync(
        string database,
        string schema,
        string spName,
        CancellationToken ct = default)
    {
        // sys.sql_modules definition sütunu max 2GB nvarchar döner
        const string sql = @"
            SELECT m.definition
            FROM   sys.sql_modules   m
            INNER JOIN sys.procedures p ON p.object_id = m.object_id
            INNER JOIN sys.schemas    s ON s.schema_id = p.schema_id
            WHERE  s.name = @schema
              AND  p.name = @spName";

        await using var conn = new SqlConnection(BuildConnectionString(database));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@spName", spName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null ? null : (string)result;
    }

    // -------------------------------------------------------------------------
    // Yardımcı
    // -------------------------------------------------------------------------

    /// <summary>
    /// Master connection string'ini hedef database için yeniden oluşturur.
    /// </summary>
    private string BuildConnectionString(string database)
    {
        var builder = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = database
        };
        return builder.ConnectionString;
    }
}