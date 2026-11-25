<#
.SYNOPSIS
    Builds a release of the OzServer plugin into artifacts\.

.DESCRIPTION
    Builds OzServerPlugin in Release, checks the DLL, and writes:

      artifacts\OzServerPlugin-v<version>.zip   for the vatSys Launcher and manual installs:
                                                OzServerPlugin.dll and LICENSE.txt at the zip root
      artifacts\OzServerPlugin.dll              the bare DLL, for the plugin's own updater

    <version> is <Version> in OzServerPlugin\OzServerPlugin.csproj. Nothing is published: the
    script ends by printing the gh command that would publish the release.

    The build goes to artifacts\build\, never to the OutputPath a developer may have pointed at a
    vatSys profile, so a running vatSys is never touched.

    A release is tagged at the commit it was built from, so the script refuses to build when the
    working tree has uncommitted changes, when HEAD is on no remote branch, or when the tag
    v<version> already exists here or on origin.

.PARAMETER VatSysPath
    The vatSys install root, if it isn't found through the registry.

.PARAMETER SkipGitChecks
    Reports the three git checks as warnings instead of refusing. For trying the build on a tree
    that isn't committed yet; never for the build that is published.

.EXAMPLE
    .\Release.ps1
#>
[CmdletBinding()]
param(
    [string] $VatSysPath,
    [switch] $SkipGitChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = $PSScriptRoot
$project   = Join-Path $root 'OzServerPlugin\OzServerPlugin.csproj'
$artifacts = Join-Path $root 'artifacts'
$buildDir  = Join-Path $artifacts 'build'
$dllName   = 'OzServerPlugin.dll'

# Files vatSys already ships. Copies in the zip would load a second, possibly different, version
# into vatSys's process (see the Private=False references in the csproj).
$vatSysFiles = @('vatSys.exe', 'VATSYSControls.dll', 'Newtonsoft.Json.dll', 'RossCarlson.Vatsim.Network.dll')

[xml] $csproj = Get-Content -Raw $project
$versionNode = $csproj.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> in $project." }
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> '$version' must be MAJOR.MINOR.PATCH: the launcher and the updater both parse it as a plain version." }

$tag     = "v$version"
$zipName = "OzServerPlugin-$tag.zip"
$zipPath = Join-Path $artifacts $zipName

# Before anything is built. The DLL has to be the build of one pushed commit: gh tags the release
# at the commit named with --target, and without one it tags the tip of the default branch.
#
# git's stderr is left alone: redirecting a native command's stderr under
# $ErrorActionPreference = 'Stop' turns anything it writes into a terminating error on Windows
# PowerShell. $root is passed as its own argument, so the space in this repository's path is safe.
$head = $null
$onRemote = @()
$gitProblems = @()
try {
    $head = "$(& git -C $root rev-parse HEAD)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $head) { $head = $null; throw 'git rev-parse HEAD failed.' }

    $dirty = @(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty.Count -gt 0) { $gitProblems += "The working tree has uncommitted changes ($($dirty.Count) path(s)), so the build would match no commit." }

    $onRemote = @(& git -C $root branch -r --contains $head | ForEach-Object { "$_".Trim() })
    if ($LASTEXITCODE -ne 0) { throw 'git branch -r --contains failed.' }
    if ($onRemote.Count -eq 0) { $gitProblems += "HEAD ($head) is on no remote branch, as far as the last fetch or push shows. Push it first." }

    $localTag = @(& git -C $root tag --list $tag)
    if ($localTag.Count -gt 0) { $gitProblems += "Tag $tag already exists in this repository." }

    $remoteTag = @(& git -C $root ls-remote --tags origin "refs/tags/$tag")
    if ($LASTEXITCODE -ne 0) { $gitProblems += "origin could not be asked whether tag $tag exists." }
    elseif ($remoteTag.Count -gt 0) { $gitProblems += "Tag $tag already exists on origin." }
}
catch {
    $gitProblems += "The git state could not be checked: $($_.Exception.Message)"
}

if ($gitProblems.Count -gt 0) {
    if (-not $SkipGitChecks) {
        throw "Not building $tag. $($gitProblems -join ' ') (-SkipGitChecks builds anyway, for trying the build only.)"
    }
    foreach ($problem in $gitProblems) { Write-Warning "$problem Building anyway (-SkipGitChecks): do not publish this build." }
}

Write-Host "Building OzServer Plugin $tag" -ForegroundColor Cyan

if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force $buildDir | Out-Null

# A forward slash ends OutputPath (MSBuild wants a trailing separator): a backslash there would sit
# directly before the closing quote PowerShell adds around an argument with a space in it.
$buildArgs = @('build', $project, '-c', 'Release', '--nologo', "-p:OutputPath=$buildDir/")
if ($VatSysPath) { $buildArgs += "-p:VatSysPath=$VatSysPath" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)." }

$dll = Join-Path $buildDir $dllName
if (-not (Test-Path $dll)) { throw "The build did not produce $dllName." }

# The launcher detects an install by this file name and its FileVersion; the updater by the
# assembly name and AssemblyVersion. A mismatch here would ship a release nobody is offered.
$info = (Get-Item $dll).VersionInfo
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($dll)
if ($assemblyName.Name -ne 'OzServerPlugin') { throw "Assembly name is '$($assemblyName.Name)', not OzServerPlugin." }
if ($info.FileVersion -ne "$version.0") { throw "FileVersion is $($info.FileVersion), expected $version.0." }
if ($assemblyName.Version.ToString() -ne "$version.0") { throw "AssemblyVersion is $($assemblyName.Version), expected $version.0." }

$shipped = Get-ChildItem $buildDir -File | Where-Object { $_.Extension -in '.dll', '.exe' }
$leaked = $shipped | Where-Object { $vatSysFiles -contains $_.Name }
if ($leaked) { throw "vatSys's own files are in the build output: $($leaked.Name -join ', '). Keep their references Private=False." }

# Anything besides the plugin would be a dependency vatSys doesn't ship; there are none today.
$dependencies = @($shipped | Where-Object { $_.Name -ne $dllName })
if ($dependencies.Count -gt 0) { Write-Host "Including dependencies: $($dependencies.Name -join ', ')" -ForegroundColor Yellow }

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    # At the zip root: the launcher extracts straight into Plugins\OzServer-Plugin.
    foreach ($file in @(Get-Item $dll) + $dependencies) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'LICENSE'), 'LICENSE.txt', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $zip.Dispose()
}

Copy-Item -Force $dll (Join-Path $artifacts $dllName)

$zipHash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
$dllHash = (Get-FileHash -Algorithm SHA256 $dll).Hash.ToLowerInvariant()

Write-Host ''
Write-Host "Built OzServer Plugin $tag" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "    sha256 $zipHash"
Write-Host "  $(Join-Path $artifacts $dllName)"
Write-Host "    sha256 $dllHash"
Write-Host "  FileVersion $($info.FileVersion), ProductVersion $($info.ProductVersion)"
Write-Host ''
Write-Host 'To publish (a normal release, not a pre-release - the launcher only sees the latest release):' -ForegroundColor Cyan
# --target: without it gh creates the tag from the default branch, not from the commit built here.
$target = if ($head) { $head } else { '<full sha of the commit built>' }
Write-Host "  gh release create $tag `"artifacts\$zipName`" `"artifacts\$dllName`" --target $target --title `"OzServer Plugin $tag`" --notes-file <notes.md> --latest"

# Only while it is true: the reminder goes once the default branch has this commit.
$defaultBranch = $null
try { $defaultBranch = "$(& git -C $root symbolic-ref -q --short refs/remotes/origin/HEAD)".Trim() } catch { }
if ($defaultBranch -and $onRemote -notcontains $defaultBranch) {
    Write-Host ''
    Write-Host "Reminder: the repository's default branch ($defaultBranch) does not have this commit - it was still at the 0.1.8 commit" -ForegroundColor Yellow
    Write-Host 'when this reminder was written. The README GitHub shows comes from the default branch, and that branch has no' -ForegroundColor Yellow
    Write-Host 'Discord release workflow: bring it up to this commit before publishing, or check afterwards that the release was announced.' -ForegroundColor Yellow
}
