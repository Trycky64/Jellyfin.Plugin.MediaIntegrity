param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'Jellyfin.Plugin.MediaIntegrity.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dllPath = Join-Path $repoRoot "bin/$Configuration/net9.0/Jellyfin.Plugin.MediaIntegrity.dll"
if (!(Test-Path -LiteralPath $dllPath)) { throw 'Build the plugin before packaging.' }
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
if ($assemblyVersion -ne "$version.0") { throw "Assembly version $assemblyVersion does not match $version." }
$artifactDirectory = Join-Path $repoRoot $OutputDirectory
[IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
$zipPath = Join-Path $artifactDirectory "Jellyfin.Plugin.MediaIntegrity-$version.zip"
$metadata = [ordered]@{
    category = 'General'
    guid = 'b8dc8a71-3d33-4e51-b4d6-8ea09f8db491'
    name = 'Media Integrity'
    description = 'Media integrity scan and validated stream-copy repair. DryRun enabled by default.'
    owner = 'Trycky64'
    targetAbi = '10.11.11.0'
    version = $assemblyVersion
    status = 'Active'
    autoUpdate = $false
    assemblies = @('Jellyfin.Plugin.MediaIntegrity.dll')
} | ConvertTo-Json

Add-Type -AssemblyName System.IO.Compression
$file = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
try {
    $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        # Stable entry order and timestamps make repeated packaging byte-identical.
        foreach ($name in @('Jellyfin.Plugin.MediaIntegrity.dll', 'meta.json')) {
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $stream = $entry.Open()
            try {
                $bytes = if ($name -eq 'meta.json') { [Text.Encoding]::UTF8.GetBytes($metadata) } else { [IO.File]::ReadAllBytes($dllPath) }
                $stream.Write($bytes, 0, $bytes.Length)
            } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $file.Dispose() }
Write-Output $zipPath
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
