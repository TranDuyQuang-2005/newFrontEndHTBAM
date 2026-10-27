using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

/// <summary>Canonical SQL scripts, one transaction and history record per script.</summary>
public static class SqlMigrationRunner
{
    public static readonly string[] Files = ["001_schema.sql", "002_seed.sql", "004_add_llm_comment_metadata.sql",
        "005_add_catalog_notes.sql", "006_add_annotated_videos.sql", "007_academic_scheduling.sql", "008_schema_integrity.sql", "009_partial_schema_relations.sql", "010_class_section_import_and_numbering.sql", "011_source_class_section_code.sql", "012_catalog_business_codes.sql", "013_lecturer_business_codes.sql", "014_course_period_and_business_sequences.sql"];

    public static string FindDirectory(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "database", "sqlserver");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Set Database__MigrationDirectory to database/sqlserver.");
    }

    public static async Task RunAsync(string connectionString, string directory, Action<string>? log = null, CancellationToken ct = default, TimeSpan? readinessTimeout = null)
    {
        // Read everything first: a missing deployment artifact must not leave a half-upgraded database.
        var scripts = Files.Select(file => (File: file, Sql: File.ReadAllText(Path.Combine(directory, file), Encoding.UTF8))).ToArray();
        var target = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(target.InitialCatalog)) throw new InvalidOperationException("A target database is required.");
        var name = target.InitialCatalog;
        await WaitForDatabaseAsync(connectionString, log, timeout: readinessTimeout, ct: ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await Execute(connection, null, "SET XACT_ABORT ON; SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF; DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'HTBAM:migrations',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=60000; IF @r<0 THROW 51000,'Migration lock timed out',1; IF OBJECT_ID('dbo.SchemaMigrations','U') IS NULL CREATE TABLE dbo.SchemaMigrations(ScriptName nvarchar(128) NOT NULL PRIMARY KEY, Sha256 char(64) NOT NULL, AppliedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());", ct);
        try
        {
            await AdoptLegacyBaselineAsync(connection, scripts[0], log, ct);
            foreach (var script in scripts)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Sql.Replace("\r\n", "\n"))));
                await using var check = new SqlCommand("SELECT Sha256 FROM dbo.SchemaMigrations WHERE ScriptName=@file", connection);
                check.Parameters.AddWithValue("@file", script.File);
                var previous = await check.ExecuteScalarAsync(ct);
                if (previous is string existing)
                {
                    if (existing != hash) throw new InvalidOperationException($"Migration checksum mismatch: {script.File}. Add a new migration; do not edit applied scripts.");
                    log?.Invoke($"Already applied: {script.File}");
                    continue;
                }
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                try
                {
                    // Legacy 005 had a hardcoded context. No script may escape the selected target database.
                    var sql = Regex.Replace(script.Sql, @"^\s*USE\s+(?:HTBAM|\[HTBAM\])\s*;?\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (Regex.IsMatch(sql, @"^\s*USE\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)) throw new InvalidOperationException("Migration cannot switch database context.");
                    foreach (var batch in Regex.Split(sql, @"^\s*GO\s*(?:--[^\r\n]*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                        if (!string.IsNullOrWhiteSpace(batch)) await Execute(connection, transaction, batch, ct);
                    await using var record = new SqlCommand("IF @@TRANCOUNT<>1 THROW 51000,'Unbalanced migration transaction',1; INSERT dbo.SchemaMigrations(ScriptName,Sha256) VALUES(@file,@hash);", connection, transaction);
                    record.Parameters.AddWithValue("@file", script.File); record.Parameters.AddWithValue("@hash", hash);
                    await record.ExecuteNonQueryAsync(ct);
                    await transaction.CommitAsync(ct);
                    log?.Invoke($"Applied successfully: {script.File}");
                }
                catch (Exception ex)
                {
                    try { await transaction.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { /* XACT_ABORT already rolled back. */ }
                    throw new InvalidOperationException($"Migration {script.File} failed and was rolled back. No success history recorded.", ex);
                }
            }
        }
        finally { await ReleaseLock(connection, "HTBAM:migrations"); }
    }

    public static async Task VerifyAsync(AppDbContext db, string directory, CancellationToken ct = default)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            foreach (var file in Files)
            {
                await using var cmd = new SqlCommand("IF OBJECT_ID('dbo.SchemaMigrations','U') IS NULL SELECT CAST(NULL AS varchar(64)); ELSE SELECT Sha256 FROM dbo.SchemaMigrations WHERE ScriptName=@file", connection);
                cmd.Parameters.AddWithValue("@file", file);
                var hash = await cmd.ExecuteScalarAsync(ct) as string;
                var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(directory, file)).Replace("\r\n", "\n"))));
                if (hash != expected) throw new InvalidOperationException($"Missing or changed migration {file}. Run the migrate service before starting backend or seed.");
            }
            // Inspect real metadata as well as history, so schema drift fails before any seed query.
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName()!;
                var identifier = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
                foreach (var property in entity.GetProperties())
                {
                    var column = property.GetColumnName(identifier)!;
                    await using var cmd = new SqlCommand("SELECT COL_LENGTH(@table,@column)", connection);
                    cmd.Parameters.AddWithValue("@table", "dbo." + table); cmd.Parameters.AddWithValue("@column", column);
                    if (await cmd.ExecuteScalarAsync(ct) is DBNull) throw new InvalidOperationException($"Schema drift: missing {table}.{column}; check migrations 001..014 before seed.");
                }
            }
        }
        finally { await connection.CloseAsync(); }
    }

    public static async Task WaitForDatabaseAsync(string connectionString, Action<string>? log = null,
        TimeSpan? timeout = null, TimeSpan? retryDelay = null, CancellationToken ct = default)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 3, Pooling = false };
        if (string.IsNullOrWhiteSpace(target.InitialCatalog)) throw new ArgumentException("Thiếu tên database đích.");
        var name = target.InitialCatalog;
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                await using var master = new SqlConnection(new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
                await master.OpenAsync(ct);
                await using var command = master.CreateCommand();
                command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'HTBAM:create-database', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=3000; IF @r<0 THROW 51000,'Database creation lock timed out',1; IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END; SELECT state_desc FROM sys.databases WHERE name=@name;";
                command.Parameters.AddWithValue("@name", name);
                command.CommandTimeout = 10;
                string? state;
                try { state = await command.ExecuteScalarAsync(ct) as string; }
                finally { await ReleaseLock(master, "HTBAM:create-database"); }
                if (state != "ONLINE") throw new DatabaseNotReadyException($"database {name}: {state ?? "chưa tồn tại"}");
                // master ONLINE is insufficient: test the actual database, including login error 4060.
                await using var probe = new SqlConnection(target.ConnectionString);
                await probe.OpenAsync(ct);
                log?.Invoke($"Database {name} ONLINE và kết nối đích sẵn sàng (lần {attempt}).");
                return;
            }
            catch (Exception ex) when (ex is DatabaseNotReadyException || ex is SqlException sql && sql.Number is -2 or 0 or 2 or 53 or 64 or 233 or 258 or 4060 or 926 or 927 or 17142 or 10054 or 10060 or 10061 or 51000)
            {
                var reason = ex is SqlException s ? $"SQL {s.Number}" : ex.Message;
                log?.Invoke($"Chờ SQL Server và database {name} ONLINE: lần {attempt}, {reason}.");
                if (DateTime.UtcNow >= until) throw new TimeoutException($"Hết thời gian chờ database {name} ONLINE/kết nối đích sau {attempt} lần thử ({reason}).", ex);
                var remaining = until - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"Hết thời gian chờ database {name} ONLINE/kết nối đích.", ex);
                var delay = retryDelay ?? TimeSpan.FromSeconds(3);
                await Task.Delay(delay < remaining ? delay : remaining, ct);
            }
        }
    }

    private sealed class DatabaseNotReadyException(string message) : Exception(message);

    private static async Task AdoptLegacyBaselineAsync(SqlConnection connection, (string File, string Sql) baseline, Action<string>? log, CancellationToken ct)
    {
        await using var state = new SqlCommand("SELECT (SELECT COUNT(*) FROM dbo.SchemaMigrations), CASE WHEN OBJECT_ID('dbo.Roles','U') IS NULL THEN 0 ELSE 1 END", connection);
        await using var reader = await state.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var historyCount = reader.GetInt32(0);
        var hasLegacySchema = reader.GetInt32(1) == 1;
        await reader.CloseAsync();
        if (historyCount != 0 || !hasLegacySchema) return;

        var tableNames = Regex.Matches(baseline.Sql, @"\bCREATE\s+TABLE\s+(?:dbo\.)?\[?(?<name>[A-Za-z0-9_]+)\]?", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tableNames.Length == 0) throw new InvalidOperationException("Cannot validate legacy baseline: 001_schema.sql has no table declarations.");
        var missing = new List<string>();
        foreach (var table in tableNames)
        {
            await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.'+@table,'U') IS NULL THEN 0 ELSE 1 END", connection);
            command.Parameters.AddWithValue("@table", table);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0) missing.Add(table);
        }
        if (missing.Count > 0)
            throw new InvalidOperationException($"Legacy database has Roles but is not a complete 001 baseline. Missing tables: {string.Join(", ", missing)}. Restore/upgrade a copy before recording migration history.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseline.Sql.Replace("\r\n", "\n"))));
        await using var adopt = new SqlCommand("INSERT dbo.SchemaMigrations(ScriptName,Sha256) VALUES(@file,@hash)", connection);
        adopt.Parameters.AddWithValue("@file", baseline.File);
        adopt.Parameters.AddWithValue("@hash", hash);
        await adopt.ExecuteNonQueryAsync(ct);
        log?.Invoke($"Adopted validated legacy baseline: {baseline.File}. Later idempotent migrations will run normally.");
    }

    private static async Task Execute(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReleaseLock(SqlConnection connection, string resource)
    {
        if (connection.State != ConnectionState.Open) return;
        await using var command = new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';", connection);
        command.Parameters.AddWithValue("@resource", resource);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
