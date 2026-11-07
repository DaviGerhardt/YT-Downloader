# Compila o YT Downloader no GitHub Actions (Windows). Gera dist/YT-Downloader.exe.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$res  = Join-Path $root 'resources'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }

$ver  = (Get-Content (Join-Path $root 'latest.json') -Raw | ConvertFrom-Json).version
$code = Get-Content (Join-Path $src 'BaixadorYT.cs') -Raw
if ($code -notmatch ('AppVersion = "' + [regex]::Escape($ver) + '"')) {
  throw "A versao do latest.json ($ver) e a AppVersion do codigo nao batem."
}

$exe = Join-Path $dist 'YT-Downloader.exe'
$cscArgs = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',
  "/out:$exe", "/win32icon:$(Join-Path $src 'YTDownloader.ico')",
  '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',
  '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
  "/resource:$(Join-Path $res 'yt-dlp_win.zip'),YTDownloader.Resources.yt-dlp_win.zip",
  "/resource:$(Join-Path $res 'ffmpeg.exe.gz'),YTDownloader.Resources.ffmpeg.exe.gz",
  "/resource:$(Join-Path $res 'ffprobe.exe.gz'),YTDownloader.Resources.ffprobe.exe.gz",
  "/resource:$(Join-Path $res 'LICENSE-FFmpeg.txt.gz'),YTDownloader.Resources.LICENSE-FFmpeg.txt",
  "/resource:$(Join-Path $res 'LICENSE-yt-dlp.txt.gz'),YTDownloader.Resources.LICENSE-yt-dlp.txt",
  "/resource:$(Join-Path $res 'FFmpeg-BUILD-INFO.txt.gz'),YTDownloader.Resources.FFmpeg-BUILD-INFO.txt",
  "/resource:$(Join-Path $res 'THIRD-PARTY-NOTICES.txt.gz'),YTDownloader.Resources.THIRD-PARTY-NOTICES.txt",
  (Join-Path $src 'BaixadorYT.cs'))
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar (codigo $LASTEXITCODE)." }
Write-Host "OK: $exe (v$ver)"
