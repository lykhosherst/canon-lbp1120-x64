# Canon LBP-1120 on Windows 11 x64

An unofficial, experimental CAPT printing bridge for the Canon LASER SHOT LBP-1120 connected over USB.

This unofficial driver and printing bridge was created with the assistance of artificial intelligence (AI).

The Windows printer queue uses the built-in **Microsoft IPP Class Driver** to send jobs to a local CAPT bridge, which communicates with the printer through `usbprint.sys`. No virtual machine, second computer, or original 32-bit Canon driver is required.

Tested on one Windows 11 x64 PC. A clean installation on another PC has not yet been verified. Windows 10 and Windows ARM64 have not been tested.

## Build from source

Requirements:

- Windows x64 with .NET Framework 4.x, including its C# compiler.
- w64devkit 2.11.0, extracted to `tools/w64devkit`, so that `tools/w64devkit/bin/g++.exe` exists.

Run these commands in PowerShell from the project directory, in order:

```powershell
.\build.ps1
.\build-bridge.ps1
.\build-setup.ps1
```

The installer is generated at `dist/Canon-LBP1120-x64-Setup.exe`.

This repository contains source code, build scripts, and documentation. Compilers, generated executables, logs, and user documents are not included. A compiler is needed to build the project, but not to run the compiled application.

## Install and print

1. Build the installer, then copy `Canon-LBP1120-x64-Setup.exe` to the target PC if needed.
2. Connect the printer over USB, turn it on, and load A4 paper.
3. Run the installer, click **Встановити** (Install), and approve the Windows UAC prompt.
4. In Word, a browser, or another Windows application, press **Ctrl+P** and select **Canon LBP-1120 x64**.

The application is installed in `%LOCALAPPDATA%\CanonLBP1120`. The bridge starts after the installing user signs in to Windows. To start it manually, run `Start-Bridge.cmd` in the installation directory. Do not move that directory after installation.

The bridge listens only on `127.0.0.1:8631`; it does not share the printer over the local network. The original Canon DLLs are not installed, and no new kernel driver is added.

For additional instructions in Ukrainian, see [PACKAGE-README.txt](PACKAGE-README.txt) and [SYSTEM-PRINTING.md](SYSTEM-PRINTING.md).

## Printing capabilities and limits

- A4 paper, 600 dpi, monochrome, single-sided printing.
- One copy per job, up to 100 pages and 128 MiB of document data per job through the system queue.
- The bridge accepts PWG Raster, PDF, and JPEG through IPP. Windows normally sends PWG Raster.
- The bridge must be running for the printer queue to work.
- Hardware errors are detected during jobs; the Windows queue does not yet show the actual USB printer status while idle.
- Failed jobs are not automatically retried by the bridge.

## Direct PDF and image printing

As an alternative to the Windows printer queue:

1. Turn on the printer, connect USB, and load A4 paper.
2. Run `Print-LBP1120.cmd` and select a PDF or image. You can also drag one file onto the script.
3. To use this method from Word, a browser, or another application, first save the document as PDF, then open it with the script.

Direct printing uses A4 at 600 dpi, one copy, and up to 100 pages per run. Pages use portrait orientation; landscape pages are fitted onto a portrait sheet. Margins are approximately 3.4 mm on the left and 5.1 mm at the top, with a 4800 x 6768 pixel image area. Color images are converted to monochrome with dithering.

Only the first frame of a multi-frame TIFF is printed. Duplex printing, page-range selection, and automatic retries after errors are not implemented. Each run prints the entire selected PDF.

Press **Ctrl+C** in the program window to cancel direct printing. A page already accepted by the printer may still print. If the USB connection is interrupted, turn the printer off and on before trying again. Do not run legacy Canon utilities at the same time as this application.

Logs from `Print-Document.ps1` are stored in `logs`. Temporary document raster files are deleted after processing finishes.

## Diagnostics

- `capt-status.exe`: read the protocol version and printer status without printing.
- `capt-print.exe --selftest`: test compression and decoding for four raster patterns without accessing the printer.
- `capt-print.exe --verify-pbm file.pbm`: validate and compress a PBM file without printing.
- `capt-print.exe --print-pbm file.pbm`: print a prepared raster file.

`Test-SystemQueue.ps1` requires the bridge to run with `--dry-run` by default. Physical printing requires the explicit `-Physical` option.

## Implementation and third-party code

Printing path:

```text
Windows application -> Microsoft IPP Class Driver -> local IPP bridge
                    -> PWG/PDF -> PBM -> CAPT -> usbprint.sys -> LBP-1120
```

CAPT protocol support and SCoA compression are based on [libcapt](https://github.com/darkvision77/libcapt), revision `8f1fd502e6fee893d409f09c5895248c189ead4b`.

The vendored library is in `upstream/libcapt`, and its BSD-2-Clause license is in [upstream/LICENSE](upstream/LICENSE). `Config.hpp` is generated during the build. Original Canon DLLs have not been modified and are not included.

PDF conversion uses `Windows.Data.Pdf`; image conversion uses .NET `System.Drawing`.

## Hardware validation

Tests performed on 2026-11-08: Windows 11 x64, USB VID `04A9`, PID `262B`, CAPT 1.0.

- Printer status retrieval worked, and a physical test page was confirmed by the user.
- PDF conversion was tested separately.
- An unnecessary page-counter wait condition was corrected after the first test.
- A one-page PDF completed conversion, transmission of 334,178 bytes of compressed raster data, and printer completion acknowledgement with exit code 0. Another PDF was printed and its output confirmed by the user.
- A two-page job sent through the Windows queue using `PrintDocument` produced both sheets, as confirmed by the user.
- An empty-tray message appeared after the last sheet. Handling was corrected using printed-page counters, and a regression check was added using the observed device status. This specific fix has not yet been verified with another physical print.

Clean installation on another PC, crash recovery, Windows restart behavior, and cancellation during physical printing still need separate validation.

This is an unofficial project, not a Canon or Microsoft product.
