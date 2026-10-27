param([string]$ComposeFile = (Join-Path $PSScriptRoot '../infra/docker-compose.yml'))
$ErrorActionPreference = 'Stop'
function Get-Counts {
    $query = 'SET NOCOUNT ON; SELECT t.name,SUM(p.rows) AS TableRows FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN(0,1) GROUP BY t.name ORDER BY t.name;'
    $output = $query | docker compose -f $ComposeFile exec -T sqlserver bash -c 'export SQLCMDPASSWORD=$MSSQL_SA_PASSWORD; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d HTBAM -W -h -1 -b'
    if ($LASTEXITCODE -ne 0) { throw "Cannot read table counts: $output" }
    return ($output -join "`n").Trim()
}
$before = Get-Counts
docker compose -f $ComposeFile run --rm --no-deps migrate
if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
docker compose -f $ComposeFile run --rm --no-deps seed
if ($LASTEXITCODE -ne 0) { throw 'Seed failed.' }
$after = Get-Counts
if ($before -ne $after) { throw "Table counts changed on replay.`nBefore:`n$before`nAfter:`n$after" }
Write-Output 'MIGRATION_AND_SEED_REPLAY_OK: every table count is unchanged.'
Write-Output $after
