$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator to remove the printer.' }
$printer=Get-Printer -Name 'Canon LBP-1120 x64' -ErrorAction SilentlyContinue
if ($printer) {
    if ($printer.DriverName -ne 'Microsoft IPP Class Driver') { throw 'The printer does not use the expected driver; removal stopped.' }
    $printerPort=$printer.PortName
    Remove-Printer -Name $printer.Name
    $stillUsed=Get-Printer | Where-Object PortName -eq $printerPort
    if (!$stillUsed) { Remove-PrinterPort -Name $printerPort -ErrorAction SilentlyContinue }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CanonLBP1120CaptBridge' -ErrorAction SilentlyContinue
$bridgePath=Join-Path $PSScriptRoot 'capt-ipp-bridge.exe'
Get-CimInstance Win32_Process -Filter "Name='capt-ipp-bridge.exe'" | Where-Object ExecutablePath -eq $bridgePath | ForEach-Object {
    Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
}
Write-Host 'Canon LBP-1120 system queue and automatic startup removed. Program files and logs were kept.'
