param([switch]$PrintOnly)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $projectDir 'tools\w64devkit\bin\g++.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Portable w64devkit compiler not found in tools.' }
$sourceDir = Join-Path $projectDir 'upstream'
$config = Get-Content -LiteralPath (Join-Path $sourceDir 'libcapt\Config.hpp.in') -Raw
$config = $config.Replace('@PROJECT_VERSION@','1.0.0').Replace('@PROJECT_VERSION_MAJOR@','1').Replace('@PROJECT_VERSION_MINOR@','0').Replace('@PROJECT_VERSION_PATCH@','0').Replace('@HAVE_BYTESWAP@','1')
Set-Content -LiteralPath (Join-Path $sourceDir 'libcapt\Config.hpp') -Value $config -Encoding Ascii
$sources = @(Get-ChildItem -LiteralPath (Join-Path $sourceDir 'libcapt') -Recurse -Filter '*.cpp' | ForEach-Object FullName)
$includes = @('-I', $sourceDir, '-I', (Join-Path $sourceDir 'libcapt'))
if (!$PrintOnly) {
    & $compiler '-std=c++23' '-O2' '-Wall' '-Wextra' '-static' @includes (Join-Path $projectDir 'capt-status.cpp') @sources '-lsetupapi' '-o' (Join-Path $projectDir 'capt-status.exe')
    if ($LASTEXITCODE -ne 0) { throw 'C++ compilation failed.' }
}
& $compiler '-std=c++23' '-O2' '-Wall' '-Wextra' '-static' '-municode' @includes (Join-Path $projectDir 'capt-print.cpp') @sources '-lsetupapi' '-o' (Join-Path $projectDir 'capt-print.exe')
if ($LASTEXITCODE -ne 0) { throw 'Print program compilation failed.' }
