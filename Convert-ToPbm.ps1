param(
    [Parameter(Mandatory=$true)][string]$InputFile,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'RasterTools.cs') -ReferencedAssemblies System.Drawing
$inputPath=(Resolve-Path -LiteralPath $InputFile).Path
$outputPath=[System.IO.Path]::GetFullPath($OutputFile)
if ($inputPath -eq $outputPath) { throw 'Input and output paths must be different.' }
$extension=[System.IO.Path]::GetExtension($inputPath).ToLowerInvariant()
$outputStream=[System.IO.File]::Open($outputPath,[System.IO.FileMode]::CreateNew)
try {
    if ($extension -ne '.pdf') {
        $image=[System.Drawing.Image]::FromFile($inputPath)
        try { [CaptRaster]::WritePbm($image,$outputStream) } finally { $image.Dispose() }
    } else {
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
        $null=[Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
        $null=[Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
        $asTask=([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]
        function Wait-Operation($Operation,[Type]$ResultType) {
            $task=$asTask.MakeGenericMethod($ResultType).Invoke($null,@($Operation))
            if (!$task.Wait(60000)) { throw 'PDF operation timed out.' }
            return $task.Result
        }
        $asAction=([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and !$_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
        })[0]
        $file=Wait-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($inputPath)) ([Windows.Storage.StorageFile])
        $pdf=Wait-Operation ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
        if ($pdf.PageCount -gt 100) { throw 'Maximum 100 PDF pages per job.' }
        for ($i=0;$i -lt $pdf.PageCount;$i++) {
            Write-Host ('Rendering page {0}/{1}' -f ($i+1),$pdf.PageCount)
            $page=$pdf.GetPage($i)
            $memory=New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
            try {
                $options=New-Object Windows.Data.Pdf.PdfPageRenderOptions
                $scale=[Math]::Min(4800.0/$page.Size.Width,6768.0/$page.Size.Height)
                $options.DestinationWidth=[uint32][Math]::Round($page.Size.Width*$scale)
                $options.DestinationHeight=[uint32][Math]::Round($page.Size.Height*$scale)
                $renderTask=$asAction.Invoke($null,@($page.RenderToStreamAsync($memory,$options)))
                if (!$renderTask.Wait(60000)) { throw 'PDF page rendering timed out.' }
                $memory.Seek(0)
                $readStream=[System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($memory)
                try {
                    $image=[System.Drawing.Image]::FromStream($readStream)
                    try { [CaptRaster]::WritePbm($image,$outputStream) } finally { $image.Dispose() }
                } finally { $readStream.Dispose() }
            } finally {
                $page.Dispose()
                $memory.Dispose()
            }
        }
    }
} finally { $outputStream.Dispose() }
Write-Host ('Raster saved: {0}' -f $outputPath)
