# scripts/verify-source-integrity.ps1
# Script de auditoría y guard de integridad de código fuente
[CmdletBinding()]
param(
    [string]$RootPath = "$PSScriptRoot/.."
)

$targetDirs = @("src", "tests-csharp", "docs")
$relevantExtensions = @(".cs", ".xaml", ".json", ".md", ".csproj", ".resx", ".ps1", ".cmd")
$excludePatterns = @("\\bin\\", "\\obj\\", "\\dist\\", "\\\.git\\", "\\\.dotnet_cli\\")

Write-Host "Iniciando verificación de integridad de código fuente..." -ForegroundColor Cyan

$emptyFiles = @()
$suspiciousFiles = @()

foreach ($dir in $targetDirs) {
    $fullDir = Join-Path $RootPath $dir
    if (Test-Path $fullDir) {
        $files = Get-ChildItem -Path $fullDir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $filePath = $_.FullName
                $isExcluded = $false
                foreach ($pattern in $excludePatterns) {
                    if ($filePath -match $pattern) {
                        $isExcluded = $true
                        break
                    }
                }
                (-not $isExcluded) -and ($_.Extension -in $relevantExtensions)
            }

        foreach ($file in $files) {
            if ($file.Length -eq 0) {
                $emptyFiles += $file
            }
        }
    }
}

if ($emptyFiles.Count -gt 0) {
    Write-Host "`n[ERROR CRÍTICO] Se encontraron $($emptyFiles.Count) archivos fuente vacíos (0 bytes):" -ForegroundColor Red
    foreach ($ef in $emptyFiles) {
        Write-Host "  - $($ef.FullName)" -ForegroundColor Red
    }
    Write-Host "`nIntegridad comprometida. Abortando operación." -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Todos los archivos de código fuente poseen contenido íntegro (0 archivos vacíos)." -ForegroundColor Green
exit 0
