<#
.SYNOPSIS
  Fetches the whisper.cpp CPU DLLs and one ggml model for Rambler's local speech to text.
.DESCRIPTION
  Nothing here is vendored into the repo. The DLLs and the model land in
  %LOCALAPPDATA%\Tomte\whisper, where Tomte looks for them: whisper.dll, ggml*.dll and
  ggml-base.en.bin (148 MB, English). Both downloads are pinned by SHA-256; a mismatch stops the script.
.EXAMPLE
  powershell -File tools\whisper\Get-Whisper.ps1
#>
param(
    [string]$Dest = (Join-Path $env:LOCALAPPDATA 'Tomte\whisper')
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = 'v1.9.2'
$zipUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/$version/whisper-bin-x64.zip"
$zipHash = '49DCC16DE826F20BD53D44F947A1AE49DFA81F86CAD67A64D80820CB192D674A'
$model = 'ggml-base.en.bin'
$modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$model"
$modelHash = 'A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002'

function Get-Pinned($url, $file, $hash) {
    Invoke-WebRequest -UseBasicParsing $url -OutFile $file
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($actual -ne $hash) { Remove-Item $file; throw "SHA-256 mismatch for $url`: got $actual" }
}

New-Item -ItemType Directory -Force $Dest | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("tomte-whisper-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    Get-Pinned $zipUrl (Join-Path $tmp 'w.zip') $zipHash
    Expand-Archive (Join-Path $tmp 'w.zip') (Join-Path $tmp 'x')
    # Only the library: whisper.dll, ggml.dll, ggml-base.dll and the ggml-cpu-* variants ggml picks from at load.
    Get-ChildItem (Join-Path $tmp 'x\Release') -Filter *.dll |
        Where-Object { $_.Name -like 'ggml*' -or $_.Name -eq 'whisper.dll' } |
        Copy-Item -Destination $Dest -Force
    $modelFile = Join-Path $Dest $model
    if (-not (Test-Path $modelFile) -or (Get-FileHash $modelFile -Algorithm SHA256).Hash -ne $modelHash) {
        Get-Pinned $modelUrl $modelFile $modelHash
    }
    "whisper.cpp $version and $model are in $Dest"
}
finally {
    Remove-Item -Recurse -Force $tmp
}
