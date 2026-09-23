Set-StrictMode -Version 2.0

function Assert-NoMachineSpecificPayloadText {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string[]]$AdditionalForbiddenText = @()
    )

    $needles = @($env:USERPROFILE, $RepositoryRoot) + @($AdditionalForbiddenText) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
    foreach ($file in Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $representations = @(
            [Text.Encoding]::ASCII.GetString($bytes),
            [Text.Encoding]::Unicode.GetString($bytes),
            [Text.Encoding]::BigEndianUnicode.GetString($bytes)
        )
        if ($bytes.Length -gt 1) {
            $representations += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
            $representations += [Text.Encoding]::BigEndianUnicode.GetString($bytes, 1, $bytes.Length - 1)
        }

        foreach ($needle in $needles) {
            if ($representations.Where({ $_.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 }, 'First').Count -gt 0) {
                $relative = [IO.Path]::GetRelativePath($PayloadDirectory, $file.FullName)
                throw "Release payload contains machine-specific text in '$relative'."
            }
        }
    }
}

function Assert-PublicHandoffDirectoryClean {
    param(
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string[]]$ExpectedFileNames,
        [switch]$RequireAll
    )

    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        if ($RequireAll) {
            throw "Public handoff directory does not exist: '$OutputDirectory'."
        }
        return
    }
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        throw "Public handoff path is not a directory: '$OutputDirectory'."
    }

    $entries = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    $unexpected = @($entries | Where-Object {
        $_.PSIsContainer -or $ExpectedFileNames -inotcontains $_.Name
    })
    if ($unexpected.Count -gt 0) {
        $names = @($unexpected | Select-Object -ExpandProperty Name) -join ', '
        throw "Public handoff directory contains files outside the three-file allowlist: $names"
    }

    if ($RequireAll) {
        $actual = @($entries | Where-Object { -not $_.PSIsContainer } | Select-Object -ExpandProperty Name | Sort-Object)
        $expected = @($ExpectedFileNames | Sort-Object)
        if (($actual -join "`n") -cne ($expected -join "`n")) {
            throw 'Public handoff directory does not contain exactly the expected release files.'
        }
    }
}
