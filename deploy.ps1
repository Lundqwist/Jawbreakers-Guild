# Publishes the site and copies it to the NAS (Z: = \\LUNARBASE\docker).
# Afterwards restart the "jawbreakers" project in DSM Container Manager.
# The database in Z:\jawbreakers\data and the .env file are never touched.

$ErrorActionPreference = 'Stop'
$target = 'Z:\jawbreakers'

if (-not (Test-Path 'Z:\')) { throw 'Drive Z: (\\LUNARBASE\docker) is not mapped.' }

dotnet publish "$PSScriptRoot\Jawbreakers.csproj" -c Release -o "$PSScriptRoot\publish" --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

New-Item -ItemType Directory -Force "$target\app", "$target\data" | Out-Null
robocopy "$PSScriptRoot\publish" "$target\app" /MIR /NFL /NDL /NJH /NP
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

Copy-Item "$PSScriptRoot\deploy\docker-compose.yml" "$target\docker-compose.yml" -Force
Write-Host "Copied to $target. Restart the 'jawbreakers' project on the NAS."
exit 0
