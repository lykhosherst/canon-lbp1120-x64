$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Drawing.dll (('/out:')+(Join-Path $PSScriptRoot 'capt-ipp-bridge.exe')) (Join-Path $PSScriptRoot 'IppBridge.cs') (Join-Path $PSScriptRoot 'PwgRaster.cs') (Join-Path $PSScriptRoot 'RasterTools.cs')
if($LASTEXITCODE -ne 0){throw 'IPP bridge build failed'}
