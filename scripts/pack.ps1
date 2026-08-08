param(
    [string] $Version = '1.0.0',
    [string] $PclCoreVersion = '2026.07.2',
    [string] $PclSdkRoot = '',
    [string] $OutputDir = 'artifacts',
    [switch] $NetworkSmoke
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PclSdkRoot)) {
    $PclSdkRoot = Join-Path $root '..\sdk\2026.07.2\x64'
}
$PclSdkRoot = [IO.Path]::GetFullPath($PclSdkRoot)

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}
if ($PclCoreVersion -notmatch '^\d{4}\.\d{2}\.\d+$') {
    throw "Invalid PCL Core version: $PclCoreVersion"
}

$project = Join-Path $root 'src\Hysteria2Link.Plugin\Hysteria2Link.Plugin.csproj'
$testProject = Join-Path $root 'tests\Hysteria2Link.Smoke\Hysteria2Link.Smoke.csproj'
$artifacts = Join-Path $root $OutputDir
$stage = Join-Path $artifacts 'xjh2009.hysteria.link'
$artifact = Join-Path $artifacts "xjh2009.hysteria.link-$Version-anycpu.pclx"
$zipPath = [IO.Path]::ChangeExtension($artifact, '.zip')
$properties = @(
    '--property:Platform=AnyCPU',
    "--property:PluginVersion=$Version",
    "--property:PclSdkRoot=$PclSdkRoot",
    "--property:PluginPackageDir=$stage"
)

dotnet build $project -t:PublishPlugin --configuration Release @properties
if ($LASTEXITCODE -ne 0) { throw 'Hysteria plugin build failed.' }

$testArguments = if ($NetworkSmoke) { @('--', '--network') } else { @() }
dotnet run --project $testProject --configuration Release @properties @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Hysteria plugin smoke test failed.' }

$pluginJsonPath = Join-Path $stage 'plugin.json'
$pluginJson = [IO.File]::ReadAllText($pluginJsonPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
$pluginJson.version = $Version
$pluginJson.pclCoreVersion = $PclCoreVersion
$pluginJsonContent = ($pluginJson | ConvertTo-Json -Depth 20) + [Environment]::NewLine
[IO.File]::WriteAllText($pluginJsonPath, $pluginJsonContent, [Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
Move-Item -LiteralPath $zipPath -Destination $artifact

$archive = [IO.Compression.ZipFile]::OpenRead($artifact)
try {
    $entries = $archive.Entries.FullName | ForEach-Object { $_ -replace '\\', '/' }
    foreach ($required in @(
        'plugin.json',
        'README.md',
        'LICENSE',
        'lib/PCL.Hysteria2LinkPlugin.dll',
        'mixins/xjh2009.hysteria.link.mixins.json'
    )) {
        if ($entries -notcontains $required) { throw "PCLX is missing $required" }
    }
    if ($entries | Where-Object { $_ -match '(^|/)PCL\.Core\.dll$|(^|/)Plain Craft Launcher 2\.dll$|(^|/)PCL\.Plugin\.Abstractions\.dll$|(^|/)FluentValidation\.dll$|(^|/)System\.Text\.Json\.dll$|(^|/)System\.Security\.Cryptography\.ProtectedData\.dll$|(^|/)hysteria\.exe$|\.pdb$' }) {
        throw 'PCLX contains a forbidden file.'
    }
} finally {
    $archive.Dispose()
}

$sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PCLX: $artifact"
Write-Host "SHA256: $sha256"
