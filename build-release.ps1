[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'GamePatchDepot.slnx'
$publisherProject = Join-Path $PSScriptRoot 'src\PatchPublisher\PatchPublisher.csproj'
$managerProject = Join-Path $PSScriptRoot 'src\PatchManager\PatchManager.csproj'
$output = Join-Path $PSScriptRoot 'dist\win-x64'

& (Join-Path $PSScriptRoot 'setup-patch-tools.ps1')

dotnet restore $solution --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $solution --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test $solution --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $PSScriptRoot 'tests\PatchManager.SmokeTests') --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet restore $publisherProject --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet restore $managerProject --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $publisherProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --no-restore `
    -p:PublishSingleFile=true `
    --output $output

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $managerProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --no-restore `
    -p:PublishSingleFile=true `
    --output $output

if ($LASTEXITCODE -eq 0) {
    Write-Host "發布工具與管理 UI 已輸出至：$output"
}

exit $LASTEXITCODE
