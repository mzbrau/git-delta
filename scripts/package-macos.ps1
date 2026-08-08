param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $RuntimeIdentifier = "osx-arm64",
    [string] $PublishPath = "artifacts/publish/GitDelta-osx-arm64",
    [string] $ReleasePath = "artifacts/release",
    [string] $PackageId = "GitDelta",
    [string] $MainExe = "GitDelta.App",
    [string] $BundleId = "com.gitdelta.app",
    [string] $RepoUrl = "",
    [string] $GitHubToken = "",
    [switch] $Prerelease,
    [switch] $SkipDownload
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$channel = "osx"

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

    $candidates = @(
        "releases.$Channel.json",
        "RELEASES.$Channel.json"
    )

    return [bool]($Release.assets | Where-Object { $candidates -contains $_.name } | Select-Object -First 1)
}

function Test-PreviousVelopackReleaseExists {
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryUrl,
        [string] $Token = "",
        [string] $Channel = "osx",
        [switch] $IncludePrerelease
    )

    $apiUrl = Get-GitHubRepositoryApiUrl $RepositoryUrl
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "gitdelta-release-packaging"
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

function Invoke-AdHocSignPortableZip {
    param(
        [Parameter(Mandatory = $true)][string] $ZipPath,
        [Parameter(Mandatory = $true)][string] $AppBundleName
    )

    if (-not (Test-Path $ZipPath -PathType Leaf)) {
        throw "Portable zip does not exist: $ZipPath"
    }

    if (-not (Get-Command ditto -ErrorAction SilentlyContinue)) {
        throw "ditto was not found on PATH. Ad-hoc signing requires macOS."
    }

    if (-not (Get-Command codesign -ErrorAction SilentlyContinue)) {
        throw "codesign was not found on PATH. Ad-hoc signing requires macOS."
    }

    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("gitdelta-osx-sign-" + [Guid]::NewGuid().ToString("N"))
    $extractDir = Join-Path $stagingRoot "extract"
    $repackDir = Join-Path $stagingRoot "repack"
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    New-Item -ItemType Directory -Path $repackDir -Force | Out-Null

    try {
        & ditto -x -k $ZipPath $extractDir
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract portable zip with ditto (exit code $LASTEXITCODE)."
        }

        $appPath = Join-Path $extractDir $AppBundleName
        if (-not (Test-Path $appPath -PathType Container)) {
            throw "App bundle '$AppBundleName' was not found inside portable zip: $ZipPath"
        }

        # Ad-hoc sign seals Contents/_CodeSignature so Gatekeeper does not treat
        # quarantined downloads as "damaged". Not a Developer ID / notarized signature.
        & codesign --force --deep --sign - $appPath
        if ($LASTEXITCODE -ne 0) {
            throw "codesign ad-hoc signing failed with exit code $LASTEXITCODE."
        }

        & codesign --verify --deep --strict --verbose=2 $appPath
        if ($LASTEXITCODE -ne 0) {
            throw "codesign verification failed with exit code $LASTEXITCODE."
        }

        $repackAppPath = Join-Path $repackDir $AppBundleName
        Move-Item -Path $appPath -Destination $repackAppPath
        $repackedZip = Join-Path $stagingRoot "portable-repacked.zip"
        & ditto -c -k --keepParent $repackAppPath $repackedZip
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to re-zip ad-hoc signed app with ditto (exit code $LASTEXITCODE)."
        }

        Move-Item -Path $repackedZip -Destination $ZipPath -Force
        Write-Host "Ad-hoc signed portable app bundle in $ZipPath"
    } finally {
        if (Test-Path $stagingRoot) {
            Remove-Item -Path $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$publishPathResolved = Resolve-RepositoryPath $PublishPath
$releasePathResolved = Resolve-RepositoryPath $ReleasePath
$mainExePath = Join-Path $publishPathResolved $MainExe
$iconPath = Join-Path $repoRoot "src/GitDelta.App/Assets/app.icns"

if (-not (Test-Path $publishPathResolved -PathType Container)) {
    throw "Publish path does not exist: $publishPathResolved"
}

if (-not (Test-Path $mainExePath -PathType Leaf)) {
    throw "Main executable does not exist in publish output: $mainExePath"
}

if (-not (Test-Path $iconPath -PathType Leaf)) {
    throw "macOS icon does not exist: $iconPath"
}

New-Item -ItemType Directory -Path $releasePathResolved -Force | Out-Null

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Velopack CLI 'vpk' was not found on PATH. Install it with: dotnet tool install --global vpk --version 0.0.1298"
}

if (-not $SkipDownload -and -not [string]::IsNullOrWhiteSpace($RepoUrl)) {
    if (Test-PreviousVelopackReleaseExists -RepositoryUrl $RepoUrl -Token $GitHubToken -Channel $channel -IncludePrerelease:$Prerelease) {
        $downloadArgs = @(
            "download", "github",
            "--repoUrl", $RepoUrl,
            "--outputDir", $releasePathResolved,
            "--channel", $channel
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
        Write-Warning "No previous Velopack GitHub release assets were found for channel '$channel'. This is expected for the first macOS release."
    }
}

& vpk pack `
    --packId $PackageId `
    --packVersion $Version `
    --packDir $publishPathResolved `
    --mainExe $MainExe `
    --outputDir $releasePathResolved `
    --runtime $RuntimeIdentifier `
    --channel $channel `
    --packAuthors "GIT DELTA contributors" `
    --packTitle "GIT DELTA" `
    --icon $iconPath `
    --bundleId $BundleId `
    --noInst

if ($LASTEXITCODE -ne 0) {
    throw "Velopack packaging failed with exit code $LASTEXITCODE."
}

$portableZipPath = Join-Path $releasePathResolved "$PackageId-$channel-Portable.zip"
Invoke-AdHocSignPortableZip -ZipPath $portableZipPath -AppBundleName "GIT DELTA.app"

Write-Host "GitDelta macOS app assets created in $releasePathResolved"
