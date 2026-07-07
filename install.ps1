$ErrorActionPreference = 'Stop'

$installDir = 'C:\Programs'
$stageDir = Join-Path $PSScriptRoot 'publish\gpt'
$projectPath = Join-Path $PSScriptRoot 'gptcheck.csproj'
$stagedExe = Join-Path $stageDir 'gpt.exe'
$targetExe = Join-Path $installDir 'gpt.exe'

dotnet publish $projectPath -c Release -r win-x64 -o $stageDir -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true

if (-not (Test-Path -LiteralPath $stagedExe)) {
    throw "Published executable was not found: $stagedExe"
}

Get-Process -Name gpt -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $stagedExe -Destination $targetExe -Force

Start-Process -FilePath $targetExe
Write-Host "Installed and started $targetExe"
