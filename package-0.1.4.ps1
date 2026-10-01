param([string]$PublishDir = "publish", [string]$Output = "RustedSpiwiStudio-0.1.5-win-x64.zip")
$ErrorActionPreference = "Stop"
$required = @(
  "RustedShpizhionStudio.exe",
  "libSkiaSharp.dll",
  "libHarfBuzzSharp.dll",
  "av_libglesv2.dll",
  "resources\rusted_warfare_docs.db"
)
foreach ($file in $required) {
  if (-not (Test-Path (Join-Path $PublishDir $file))) { throw "Не найден обязательный файл: $file" }
}
Push-Location $PublishDir
try {
  if (Test-Path (Join-Path (Split-Path $Output -Parent) (Split-Path $Output -Leaf))) { Remove-Item $Output -Force }
  Compress-Archive -Path @(
    "RustedShpizhionStudio.exe",
    "libSkiaSharp.dll",
    "libHarfBuzzSharp.dll",
    "av_libglesv2.dll",
      "resources\rusted_warfare_docs.db"
  ) -DestinationPath (Join-Path (Get-Location) $Output) -CompressionLevel Optimal
} finally { Pop-Location }
$hash = (Get-FileHash -Path $Output -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "ZIP: $Output"
Write-Host "SHA-256: $hash"
