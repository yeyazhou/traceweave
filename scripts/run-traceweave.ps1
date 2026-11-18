$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "TraceWeave requires .NET 10 SDK. Install it or place dotnet.exe under $env:USERPROFILE\.dotnet."
    }
    $dotnet = $command.Source
}

$project = Join-Path $root "src\TraceWeave.App\TraceWeave.App.csproj"
$solution = Join-Path $root "TraceWeave.sln"

Push-Location $root
try {
    & $dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnet run --project $project -c Debug --no-restore
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
