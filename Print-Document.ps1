param([string]$Document)
$ErrorActionPreference='Stop'
$jobRaster=$null
$logPath=$null
try {
    if ([string]::IsNullOrWhiteSpace($Document)) {
        Add-Type -AssemblyName System.Windows.Forms
        $picker=New-Object System.Windows.Forms.OpenFileDialog
        $picker.Title='Canon LBP-1120: choose a document to print'
        $picker.Filter='PDF and images|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|PDF|*.pdf|Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff'
        try {
            if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
            $Document=$picker.FileName
        } finally { $picker.Dispose() }
    }
    $documentPath=(Resolve-Path -LiteralPath $Document).Path
    $extension=[System.IO.Path]::GetExtension($documentPath).ToLowerInvariant()
    if ($extension -notin @('.pdf','.png','.jpg','.jpeg','.bmp','.tif','.tiff')) { throw 'Supported formats: PDF, PNG, JPEG, BMP, TIFF (first image frame).' }
    $logDir=Join-Path $PSScriptRoot 'logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $logPath=Join-Path $logDir ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N')+'.txt')
    Write-Host 'Canon LBP-1120 direct print: A4, 600 dpi, one copy.'
    Write-Host 'Landscape pages are fitted onto portrait A4. Maximum 100 pages.'
    $jobRaster=Join-Path ([System.IO.Path]::GetTempPath()) ('capt1120-'+[guid]::NewGuid().ToString('N')+'.pbm')
    & (Join-Path $PSScriptRoot 'Convert-ToPbm.ps1') -InputFile $documentPath -OutputFile $jobRaster
    $savedPreference=$ErrorActionPreference
    $ErrorActionPreference='Continue'
    try {
        & (Join-Path $PSScriptRoot 'capt-print.exe') --print-pbm $jobRaster 2>&1 | ForEach-Object {
            $line=$_.ToString()
            Write-Host $line
            Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8 -ErrorAction Stop
        }
        $printResult=$LASTEXITCODE
    } finally { $ErrorActionPreference=$savedPreference }
    if ($printResult -ne 0) { throw ('Printing failed. Log: '+$logPath) }
    Write-Host 'The printer reports that the job completed.'
} catch {
    Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($jobRaster -and (Test-Path -LiteralPath $jobRaster)) { Remove-Item -LiteralPath $jobRaster -Force }
}
