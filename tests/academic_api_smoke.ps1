param([int]$Port = 5511)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $workspace '.runtime-fix-backup-academic-review'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$settings = Get-Content -LiteralPath (Join-Path $workspace 'services/backend/src/HTBAM.Api/appsettings.json') -Raw | ConvertFrom-Json
$testDatabase = 'HTBAM_AcademicTests_' + [guid]::NewGuid().ToString('N')
if ($testDatabase -notmatch '^HTBAM_AcademicTests_[a-f0-9]{32}$') { throw 'Invalid isolated database name.' }
$masterBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($settings.ConnectionStrings.SqlServer)
$databaseBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($settings.ConnectionStrings.SqlServer)
$databaseBuilder['Initial Catalog'] = $testDatabase
$env:SQLCMDPASSWORD = $masterBuilder.Password
function Invoke-SmokeSql {
    param([string]$Database, [string]$Query, [string]$InputFile)
    if ($InputFile) { $Query = Get-Content -LiteralPath $InputFile -Raw -Encoding UTF8 }
    $Query | docker compose -f (Join-Path $workspace 'infra/docker-compose.yml') exec -T sqlserver bash -c ('export SQLCMDPASSWORD=$MSSQL_SA_PASSWORD; /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d ' + $Database)
    if ($LASTEXITCODE -ne 0) { throw 'SQL setup/cleanup failed for the isolated smoke database.' }
}
$backendProcess = $null
$created = $false
try {
    Invoke-SmokeSql -Database 'master' -Query ('CREATE DATABASE [' + $testDatabase + ']')
    $created = $true
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ConnectionStrings__SqlServer = $databaseBuilder.ConnectionString
    $env:ObjectStorage__EnsureBucketOnStartup = 'false'
    $env:Seed__Development = 'true'
    $dll = Join-Path $workspace 'services/backend/src/HTBAM.Api/bin/Debug/net9.0/HTBAM.Api.dll'
    Push-Location (Join-Path $workspace 'services/backend/src/HTBAM.Api')
    try {
        & dotnet $dll --migrate
        if ($LASTEXITCODE -ne 0) { throw 'Smoke migration failed.' }
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        & dotnet $dll --seed
        if ($LASTEXITCODE -ne 0) { throw 'Production system seed failed.' }
        Invoke-SmokeSql -Database $testDatabase -Query "IF EXISTS(SELECT 1 FROM Users) OR EXISTS(SELECT 1 FROM AcademicTerms WHERE IsDevelopment=1) THROW 51000,'Production created demo data',1;"
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:Seed__Development = 'false'
        & dotnet $dll --seed
        if ($LASTEXITCODE -ne 0) { throw 'Disabled development seed failed.' }
        Invoke-SmokeSql -Database $testDatabase -Query "IF EXISTS(SELECT 1 FROM Users) THROW 51000,'Disabled seed created accounts',1;"
        $env:Seed__Development = 'true'
        & dotnet $dll --seed
        if ($LASTEXITCODE -ne 0) { throw 'Smoke seed failed.' }
    } finally { Pop-Location }
    $backendProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @($dll,'--urls',('http://127.0.0.1:' + $Port)) -WorkingDirectory (Join-Path $workspace 'services/backend/src/HTBAM.Api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $scratch 'api-smoke-backend.log') -RedirectStandardError (Join-Path $scratch 'api-smoke-backend-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $Port + '/health') -UseBasicParsing -TimeoutSec 1
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        if ($backendProcess.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Isolated backend did not become ready; inspect scratch API logs.' }
    & node (Join-Path $PSScriptRoot 'academic_api_smoke.js') ('http://127.0.0.1:' + $Port)
    if ($LASTEXITCODE -ne 0) { throw 'Academic API smoke checks failed.' }
    & node (Join-Path $PSScriptRoot 'class_section_import_api_smoke.cjs') ('http://127.0.0.1:' + $Port)
    if ($LASTEXITCODE -ne 0) { throw 'Class-section import smoke checks failed.' }
} finally {
    if ($null -ne $backendProcess -and -not $backendProcess.HasExited) {
        Stop-Process -Id $backendProcess.Id -Force
        $backendProcess.WaitForExit()
    }
    if ($created) {
        Invoke-SmokeSql -Database 'master' -Query ('ALTER DATABASE [' + $testDatabase + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [' + $testDatabase + '];')
    }
    Remove-Item Env:ConnectionStrings__SqlServer -ErrorAction SilentlyContinue
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
}
