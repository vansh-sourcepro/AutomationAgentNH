<#
.SYNOPSIS
    Installs the NewHorizon Automation Agent as a Windows Service.

.DESCRIPTION
    Publishes the Worker, copies it to the install directory, creates the service with automatic
    restart on failure, and applies the database schema.

    Secrets are NOT written by this script. appsettings.json ships placeholders; set the real
    connection string, ERP client secret and inbound API key as machine environment variables (see
    -ShowSecretHelp) or through a DPAPI-protected store before starting the service.

.EXAMPLE
    .\install.ps1 -InstallPath 'C:\NewHorizon\AutomationAgent' -SqlServer 'PC67\SQLEXPRESS2025' -Database 'PGTPL_AutomationAgent'
#>
[CmdletBinding()]
param(
    [string] $InstallPath = 'C:\NewHorizon\AutomationAgent',
    [string] $ServiceName = 'NewHorizonAutomationAgent',
    [string] $DisplayName = 'NewHorizon Automation Agent',
    [string] $SqlServer,
    [string] $Database = 'PGTPL_AutomationAgent',
    [switch] $SkipMigrations,
    [switch] $ShowSecretHelp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\NewHorizon.Automation.Worker'

if ($ShowSecretHelp) {
    @"
Set these as MACHINE environment variables, then restart the service:

  [Environment]::SetEnvironmentVariable('AutomationAgent__Database__ConnectionString', '<value>', 'Machine')
  [Environment]::SetEnvironmentVariable('AutomationAgent__ErpApi__BaseUrl',            '<value>', 'Machine')
  [Environment]::SetEnvironmentVariable('AutomationAgent__ErpApi__ClientSecret',       '<value>', 'Machine')
  [Environment]::SetEnvironmentVariable('AutomationAgent__Host__InboundApiKey',        '<value>', 'Machine')

Use a dedicated least-privilege SQL login - db_datareader, db_datawriter and EXECUTE on the
automation database only, and nothing at all on the ERP database. Never 'sa'.
"@
    return
}

if (-not (Test-Path $project)) {
    throw "Worker project not found at '$project'. Run this script from the repository's deploy folder."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists. Use update.ps1 to deploy a new version."
}

Write-Host "Publishing the agent..." -ForegroundColor Cyan
dotnet publish $project -c Release -o $InstallPath --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if (-not $SkipMigrations) {
    if (-not $SqlServer) {
        throw "-SqlServer is required unless -SkipMigrations is given."
    }

    Write-Host "Applying the database schema to [$Database] on [$SqlServer]..." -ForegroundColor Cyan

    # The full idempotent script, so the server needs no .NET SDK and no dotnet-ef tool.
    $script = Join-Path $PSScriptRoot 'sql\001_Schema.sql'
    & sqlcmd -S $SqlServer -d $Database -b -i $script
    if ($LASTEXITCODE -ne 0) { throw "Schema migration failed with exit code $LASTEXITCODE." }

    $seed = Join-Path $PSScriptRoot 'sql\002_SeedAutomationConfig.sql'
    & sqlcmd -S $SqlServer -d $Database -b -i $seed
    if ($LASTEXITCODE -ne 0) { throw "Config seed failed with exit code $LASTEXITCODE." }
}

$binary = Join-Path $InstallPath 'NewHorizon.Automation.Worker.exe'
if (-not (Test-Path $binary)) { throw "Published binary not found at '$binary'." }

Write-Host "Creating service '$ServiceName'..." -ForegroundColor Cyan
& sc.exe create $ServiceName binPath= "`"$binary`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }

& sc.exe description $ServiceName "Runs NewHorizon ERP automation cycles (OAF to SJO, then AutoShop sequencing)." | Out-Null

# Restart on failure, three times, then leave it down for an operator. Resume picks each job up at
# its last completed operation, so a restart never repeats ERP work.
& sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null

Write-Host ""
Write-Host "Installed to $InstallPath" -ForegroundColor Green
Write-Host "The service is NOT started: set the secrets first (run with -ShowSecretHelp), then:" -ForegroundColor Yellow
Write-Host "  Start-Service $ServiceName"
Write-Host "  curl http://localhost:5080/api/automation/health"
