param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $RuntimeIdentifier = "win-x64",
    [string] $PublishPath = "artifacts/publish/CodeReviewr-win-x64",
    [string] $ReleasePath = "artifacts/release",
    [string] $PackageId = "CodeReviewr",
    [string] $MainExe = "CodeReviewr.App.exe",
    [string] $RepoUrl = "",
    [string] $GitHubToken = "",
    [switch] $Prerelease,
    [switch] $SkipDownload
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function Get-GitHubRepositoryApiUrl {
    param([Parameter(Mandatory = $true)][string] $RepositoryUrl)

    if ($RepositoryUrl -notmatch '^https://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$') {
        throw "Repository URL must be a GitHub HTTPS repository URL: $RepositoryUrl"
    }

    return "https://api.github.com/repos/$($Matches[1])/$($Matches[2])"
}

function Test-ReleaseHasVelopackChannelAssets {
    param(
        [Parameter(Mandatory = $true)] $Release,
        [Parameter(Mandatory = $true)][string] $Channel
    )

    $releaseMetadataAsset = "RELEASES.$Channel.json"
    return [bool]($Release.assets | Where-Object { $_.name -eq $releaseMetadataAsset } | Select-Object -First 1)
}

function Test-PreviousVelopackReleaseExists {
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryUrl,
        [string] $Token = "",
        [string] $Channel = "win",
        [switch] $IncludePrerelease
    )

    $apiUrl = Get-GitHubRepositoryApiUrl $RepositoryUrl
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "codereviewr-release-packaging"
    }

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers["Authorization"] = "Bearer $Token"
    }

    try {
        if ($IncludePrerelease) {
            $releases = Invoke-RestMethod -Uri "$apiUrl/releases?per_page=100" -Headers $headers
            $release = $releases | Where-Object { $_.prerelease } | Select-Object -First 1
            return [bool]($release -and (Test-ReleaseHasVelopackChannelAssets -Release $release -Channel $Channel))
        }

        $release = Invoke-RestMethod -Uri "$apiUrl/releases/latest" -Headers $headers
        return Test-ReleaseHasVelopackChannelAssets -Release $release -Channel $Channel
    } catch {
        $statusCode = if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            [int]$_.Exception.Response.StatusCode
        } else {
            $null
        }

        if ($statusCode -eq 404) {
            return $false
        }

        throw
    }
}

$publishPathResolved = Resolve-RepositoryPath $PublishPath
$releasePathResolved = Resolve-RepositoryPath $ReleasePath
$mainExePath = Join-Path $publishPathResolved $MainExe
$iconPath = Join-Path $repoRoot "src/CodeReviewr.App/Assets/app.ico"

if (-not (Test-Path $publishPathResolved -PathType Container)) {
    throw "Publish path does not exist: $publishPathResolved"
}

if (-not (Test-Path $mainExePath -PathType Leaf)) {
    throw "Main executable does not exist in publish output: $mainExePath"
}

New-Item -ItemType Directory -Path $releasePathResolved -Force | Out-Null

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Velopack CLI 'vpk' was not found on PATH. Install it with: dotnet tool install --global vpk --version 0.0.1298"
}

if (-not $SkipDownload -and -not [string]::IsNullOrWhiteSpace($RepoUrl)) {
    if (Test-PreviousVelopackReleaseExists -RepositoryUrl $RepoUrl -Token $GitHubToken -Channel "win" -IncludePrerelease:$Prerelease) {
        $downloadArgs = @(
            "download", "github",
            "--repoUrl", $RepoUrl,
            "--outputDir", $releasePathResolved,
            "--channel", "win"
        )

        if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
            $downloadArgs += @("--token", $GitHubToken)
        }

        if ($Prerelease) {
            $downloadArgs += "--pre"
        }

        & vpk @downloadArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Velopack previous-release download failed with exit code $LASTEXITCODE."
        }
    } else {
        Write-Warning "No previous Velopack GitHub release assets were found. This is expected for the first release."
    }
}

& vpk pack `
    --packId $PackageId `
    --packVersion $Version `
    --packDir $publishPathResolved `
    --mainExe $MainExe `
    --outputDir $releasePathResolved `
    --runtime $RuntimeIdentifier `
    --channel win `
    --packAuthors "CodeReviewr contributors" `
    --packTitle "CodeReviewr" `
    --icon $iconPath

if ($LASTEXITCODE -ne 0) {
    throw "Velopack packaging failed with exit code $LASTEXITCODE."
}

Write-Host "CodeReviewr installer assets created in $releasePathResolved"
