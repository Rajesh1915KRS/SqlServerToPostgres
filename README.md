# SQL Server to PostgreSQL Migration Tool

This is a high-performance, command-line C# utility designed to migrate database schemas and data from Microsoft SQL Server to PostgreSQL. 

The tool reads the source schema, automatically maps SQL Server data types to their PostgreSQL equivalents, provisions the tables, and streams the data directly using PostgreSQL's high-speed binary `COPY` protocol.

## Features

* **High-Speed Data Streaming:** Bypasses standard `INSERT` statements by piping a SQL Server `SqlDataReader` directly into an `NpgsqlBinaryImporter`.
* **Automatic Schema Translation:** Reads tables, columns, primary keys, indexes, and foreign keys from SQL Server system catalogs (`sys.tables`, `sys.columns`, etc.) and converts them to PostgreSQL DDL.
* **Automatic Type Mapping:** Safely translates types like `datetime2` to `timestamp`, `uniqueidentifier` to `uuid`, `tinyint` to `smallint`, etc.
* **Constraint Deferment:** Creates primary keys, indexes, and foreign keys *after* the data is fully loaded to maximize bulk insert speeds.
* **Integrity Validation:** Automatically compares the row count between the source and target tables at the end of the migration to ensure data integrity.
* **Safe Reset Mechanism:** Includes a command-line safeguard to prevent accidental target database wiping.

## Prerequisites

* [.NET SDK](https://dotnet.microsoft.com/download) (Version 6.0 or higher recommended)
* Access to the source Microsoft SQL Server database.
* Access to the target PostgreSQL database.
* Required NuGet packages:
  * `Microsoft.Data.SqlClient`
  * `Npgsql`

## Configuration

Before running the tool, you must configure your database connection strings. Open `Program.cs` and locate the connection string constants:

```csharp
private const string MSSQL_CONNECTION_STRING = "Server=...;Database=...;User ID=...;Password=...;";
private const string POSTGRES_CONNECTION_STRING = "Host=...;Port=5432;Database=...;Username=...;Password=...;";
```

*Update these strings with your actual database credentials.*

*(Note: For production use, it is highly recommended to uncomment the `Environment.GetEnvironmentVariable` lines in the code to securely inject these connection strings rather than hardcoding them.)*

## Usage

To run the migration in standard mode (which will fail if target tables already exist):

```bash
dotnet run
```

### Resetting the Target Database

If you want to clear out existing tables in the target `public` schema before migrating, you must explicitly pass the reset and confirm flags. **Use this with caution, as it uses `CASCADE` and will permanently delete data.**

```bash
dotnet run --reset --confirm-reset
```

## How It Works (Phases)

1. **Metadata Reading:** The program queries SQL Server's `sys` schema to discover all user tables, columns, types, primary keys, indexes, and foreign keys.
2. **Validation:** Checks for potential issues like lowercase name collisions (since Postgres is case-sensitive and the tool normalizes names to lowercase), missing primary keys, or empty tables.
3. **Target Teardown (Optional):** If the reset flags are provided, drops the target tables in PostgreSQL.
4. **Base Schema Creation:** Executes `CREATE TABLE` commands in Postgres.
5. **Data Streaming:** Opens a transaction and streams data table-by-table.
6. **Constraint Creation:** Creates `PRIMARY KEY`, `INDEX`, and `FOREIGN KEY` constraints on the newly populated Postgres tables.
7. **Validation:** Executes a `COUNT(*)` on both databases and throws an error if they do not match.

## Limitations

* **Computed Columns:** The tool currently throws an exception if it detects a computed column, as these must be explicitly translated or materialized.
* **`sql_variant`:** This legacy SQL Server type is not supported and will throw an error.
* **Programmability:** This tool migrates *tables and data only*. It does not migrate Views, Stored Procedures, Functions, or Triggers.