
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

internal static class Program
{
    private const string TargetSchema = "public";
    private const string MSSQL_CONNECTION_STRING =
    "Server=DESKTOP-JDDOPTR\\MSSQLSERVER2019;" +
    "Database=SSC_TEST_APR_MAYGD_2026;" +
    "User ID=sa;" +
    "Password=P@ssw0rd;" +
    "Encrypt=True;" +
    "TrustServerCertificate=True;";

    private const string POSTGRES_CONNECTION_STRING =
        "Host=127.0.0.1;" +
        "Port=5432;" +
        "Database=TEST_DB;" +
        "Username=postgres;" +
        "Password=P@ssw0rd;" +
        "SSL Mode=Disable;";
    private static string SqlConnectionString => MSSQL_CONNECTION_STRING;


    //Environment.GetEnvironmentVariable("MSSQL_CONNECTION_STRING")
    //?? throw new InvalidOperationException(
    //    "MSSQL_CONNECTION_STRING is not configured.");

    private static string PgConnectionString => POSTGRES_CONNECTION_STRING;
    //Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
    //?? throw new InvalidOperationException(
    //    "POSTGRES_CONNECTION_STRING is not configured.");

    private static async Task<int> Main(string[] args)
    {
        bool reset = args.Contains("--reset", StringComparer.Ordinal);
        bool confirmReset = args.Contains("--confirm-reset", StringComparer.Ordinal);

        if (reset && !confirmReset)
        {
            Console.Error.WriteLine(
                "Reset requested. Review the target database, then rerun " +
                "with --reset --confirm-reset.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await using var sql = new SqlConnection(SqlConnectionString);
            await using var pg = new NpgsqlConnection(PgConnectionString);

            await sql.OpenAsync(cts.Token);
            await pg.OpenAsync(cts.Token);

            Console.WriteLine("Reading SQL Server metadata...");
            var tables = await MetadataReader.ReadAsync(sql, cts.Token);

            if (tables.Count == 0)
            {
                Console.WriteLine("No eligible user tables found.");
                return 0;
            }

            ValidateMetadata(tables);

            Console.WriteLine(
                $"Discovered {tables.Count} tables, " +
                $"{tables.Sum(t => t.Columns.Count)} columns.");

            if (reset)
            {
                Console.WriteLine(
                    "WARNING: dropping target tables in schema " +
                    TargetSchema + ".");

                // Only source-matched tables are dropped. CASCADE may also
                // remove dependent target objects; inspect the target first.
                foreach (var table in tables.AsEnumerable().Reverse())
                {
                    await using var cmd = new NpgsqlCommand(
                        $"DROP TABLE IF EXISTS " +
                        $"{Q(TargetSchema)}.{Q(table.TargetName)} CASCADE;",
                        pg);
                    await cmd.ExecuteNonQueryAsync(cts.Token);
                }
            }

            Console.WriteLine("Creating base schema...");
            foreach (var table in tables)
                await SchemaWriter.CreateTableAsync(pg, table, cts.Token);

            Console.WriteLine("Streaming table data...");
            foreach (var table in tables)
            {
                long count = await DataLoader.CopyTableAsync(
                    sql, pg, table, cts.Token);

                table.ImportedRows = count;
                Console.WriteLine(
                    $"  {table.SourceSchema}.{table.SourceName}: " +
                    $"{count:N0} rows imported");
            }

            Console.WriteLine("Creating primary keys and indexes...");
            foreach (var table in tables)
                await SchemaWriter.CreatePrimaryKeyAsync(
                    pg, table, cts.Token);

            foreach (var table in tables)
                await SchemaWriter.CreateIndexesAsync(
                    pg, table, cts.Token);

            Console.WriteLine("Creating foreign keys...");
            foreach (var table in tables)
                await SchemaWriter.CreateForeignKeysAsync(
                    pg, table, cts.Token);

            Console.WriteLine("Validating row counts...");
            foreach (var table in tables)
            {
                long sourceCount = await MetadataReader.CountRowsAsync(
                    sql, table, cts.Token);

                long targetCount = await MetadataReader.CountTargetRowsAsync(
                    pg, table, cts.Token);

                Console.WriteLine(
                    $"  {table.TargetName}: source={sourceCount:N0}, " +
                    $"target={targetCount:N0}");

                if (sourceCount != targetCount)
                    throw new InvalidOperationException(
                        $"Row count mismatch for {table.TargetName}.");
            }

            Console.WriteLine("Migration completed successfully.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "Migration cancelled. Already completed tables may remain.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("MIGRATION FAILED");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ValidateMetadata(List<TableMetadata> tables)
    {
        // Lowercasing can cause collisions, e.g. dbo.Customer and
        // dbo.customer. Never silently merge distinct source objects.
        var collisions = tables
            .GroupBy(t => t.TargetName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        if (collisions.Count > 0)
            throw new InvalidOperationException(
                "Lowercase table-name collision: " +
                string.Join(", ", collisions.Select(g => g.Key)));

        foreach (var table in tables)
        {
            if (table.Columns.Count == 0)
                throw new InvalidOperationException(
                    $"Table {table.SourceName} has no columns.");

            var columnCollisions = table.Columns
                .GroupBy(c => c.TargetName, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (columnCollisions.Count > 0)
                throw new InvalidOperationException(
                    $"Lowercase column-name collision in " +
                    $"{table.SourceName}: {string.Join(", ", columnCollisions)}");

            if (table.PrimaryKey != null &&
                table.PrimaryKey.Columns.Count == 0)
                throw new InvalidOperationException(
                    $"Empty primary key on {table.SourceName}.");
        }

        var tableNames = tables
            .Select(t => t.TargetName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var table in tables)
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.TargetSchema == TargetSchema &&
                    tableNames.Contains(fk.TargetTable) &&
                    fk.Columns.Count != fk.TargetColumns.Count)
                    throw new InvalidOperationException(
                        $"Invalid foreign key metadata: {fk.Name}");
            }
    }

    internal static string Q(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    internal static string Normalize(string name) =>
        name.ToLowerInvariant();
}

internal static class MetadataReader
{
    private const string TargetSchema = "public";
    public static async Task<List<TableMetadata>> ReadAsync(
        SqlConnection sql, CancellationToken ct)
    {
        var tables = new List<TableMetadata>();
        var bySource = new Dictionary<string, TableMetadata>(
            StringComparer.Ordinal);

        const string tableSql = """
            SELECT s.name AS schema_name, t.name AS table_name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        await using (var cmd = new SqlCommand(tableSql, sql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var t = new TableMetadata
                {
                    SourceSchema = r.GetString(0),
                    SourceName = r.GetString(1)
                };

                t.TargetName = Program.Normalize(t.SourceName);
                tables.Add(t);
                bySource[Key(t.SourceSchema, t.SourceName)] = t;
            }
        }

        const string columnsSql = """
            SELECT s.name, t.name, c.column_id, c.name,
                   ty.name, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;
            """;

        await using (var cmd = new SqlCommand(columnsSql, sql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var table = bySource[Key(r.GetString(0), r.GetString(1))];
                var sourceType = r.GetString(4).ToLowerInvariant();

                var column = new ColumnMetadata
                {
                    Ordinal = r.GetInt32(2),
                    SourceName = r.GetString(3),
                    TargetName = Program.Normalize(r.GetString(3)),
                    SourceType = sourceType,
                    MaxLength = r.GetInt16(5),
                    Precision = r.GetByte(6),
                    Scale = r.GetByte(7),
                    Nullable = r.GetBoolean(8),
                    Identity = r.GetBoolean(9),
                    Computed = r.GetBoolean(10)
                };

                if (column.Computed)
                    throw new NotSupportedException(
                        $"Computed column {table.SourceName}." +
                        $"{column.SourceName} must be explicitly migrated.");

                column.TargetType = TypeMapper.ToPostgres(column);
                table.Columns.Add(column);
            }
        }

        const string pkSql = """
            SELECT s.name, t.name, kc.name, c.name, ic.key_ordinal
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic
              ON ic.object_id = t.object_id
             AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c
              ON c.object_id = t.object_id
             AND c.column_id = ic.column_id
            WHERE kc.type = 'PK'
            ORDER BY s.name, t.name, ic.key_ordinal;
            """;

        await using (var cmd = new SqlCommand(pkSql, sql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var t = bySource[Key(r.GetString(0), r.GetString(1))];
                t.PrimaryKey ??= new IndexMetadata
                {
                    Name = Program.Normalize(r.GetString(2)),
                    IsPrimary = true,
                    IsUnique = true
                };
                t.PrimaryKey.Columns.Add(Program.Normalize(r.GetString(3)));
            }
        }

        const string indexSql = """
            SELECT s.name, t.name, i.name, i.is_unique,
                   c.name, ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic
              ON ic.object_id = i.object_id
             AND ic.index_id = i.index_id
            JOIN sys.columns c
              ON c.object_id = t.object_id
             AND c.column_id = ic.column_id
            WHERE i.type IN (1, 2)
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.is_disabled = 0
              AND i.name IS NOT NULL
              AND ic.key_ordinal > 0
              AND ic.is_included_column = 0
            ORDER BY s.name, t.name, i.name, ic.key_ordinal;
            """;

        await using (var cmd = new SqlCommand(indexSql, sql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var t = bySource[Key(r.GetString(0), r.GetString(1))];
                var name = Program.Normalize(r.GetString(2));

                var idx = t.Indexes.FirstOrDefault(
                    x => x.Name == name);

                if (idx == null)
                {
                    idx = new IndexMetadata
                    {
                        Name = name,
                        IsUnique = r.GetBoolean(3)
                    };
                    t.Indexes.Add(idx);
                }

                idx.Columns.Add(Program.Normalize(r.GetString(4)));
            }
        }

        const string fkSql = """
            SELECT ps.name, pt.name, fk.name,
                   rs.name, rt.name, pc.name, rc.name, fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.foreign_key_columns fkc
              ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc
              ON pc.object_id = pt.object_id
             AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc
              ON rc.object_id = rt.object_id
             AND rc.column_id = fkc.referenced_column_id
            WHERE fk.is_disabled = 0
            ORDER BY ps.name, pt.name, fk.name, fkc.constraint_column_id;
            """;

        await using (var cmd = new SqlCommand(fkSql, sql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var t = bySource[Key(r.GetString(0), r.GetString(1))];
                var name = Program.Normalize(r.GetString(2));
                var fk = t.ForeignKeys.FirstOrDefault(x => x.Name == name);

                if (fk == null)
                {
                    fk = new ForeignKeyMetadata
                    {
                        Name = name,
                        TargetSchema = Program.Normalize(r.GetString(3)),
                        TargetTable = Program.Normalize(r.GetString(4))
                    };
                    t.ForeignKeys.Add(fk);
                }

                fk.Columns.Add(Program.Normalize(r.GetString(5)));
                fk.TargetColumns.Add(Program.Normalize(r.GetString(6)));
            }
        }

        return tables;
    }

    public static async Task<long> CountRowsAsync(
        SqlConnection sql, TableMetadata table, CancellationToken ct)
    {
        string query =
            $"SELECT COUNT_BIG(*) FROM " +
            $"{Program.Q(table.SourceSchema)}.{Program.Q(table.SourceName)}";

        await using var cmd = new SqlCommand(query, sql);
        return Convert.ToInt64(
            await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public static async Task<long> CountTargetRowsAsync(NpgsqlConnection pg, TableMetadata table, CancellationToken ct)
    {
        string query =
            $"SELECT COUNT(*) FROM {Program.Q(TargetSchema)}." +
            $"{Program.Q(table.TargetName)}";

        await using var cmd = new NpgsqlCommand(query, pg);
        return Convert.ToInt64(
            await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static string Key(string schema, string table) =>
        schema + "\0" + table;
}

internal static class TypeMapper
{
    public static string ToPostgres(ColumnMetadata c)
    {
        return c.SourceType switch
        {
            "int" => "integer",
            "bigint" => "bigint",
            "smallint" or "tinyint" => "smallint",
            "bit" => "boolean",
            "varchar" or "char" or "nvarchar" or "nchar" =>
                CharacterType(c),
            "text" or "ntext" => "text",
            "datetime" or "datetime2" or "smalldatetime" =>
                "timestamp without time zone",
            "datetimeoffset" => "timestamp with time zone",
            "uniqueidentifier" => "uuid",
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" =>
                "bytea",
            "decimal" or "numeric" => NumericType(c),
            "money" or "smallmoney" => "numeric",
            "float" => "double precision",
            "real" => "real",
            "date" => "date",
            "time" => $"time({Math.Min(c.Scale, (byte)6)})",
            "xml" => "xml",
            "sql_variant" => throw new NotSupportedException(
                $"sql_variant column {c.SourceName} needs an explicit conversion."),
            _ => throw new NotSupportedException(
                $"Unsupported SQL Server type '{c.SourceType}' " +
                $"for column '{c.SourceName}'.")
        };
    }

    private static string CharacterType(ColumnMetadata c)
    {
        if (c.SourceType is "text" or "ntext")
            return "text";

        if (c.MaxLength == -1)
            return "text";

        // SQL Server reports nchar/nvarchar lengths in bytes.
        int length = c.SourceType.StartsWith('n')
            ? c.MaxLength / 2
            : c.MaxLength;

        if (length <= 0)
            throw new NotSupportedException(
                $"Invalid length for {c.SourceName}.");

        return $"{(c.SourceType is "char" or "nchar" ? "char" : "varchar")}({length})";
    }

    private static string NumericType(ColumnMetadata c)
    {
        if (c.Precision == 0 || c.Precision > 1000 || c.Scale > c.Precision)
            throw new NotSupportedException(
                $"Invalid numeric precision/scale for {c.SourceName}.");

        // PostgreSQL supports at most 1000 digits of numeric precision.
        return $"numeric({c.Precision},{c.Scale})";
    }

    public static NpgsqlDbType ToNpgsqlDbType(ColumnMetadata c) =>
        c.SourceType switch
        {
            "int" => NpgsqlDbType.Integer,
            "bigint" => NpgsqlDbType.Bigint,
            "smallint" or "tinyint" => NpgsqlDbType.Smallint,
            "bit" => NpgsqlDbType.Boolean,
            "varchar" or "char" or "nvarchar" or "nchar" or "text" or "ntext" =>
                NpgsqlDbType.Text,
            "datetime" or "datetime2" or "smalldatetime" =>
                NpgsqlDbType.Timestamp,
            "datetimeoffset" => NpgsqlDbType.TimestampTz,
            "uniqueidentifier" => NpgsqlDbType.Uuid,
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" =>
                NpgsqlDbType.Bytea,
            "decimal" or "numeric" or "money" or "smallmoney" =>
                NpgsqlDbType.Numeric,
            "float" => NpgsqlDbType.Double,
            "real" => NpgsqlDbType.Real,
            "date" => NpgsqlDbType.Date,
            "time" => NpgsqlDbType.Time,
            "xml" => NpgsqlDbType.Xml,
            _ => throw new NotSupportedException(
                $"No COPY type mapping for {c.SourceType}.")
        };
}

internal static class SchemaWriter
{
    private const string TargetSchema = "public";
    public static async Task CreateTableAsync(
        NpgsqlConnection pg, TableMetadata t, CancellationToken ct)
    {
        var definitions = t.Columns.Select(c =>
            $"{Program.Q(c.TargetName)} {c.TargetType}" +
            (c.Nullable ? "" : " NOT NULL") +
            (c.Identity ? " GENERATED BY DEFAULT AS IDENTITY" : ""));

        string ddl =
            $"CREATE TABLE {Program.Q(TargetSchema)}.{Program.Q(t.TargetName)} " +
            $"({string.Join(", ", definitions)});";

        await using var cmd = new NpgsqlCommand(ddl, pg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task CreatePrimaryKeyAsync(
        NpgsqlConnection pg, TableMetadata t, CancellationToken ct)
    {
        if (t.PrimaryKey == null) return;

        string ddl =
            $"ALTER TABLE {Program.Q(TargetSchema)}.{Program.Q(t.TargetName)} " +
            $"ADD CONSTRAINT {Program.Q(ShortName(t.PrimaryKey.Name))} " +
            $"PRIMARY KEY ({JoinColumns(t.PrimaryKey.Columns)});";

        await ExecuteAsync(pg, ddl, ct);
    }

    public static async Task CreateIndexesAsync(
        NpgsqlConnection pg, TableMetadata t, CancellationToken ct)
    {
        foreach (var idx in t.Indexes)
        {
            string ddl =
                $"CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX " +
                $"{Program.Q(ShortName(idx.Name))} ON " +
                $"{Program.Q(TargetSchema)}.{Program.Q(t.TargetName)} " +
                $"({JoinColumns(idx.Columns)});";

            await ExecuteAsync(pg, ddl, ct);
        }
    }

    public static async Task CreateForeignKeysAsync(
        NpgsqlConnection pg, TableMetadata t, CancellationToken ct)
    {
        foreach (var fk in t.ForeignKeys)
        {
            string ddl =
                $"ALTER TABLE {Program.Q(TargetSchema)}.{Program.Q(t.TargetName)} " +
                $"ADD CONSTRAINT {Program.Q(ShortName(fk.Name))} FOREIGN KEY " +
                $"({JoinColumns(fk.Columns)}) REFERENCES " +
                $"{Program.Q(fk.TargetSchema)}.{Program.Q(fk.TargetTable)} " +
                $"({JoinColumns(fk.TargetColumns)});";

            await ExecuteAsync(pg, ddl, ct);
        }
    }

    private static string JoinColumns(IEnumerable<string> columns) =>
        string.Join(", ", columns.Select(Program.Q));

    private static string ShortName(string name) =>
        name.Length <= 63 ? name : name[..54] + "_" +
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();

    private static async Task ExecuteAsync(
        NpgsqlConnection pg, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, pg);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal static class DataLoader
{
    private const string TargetSchema = "public";
    internal static async Task<long> CopyTableAsync(
    SqlConnection sql,
    NpgsqlConnection pg,
    TableMetadata table,
    CancellationToken ct)
    {
        string sourceColumns = string.Join(", ",
            table.Columns.Select(c => Program.Q(c.SourceName)));

        string sourceSql =
            $"SELECT {sourceColumns} FROM " +
            $"{Program.Q(table.SourceSchema)}.{Program.Q(table.SourceName)}";

        string targetColumns = string.Join(", ",
            table.Columns.Select(c => Program.Q(c.TargetName)));

        string copySql =
            $"COPY {Program.Q("public")}.{Program.Q(table.TargetName)} " +
            $"({targetColumns}) FROM STDIN (FORMAT BINARY)";

        await using var transaction =
            await pg.BeginTransactionAsync(ct);

        try
        {
            long rows = 0;

            await using (var cmd = new SqlCommand(sourceSql, sql))
            {
                cmd.CommandTimeout = 0;

                await using (var reader = await cmd.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess, ct))
                {
                    // IMPORTANT: The importer must be disposed before
                    // attempting to commit the PostgreSQL transaction.
                    await using (var importer =
                        await pg.BeginBinaryImportAsync(copySql, ct))
                    {
                        while (await reader.ReadAsync(ct))
                        {
                            await importer.StartRowAsync(ct);

                            for (int i = 0; i < table.Columns.Count; i++)
                            {
                                if (await reader.IsDBNullAsync(i, ct))
                                {
                                    await importer.WriteNullAsync(ct);
                                    continue;
                                }

                                object value = reader.GetValue(i);
                                var column = table.Columns[i];

                                if (column.SourceType == "tinyint")
                                {
                                    value = Convert.ToInt16(
                                        value, CultureInfo.InvariantCulture);
                                }

                                await importer.WriteAsync(
                                    value,
                                    TypeMapper.ToNpgsqlDbType(column),
                                    ct);
                            }

                            rows++;
                        }

                        // Finish the PostgreSQL COPY protocol.
                        await importer.CompleteAsync(ct);
                    } // Importer disposed here.

                } // SQL Server reader disposed here.
            } // SQL command disposed here.

            // COPY has finished and its importer has been disposed.
            await transaction.CommitAsync(ct);

            return rows;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original migration exception.
            }

            throw;
        }
    }
}

internal sealed class TableMetadata
{
    public string SourceSchema { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public long ImportedRows { get; set; }
    public List<ColumnMetadata> Columns { get; } = new();
    public IndexMetadata? PrimaryKey { get; set; }
    public List<IndexMetadata> Indexes { get; } = new();
    public List<ForeignKeyMetadata> ForeignKeys { get; } = new();
}

internal sealed class ColumnMetadata
{
    public int Ordinal { get; set; }
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string TargetType { get; set; } = "";
    public short MaxLength { get; set; }
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public bool Nullable { get; set; }
    public bool Identity { get; set; }
    public bool Computed { get; set; }
}

internal sealed class IndexMetadata
{
    public string Name { get; set; } = "";
    public bool IsPrimary { get; set; }
    public bool IsUnique { get; set; }
    public List<string> Columns { get; } = new();
}

internal sealed class ForeignKeyMetadata
{
    public string Name { get; set; } = "";
    public string TargetSchema { get; set; } = "";
    public string TargetTable { get; set; } = "";
    public List<string> Columns { get; } = new();
    public List<string> TargetColumns { get; } = new();
}
