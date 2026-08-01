param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\AIUsageMonitor\AIUsageMonitor.csproj'
$output = Join-Path $root 'publish\win-x64'
$publishedExe = Join-Path $output 'AIUsageMonitor.exe'
Get-Process AIUsageMonitor -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $publishedExe } |
    Stop-Process -Force
dotnet publish $project -c Release -o $output
git add $root
git commit -m "release: v$Version"
git tag -a "v$Version" -m "AI Usage Monitor $Version"
git push origin main --follow-tags
# The tag push triggers .github/workflows/release.yml, which creates the release and uploads the EXE.
