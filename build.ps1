param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$publishDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$projectDirectory = Join-Path $PSScriptRoot 'FeatherPlayer'

Push-Location -LiteralPath $projectDirectory
try {
    dotnet publish FeatherPlayer.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $publishDirectory -Recurse -Force
    Write-Output "Published to $publishDirectory"
}
finally {
    Pop-Location
}
