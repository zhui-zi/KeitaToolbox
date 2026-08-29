[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$SkipNpmInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-LastExitCode([string]$Step)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try
{
    if (!$SkipRestore)
    {
        & dotnet restore 'KeitaToolbox\KeitaToolbox.csproj'
        Assert-LastExitCode 'Plugin restore'
    }

    & dotnet build 'KeitaToolbox\KeitaToolbox.csproj' `
        --configuration Release `
        --no-restore `
        --no-incremental
    Assert-LastExitCode 'Plugin build'

    $project = [xml](Get-Content -LiteralPath 'KeitaToolbox\KeitaToolbox.csproj' -Raw -Encoding utf8)
    $projectVersion = [string]$project.Project.PropertyGroup.Version
    $buildDirectory = 'KeitaToolbox\bin\Release'
    $buildDll = Join-Path $buildDirectory 'KeitaToolbox.dll'
    $packagePath = Join-Path $buildDirectory 'KeitaToolbox\latest.zip'
    $dllVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
        (Resolve-Path -LiteralPath $buildDll)).Version.ToString()

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = [System.IO.Compression.ZipFile]::OpenRead(
        (Resolve-Path -LiteralPath $packagePath))
    try
    {
        $manifestEntry = $package.Entries |
            Where-Object FullName -eq 'KeitaToolbox.json'
        $dllEntry = $package.Entries |
            Where-Object FullName -eq 'KeitaToolbox.dll'
        if ($null -eq $manifestEntry -or $null -eq $dllEntry)
        {
            throw 'Release package is missing its manifest or plugin DLL.'
        }

        $manifestReader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try
        {
            $packageVersion = [string](
                $manifestReader.ReadToEnd() | ConvertFrom-Json).AssemblyVersion
        }
        finally
        {
            $manifestReader.Dispose()
        }

        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $dllStream = $dllEntry.Open()
        try
        {
            $packageDllHash = [Convert]::ToHexString(
                $sha256.ComputeHash($dllStream))
        }
        finally
        {
            $dllStream.Dispose()
            $sha256.Dispose()
        }
    }
    finally
    {
        $package.Dispose()
    }

    $buildDllHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildDll).Hash
    if ($projectVersion -ne $dllVersion -or
        $projectVersion -ne $packageVersion)
    {
        throw "Release version mismatch: project=$projectVersion DLL=$dllVersion package=$packageVersion."
    }
    if ($buildDllHash -ne $packageDllHash)
    {
        throw 'Release package DLL does not match the build output.'
    }

    & dotnet test `
        --project 'tests\KeitaToolbox.CoreChecks\KeitaToolbox.CoreChecks.csproj' `
        --configuration Release `
        --minimum-expected-tests 31
    Assert-LastExitCode 'Core tests'

    $architectureLimits = @{
        'KeitaToolbox\Plugin.Settings.cs' = 400
        'KeitaToolbox\OccultPotFeature.cs' = 5000
        'KeitaToolbox\OccultPotFeature.AutoDig.cs' = 3500
        'KeitaToolbox\OccultPotFeature.CofferHunt.cs' = 1000
        'KeitaToolbox\OccultPotFeature.TrackerModels.cs' = 200
        'KeitaToolbox\AsyncOperationGate.cs' = 100
    }
    foreach ($entry in $architectureLimits.GetEnumerator())
    {
        $lineCount = (Get-Content -LiteralPath $entry.Key).Count
        if ($lineCount -gt $entry.Value)
        {
            throw "$($entry.Key) exceeded its $($entry.Value)-line architecture limit."
        }
    }

    if (!$SkipNpmInstall)
    {
        & npm.cmd ci --prefix 'workers\keita-toolbox-stats'
        Assert-LastExitCode 'Stats Worker install'
    }

    & npm.cmd run check --prefix 'workers\keita-toolbox-stats'
    Assert-LastExitCode 'Stats Worker checks'

    & git diff --check
    Assert-LastExitCode 'Git diff check'

    Write-Host 'All verification checks passed.'
}
finally
{
    Pop-Location
}
