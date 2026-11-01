<#
.SYNOPSIS
Applies the rulesets in .github/rulesets and makes dev the default branch.
.DESCRIPTION
A maintainer runs this from a checkout with the GitHub CLI signed in as a
repository administrator. It is safe to run again:

1. Creates dev from main if dev does not exist yet.
2. Creates or updates each ruleset in .github/rulesets, matched by name.
3. Warns about any other ruleset, which still applies on top of these. With
   -RemoveOthers it deletes them instead, after the new ones are in place, so
   main is never left unprotected.
4. Sets the merge options the rulesets rely on: squash for pull requests into
   dev and merge commits for promotions into main, both titled with the pull
   request title and described by its body. Rebase merging, which no ruleset
   allows, is turned off. Enables the Update branch button and automatic
   deletion of merged topic branches.
5. Makes dev the default branch, unless -KeepDefaultBranch is given, and lists
   open pull requests that still target main from another branch.

See docs/security-maintenance.md for what each ruleset enforces.
#>
[CmdletBinding()]
param(
    [string]$Repository = 'Larson-Logan/medal-sync',
    [string]$Definitions = (Join-Path (Split-Path -Parent $PSScriptRoot) '.github/rulesets'),
    [switch]$RemoveOthers,
    [switch]$KeepDefaultBranch
)
$ErrorActionPreference = 'Stop'

function Invoke-Gh([string[]]$Arguments, [string]$Body) {
    if ($Body) {
        $output = $Body | & gh @Arguments --input -
    } else {
        $output = & gh @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed. Sign the GitHub CLI in as a repository administrator: gh auth login"
    }
    $output
}

# Every element of every page, printed one compact JSON object per line.
function Get-GhPages([string]$Path) {
    @(Invoke-Gh @('api', '--paginate', $Path, '--jq', '.[] | tojson') | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

$rulesets = @(Get-ChildItem -LiteralPath $Definitions -Filter '*.json' -File | Sort-Object Name | ForEach-Object {
    $body = Get-Content -LiteralPath $_.FullName -Raw
    [pscustomobject]@{ Name = ($body | ConvertFrom-Json).name; Body = $body }
})
if (-not $rulesets) { throw "No ruleset definitions found in $Definitions." }
$duplicate = $rulesets | Group-Object Name | Where-Object Count -gt 1 | Select-Object -First 1
if ($duplicate) { throw "More than one definition is named '$($duplicate.Name)'." }

# 1. The dev branch.
& gh api "repos/$Repository/branches/dev" --silent 2>$null
if ($LASTEXITCODE -ne 0) {
    $sha = (Invoke-Gh @('api', "repos/$Repository/branches/main", '--jq', '.commit.sha')).Trim()
    Invoke-Gh @('api', '--method', 'POST', "repos/$Repository/git/refs", '-f', 'ref=refs/heads/dev', '-f', "sha=$sha", '--silent') | Out-Null
    Write-Output "Created dev from main at $($sha.Substring(0, 12))."
}
$global:LASTEXITCODE = 0

# 2. The defined rulesets.
$existing = @(Get-GhPages "repos/$Repository/rulesets?includes_parents=false" | Where-Object source_type -eq 'Repository')
foreach ($ruleset in $rulesets) {
    $match = @($existing | Where-Object name -ceq $ruleset.Name)
    if ($match.Count -gt 1) { throw "More than one ruleset is named '$($ruleset.Name)'. Delete the extra ones in Settings > Rules first." }
    if ($match) {
        Invoke-Gh @('api', '--method', 'PUT', "repos/$Repository/rulesets/$($match[0].id)", '--silent') $ruleset.Body | Out-Null
        Write-Output "Updated ruleset '$($ruleset.Name)'."
    } else {
        Invoke-Gh @('api', '--method', 'POST', "repos/$Repository/rulesets", '--silent') $ruleset.Body | Out-Null
        Write-Output "Created ruleset '$($ruleset.Name)'."
    }
}

# 3. Rulesets that are not defined here.
foreach ($other in $existing | Where-Object { $_.name -cnotin $rulesets.Name }) {
    if ($RemoveOthers) {
        Invoke-Gh @('api', '--method', 'DELETE', "repos/$Repository/rulesets/$($other.id)", '--silent') | Out-Null
        Write-Output "Deleted ruleset '$($other.name)', which .github/rulesets does not define."
    } else {
        Write-Warning ("Ruleset '$($other.name)' is not defined in .github/rulesets and still applies. " +
            'If it targets the default branch, it will also protect dev once dev is the default. ' +
            'Review it in Settings > Rules, or run again with -RemoveOthers.')
    }
}

# 4 and 5. Merge options and the default branch.
$settings = @('api', '--method', 'PATCH', "repos/$Repository",
    '-F', 'allow_merge_commit=true', '-F', 'allow_squash_merge=true', '-F', 'allow_rebase_merge=false',
    '-f', 'merge_commit_title=PR_TITLE', '-f', 'merge_commit_message=PR_BODY',
    '-f', 'squash_merge_commit_title=PR_TITLE', '-f', 'squash_merge_commit_message=PR_BODY',
    '-F', 'allow_update_branch=true', '-F', 'delete_branch_on_merge=true', '--silent')
if (-not $KeepDefaultBranch) { $settings += @('-f', 'default_branch=dev') }
Invoke-Gh $settings | Out-Null
if (-not $KeepDefaultBranch) {
    Write-Output 'dev is the default branch. New pull requests target it.'
    $stale = @(Get-GhPages "repos/$Repository/pulls?state=open&base=main&per_page=100" | Where-Object { $_.head.ref -ne 'dev' -or $_.head.repo.full_name -ne $Repository })
    foreach ($pull in $stale) {
        Write-Warning "#$($pull.number) targets main from $($pull.head.label); only dev can merge into main. Retarget it: gh pr edit $($pull.number) --repo $Repository --base dev"
    }
}
