# Publishes a folder to https://<name>.ai-dod-spse.davidbenes.cz
# Usage: powershell -ExecutionPolicy Bypass -File deploy.ps1 [folder] [name]   or   ... deploy.ps1 [name]
#   folder defaults to the folder this script is in, so you can copy deploy.ps1 into the website and run it.
#   DOD_TOKEN   event token
#   DOD_SERVER  deployer URL (default https://deploy.ai-dod-spse.davidbenes.cz)
# Each is taken from the environment, else from .env in the website folder, else from .env in the
# current folder; a missing token is asked for. .env is never uploaded (dotfiles are skipped).
param([string]$Dir, [string]$Name)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

$Curl = (Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $Curl -and $IsWindows -eq $false) {  # PowerShell 7 on macOS/Linux
    $Curl = (Get-Command curl -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not $Curl) { Fail 'Chybí curl.exe (je součástí Windows 10 a novějších).' }

# A single argument that isn't a folder is the site name; no folder means the script's own folder.
if ($Dir -and -not $Name -and -not (Test-Path $Dir -PathType Container)) { $Name = $Dir; $Dir = $null }
if (-not $Dir) { $Dir = $PSScriptRoot }
if (-not (Test-Path $Dir -PathType Container)) { Fail "Složka '$Dir' neexistuje." }
$Dir = (Resolve-Path $Dir).Path
if (-not ((Test-Path (Join-Path $Dir 'index.html')) -or (Test-Path (Join-Path $Dir 'index.php')))) {
    Fail "Ve složce '$Dir' chybí index.html (nebo index.php)."
}
# Reads KEY from a .env file without executing it: KEY=value, export KEY=value, quotes optional.
function EnvFileValue([string]$key, [string]$file) {
    if (-not (Test-Path $file -PathType Leaf)) { return $null }
    $value = $null
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        if ($line -match "^\s*(export\s+)?$key\s*=\s*(.*?)\s*$") {
            $value = $Matches[2] -replace '^"(.*)"$', '$1' -replace "^'(.*)'$", '$1'
        }
    }
    return $value
}
function Setting([string]$key) {  # environment > <folder>/.env > ./.env
    $v = [Environment]::GetEnvironmentVariable($key)
    if (-not $v) { $v = EnvFileValue $key (Join-Path $Dir '.env') }
    if (-not $v -and $Dir -ne (Get-Location).Path) { $v = EnvFileValue $key (Join-Path (Get-Location).Path '.env') }
    return $v
}
$Server = Setting 'DOD_SERVER'
if (-not $Server) { $Server = 'https://deploy.ai-dod-spse.davidbenes.cz' }

$SecretFile = Join-Path $Dir '.deploy-secret'
$NameFile = Join-Path $Dir '.deploy-name'
if (-not $Name -and (Test-Path $NameFile)) { $Name = (Get-Content $NameFile -Raw).Trim() }
if (-not $Name) { $Name = Read-Host 'Jméno webu (např. jiri)' }
$Token = Setting 'DOD_TOKEN'
if (-not $Token) {
    $sec = Read-Host 'Token akce' -AsSecureString
    $Token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("dod-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $Tmp | Out-Null
try {
    # Build the zip with forward slashes (Compress-Archive on PS 5.1 writes backslashes).
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $ZipPath = Join-Path $Tmp 'site.zip'
    $zip = [IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        Get-ChildItem $Dir -Recurse -File -Force | ForEach-Object {
            $rel = $_.FullName.Substring($Dir.Length).TrimStart('\', '/').Replace('\', '/')
            if ($rel -split '/' | Where-Object { $_.StartsWith('.') }) { return }
            if ($rel -in @('deploy.ps1', 'deploy.sh')) { return }  # never upload the deploy scripts
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel) | Out-Null
        }
    } finally { $zip.Dispose() }

    $OutFile = Join-Path $Tmp 'out.json'
    $curlArgs = @('-sS', '-o', $OutFile, '-w', '%{http_code}',
        '-H', "X-Event-Token: $Token",
        '--form-string', "name=$Name",
        '-F', "zip=@$ZipPath;type=application/zip")
    if (Test-Path $SecretFile) { $curlArgs += @('-H', "X-Site-Secret: $((Get-Content $SecretFile -Raw).Trim())") }
    $Status = & $Curl @curlArgs "$Server/deploy"
    if ($LASTEXITCODE -ne 0) { Fail "Server není dostupný: $Server" }

    $Res = [IO.File]::ReadAllText($OutFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($Status -ne '200') { Fail "Chyba ($Status): $($Res.error)" }

    # Bookkeeping files are hidden in Explorer; un-hide before rewriting (hidden files can't be overwritten).
    function Save([string]$path, [string]$text) {
        if (Test-Path $path) { try { (Get-Item $path -Force).Attributes = 'Normal' } catch {} }
        [IO.File]::WriteAllText($path, $text)
        try { (Get-Item $path -Force).Attributes = 'Hidden' } catch {}
    }
    if ($Res.secret) { Save $SecretFile $Res.secret }
    Save $NameFile $Res.name

    Write-Host ''
    Write-Host $Res.qrAscii
    Write-Host ''
    Write-Host "  Hotovo! Web běží na:  $($Res.url)" -ForegroundColor Green
    Write-Host "  QR na celou obrazovku: $($Res.qrUrl)"
    Write-Host ''
} finally {
    Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}
