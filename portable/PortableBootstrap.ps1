param(
    [Parameter(Mandatory=$true)][string]$ProjectRoot,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [Parameter(Mandatory=$true)][string]$PackageUrl,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [string]$TestArchivePath = '',
    [switch]$CiMode
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.IO.Compression.FileSystem
$parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ProjectRoot))
$output = [IO.Path]::GetFullPath($OutputDir)
[IO.Directory]::CreateDirectory($parent) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
$lock = $null
$work = Join-Path $parent ('.setup-' + [guid]::NewGuid().ToString('N'))
$installerWork = $null
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $root = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    if (-not $root) { return $null }
    $exe = Join-Path $root 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }
    return $null
}
try {
    try { $lock = [IO.File]::Open((Join-Path $parent '.installation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another loader setup is running. Wait for it to finish.' }
    if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $work | Out-Null
        $archivePath = Join-Path $work 'project.zip'
        if ($TestArchivePath) { Copy-Item -LiteralPath $TestArchivePath -Destination $archivePath }
        else {
            Write-Host 'Downloading source package...'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $PackageUrl -OutFile $archivePath -UseBasicParsing
        }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash -ne $ExpectedHash) { throw 'Downloaded package failed SHA-256 verification.' }
        $stage = Join-Path $work 'project'
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $base = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
        $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            [long]$total = 0
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.Replace('/', '\')
                if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Invalid package path.' }
                $path = [IO.Path]::GetFullPath((Join-Path $stage $name))
                if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes project folder.' }
                $total += $entry.Length
                if ($total -gt 2GB) { throw 'Package exceeds extraction limit.' }
                if ($name.EndsWith('\')) { [IO.Directory]::CreateDirectory($path) | Out-Null; continue }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            }
        } finally { $zip.Dispose() }
        foreach ($relative in @('Trainer.sln', 'Trainer.vcxproj', 'tools\update_sdk_build.ps1', 'tools\complete_sdk_update.ps1', 'dist\Dumper-7.dll', '.smite-portable-v2')) {
            if (-not (Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf)) { throw "Package is missing $relative" }
        }
        if (Test-Path -LiteralPath $ProjectRoot) { throw 'Project folder appeared during setup; nothing was overwritten.' }
        Move-Item -LiteralPath $stage -Destination $ProjectRoot
    }
    foreach ($relative in @('Trainer.sln', 'Trainer.vcxproj', 'tools\update_sdk_build.ps1', 'tools\complete_sdk_update.ps1', 'dist\Dumper-7.dll', '.smite-portable-v2')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $relative) -PathType Leaf)) { throw "Existing project folder is not a valid portable project: $ProjectRoot. Nothing was overwritten." }
    }
    $probe = Join-Path $output ('.write-test-' + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($probe, 'test')
    Remove-Item -LiteralPath $probe -Force
    $dumper = Join-Path $output 'Dumper-7.dll'
    if (-not (Test-Path -LiteralPath $dumper -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot 'dist\Dumper-7.dll') -Destination $dumper
    }
    if (-not (Find-MSBuild)) {
        if ($CiMode) { throw 'Required MSVC build tools not found on test runner.' }
        $answer = [Windows.Forms.MessageBox]::Show('Для локальной сборки нужны Visual Studio Build Tools с C++ и Windows SDK. Скачать и запустить установщик Microsoft? Потребуются место на диске и права администратора.', 'SMITE Loader — компоненты сборки', 'YesNo', 'Question')
        if ($answer -ne [Windows.Forms.DialogResult]::Yes) { throw 'Build tools installation cancelled.' }
        $installerWork = Join-Path ([IO.Path]::GetTempPath()) ('SmiteBuildTools-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($installerWork) | Out-Null
        $bootstrap = Join-Path $installerWork 'vs_buildtools.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vs_buildtools.exe' -OutFile $bootstrap -UseBasicParsing
        $sig = Get-AuthenticodeSignature -LiteralPath $bootstrap
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Microsoft installer signature could not be verified.' }
        $params = @('--wait','--passive','--norestart','--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended')
        Write-Host "Starting Microsoft Build Tools installer from: $installerWork"
        $child = Start-Process -FilePath $bootstrap -ArgumentList $params -WorkingDirectory $installerWork -Verb RunAs -Wait -PassThru
        if ($child.ExitCode -eq 3010) { throw 'Windows restart required. Restart and run the loader again.' }
        if ($child.ExitCode -ne 0) { throw "Visual Studio installer returned $($child.ExitCode)." }
        if (-not (Find-MSBuild)) { throw 'MSVC build tools are still missing.' }
    }
    $header = Get-Content -LiteralPath (Join-Path $ProjectRoot 'CppSDK\SDK.hpp') -Raw
    $version = [regex]::Match($header, '(?m)^//\s*\d+\.\d+\.\d+-(\d+)\s*$')
    if (-not $version.Success) { throw 'SDK build number was not found.' }
    $target = Join-Path $output ("Smite_Internal_{0}.dll" -f $version.Groups[1].Value)
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        Write-Host 'Building trainer from the current SDK...'
        & (Join-Path $ProjectRoot 'tools\update_sdk_build.ps1') -ProjectRoot $ProjectRoot -OutputDir $output
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target)) { throw 'Trainer build failed.' }
    } else { Write-Host "Trainer already exists: $target" }
    Write-Host "Project: $ProjectRoot"
    Write-Host "DLL directory: $output"
} finally {
    if ($lock) { $lock.Dispose() }
    if ($installerWork -and (Test-Path -LiteralPath $installerWork)) { Remove-Item -LiteralPath $installerWork -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
