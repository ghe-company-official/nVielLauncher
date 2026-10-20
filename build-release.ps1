param(
    [string]$Output = "dist",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\nVielLauncher\nVielLauncher.csproj"
$dotnet = if (Test-Path "C:\dotnet-sdk\dotnet.exe") { "C:\dotnet-sdk\dotnet.exe" } else { "dotnet" }

Get-Process nVielLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$out = Join-Path $root $Output
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    --nologo -o $out

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$exe = Get-ChildItem $out -Filter *.exe | Select-Object -First 1
if (-not $exe) { throw "No exe in $out" }

