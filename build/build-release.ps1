<#
.SYNOPSIS
    Builds the WriteLite Windows release: publish, bundled Java runtime, installer,
    checksums and manifest.

.DESCRIPTION
    Produces, under -OutputRoot:

        staging\WriteLite-<version>\   the exact tree the installer packages
        dist\WriteLite-Setup-<version>.exe
        dist\SHA256SUMS.txt
        dist\release-manifest.json

    Three things this script exists to get right.

    1. Self-contained publish. The previous release was self-contained (verified from
       its install log: coreclr.dll and hostfxr.dll are in the installed tree) and this
       keeps that: WriteLite targets net10.0-windows, .NET 10 is new, and requiring a
       user to find and install a Desktop Runtime before a text tool will open is a
       barrier the ~150 MB buys away on a payload that is already near a gigabyte.

    2. A bundled Java runtime. ThirdParty/LanguageEngine is LanguageTool 6.4, which is
       Java, and nothing in the repository ships a JVM. Without one the engine host
       throws java-not-found and the product silently drops to its own basic checking —
       which is what the shipped 1.0.0 installer did on every machine without a system
       Java. WriteLiteJavaResolver already looks in Runtime\Java first, so a jlink image
       goes there. jdk.httpserver is not part of java.se and LanguageTool's HTTPServer
       will not load without it.

    3. One version number. It is read back out of the published WriteLite.exe rather
       than passed in, so the installer cannot disagree with the assembly. Raise the
       version in Directory.Build.props and nowhere else.

.PARAMETER OutputRoot
    Where staging/ and dist/ are written. Defaults to a sibling of the repository, kept
    off the system drive because the staging tree is about 1.2 GB.

.PARAMETER SkipInstaller
    Publish and stage only; do not run ISCC.

.PARAMETER SkipTests
    Do not run the test suite first. The default is to refuse to build a release from a
    tree whose tests do not pass.
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = 'E:\Programming\WriteLite-Build',
    [string] $JdkHome,
    [string] $InnoSetup,
    [switch] $SkipInstaller,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'src\WriteLite.App\WriteLite.App.csproj'
$TestProject = Join-Path $RepoRoot 'tests\WriteLite.Tests\WriteLite.Tests.csproj'
$IssFile = Join-Path $RepoRoot 'installer\WriteLite.iss'

function Step([string] $Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Fail([string] $Text) {
    throw $Text
}

# ---------------------------------------------------------------- prerequisites

if (-not $JdkHome) {
    # jlink lives in a JDK, not a JRE. Prefer JAVA_HOME, then the usual Adoptium path.
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += $env:JAVA_HOME }
    $candidates += Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'jdk-*' } |
        Sort-Object Name -Descending |
        ForEach-Object { $_.FullName }
    $JdkHome = $candidates | Where-Object { Test-Path (Join-Path $_ 'bin\jlink.exe') } | Select-Object -First 1
}
if (-not $JdkHome -or -not (Test-Path (Join-Path $JdkHome 'bin\jlink.exe'))) {
    Fail 'No JDK with jlink found. Pass -JdkHome, or set JAVA_HOME to a JDK 17+ installation.'
}
$Jlink = Join-Path $JdkHome 'bin\jlink.exe'

if (-not $SkipInstaller) {
    if (-not $InnoSetup) {
        $InnoSetup = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $InnoSetup -or -not (Test-Path $InnoSetup)) {
        Fail 'Inno Setup 6 (ISCC.exe) not found. Pass -InnoSetup, or use -SkipInstaller.'
    }
}

# ------------------------------------------------------------------------ tests

if (-not $SkipTests) {
    Step 'Running the test suite'
    & dotnet test $TestProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed; not building a release from this tree.' }
}

# ---------------------------------------------------------------------- publish

$Publish = Join-Path $OutputRoot 'publish'
if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish | Out-Null

Step 'Publishing WriteLite (self-contained, win-x64)'
& dotnet publish $AppProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=false `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:SatelliteResourceLanguages='ru;en' `
    -o $Publish `
    --nologo
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed.' }

$Exe = Join-Path $Publish 'WriteLite.exe'
if (-not (Test-Path $Exe)) { Fail "Publish produced no WriteLite.exe in $Publish" }

# The single source of the version: whatever the build actually stamped.
$Version = (Get-Item $Exe).VersionInfo.ProductVersion
if (-not $Version) { Fail 'WriteLite.exe carries no ProductVersion.' }
$Version = ($Version -split '\+')[0].Trim()
Write-Host "    version from binary: $Version"

# ------------------------------------------------------------- bundled runtime

Step 'Building the bundled Java runtime (jlink)'
$JavaOut = Join-Path $Publish 'Runtime\Java'
if (Test-Path $JavaOut) { Remove-Item $JavaOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $JavaOut) | Out-Null

# java.se is the whole SE API; the four jdk.* modules are the ones LanguageTool needs
# that java.se does not carry. jdk.httpserver in particular: without it the server
# fails at startup with NoClassDefFoundError: com/sun/net/httpserver/HttpHandler.
& $Jlink `
    --add-modules java.se,jdk.httpserver,jdk.unsupported,jdk.crypto.ec,jdk.localedata,jdk.zipfs `
    --include-locales=en,ru `
    --strip-debug --no-header-files --no-man-pages --compress zip-6 `
    --output $JavaOut
if ($LASTEXITCODE -ne 0) { Fail 'jlink failed.' }

foreach ($launcher in @('java.exe', 'javaw.exe')) {
    if (-not (Test-Path (Join-Path $JavaOut "bin\$launcher"))) {
        Fail "jlink image is missing bin\$launcher"
    }
}

# The runtime's own licence has to travel with it.
$JdkLicense = Join-Path $JdkHome 'legal'
if (Test-Path $JdkLicense) {
    Copy-Item $JdkLicense (Join-Path $JavaOut 'legal') -Recurse -Force -ErrorAction SilentlyContinue
}

# --------------------------------------------------- prove the engine can start

Step 'Verifying LanguageTool starts under the bundled runtime'
$EngineDir = Join-Path $Publish 'ThirdParty\LanguageEngine\6.4'
if (-not (Test-Path (Join-Path $EngineDir 'languagetool-server.jar'))) {
    Fail "Published tree has no language engine at $EngineDir"
}
$probePort = 18999
$probe = Start-Process -FilePath (Join-Path $JavaOut 'bin\java.exe') `
    -ArgumentList @('-Xmx512m', '-cp', "$EngineDir\languagetool-server.jar;$EngineDir\libs\*",
                    'org.languagetool.server.HTTPServer', '--port', "$probePort") `
    -WorkingDirectory $EngineDir -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $OutputRoot 'engine-probe-out.log') `
    -RedirectStandardError  (Join-Path $OutputRoot 'engine-probe-err.log')
try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            if ((Invoke-WebRequest "http://127.0.0.1:$probePort/v2/languages" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) {
                $ready = $true; break
            }
        } catch { }
        if ($probe.HasExited) { break }
    }
    if (-not $ready) {
        Fail "LanguageTool did not start under the bundled runtime. See engine-probe-err.log."
    }
    Write-Host '    engine answered on /v2/languages'
} finally {
    if (-not $probe.HasExited) { Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------- staging

Step 'Staging the installer payload'
$Staging = Join-Path $OutputRoot "staging\WriteLite-$Version"
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Staging | Out-Null
Copy-Item (Join-Path $Publish '*') $Staging -Recurse -Force

# Nothing a user installs should carry symbols, logs or developer leftovers.
Get-ChildItem $Staging -Recurse -Include '*.pdb', '*.log', '*.xml.bak', '*.pid' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$leftovers = Get-ChildItem $Staging -Recurse -Include '*.pdb' -File -ErrorAction SilentlyContinue
if ($leftovers) { Fail "Staging still contains $($leftovers.Count) .pdb file(s)." }

$stageStats = Get-ChildItem $Staging -Recurse -File | Measure-Object Length -Sum
Write-Host ('    staged {0:N1} MB in {1} files' -f ($stageStats.Sum / 1MB), $stageStats.Count)

if ($SkipInstaller) {
    Write-Host ''
    Write-Host "Staging tree ready: $Staging" -ForegroundColor Green
    return
}

# -------------------------------------------------------------------- installer

Step 'Compiling the installer (Inno Setup)'
$Dist = Join-Path $OutputRoot 'dist'
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

& $InnoSetup `
    "/DAppVersion=$Version" `
    "/DSourceDir=$Staging" `
    "/DOutDir=$Dist" `
    "/DAssetsDir=$(Join-Path $RepoRoot 'installer\assets')" `
    $IssFile
if ($LASTEXITCODE -ne 0) { Fail 'ISCC failed.' }

$SetupName = "WriteLite-Setup-$Version.exe"
$Setup = Join-Path $Dist $SetupName
if (-not (Test-Path $Setup)) { Fail "Installer not produced: $Setup" }

# --------------------------------------------------------- checksums + manifest

Step 'Computing checksums'
$setupItem = Get-Item $Setup
$setupHash = (Get-FileHash $Setup -Algorithm SHA256).Hash.ToLowerInvariant()

"$setupHash  $SetupName" | Set-Content (Join-Path $Dist 'SHA256SUMS.txt') -Encoding ascii

$manifest = [ordered]@{
    schemaVersion = 1
    product       = 'WriteLite'
    version       = $Version
    channel       = 'stable'
    platform      = 'windows'
    architecture  = 'x64'
    buildDate     = (Get-Date -Format 'yyyy-MM-dd')
    minimumOs     = 'Windows 10 (x64)'
    offline       = $true
    codeSigned    = $false
    installedSizeBytes = [int64]$stageStats.Sum
    components    = [ordered]@{
        languageModel  = 'WriteLite-Qwen-0.6B-GEC-1.0.1'
        modelRuntime   = 'llama.cpp (GGUF, CPU only)'
        languageEngine = 'LanguageTool 6.4'
        javaRuntime    = "OpenJDK $((Get-Item (Join-Path $JavaOut 'bin\java.exe')).VersionInfo.ProductVersion) (jlink, bundled)"
        spelling       = 'Hunspell ru_RU / en_US'
        dotnet         = 'self-contained .NET 10 (no runtime install required)'
    }
    artifacts     = @(
        [ordered]@{
            kind      = 'installer'
            name      = $SetupName
            sizeBytes = [int64]$setupItem.Length
            sha256    = $setupHash
            url       = $null
        }
    )
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Dist 'release-manifest.json') -Encoding utf8

Write-Host ''
Write-Host 'Release build complete.' -ForegroundColor Green
Write-Host ('  {0}' -f $Setup)
Write-Host ('  {0:N0} bytes ({1:N1} MB)' -f $setupItem.Length, ($setupItem.Length / 1MB))
Write-Host ("  sha256 {0}" -f $setupHash)
