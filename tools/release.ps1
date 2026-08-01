param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\AIUsageMonitor\AIUsageMonitor.csproj'
$output = Join-Path $root 'publish\win-x64'
dotnet publish $project -c Release -o $output
git add $root
git commit -m "release: v$Version"
git tag -a "v$Version" -m "AI Usage Monitor $Version"
git push origin main --follow-tags
gh release create "v$Version" (Join-Path $output 'AIUsageMonitor.exe') --repo jpribil/ai-usage --title "AI Usage Monitor $Version" --generate-notes
