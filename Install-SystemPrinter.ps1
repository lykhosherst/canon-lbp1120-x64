param([switch]$NoAutoStart)
$ErrorActionPreference='Stop'
$resultPath=Join-Path $PSScriptRoot 'install-result.json'
$printerName='Canon LBP-1120 x64'
$printerUri='http://127.0.0.1:8631/ipp/print'
try {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $principal=New-Object Security.Principal.WindowsPrincipal($identity)
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer as administrator (UAC is required to add a Windows printer).' }
    $bridge=Join-Path $PSScriptRoot 'capt-ipp-bridge.exe'
    if (!(Test-Path -LiteralPath $bridge)) { throw 'Build the IPP bridge first.' }
    $status=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8631/' -TimeoutSec 5
    if (!$status.Content.Contains('CAPT bridge is running.')) { throw 'The local CAPT bridge is not running.' }
    $existing=Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    if (!$existing) {
        Add-Printer -Name $printerName -IppURL $printerUri -ErrorAction Stop
    } elseif ($existing.DriverName -ne 'Microsoft IPP Class Driver') {
        throw 'A different printer already uses this name. No settings were changed.'
    }
    $printer=Get-Printer -Name $printerName -ErrorAction Stop
    if ($printer.DriverName -ne 'Microsoft IPP Class Driver') { throw ('Unexpected printer driver: '+$printer.DriverName) }
    Set-PrintConfiguration -PrinterName $printerName -PaperSize A4 -Color $false -DuplexingMode OneSided -ErrorAction Stop
    if (!$NoAutoStart) {
        if ($status.Content.Contains('DRY RUN')) { throw 'Printer added, but automatic startup was not enabled because the bridge is in dry-run mode.' }
        New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CanonLBP1120CaptBridge' -Value ('"'+$bridge+'"') -PropertyType String -Force | Out-Null
    }
    @{Success=$true;Printer=$printer.Name;Driver=$printer.DriverName;Port=$printer.PortName;AutoStart=(!$NoAutoStart)} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host ('Installed: '+$printerName)
} catch {
    @{Success=$false;Error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Error $_.Exception.Message
    exit 1
}
