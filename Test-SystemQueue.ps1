param([ValidateRange(1,3)][int]$Pages=2,[switch]$Physical)
$ErrorActionPreference='Stop'
$status=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8631/' -TimeoutSec 5
$isDryRun=$status.Content.Contains('DRY RUN')
if (!$Physical -and !$isDryRun) { throw 'Bridge must be in dry-run mode. Use -Physical explicitly to print paper.' }
if ($Physical -and $isDryRun) { throw 'Bridge is in dry-run mode: a physical test would not print.' }
Add-Type -AssemblyName System.Drawing
$document=New-Object System.Drawing.Printing.PrintDocument
$document.PrinterSettings.PrinterName='Canon LBP-1120 x64'
if (!$document.PrinterSettings.IsValid) { throw 'System printer not installed.' }
$document.DocumentName='Canon system queue test - '+$Pages+' pages'
$document.PrintController=New-Object System.Drawing.Printing.StandardPrintController
$a4=$document.PrinterSettings.PaperSizes | Where-Object RawKind -eq 9 | Select-Object -First 1
if ($a4) { $document.DefaultPageSettings.PaperSize=$a4 }
$script:queueTestPage=0
$handler=[System.Drawing.Printing.PrintPageEventHandler]{
    param($sender,$e)
    $script:queueTestPage++
    $font=New-Object System.Drawing.Font('Arial',22)
    $small=New-Object System.Drawing.Font('Arial',12)
    $pen=New-Object System.Drawing.Pen([System.Drawing.Color]::Black,1)
    try {
        $e.Graphics.DrawString('Canon LBP-1120 x64',$font,[System.Drawing.Brushes]::Black,60,70)
        $e.Graphics.DrawString(('Windows system queue - page {0} of {1}' -f $script:queueTestPage,$Pages),$small,[System.Drawing.Brushes]::Black,60,125)
        $e.Graphics.DrawString('Print from Windows applications through Microsoft IPP Class Driver.',$small,[System.Drawing.Brushes]::Black,60,160)
        $e.Graphics.DrawString(('Test time: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),$small,[System.Drawing.Brushes]::Black,60,195)
        $e.Graphics.DrawRectangle($pen,60,250,600,150)
        $e.Graphics.DrawString(('PAGE '+$script:queueTestPage),$font,[System.Drawing.Brushes]::Black,90,290)
        $e.HasMorePages=($script:queueTestPage -lt $Pages)
    } finally { $font.Dispose();$small.Dispose();$pen.Dispose() }
}
$document.add_PrintPage($handler)
try { $document.Print() } finally { $document.remove_PrintPage($handler);$document.Dispose() }
Write-Host ('Submitted {0} page(s) to the Windows system queue. DryRun={1}' -f $Pages,$isDryRun)
