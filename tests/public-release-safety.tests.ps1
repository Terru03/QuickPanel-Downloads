[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'scripts\public-release-safety.ps1')

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Message
    )
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('QuickPanel.PublicReleaseSafety.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $payload = Join-Path $root 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $privatePath = 'C:\Users\Private Person\source\QuickPanel'

    $asciiBytes = [byte[]](0, 1, 2) + [Text.Encoding]::ASCII.GetBytes($privatePath) + [byte[]](253, 254, 255)
    [IO.File]::WriteAllBytes((Join-Path $payload 'embedded-ascii.dll'), $asciiBytes)
    Assert-Rejected `
        -Action { Assert-NoMachineSpecificPayloadText -PayloadDirectory $payload -RepositoryRoot $repo -AdditionalForbiddenText $privatePath } `
        -Message 'Binary ASCII machine path was accepted.'

    Remove-Item -LiteralPath (Join-Path $payload 'embedded-ascii.dll') -Force
    $unicodeBytes = [byte[]](7) + [Text.Encoding]::Unicode.GetBytes($privatePath) + [byte[]](8)
    [IO.File]::WriteAllBytes((Join-Path $payload 'embedded-unicode.exe'), $unicodeBytes)
    Assert-Rejected `
        -Action { Assert-NoMachineSpecificPayloadText -PayloadDirectory $payload -RepositoryRoot $repo -AdditionalForbiddenText $privatePath } `
        -Message 'Binary UTF-16 machine path was accepted.'

    $handoff = Join-Path $root 'handoff'
    New-Item -ItemType Directory -Path $handoff -Force | Out-Null
    $expected = @('QuickPanel-2.5.0-win-x64.zip', 'version.json', 'SHA256SUMS.txt')
    foreach ($name in $expected) {
        [IO.File]::WriteAllText((Join-Path $handoff $name), 'fixture')
    }
    Assert-PublicHandoffDirectoryClean -OutputDirectory $handoff -ExpectedFileNames $expected -RequireAll

    [IO.File]::WriteAllText((Join-Path $handoff 'verification-report.json'), '{"privatePath":"C:\\Users\\Private Person"}')
    Assert-Rejected `
        -Action { Assert-PublicHandoffDirectoryClean -OutputDirectory $handoff -ExpectedFileNames $expected -RequireAll } `
        -Message 'Public handoff accepted a verification report with a personal path.'

    Write-Output 'Public release safety tests pass.'
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
