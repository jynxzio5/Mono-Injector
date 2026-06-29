# ============================================
#   GAMBLE DUMB - MONO INJECTOR BUILDER
# ============================================

$CSC    = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Output = "MonoInjector.exe"
$Source = "MonoInjector.cs"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  GAMBLE DUMB - MONO INJECTOR BUILDER" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $CSC)) {
    Write-Host "[ERROR] csc.exe not found at: $CSC" -ForegroundColor Red
    exit 1
}

Write-Host "[1/2] Compiling Mono Injector (x64)..." -ForegroundColor Yellow

$argList = @(
    "/target:exe",
    "/out:$Output",
    "/platform:x64",
    "/optimize+",
    "/nowarn:0169,0649",
    "`"$Source`""
)

$fullCmd = "& `"$CSC`" " + ($argList -join " ")
Invoke-Expression $fullCmd

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[ERROR] Compilation failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  BUILD OK: $Output" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Usage:" -ForegroundColor Cyan
Write-Host "    1. Build GambleDumbMenu.dll using build.ps1" -ForegroundColor White
Write-Host "    2. Copy GambleDumbMenu.dll into this Injector folder" -ForegroundColor White
Write-Host "    3. Launch the game" -ForegroundColor White
Write-Host "    4. Run MonoInjector.exe as Administrator" -ForegroundColor White
Write-Host "    5. Press INSERT in-game to open the cheat menu" -ForegroundColor White
Write-Host ""
