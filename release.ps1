<#
.SYNOPSIS
    Cuts a new MacosUtils release by pushing a v* tag.

.DESCRIPTION
    Checks that you are on an up-to-date, clean main branch and that the tag
    does not exist yet, then creates an annotated tag and pushes it. The push
    starts .github/workflows/release.yml, which builds MacosUtils.app (zipped)
    and the mutils command-line tool for osx-arm64 and osx-x64 and publishes
    the GitHub release with the four files attached.

.EXAMPLE
    .\release.ps1 0.0.2

.EXAMPLE
    .\release.ps1 v0.1.0 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Git {
    $output = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed:`n$output" }
    $output
}

$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
if ($tag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a valid version. Use e.g. 0.0.2 or v1.2.0-beta.1."
}

$branch = Invoke-Git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { throw "Switch to main first (current branch: $branch)." }

if (Invoke-Git status --porcelain) { throw 'Working tree has uncommitted changes. Commit or stash them first.' }

Invoke-Git fetch origin main --tags | Out-Null
$local = Invoke-Git rev-parse HEAD
$remote = Invoke-Git rev-parse origin/main
if ($local -ne $remote) { throw 'Local main differs from origin/main. Run git pull (and push any local commits) first.' }

if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists." }
if (Invoke-Git ls-remote --tags origin "refs/tags/$tag") { throw "Tag $tag already exists on origin." }

$last = & git describe --tags --abbrev=0 2>$null
Write-Host "Releasing $tag from $($local.Substring(0, 7))$(if ($last) { " (previous: $last)" })"

if ($PSCmdlet.ShouldProcess($tag, 'Create and push tag (publishes a public GitHub release)')) {
    Invoke-Git tag -a $tag -m "Release $tag" | Out-Null
    Invoke-Git push origin $tag | Out-Null
    Write-Host "Pushed $tag. Build:   https://github.com/anilkay/MacosUtils/actions/workflows/release.yml"
    Write-Host "Release (when the build finishes): https://github.com/anilkay/MacosUtils/releases/tag/$tag"
}
