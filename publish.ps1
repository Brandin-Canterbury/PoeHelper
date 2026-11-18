# Builds a self-contained single-file PoeHelper.exe into .\publish (no .NET runtime needed on the target PC).
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\src\PoeHelper.App\PoeHelper.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PSScriptRoot\publish"
Write-Host "Done: $PSScriptRoot\publish\PoeHelper.exe"
