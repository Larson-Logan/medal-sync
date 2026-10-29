<#
.SYNOPSIS
Creates or updates the Medal Sync GitHub project from .github/project.json.
.DESCRIPTION
A maintainer runs this from a checkout with the GitHub CLI signed in to the
account that owns the repository. Projects belong to that account, so grant the
CLI the project scope first:

    gh auth refresh -s project

The script is safe to run again. It reuses the project with the same title,
links it to the repository, adds missing fields and views, and adds every open
issue and pull request. It never deletes, renames or reorders anything; a field
whose options differ from the definition is reported for a manual edit.

Built-in project workflows, such as adding new issues automatically, can only
be turned on in the project's settings. The script lists them when it finishes;
see docs/triage.md.
#>
[CmdletBinding()]
param(
    [string]$Owner = 'Larson-Logan',
    [string]$Repository = 'medal-sync',
    [string]$Definition = (Join-Path (Split-Path -Parent $PSScriptRoot) '.github/project.json'),
    [switch]$SkipItems
)
$ErrorActionPreference = 'Stop'

function Invoke-Gh([string[]]$Arguments, $Body) {
    if ($null -ne $Body) {
        $output = $Body | ConvertTo-Json -Depth 10 -Compress | & gh @Arguments --input -
    } else {
        $output = & gh @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed. If access was refused, run: gh auth refresh -s project"
    }
    $output
}

function Invoke-GhJson([string[]]$Arguments, $Body) {
    $text = (Invoke-Gh $Arguments $Body) -join "`n"
    if ($text) { $text | ConvertFrom-Json } else { $null }
}

# Every element of every page, printed one compact JSON object per line.
function Get-GhPages([string]$Path) {
    @(Invoke-Gh @('api', '--paginate', $Path, '--jq', '.[] | tojson') | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Get-FieldId($Fields, [string]$Name) {
    $field = $Fields | Where-Object name -ceq $Name | Select-Object -First 1
    if (-not $field) { throw "Project has no field named '$Name'. Check .github/project.json." }
    [long]$field.id
}

$spec = Get-Content $Definition -Raw | ConvertFrom-Json
$user = Invoke-GhJson @('api', "users/$Owner")
if ($user.type -ne 'User') { throw "$Owner is not a user account; this script manages user-owned projects." }

# Find or create the project.
$projects = (Invoke-GhJson @('project', 'list', '--owner', $Owner, '--format', 'json', '--limit', '200')).projects
$project = @($projects | Where-Object title -ceq $spec.title)
if ($project.Count -gt 1) { throw "More than one project is titled '$($spec.title)'. Rename or close the extra one first." }
if ($project.Count -eq 1) {
    $project = $project[0]
    Write-Output "Using project #$($project.number): $($project.url)"
} else {
    $project = Invoke-GhJson @('project', 'create', '--owner', $Owner, '--title', $spec.title, '--format', 'json')
    Write-Output "Created project #$($project.number): $($project.url)"
}
$number = [string]$project.number
Invoke-Gh @('project', 'edit', $number, '--owner', $Owner, '--description', $spec.description, '--visibility', $spec.visibility) | Out-Null

# Link the repository so the project appears on its Projects tab.
$linkQuery = 'query($owner:String!,$name:String!){repository(owner:$owner,name:$name){projectsV2(first:100){nodes{number owner{... on User{login}}}}}}'
$linked = Invoke-GhJson @('api', 'graphql', '-f', "query=$linkQuery", '-f', "owner=$Owner", '-f', "name=$Repository")
$isLinked = $linked.data.repository.projectsV2.nodes | Where-Object { $_.number -eq $project.number -and $_.owner.login -eq $Owner }
if (-not $isLinked) {
    Invoke-Gh @('project', 'link', $number, '--owner', $Owner, '--repo', $Repository) | Out-Null
    Write-Output "Linked $Owner/$Repository"
}

# Fields. GitHub provides Status, Milestone, Labels and the other built-in fields.
$fieldsPath = "users/$Owner/projectsV2/$number/fields?per_page=100"
$fields = Get-GhPages $fieldsPath
foreach ($wanted in $spec.fields) {
    $existing = $fields | Where-Object name -ceq $wanted.name | Select-Object -First 1
    if (-not $existing) {
        $body = [ordered]@{
            name = $wanted.name
            data_type = $wanted.data_type
            single_select_options = @($wanted.options | ForEach-Object {
                [ordered]@{ name = $_.name; color = $_.color; description = $_.description }
            })
        }
        Invoke-Gh @('api', '--method', 'POST', "users/$Owner/projectsV2/$number/fields") $body | Out-Null
        Write-Output "Added field $($wanted.name)"
        continue
    }
    $have = @($existing.options | ForEach-Object { $_.name.raw }) -join ', '
    $want = @($wanted.options | ForEach-Object name) -join ', '
    if ($have -cne $want) {
        Write-Warning "Field $($wanted.name) has options [$have]; the definition lists [$want]. Edit them in the project settings."
    }
}
$fields = Get-GhPages $fieldsPath

# Views. REST can create views but not list them, so names are read through GraphQL.
$viewQuery = 'query($owner:String!,$number:Int!){user(login:$owner){projectV2(number:$number){views(first:100){nodes{name}}}}}'
$views = Invoke-GhJson @('api', 'graphql', '-f', "query=$viewQuery", '-f', "owner=$Owner", '-F', "number=$number")
$viewNames = @($views.data.user.projectV2.views.nodes | ForEach-Object name)
foreach ($view in $spec.views) {
    if ($viewNames -ccontains $view.name) { continue }
    $body = [ordered]@{ name = $view.name; layout = $view.layout; filter = $view.filter }
    if ($view.fields) { $body.visible_fields = @($view.fields | ForEach-Object { Get-FieldId $fields $_ }) }
    if ($view.group_by) { $body.group_by = @(Get-FieldId $fields $view.group_by) }
    if ($view.vertical_group_by) { $body.vertical_group_by = @(Get-FieldId $fields $view.vertical_group_by) }
    if ($view.sort_by) {
        $body.sort_by = @($view.sort_by | ForEach-Object { , @((Get-FieldId $fields $_[0]), $_[1]) })
    }
    # The docs name this parameter user_id, but GitHub answers 404 for the numeric ID; it takes the login.
    Invoke-Gh @('api', '--method', 'POST', "users/$Owner/projectsV2/$number/views") $body | Out-Null
    Write-Output "Added view $($view.name)"
}

# Open issues and pull requests. GitHub refuses to add an item that is already present.
if (-not $SkipItems) {
    $present = @(Get-GhPages "users/$Owner/projectsV2/$number/items?per_page=100" | ForEach-Object { $_.content.html_url })
    $open = Get-GhPages "repos/$Owner/$Repository/issues?state=open&per_page=100"
    foreach ($item in $open | Where-Object { $_.html_url -cnotin $present }) {
        $body = [ordered]@{
            type = if ($item.pull_request) { 'PullRequest' } else { 'Issue' }
            owner = $Owner
            repo = $Repository
            number = [int]$item.number
        }
        Invoke-Gh @('api', '--method', 'POST', "users/$Owner/projectsV2/$number/items") $body | Out-Null
    }
    Write-Output "Added or confirmed $($open.Count) open issues and pull requests"
}

Write-Output @"

Project ready: $($project.url)
Finish in the project's Workflows settings (these cannot be set from the CLI):
  - Auto-add to project: repository $Repository, filter is:issue,pr is:open
  - Item added to project: set Status to Todo
  - Item closed and Pull request merged: set Status to Done
  - Item reopened: set Status to In Progress
  - Auto-archive items: is:closed updated:<@today-30d
Delete or rename the default 'View 1' view if you do not need it.
"@
