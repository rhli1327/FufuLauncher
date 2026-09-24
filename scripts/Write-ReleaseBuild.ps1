param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$BuildId,
    [Parameter(Mandatory)][string]$SourceCommit,
    [ValidateSet('stable', 'preview')][string]$Channel = 'stable'
)
$ErrorActionPreference = 'Stop'
$files = @(
    'FufuLauncher.dll', 'UpdateFufuLauncher.exe', 'CaptureApp.exe',
    'Launcher.dll', 'Launcher_2.exe', 'Plugins/FuFuPlugin/FufuLauncher.UnlockerIsland.dll',
    'Assets/Launcher/hash.txt'
)
$hashes = [ordered]@{}
$lines = foreach ($relativePath in $files) {
    $hash = (Get-FileHash (Join-Path $OutputDirectory $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashes[$relativePath] = $hash
    "$relativePath=$hash"
}
$contentBytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
$contentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($contentBytes)).ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    buildId = $BuildId
    sourceCommit = $SourceCommit
    contentSha256 = $contentHash
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    channel = $Channel
    files = $hashes
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'release-build.json') -Encoding utf8NoBOM
