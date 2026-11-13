param(
    [string]$Output = "dist",
    [string]$Runtime = "win-x64",
    [string]$SigningKey = "$env:APPDATA\.nviel-signing\update-private-key.b64",
    [string]$PublicKey = "$env:APPDATA\.nviel-signing\update-public-key.b64",
    [string]$ModJar = "",
    [string]$ModVersion = "",
    [string]$Notes = "",
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

# ------------------------------------------------------------------ client mod + manifest

$embedded = Join-Path $root "src\nVielLauncher\embedded\nviel-1.0.0.jar"
if (-not $ModJar) {
    $ModJar = if (Test-Path $embedded) { $embedded } else { "" }
}

$modFile = $null
$modHash = $null
$modSize = 0

if ($ModJar -and (Test-Path $ModJar)) {
    $modFile = Join-Path $out (Split-Path -Leaf $ModJar)
    Copy-Item -LiteralPath $ModJar -Destination $modFile -Force
    $modHash = (Get-FileHash $modFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $modSize = (Get-Item $modFile).Length
    Write-Host "Client mod:    $modFile ($modSize bytes, SHA-256 $modHash)"
} else {
    Write-Warning "Client mod jar not found; manifest will be published without a mod."
}

if (-not $ModVersion -and $modFile) {
    $match = [regex]::Match((Split-Path -Leaf $modFile), '(\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.\-]+)?)')
    if ($match.Success) { $ModVersion = $match.Groups[1].Value }
}
if (-not $ModVersion) { $ModVersion = "1.0.0" }

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).ProductVersion
if ($version -match '^(\d+\.\d+\.\d+(?:\.\d+)?)') { $version = $matches[1] }

if (-not $Notes) {
    $Notes = "Обновление лаунчера и клиентского мода nViel."
}

$clientMod = $null
if ($modFile) {
    $clientMod = [ordered]@{
        version   = $ModVersion
        asset     = (Split-Path -Leaf $modFile)
        file_name = (Split-Path -Leaf $modFile)
        sha256    = $modHash
        size      = $modSize
        notes     = "Клиентский мод nViel $ModVersion для Fabric 1.21.4."
    }
}

$manifest = [ordered]@{
    schema     = 1
    launcher   = [ordered]@{
        version = $version
        asset   = $exe.Name
        sha256  = $hash
        size    = $exe.Length
    }
    client_mod = $clientMod
    min_java   = 21
    fabric     = "1.21.4"
    notes      = $Notes
}

$manifestPath = Join-Path $out "manifest.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))
$manifestSignature = $manifestPath + ".sig"

if (-not $NoSign) {
    & $dotnet run --project $signer -c Release -- $manifestPath $manifestSignature $SigningKey
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $manifestSignature)) { throw "Manifest signature failed" }

    if (Test-Path $PublicKey) {
        & $dotnet run --project $signer -c Release -- verify $manifestPath $manifestSignature $PublicKey
        if ($LASTEXITCODE -ne 0) { throw "Manifest signature verification failed" }
    }
} else {
    Remove-Item $manifestSignature -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Release build: $($exe.FullName)"
Write-Host "Version:       $version"
Write-Host "Size:          $($exe.Length) bytes"
Write-Host "SHA-256:       $hash"
if (Test-Path $signature) { Write-Host "Signature:     $signature" }
if ($modFile) {
    Write-Host "Client mod:    $modFile"
    Write-Host "Mod version:   $ModVersion"
    Write-Host "Mod SHA-256:   $modHash"
}
Write-Host "Manifest:      $manifestPath"
if (Test-Path $manifestSignature) { Write-Host "Manifest sig:  $manifestSignature" }

