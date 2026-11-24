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

.PARAMETER VatSysPath
    The vatSys install root, if it isn't found through the registry.

.EXAMPLE
    .\Release.ps1
#>
[CmdletBinding()]
param(
    [string] $VatSysPath
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

Write-Host "Building OzServer Plugin $tag" -ForegroundColor Cyan

if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force $buildDir | Out-Null

$buildArgs = @('build', $project, '-c', 'Release', '--nologo', "-p:OutputPath=$buildDir\")
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
Write-Host "  gh release create $tag `"artifacts\$zipName`" `"artifacts\$dllName`" --title `"OzServer Plugin $tag`" --notes-file <notes.md> --latest"
