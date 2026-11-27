<#
.SYNOPSIS
    Checks the update a vatSys launch applies to itself, without opening vatSys or publishing
    anything.

.DESCRIPTION
    Builds three test versions of the plugin from source (9.9.8, 9.9.9 and 9.9.10) and the
    HandoverTest host, then runs each scenario: vatSys's own plugin loader, over a scratch folder
    holding 9.9.8, with a local server offering the newer ones.

    Each scenario prints which build vatSys ended up holding, what is left in the plugin folder,
    what reached vatSys's Errors window and the plugin's Update log lines. Read them against the
    expectations printed beside each scenario's name; nothing is asserted.

    Everything is written under tools\HandoverTest\bin, which git ignores. The "offline" scenario
    asks GitHub's public releases API once or twice, as the plugin does when OzServer is down.

.PARAMETER OutgoingSource
    A checkout of an earlier release to build the installed version (9.9.8) from, instead of this
    one. That release's outgoing side then hands over to this checkout's incoming side, which is
    what happens on controllers' PCs when this checkout is released.

.PARAMETER Scenario
    Run only these scenarios.
#>
param(
    [string]$OutgoingSource,
    [string[]]$Scenario
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$work = Join-Path $here 'bin\work'
$outgoing = if ($OutgoingSource) { (Resolve-Path $OutgoingSource).Path } else { $repo }

function Invoke-Build([string[]]$Arguments) {
    $output = & dotnet build @Arguments -c Release -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Where-Object { $_ -match ': error ' } | Select-Object -Unique | Write-Host
        throw "Build failed: dotnet build $($Arguments[0])"
    }
}

$versions = [ordered]@{ '9.9.8' = $outgoing; '9.9.9' = $repo; '9.9.10' = $repo }
foreach ($version in $versions.Keys) {
    Write-Host "Building test version $version from $($versions[$version])"
    # The trailing slash is required, and has to be a forward one to survive the quoting.
    Invoke-Build @((Join-Path $here 'Build\TestBuild.csproj'), "-p:Version=$version",
                   "-p:PluginSource=$($versions[$version])", "-p:OutputPath=$work/builds/$version/")
}
Invoke-Build @((Join-Path $here 'HandoverTest.csproj'))

$scenarios = [ordered]@{
    'none'            = 'Nothing newer. 9.9.8 runs the session; one release request.'
    'update'          = '9.9.9 is out. 9.9.8 installs it and starts nothing; 9.9.9 runs the session and vatSys ends up holding it.'
    'next-start'      = 'The start after that. vatSys loads 9.9.9 itself; the .backup and .live files are gone.'
    'second-update'   = '9.9.10 comes out after a launch-time update to 9.9.9. It is installed for the next start, beside a .backup2.'
    'slow-check'      = 'OzServer takes 5 s to answer. The launch waits 3 s, 9.9.8 runs, and 9.9.9 is installed in the background.'
    'slow-download'   = 'The download takes 12 s. The launch waits 10 s, 9.9.8 runs, and 9.9.9 is installed in the background.'
    'bad-hash'        = 'The download does not match its checksum. 9.9.8 runs, nothing is installed, and the Errors window says why.'
    'incoming-throws' = '9.9.9 throws as it starts. 9.9.8 starts nothing either, and the Errors window says to restart.'
    'offline'         = 'No OzServer. GitHub is asked instead, within the same 3 s; 9.9.8 runs.'
}

$exe = Join-Path $here 'bin\HandoverTest.exe'
foreach ($name in $scenarios.Keys) {
    if ($Scenario -and $Scenario -notcontains $name) { continue }

    Write-Host ''
    Write-Host "##### $name - expect: $($scenarios[$name])" -ForegroundColor Cyan
    if ($name -eq 'next-start') {
        if (-not (Test-Path (Join-Path $work 'runs\update'))) { & $exe update $work | Out-Null }
        & $exe update $work --keep
    }
    else {
        & $exe $name $work
    }
}
