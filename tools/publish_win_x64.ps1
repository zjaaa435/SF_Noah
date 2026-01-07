$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "..\buglens_lite_cs\BugLensLite\BugLensLite.csproj"
$out = Join-Path $PSScriptRoot "..\dist\win-x64"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet restore $proj
dotnet publish $proj -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false

$pub = Join-Path (Split-Path $proj) "bin\Release\net8.0-windows\win-x64\publish"
Copy-Item -Path (Join-Path $pub "*") -Destination $out -Recurse -Force

# Include user-facing docs in the release folder
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$readme = Join-Path $repoRoot "README.md"
if (Test-Path $readme) {
  Copy-Item -Force $readme (Join-Path $out "README.md")
}

$docsDir = Join-Path $out "docs"
New-Item -ItemType Directory -Force -Path $docsDir | Out-Null

$manual = Join-Path $repoRoot "docs\使用说明.md"
if (-not (Test-Path $manual)) {
  # Fallback: if filename got mangled by encoding, pick the first .md in docs/
  $alt = Get-ChildItem (Join-Path $repoRoot "docs") -Filter *.md -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($alt) { $manual = $alt.FullName }
}
if (Test-Path $manual) {
  Copy-Item -Force $manual (Join-Path $docsDir "使用说明.md")
}

# Also include an ASCII filename copy to avoid garbled Chinese filenames in some unzip tools
$manualAscii = Join-Path $repoRoot "docs\USER_MANUAL.md"
if (Test-Path $manualAscii) {
  Copy-Item -Force $manualAscii (Join-Path $docsDir "USER_MANUAL.md")
}

$zip = Join-Path $PSScriptRoot "..\dist\SF_Noah-win-x64.zip"
if (Test-Path $zip) {
  try {
    Remove-Item $zip -Force
  } catch {
    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $zip = Join-Path $PSScriptRoot "..\dist\SF_Noah-win-x64-$ts.zip"
    Write-Warning "Existing zip is in use and could not be removed. Will create: $zip"
  }
}
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

Write-Host "Published to: $out"
Write-Host "Zip: $zip"




