$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageRoot=Join-Path $PSScriptRoot 'setup-build'
$payloadRoot=Join-Path $packageRoot 'payload'
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
$files=@('capt-ipp-bridge.exe','capt-print.exe','capt-status.exe','Convert-ToPbm.ps1','RasterTools.cs','Print-Document.ps1','Print-LBP1120.cmd','Start-Bridge.cmd','Package-Printer.ps1')
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $payloadRoot $file) -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream\LICENSE') -Destination (Join-Path $payloadRoot 'LICENSE-libcapt.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools\w64devkit\COPYING.MinGW-w64-runtime.txt') -Destination (Join-Path $payloadRoot 'LICENSE-MinGW-runtime.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $payloadRoot 'README.txt') -Force
$manifest=@{}
Get-ChildItem -LiteralPath $payloadRoot -File | ForEach-Object {$manifest[$_.Name]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
$manifestPath=Join-Path $packageRoot 'manifest.json'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$payloadPath=Join-Path $packageRoot 'payload.zip'
if(Test-Path -LiteralPath $payloadPath){Remove-Item -LiteralPath $payloadPath}
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadRoot,$payloadPath,[System.IO.Compression.CompressionLevel]::Optimal,$false)
$outputRoot=Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$output=Join-Path $outputRoot 'Canon-LBP1120-x64-Setup.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ('/win32manifest:'+(Join-Path $PSScriptRoot 'Setup.manifest')) ('/resource:'+$payloadPath+',payload.zip') ('/resource:'+$manifestPath+',manifest.json') ('/out:'+$output) (Join-Path $PSScriptRoot 'Setup.cs')
if($LASTEXITCODE -ne 0){throw 'EXE installer compilation failed'}
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
($hash+'  Canon-LBP1120-x64-Setup.exe') | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256.txt') -Encoding Ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $outputRoot 'README.txt') -Force
Get-Item -LiteralPath $output | Select-Object FullName,Length
