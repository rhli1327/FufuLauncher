param(
    [Parameter(Mandatory = $true)][string]$PluginZip,
    [Parameter(Mandatory = $true)][string]$PluginDll,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$ReleaseTag
)

$ErrorActionPreference = 'Stop'
if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid plugin source commit.' }
if ($ReleaseTag -notmatch '^[a-zA-Z0-9._-]+$') { throw 'Invalid release tag.' }
$bundleUrl = "https://github.com/rhli1327/FufuLauncher/releases/download/$ReleaseTag/FuFuPlugin.zip"
$zipHash = (Get-FileHash $PluginZip -Algorithm SHA256).Hash.ToLowerInvariant()
$dllHash = (Get-FileHash $PluginDll -Algorithm SHA256).Hash.ToLowerInvariant()
$pinFile = Join-Path $PSScriptRoot '..\FufuLauncher\Constants\PluginBuildPins.cs'
@"
namespace FufuLauncher.Constants;

// Generated from plugin source $SourceCommit and the final packaged binaries.
internal static class PluginBuildPins
{
    internal const string BundleUrl = "$bundleUrl";
    internal const string BundleSha256 = "$zipHash";
    internal const string DllSha256 = "$dllHash";
}
"@ | Set-Content $pinFile -Encoding utf8

@{
    sourceCommit = $SourceCommit
    bundleUrl = $bundleUrl
    bundleSha256 = $zipHash
    dllSha256 = $dllHash
} | ConvertTo-Json | Set-Content (Join-Path (Split-Path $PluginZip) 'plugin-build.json') -Encoding utf8
Write-Host "Pinned source-built plugin ZIP $zipHash and DLL $dllHash"
