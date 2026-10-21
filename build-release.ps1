param(
    [string]$Output = "dist",
    [string]$Runtime = "win-x64",
    [string]$SigningKey = "$env:APPDATA\.nviel-signing\update-private-key.b64",
    [string]$PublicKey = "$env:APPDATA\.nviel-signing\update-public-key.b64",
    [switch]$NoSign
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

$signer = Join-Path $root "tools\ReleaseSigner\ReleaseSigner.csproj"
$signature = $exe.FullName + ".sig"

if (-not $NoSign) {
    # Optional Authenticode certificate. A real code-signing certificate is not
    # required for updates: the detached ECDSA signature below is what the updater checks.
    $cert = $env:NVIEL_SIGN_CERT_PATH
    if ($cert -and (Test-Path $cert)) {
        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($signtool) {
            Write-Host "Authenticode signing: $cert"
            & $signtool.Source sign /fd SHA256 /f $cert /p $env:NVIEL_SIGN_CERT_PASSWORD `
                /tr http://timestamp.digicert.com /td SHA256 $exe.FullName
            if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed" }
        } else {
            Write-Warning "signtool.exe not found; skipping Authenticode."
        }
    } else {
        Write-Host "Authenticode certificate not configured; using the nViel detached signature."
    }

    if (-not (Test-Path $SigningKey)) {
        throw "Signing key not found at $SigningKey. Pass -NoSign only for a local unsigned build."
    }

    & $dotnet run --project $signer -c Release -- $exe.FullName $signature $SigningKey
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $signature)) { throw "Release signature failed" }

    if (Test-Path $PublicKey) {
        & $dotnet run --project $signer -c Release -- verify $exe.FullName $signature $PublicKey
        if ($LASTEXITCODE -ne 0) { throw "Release signature verification failed" }
    } else {
        Write-Warning "Public key not found at $PublicKey; signature was not cross-checked."
    }
} else {
    Write-Warning "Building UNSIGNED: the updater will reject this file."
    Remove-Item $signature -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Release build: $($exe.FullName)"
Write-Host "Version:       $($exe.VersionInfo.ProductVersion)"
Write-Host "Size:          $($exe.Length) bytes"
Write-Host "SHA-256:       $hash"
if (Test-Path $signature) { Write-Host "Signature:     $signature" }

