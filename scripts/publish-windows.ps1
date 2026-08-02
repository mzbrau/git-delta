param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
    [string] $OutputPath = "artifacts/publish/CodeReviewr-win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src/CodeReviewr.App/CodeReviewr.App.csproj"
$publishProfile = Join-Path $repoRoot "src/CodeReviewr.App/Properties/PublishProfiles/win-x64-folder.pubxml"
$publishPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repoRoot $OutputPath
}

dotnet publish $projectPath `
    --configuration $Configuration `
    -p:PublishProfile="$publishProfile" `
    -p:RuntimeIdentifier=$RuntimeIdentifier `
    --output $publishPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "CodeReviewr published to $publishPath"
