param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
$python = Join-Path $workspace ".venv\Scripts\python.exe"
$publish = Join-Path $workspace "artifacts\publish"
$raopPublish = Join-Path $publish "RaopHost"

if (-not (Test-Path -LiteralPath $python)) {
    py -3.12 -m venv (Join-Path $workspace ".venv")
}

& $python -m pip install -r (Join-Path $workspace "src\AirBridge.RaopHost\requirements.txt") pyinstaller==6.16.0
if (-not $SkipTests) {
    # WASAPI integration checks are machine-level hardware smoke tests and can
    # block indefinitely inside the Windows audio COM API on some device states.
    dotnet test (Join-Path $workspace "AirBridge.sln") -c $Configuration --filter "FullyQualifiedName!~WasapiIntegrationTests"
}
dotnet publish (Join-Path $workspace "src\AirBridge.App\AirBridge.App.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
dotnet publish (Join-Path $workspace "src\AirBridge.NativeMessaging\AirBridge.NativeMessaging.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $publish "NativeMessaging")

New-Item -ItemType Directory -Force -Path $raopPublish | Out-Null
Push-Location (Join-Path $workspace "src\AirBridge.RaopHost")
try {
    & $python -m PyInstaller --noconfirm --clean --onefile --name AirBridge.RaopHost --collect-all pyatv --collect-all miniaudio --distpath $raopPublish --workpath (Join-Path $workspace "artifacts\pyinstaller-work") --specpath (Join-Path $workspace "artifacts") host.py
}
finally {
    Pop-Location
}

Write-Host "Published AirBridge to $publish"

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    dotnet tool restore --tool-manifest (Join-Path $workspace ".config\dotnet-tools.json")
    dotnet wix build (Join-Path $workspace "installer\wix\Package.wxs") -arch x64 -out (Join-Path $workspace "artifacts\AirBridge.msi")
}
