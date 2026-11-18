$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "TraceWeave requires .NET 10 SDK."
    }
    $dotnet = $command.Source
}

$project = Join-Path $root "src\TraceWeave.App\TraceWeave.App.csproj"
$output = Join-Path $root "artifacts\TraceWeave-win-x64"

Push-Location $root
try {
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host ""
    Write-Host "TraceWeave Windows x64 package:" -ForegroundColor Green
    Write-Host (Join-Path $output "TraceWeave.App.exe")
}
finally {
    Pop-Location
}
