<#
.SYNOPSIS
    Deploys a new version of the agent over an existing installation.

.DESCRIPTION
    Stop -> deploy -> migrate -> start, which is the order that makes an update safe:

      - Stopping first lets in-flight jobs reach a checkpoint boundary. Anything still Running when
        the process dies is re-queued by the orphan sweep on the next start and resumes at its
        first operation that is not Completed, so no ERP document is created twice.
      - Migrating after the binaries are in place keeps the schema and the code that reads it in
        step, and the script is idempotent so a re-run is harmless.

.EXAMPLE
    .\update.ps1 -SqlServer 'PC67\SQLEXPRESS2025' -Database 'PGTPL_AutomationAgent'
#>
[CmdletBinding()]
param(
    [string] $InstallPath = 'C:\NewHorizon\AutomationAgent',
    [string] $ServiceName = 'NewHorizonAutomationAgent',
    [string] $SqlServer,
    [string] $Database = 'PGTPL_AutomationAgent',
    [switch] $SkipMigrations,
    [int] $StopTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\NewHorizon.Automation.Worker'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    throw "Service '$ServiceName' is not installed. Use install.ps1 first."
}

if ($service.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName (allowing in-flight work to checkpoint)..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName

    try {
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($StopTimeoutSeconds))
    }
    catch [System.ServiceProcess.TimeoutException] {
        throw "The service did not stop within $StopTimeoutSeconds seconds. Investigate before deploying; killing it mid-operation is safe for data but leaves jobs to be recovered."
    }
}

# Keep the previous build so a bad deploy can be rolled back by swapping the folder back.
if (Test-Path $InstallPath) {
    $backup = "$InstallPath.backup"
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Copy-Item $InstallPath $backup -Recurse
    Write-Host "Previous build kept at $backup" -ForegroundColor DarkGray
}

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish $project -c Release -o $InstallPath --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

if (-not $SkipMigrations) {
    if (-not $SqlServer) { throw "-SqlServer is required unless -SkipMigrations is given." }

    Write-Host "Applying migrations..." -ForegroundColor Cyan
    & sqlcmd -S $SqlServer -d $Database -b -i (Join-Path $PSScriptRoot 'sql\001_Schema.sql')
    if ($LASTEXITCODE -ne 0) { throw "Schema migration failed with exit code $LASTEXITCODE." }
}

Write-Host "Starting $ServiceName..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

Start-Sleep -Seconds 5

try {
    $health = Invoke-RestMethod 'http://localhost:5080/api/automation/health' -TimeoutSec 20
    Write-Host "Health: $($health.status) - database=$($health.checks.database), erpApi=$($health.checks.erpApi)" -ForegroundColor Green
}
catch {
    Write-Warning "The service started but health could not be read: $($_.Exception.Message)"
    Write-Warning "Check the log directory and the AutomationAgent environment variables."
}
