param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $repoRoot "release"
$publishDir = Join-Path $releaseDir "portable"
$project = Join-Path $repoRoot "src\CodexQuotaWidget.App\CodexQuotaWidget.App.csproj"
$artifactName = "Codex$([char]0x989D)$([char]0x5EA6)$([char]0x5C0F)$([char]0x7A97)-1.0.0-portable.exe"
$fallbackName = "Codex$([char]0x989D)$([char]0x5EA6)$([char]0x5C0F)$([char]0x7A97)-1.0.0-portable-iconfix.exe"
$artifact = Join-Path $releaseDir $artifactName
$fallbackArtifact = Join-Path $releaseDir $fallbackName

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -o $publishDir

$publishedExe = Get-ChildItem -LiteralPath $publishDir -Filter "*.exe" | Select-Object -First 1
if ($null -eq $publishedExe) {
    throw "Published exe was not found under $publishDir"
}

try {
    Copy-Item -LiteralPath $publishedExe.FullName -Destination $artifact -Force
    Get-Item $artifact | Select-Object FullName, Length, LastWriteTime
} catch {
    Write-Warning "Could not overwrite $artifactName. A running process may be locking it. Writing fallback artifact instead."
    Copy-Item -LiteralPath $publishedExe.FullName -Destination $fallbackArtifact -Force
    Get-Item $fallbackArtifact | Select-Object FullName, Length, LastWriteTime
}
