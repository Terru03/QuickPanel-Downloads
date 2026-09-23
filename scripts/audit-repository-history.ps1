[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $repository '.git') -PathType Container)) {
    throw "RepositoryRoot is not a Git working tree."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repository 'artifacts\diagnostics\repository-history-audit.json'
}
$reportPath = [IO.Path]::GetFullPath($OutputPath)
$reportDirectory = Split-Path -Parent $reportPath
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null

function Invoke-GitLines {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowNoMatches)

    $lines = @(& git -C $repository @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not ($AllowNoMatches -and $exitCode -eq 1)) {
        throw "Git audit command failed with exit code $exitCode."
    }
    return @($lines | ForEach-Object { [string]$_ })
}

$findingsByIdentity = [ordered]@{}
function Add-Finding {
    param(
        [Parameter(Mandatory)][ValidateSet('blocker', 'review')][string]$Severity,
        [Parameter(Mandatory)][string]$Category,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Path
    )

    # Never include a matching value, line, email address, URL, or secret in the report.
    $identity = "$Severity|$Category|$Path"
    if (-not $findingsByIdentity.Contains($identity)) {
        $findingsByIdentity[$identity] = [ordered]@{
            severity = $Severity
            category = $Category
            firstCommit = $Commit
            path = $Path
        }
    }
}

$commits = @(Invoke-GitLines -Arguments @('rev-list', '--reverse', '--all'))
if ($commits.Count -eq 0) {
    throw 'Repository history contains no commits.'
}

$suspiciousPathPatterns = @(
    @{ Severity = 'blocker'; Category = 'environment-file'; Pattern = '(^|/)(\.env($|\.)|environment\.json$)' },
    @{ Severity = 'blocker'; Category = 'private-key-or-certificate-file'; Pattern = '\.(pfx|p12|pem|key|snk)$' },
    @{ Severity = 'blocker'; Category = 'browser-or-session-database'; Pattern = '(^|/)(Cookies|Login Data|Web Data|History|Sessions?)(/|$)|\.(sqlite|sqlite3|db)$' },
    @{ Severity = 'blocker'; Category = 'committed-user-settings'; Pattern = '(^|/)(settings|profiles)\.json$' },
    @{ Severity = 'blocker'; Category = 'committed-browser-profile-data'; Pattern = '(^|/)(WebView2|User Data|IconCache)(/|$)' },
    @{ Severity = 'review'; Category = 'credential-named-file'; Pattern = '(^|/)(auth|credentials?|secrets?|tokens?)\.(json|ya?ml|txt|config)$' }
)

$contentPatterns = @(
    @{ Severity = 'blocker'; Category = 'private-key-content'; Pattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    @{ Severity = 'blocker'; Category = 'known-token-signature'; Pattern = 'github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|sk-[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16}' },
    @{ Severity = 'review'; Category = 'credential-assignment'; Pattern = '(api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password|passwd)[[:space:]]*[:=][[:space:]]*["''][A-Za-z0-9_./+-]{12,}' },
    @{ Severity = 'review'; Category = 'email-address'; Pattern = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' },
    @{ Severity = 'review'; Category = 'personal-absolute-path'; Pattern = '([A-Za-z]:\\Users\\|/Users/|/home/)[^/\\[:space:]]+' },
    @{ Severity = 'review'; Category = 'private-network-url'; Pattern = 'https?://(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)' }
)

foreach ($commit in $commits) {
    $paths = @(Invoke-GitLines -Arguments @('ls-tree', '-r', '--name-only', $commit))
    foreach ($path in $paths) {
        foreach ($rule in $suspiciousPathPatterns) {
            if ($path -match $rule.Pattern) {
                Add-Finding -Severity $rule.Severity -Category $rule.Category -Commit $commit -Path $path
            }
        }
    }

    foreach ($rule in $contentPatterns) {
        $matches = @(Invoke-GitLines -Arguments @(
            'grep', '-I', '-i', '-l', '-E', '-e', $rule.Pattern, $commit, '--'
        ) -AllowNoMatches)
        foreach ($match in $matches) {
            $prefix = $commit + ':'
            $path = if ($match.StartsWith($prefix, [StringComparison]::Ordinal)) {
                $match.Substring($prefix.Length)
            } else {
                $match
            }
            Add-Finding -Severity $rule.Severity -Category $rule.Category -Commit $commit -Path $path
        }
    }
}

$authorReviewCount = 0
foreach ($commit in $commits) {
    $authorEmail = (Invoke-GitLines -Arguments @('show', '-s', '--format=%ae', $commit) | Select-Object -First 1)
    if (-not [string]::IsNullOrWhiteSpace($authorEmail) -and
        -not $authorEmail.EndsWith('@users.noreply.github.com', [StringComparison]::OrdinalIgnoreCase)) {
        $authorReviewCount++
        Add-Finding -Severity 'review' -Category 'non-github-noreply-author-email' -Commit $commit -Path '<commit metadata>'
    }
}

$findings = @($findingsByIdentity.Values)
$blockers = @($findings | Where-Object { $_.severity -eq 'blocker' })
$reviews = @($findings | Where-Object { $_.severity -eq 'review' })
$report = [ordered]@{
    schemaVersion = 1
    repository = (Split-Path -Leaf $repository)
    auditedCommitCount = $commits.Count
    auditedRefs = 'all'
    redaction = 'The report contains category, path, and first commit only; no matched content is recorded.'
    blockerCount = $blockers.Count
    reviewCount = $reviews.Count
    nonGithubNoreplyAuthorCommitCount = $authorReviewCount
    findings = $findings
}

$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($reportPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

Write-Output "Repository history audit inspected $($commits.Count) commits."
Write-Output "Redacted findings: $($blockers.Count) blocker candidate(s), $($reviews.Count) review candidate(s)."
Write-Output "Report: $reportPath"

if ($blockers.Count -gt 0) {
    exit 2
}
