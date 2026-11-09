param(
    [ValidateSet('Install','Remove')][string]$Action,
    [Parameter(Mandatory=$true)][string]$ResultFile,
    [string]$ExpectedPort
)
$ErrorActionPreference='Stop'
$created=$false
$printerName='Canon LBP-1120 x64'
try {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required to change the Windows printer queue.' }
    $printer=Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    if ($Action -eq 'Install') {
        if ($printer) { throw 'A printer named Canon LBP-1120 x64 already exists. Remove the previous installation before installing this package.' }
        $status=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8631/' -TimeoutSec 5
        if (!$status.Content.Contains('CAPT bridge is running.') -or $status.Content.Contains('DRY RUN')) { throw 'The CAPT bridge is not ready for printing.' }
        Add-Printer -Name $printerName -IppURL 'http://127.0.0.1:8631/ipp/print' -ErrorAction Stop
        $created=$true
        Set-PrintConfiguration -PrinterName $printerName -PaperSize A4 -Color $false -DuplexingMode OneSided -ErrorAction Stop
        $printer=Get-Printer -Name $printerName -ErrorAction Stop
        if ($printer.DriverName -ne 'Microsoft IPP Class Driver') { throw 'Windows did not assign Microsoft IPP Class Driver.' }
        @{Success=$true;Port=$printer.PortName} | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding UTF8
    } else {
        if ($printer) {
            if ($printer.DriverName -ne 'Microsoft IPP Class Driver' -or !$ExpectedPort -or $printer.PortName -ne $ExpectedPort) { throw 'Printer settings have changed. Removal stopped to protect a different printer.' }
            if (Get-PrintJob -PrinterName $printerName) { throw 'The printer queue is not empty. Finish or cancel its jobs, then run uninstall again.' }
            Remove-Printer -Name $printerName -ErrorAction Stop
            if (!(Get-Printer | Where-Object PortName -eq $ExpectedPort)) { Remove-PrinterPort -Name $ExpectedPort -ErrorAction SilentlyContinue }
        }
        @{Success=$true} | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding UTF8
    }
} catch {
    if ($created) { Remove-Printer -Name $printerName -ErrorAction SilentlyContinue }
    @{Success=$false;Error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding UTF8
    exit 1
}
