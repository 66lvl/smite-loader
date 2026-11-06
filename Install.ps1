$ErrorActionPreference = 'Stop'
$ExpectedHash = '09418cc474350c3762ef697f66fc9e4c9be0a4c251fa412fb7e46278a593c6ba'
$Repository = '66lvl/smite-loader'
$Tag = 'v1.0.0'
$Asset = 'SmiteLoader-package.zip'
$InstallDir = Join-Path $env:LOCALAPPDATA 'SmiteLoader'
$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ('SmiteLoader-' + [Guid]::NewGuid().ToString('N'))

function Ask([string]$Question) {
    $a = Read-Host "$Question [y/N]"
    return $a -match '^(y|yes|д|да)$'
}
function Find-Gh {
    $c = Get-Command gh.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($candidate in @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'), (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $root = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    if (-not $root) { return $null }
    $exe = Join-Path $root 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    return $null
}
function Has-TargetingPack {
    return (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll'))
}
try {
    Write-Host 'SMITE Loader - private installer' -ForegroundColor Cyan
    Write-Host 'You must accept the repository invitation on GitHub before installation.'
    Write-Host 'No GitHub password or token is embedded in this installer.'
    if (Test-Path -LiteralPath $InstallDir) { throw "Installation folder already exists: $InstallDir. Stop and back it up or choose a fresh Windows account; nothing was overwritten." }
    New-Item -ItemType Directory -Path $WorkDir | Out-Null
    $gh = Find-Gh
    if (-not $gh) {
        if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw 'GitHub CLI and winget were not found. Install GitHub CLI from https://cli.github.com/, then rerun setup.' }
        if (-not (Ask 'Install GitHub CLI using Windows Package Manager?')) { throw 'GitHub CLI is required. Setup cancelled.' }
        & winget.exe install --id GitHub.cli --exact --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { throw "GitHub CLI installation failed (exit $LASTEXITCODE)." }
        $gh = Find-Gh
        if (-not $gh) { throw 'GitHub CLI could not be found after installation. Restart setup.' }
    }
    & $gh auth status --hostname github.com
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Sign into YOUR invited GitHub account in the browser when prompted.'
        & $gh auth login --hostname github.com --git-protocol https --web
        if ($LASTEXITCODE -ne 0) { throw 'GitHub login failed or was cancelled.' }
    }
    Write-Host 'Downloading private project release...'
    & $gh release download $Tag --repo $Repository --pattern $Asset --dir $WorkDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the release. Confirm the active GitHub account accepted the repository invitation; run gh auth status and retry.' }
    $zip = Join-Path $WorkDir $Asset
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash -ne $ExpectedHash) { throw 'SHA-256 verification failed. The downloaded package was not installed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $WorkDir 'project'
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $base = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        [Int64]$total = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Invalid path in package.' }
            $path = [IO.Path]::GetFullPath((Join-Path $stage $name))
            if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package entry escapes installation folder.' }
            $total += $entry.Length
            if ($total -gt 2GB) { throw 'Package exceeds the extraction size limit.' }
            if ($name.EndsWith('\')) { [IO.Directory]::CreateDirectory($path) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    } finally { $archive.Dispose() }
    foreach ($required in @('Trainer.vcxproj','tools\loader\SmiteLoader.csproj','tools\update_sdk_build.ps1','dist\Dumper-7.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Package is missing $required." }
    }
    if (-not (Find-MSBuild) -or -not (Has-TargetingPack)) {
        Write-Host 'MSVC Build Tools or the .NET Framework 4.8 targeting pack is missing.'
        if (-not (Ask 'Download and run Microsoft Visual Studio 2022 Build Tools setup? This needs administrator approval and several GB.')) { throw 'Build tools are required. Setup cancelled.' }
        $bootstrap = Join-Path $WorkDir 'vs_buildtools.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vs_buildtools.exe' -OutFile $bootstrap -UseBasicParsing
        $sig = Get-AuthenticodeSignature -LiteralPath $bootstrap
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Microsoft installer signature could not be verified.' }
        $params = @('--wait','--passive','--norestart','--add','Microsoft.VisualStudio.Workload.VCTools','--add','Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools','--add','Microsoft.Net.Component.4.8.TargetingPack','--includeRecommended')
        $child = Start-Process -FilePath $bootstrap -ArgumentList $params -Verb RunAs -Wait -PassThru
        if ($child.ExitCode -notin @(0,3010)) { throw "Visual Studio installer returned $($child.ExitCode)." }
        if ($child.ExitCode -eq 3010) { throw 'A Windows restart is required. Restart Windows and run setup again.' }
        if (-not (Find-MSBuild) -or -not (Has-TargetingPack)) { throw 'The required tools were not detected after installation. Use Visual Studio Installer to install MSVC v143, a Windows SDK, C# build tools, and .NET Framework 4.8 Targeting Pack.' }
    }
    $msbuild = Find-MSBuild
    Write-Host 'Building loader...'
    & $msbuild (Join-Path $stage 'tools\loader\SmiteLoader.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /m /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Loader build failed (exit $LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $stage 'tools\loader\bin\Release\SmiteLoader.exe') -Destination (Join-Path $stage 'dist\SmiteLoader.exe')
    Write-Host 'Building trainer from the bundled SDK; this may take several minutes...'
    & (Join-Path $stage 'tools\update_sdk_build.ps1') -ProjectRoot $stage
    if ($LASTEXITCODE -ne 0) { throw "Trainer build failed (exit $LASTEXITCODE)." }
    Move-Item -LiteralPath $stage -Destination $InstallDir
    try {
        $shell = New-Object -ComObject WScript.Shell
        $desktop = [Environment]::GetFolderPath('DesktopDirectory')
        $shortcut = $shell.CreateShortcut((Join-Path $desktop 'SMITE Loader.lnk'))
        $shortcut.TargetPath = Join-Path $InstallDir 'dist\SmiteLoader.exe'
        $shortcut.WorkingDirectory = Join-Path $InstallDir 'dist'
        $shortcut.Save()
    } catch { Write-Warning "Shortcut not created: $_" }
    Write-Host "Installation completed: $InstallDir\dist\SmiteLoader.exe" -ForegroundColor Green
    Write-Host 'Start the game through Steam. The installed SDK build must match the game build. Use the loader update workflow after a game update.'
} catch {
    Write-Host "INSTALLATION FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'If a build failed, copy any build errors shown above before closing this window.'
} finally {
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ''
    [void](Read-Host 'Press Enter to close')
}
