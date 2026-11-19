param(
    [ValidateSet("prod", "inline", "local", "header", "status")]
    [string]$Target
)

$devJsonPath = "d:\Automation\AutomationAgentNH\src\NewHorizon.Automation.Worker\appsettings.Development.json"
$appJsonPath = "d:\Automation\AutomationAgentNH\src\NewHorizon.Automation.Worker\appsettings.json"

function Show-Status {
    $content = Get-Content $devJsonPath -Raw
    if ($content -match 'BaseUrl":\s*"http://192\.168\.0\.189:8011/' -and $content -notmatch '//\s*"BaseUrl":\s*"http://192\.168\.0\.189:8011/') {
        Write-Host "Current Active Environment: [PROD ERP - INLINE RATE STRUCTURE] (192.168.0.189:8011 / PC43)" -ForegroundColor Green
    } elseif ($content -match 'BaseUrl":\s*"http://localhost:4400/' -and $content -notmatch '//\s*"BaseUrl":\s*"http://localhost:4400/') {
        Write-Host "Current Active Environment: [LOCAL ERP - HEADER RATE STRUCTURE] (localhost:4400 / PC87)" -ForegroundColor Cyan
    } else {
        Write-Host "Current Active Environment: Custom or Mixed configuration." -ForegroundColor Yellow
    }
}

function Set-Prod {
    $devJson = @'
{
  "AutomationAgent": {
    "Host": {
      "InboundApiKey": "dev-local-key",
      "BindToLoopbackOnly": false
    },
    "Cors": {
      "AllowedOrigins": [ "http://localhost:4200", "http://localhost:4400", "http://192.168.0.189:8014" ]
    },
    "Serilog": {
      "MinimumLevel": "Debug"
    },

    // =========================================================================================
    //  OPTION 1: PROD ERP  --->  INLINE RATE STRUCTURE (PC43 / 192.168.0.189)
    //  [ACTIVE]
    // =========================================================================================
    "ErpApi": {
      "BaseUrl": "http://192.168.0.189:8011/",
      "UserName": "su",
      "Password": "10203040506070",
      "LoginConnectionString": "Server=PC43\\SQLEXPRESS;Database=NH1_IMCO_A_Dixit_10102026;uid=sa;pwd=;Connection TimeOut= 3000;",
      "CompanyId": 1
    },
    "DcToInvoice": {
      "SiteId": 1,
      "SiteCode": "IMC",
      "CustomerCode": "",
      // RateStructureMode: "Auto" (respects DB flag), "Header" (forces header), "Inline" (forces line-level)
      "RateStructureMode": "Auto"
    },
    "SoAllocationToDC": {
      "SiteId": 1,
      "SiteCode": "IMC",
      "CustomerCode": ""
    }

    // =========================================================================================
    //  OPTION 2: LOCAL ERP  --->  HEADER RATE STRUCTURE (PC87 / localhost:4400)
    //  [INACTIVE - UNCOMMENT TO ACTIVATE]
    // =========================================================================================
    /*
    "ErpApi": {
      "BaseUrl": "http://localhost:4400/",
      "UserName": "super",
      "Password": "super",
      "LoginConnectionString": "Server=PC87;Database=Main_4Automation_A_Fab_2511;uid=sa;pwd=;Connection TimeOut=3000;",
      "CompanyId": 1
    },
    "DcToInvoice": {
      "SiteId": 1,
      "SiteCode": "NF1",
      "CustomerCode": "",
      "RateStructureMode": "Header"
    },
    "SoAllocationToDC": {
      "SiteId": 1,
      "SiteCode": "NF1",
      "CustomerCode": ""
    }
    */
  }
}
'@
    [System.IO.File]::WriteAllText($devJsonPath, $devJson, [System.Text.Encoding]::UTF8)
    Write-Host "`n Successfully switched to: PROD ERP (Inline Rate Structure - PC43 / 192.168.0.189)" -ForegroundColor Green
    Write-Host "   SiteCode: IMC | RateStructureMode: Auto (Inline)" -ForegroundColor Gray
}

function Set-Local {
    $devJson = @'
{
  "AutomationAgent": {
    "Host": {
      "InboundApiKey": "dev-local-key",
      "BindToLoopbackOnly": false
    },
    "Cors": {
      "AllowedOrigins": [ "http://localhost:4200", "http://localhost:4400", "http://192.168.0.189:8014" ]
    },
    "Serilog": {
      "MinimumLevel": "Debug"
    },

    // =========================================================================================
    //  OPTION 1: PROD ERP  --->  INLINE RATE STRUCTURE (PC43 / 192.168.0.189)
    //  [INACTIVE - UNCOMMENT TO ACTIVATE]
    // =========================================================================================
    /*
    "ErpApi": {
      "BaseUrl": "http://192.168.0.189:8011/",
      "UserName": "su",
      "Password": "10203040506070",
      "LoginConnectionString": "Server=PC43\\SQLEXPRESS;Database=NH1_IMCO_A_Dixit_10102026;uid=sa;pwd=;Connection TimeOut= 3000;",
      "CompanyId": 1
    },
    "DcToInvoice": {
      "SiteId": 1,
      "SiteCode": "IMC",
      "CustomerCode": "",
      "RateStructureMode": "Auto"
    },
    "SoAllocationToDC": {
      "SiteId": 1,
      "SiteCode": "IMC",
      "CustomerCode": ""
    }
    */

    // =========================================================================================
    //  OPTION 2: LOCAL ERP  --->  HEADER RATE STRUCTURE (PC87 / localhost:4400)
    //  [ACTIVE]
    // =========================================================================================
    "ErpApi": {
      "BaseUrl": "http://localhost:4400/",
      "UserName": "super",
      "Password": "super",
      "LoginConnectionString": "Server=PC87;Database=Main_4Automation_A_Fab_2511;uid=sa;pwd=;Connection TimeOut=3000;",
      "CompanyId": 1
    },
    "DcToInvoice": {
      "SiteId": 1,
      "SiteCode": "NF1",
      "CustomerCode": "",
      // RateStructureMode: "Auto" (respects DB flag), "Header" (forces header), "Inline" (forces line-level)
      "RateStructureMode": "Auto"
    },
    "SoAllocationToDC": {
      "SiteId": 1,
      "SiteCode": "NF1",
      "CustomerCode": ""
    }
  }
}
'@
    [System.IO.File]::WriteAllText($devJsonPath, $devJson, [System.Text.Encoding]::UTF8)
    Write-Host "`n Successfully switched to: LOCAL ERP (Header Rate Structure - PC87 / localhost:4400)" -ForegroundColor Cyan
    Write-Host "   SiteCode: NF1 | RateStructureMode: Header" -ForegroundColor Gray
}

if (-not $Target) {
    Write-Host "========================================================================" -ForegroundColor Magenta
    Write-Host "          NEW HORIZON AUTOMATION - ENVIRONMENT SWITCHER" -ForegroundColor Yellow
    Write-Host "========================================================================" -ForegroundColor Magenta
    Show-Status
    Write-Host "------------------------------------------------------------------------"
    Write-Host " Select an option:"
    Write-Host "   [1] Switch to PROD (Inline Rate Structure - PC43 / 192.168.0.189:8011)" -ForegroundColor Green
    Write-Host "   [2] Switch to LOCAL (Header Rate Structure - PC87 / localhost:4400)" -ForegroundColor Cyan
    Write-Host "   [3] Exit"
    Write-Host "------------------------------------------------------------------------"
    $choice = Read-Host "Enter Choice (1, 2, or 3)"
    switch ($choice) {
        "1" { Set-Prod }
        "2" { Set-Local }
        default { Write-Host "Exiting without changes." }
    }
} else {
    switch ($Target.ToLower()) {
        { $_ -in "prod", "inline" } { Set-Prod }
        { $_ -in "local", "header" } { Set-Local }
        "status" { Show-Status }
    }
}
