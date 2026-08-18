$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$partsDirectory = Join-Path $repositoryRoot "assets/screensaver-parts"
$outputPath = Join-Path $repositoryRoot "assets/MulletHopScreensaver.mp4"
$expectedSha256 = "a1bd6120c0457265808beb003a7aa7b44ca4739ec7b1918508bde859e87b966e"

$parts = @(Get-ChildItem -LiteralPath $partsDirectory -File -Filter "part-*.bin" |
    Sort-Object Name)
if ($parts.Count -eq 0) {
    throw "No screensaver video parts were found in $partsDirectory."
}

$output = [System.IO.File]::Create($outputPath)
try {
    foreach ($part in $parts) {
        $input = [System.IO.File]::OpenRead($part.FullName)
        try {
            $input.CopyTo($output)
        }
        finally {
            $input.Dispose()
        }
    }
}
finally {
    $output.Dispose()
}

$actualSha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "The assembled screensaver video did not match the uploaded MP4."
}

$video = Get-Item -LiteralPath $outputPath
Write-Host "Screensaver video assembled and verified: $($video.Length) bytes."
