<#
.SYNOPSIS
    Stops and removes the Automation Agent Windows Service.

.DESCRIPTION
    Removes the service and, optionally, the installed binaries. The automation DATABASE is never
    touched: it holds the job history and audit trail, and dropping it is a deliberate act that
    should be done by hand.

.EXAMPLE
    .\uninstall.ps1 -RemoveFiles
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $InstallPath = 'C:\NewHorizon\AutomationAgent',
    [string] $ServiceName = 'NewHorizonAutomationAgent',
    [switch] $RemoveFiles
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed; nothing to do." -ForegroundColor Yellow
    return
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete the Windows Service')) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
        Stop-Service -Name $ServiceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
    }

    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }

    Write-Host "Service removed." -ForegroundColor Green
}

if ($RemoveFiles -and (Test-Path $InstallPath)) {
    if ($PSCmdlet.ShouldProcess($InstallPath, 'Delete the installed binaries')) {
        Remove-Item $InstallPath -Recurse -Force
        Write-Host "Removed $InstallPath" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "The automation database was left untouched - drop it by hand if that is really intended." -ForegroundColor Yellow
