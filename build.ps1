# Builds dist\HDRScreenshot.exe
#   .\build.ps1                 single exe, needs the .NET 8 Desktop Runtime (small)
#   .\build.ps1 -SelfContained  single exe with the runtime bundled (no install needed, ~70 MB)
param([switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish "$PSScriptRoot\src\HdrScreenshot\HdrScreenshot.csproj" -c Release -r win-x64 `
    --self-contained $sc -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$sc -p:DebugType=none -o "$PSScriptRoot\dist"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $PSScriptRoot\dist\HDRScreenshot.exe"
