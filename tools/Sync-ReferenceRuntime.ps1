param(
    [Parameter(Mandatory = $true)][string] $ReferenceRoot,
    [Parameter(Mandatory = $true)][string] $Revision
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$referenceRoot = [IO.Path]::GetFullPath($ReferenceRoot)
$engineTarget = Join-Path $repositoryRoot "engine"
$listsTarget = Join-Path $repositoryRoot "lists"

foreach ($pair in @(@((Join-Path $referenceRoot "bin"), $engineTarget), @((Join-Path $referenceRoot "lists"), $listsTarget))) {
    $source = $pair[0]
    $target = $pair[1]
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing reference directory: $source" }
    [IO.Directory]::CreateDirectory($target) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File) {
        [IO.File]::Copy($file.FullName, (Join-Path $target $file.Name), $true)
    }
}

# The source tree keeps the downloaded IPSet in .backup when its interactive
# switch is in "none" mode. Rafferty has no such switch yet, so ship loaded mode.
$ipset = Join-Path $listsTarget "ipset-all.txt"
$ipsetBackup = Join-Path $listsTarget "ipset-all.txt.backup"
if ((Test-Path -LiteralPath $ipsetBackup) -and ((Get-Item -LiteralPath $ipset).Length -lt 1024)) {
    [IO.File]::Copy($ipsetBackup, $ipset, $true)
}

[IO.File]::Copy((Join-Path $referenceRoot "LICENSE.txt"), (Join-Path $repositoryRoot "licenses\Reference-LICENSE.txt"), $true)

$files = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $engineTarget -File | Where-Object Name -ne "engine-manifest.json" | Sort-Object Name) {
    $files[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{
    source = "official upstream repository revision $Revision"
    releaseArchiveSha256 = "source-tree-$Revision"
    files = $files
}
[IO.File]::WriteAllText((Join-Path $engineTarget "engine-manifest.json"), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

dotnet run --project (Join-Path $repositoryRoot "tools\Rafferty.StrategyImporter\Rafferty.StrategyImporter.csproj") --configuration Release -- $referenceRoot (Join-Path $repositoryRoot "strategies\strategies.json") $Revision
if ($LASTEXITCODE -ne 0) { throw "Strategy import failed with exit code $LASTEXITCODE" }

Write-Host "Runtime, lists, license, integrity manifest and strategies synchronized from $Revision."
