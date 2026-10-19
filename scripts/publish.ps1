param(
    [string]$DotnetPath = 'dotnet',
    [ValidatePattern('^v?\d+\.\d+\.\d+([-.][a-zA-Z0-9.]+)?$')]
    [string]$Version = 'v0.1.0'
)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactPath = Join-Path $taskRoot 'artifacts'
$publishPath = Join-Path $artifactPath 'publish-win-x64'
$packageVersion = $Version.TrimStart('v')
New-Item -ItemType Directory -Force -Path $artifactPath | Out-Null

& $DotnetPath publish (Join-Path $taskRoot 'src/LexiLens.App/LexiLens.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:Version=$packageVersion -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.en.md') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $publishPath -Recurse -Force
$sdkRoot = Split-Path -Parent (Get-Command $DotnetPath).Source
foreach ($noticeName in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $noticePath = Join-Path $sdkRoot $noticeName
    if (Test-Path -LiteralPath $noticePath) {
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $publishPath ('.NET-' + $noticeName))
    }
}
$archivePath = Join-Path $artifactPath ('LexiLens-' + $Version + '-win-x64.zip')
Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $archivePath -Force
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
($hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($archivePath)) | Set-Content -LiteralPath (Join-Path $artifactPath 'SHA256SUMS.txt') -Encoding ascii
Write-Output $archivePath
