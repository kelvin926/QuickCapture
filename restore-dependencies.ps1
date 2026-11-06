$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$deps = Join-Path $PSScriptRoot '.deps'
$archives = New-Item -ItemType Directory -Force -Path (Join-Path $deps 'archives')
$libraries = New-Item -ItemType Directory -Force -Path (Join-Path $deps 'lib')
$media = New-Item -ItemType Directory -Force -Path (Join-Path $deps 'media')

function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Sha256) {
    $file = Join-Path $archives.FullName $Name
    if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-FileHash -LiteralPath $file).Hash -eq $Sha256) {
        return $file
    }
    $partial = $file + '.part'
    try {
        & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 12 --max-time 180 $Url -o $partial
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $Name ($LASTEXITCODE)." }
        if ((Get-FileHash -LiteralPath $partial).Hash -ne $Sha256) { throw "SHA-256 mismatch for $Name." }
        Move-Item -LiteralPath $partial -Destination $file -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
    }
    return $file
}

$packages = @(
    @{ Id = 'naudio.core'; Version = '2.2.1'; File = 'lib/netstandard2.0/NAudio.Core.dll';
        Sha256 = '794645dbfd30e4880663d52d9d9224d55c301fa54228ff623c95d572c7887347' },
    @{ Id = 'naudio.wasapi'; Version = '2.2.1'; File = 'lib/netstandard2.0/NAudio.Wasapi.dll';
        Sha256 = 'c8396b8dbab86a2619edfd9158beaf18c21cb86677099b5594950fd8651b9d1a' },
    @{ Id = 'zxing.net'; Version = '0.16.11'; File = 'lib/net48/zxing.dll';
        Sha256 = '7d39234d668e558b3d374d18116ed57021d7c0df482801f1e1db4f1db9314ec3' },
    @{ Id = 'sharpdx'; Version = '4.2.0'; File = 'lib/net45/SharpDX.dll';
        Sha256 = '117126040200faaa118f1312ad5a5b1e7d067c90abf6e726f18b6d1635216fc3' },
    @{ Id = 'sharpdx.direct3d11'; Version = '4.2.0'; File = 'lib/net45/SharpDX.Direct3D11.dll';
        Sha256 = '21dedcf8b1ddb50ea3ff81f61d45511aec79d727d15bda9ae45c05d5058ee219' },
    @{ Id = 'sharpdx.dxgi'; Version = '4.2.0'; File = 'lib/net45/SharpDX.DXGI.dll';
        Sha256 = 'cb4a31bd0137ec8845b234621a690e5ab2b45830bde0a3d27caabc5bced5fc97' }
)

foreach ($package in $packages) {
    $name = "$($package.Id).$($package.Version).nupkg"
    $url = "https://api.nuget.org/v3-flatcontainer/$($package.Id)/$($package.Version)/$name"
    $file = Get-VerifiedArchive $url $name $package.Sha256
    $archive = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $entry = $archive.GetEntry($package.File)
        if (!$entry) { throw "Missing $($package.File) in $name." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $libraries.FullName $entry.Name), $true)
    } finally {
        $archive.Dispose()
    }
}

$version = '9.0.2'
$folder = "ffmpeg-$version-essentials_build"
$name = "$folder.7z"
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$version/$name"
$file = Get-VerifiedArchive $url $name '4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077'
& tar.exe -xf $file -C $deps "$folder/bin/ffmpeg.exe" "$folder/LICENSE" "$folder/README.txt"
if ($LASTEXITCODE -ne 0) { throw "FFmpeg extraction failed ($LASTEXITCODE)." }
foreach ($relativePath in 'bin/ffmpeg.exe','LICENSE','README.txt') {
    Copy-Item -LiteralPath (Join-Path $deps "$folder/$relativePath") -Destination $media.FullName -Force
}
