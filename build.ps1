param([string]$OutputPath = (Join-Path $PSScriptRoot 'dist\QuickCapture.exe'))

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = [IO.Path]::GetFullPath($OutputPath)
$manifest = Join-Path $PSScriptRoot 'QuickCapture.manifest'
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName
$deps = Join-Path $PSScriptRoot '.deps'
$libraries = 'NAudio.Core.dll','NAudio.Wasapi.dll','zxing.dll','SharpDX.dll',
    'SharpDX.Direct3D11.dll','SharpDX.DXGI.dll' | ForEach-Object { Join-Path $deps "lib/$_" }
$media = Join-Path $deps 'media'
$missing = @($libraries | Where-Object { !(Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missing.Count -or !(Test-Path -LiteralPath (Join-Path $media 'ffmpeg.exe') -PathType Leaf)) {
    throw 'Dependencies are missing. Run .\restore-dependencies.ps1 first.'
}
$destination = Split-Path -Parent $output
$metadata = Get-ChildItem (Join-Path $env:WINDIR 'System32/WinMetadata') -Filter '*.winmd' | ForEach-Object { '/reference:' + $_.FullName }
$runtime = foreach ($name in 'System.Runtime','System.Runtime.InteropServices.WindowsRuntime','netstandard') {
    Get-ChildItem (Join-Path $env:WINDIR "Microsoft.NET/assembly/GAC_MSIL/$name") -Recurse -Filter "$name.dll" |
        Select-Object -First 1 -ExpandProperty FullName
}
$references = @($runtime) + $libraries + @((Join-Path $framework 'System.Runtime.WindowsRuntime.dll')) |
    ForEach-Object { '/reference:' + $_ }
$wpf = 'WindowsBase','PresentationCore','PresentationFramework','WindowsFormsIntegration' |
    ForEach-Object { '/reference:' + (Join-Path $framework "WPF/$_.dll") }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /warnaserror+ /unsafe `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll /reference:System.Xaml.dll `
    "/win32manifest:$manifest" "/out:$output" $references $wpf $metadata $sources
if ($LASTEXITCODE -ne 0) { throw "QuickCapture compilation failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath $libraries -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination -Force
foreach ($folder in 'media','licenses') {
    $source = if ($folder -eq 'media') { $media } else { Join-Path $PSScriptRoot $folder }
    $target = New-Item -ItemType Directory -Path (Join-Path $destination $folder) -Force
    Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $target.FullName -Force
}
