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

$halloweenPartsDirectory = Join-Path $repositoryRoot "assets/halloween-screensaver-parts"
$halloweenOutputPath = Join-Path $repositoryRoot "assets/MulletHopHalloweenScreensaver.mp4"
$halloweenExpectedSha256 = "e4a5623591dde425bf7f27b2429e9de40b36d6d32e83c6c55aa0bf56cf964cbc"

$halloweenParts = @(Get-ChildItem -LiteralPath $halloweenPartsDirectory -File -Filter "part-*.bin" |
    Sort-Object Name)
if ($halloweenParts.Count -eq 0) {
    throw "No Halloween screensaver video parts were found in $halloweenPartsDirectory."
}

$halloweenOutput = [System.IO.File]::Create($halloweenOutputPath)
try {
    foreach ($part in $halloweenParts) {
        $input = [System.IO.File]::OpenRead($part.FullName)
        try {
            $input.CopyTo($halloweenOutput)
        }
        finally {
            $input.Dispose()
        }
    }
}
finally {
    $halloweenOutput.Dispose()
}

$halloweenActualSha256 =
    (Get-FileHash -LiteralPath $halloweenOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($halloweenActualSha256 -ne $halloweenExpectedSha256) {
    throw "The assembled Halloween screensaver video did not match the uploaded MP4."
}

$halloweenVideo = Get-Item -LiteralPath $halloweenOutputPath
Write-Host "Halloween screensaver video assembled and verified: $($halloweenVideo.Length) bytes."

$businessClosedPartsDirectory = Join-Path $repositoryRoot "assets/business-closed-parts"
$businessClosedOutputPath = Join-Path $repositoryRoot "assets/MulletHopBusinessClosed.mp4"
$businessClosedExpectedSha256 = "b8d7cd5142a000027e5e0bff2d3024d3cf614b8064bcf8c7bb0d719b7c42ef1b"

$businessClosedParts = @(Get-ChildItem -LiteralPath $businessClosedPartsDirectory -File -Filter "part-*.bin" |
    Sort-Object Name)
if ($businessClosedParts.Count -eq 0) {
    throw "No Business Closed video parts were found in $businessClosedPartsDirectory."
}

$businessClosedOutput = [System.IO.File]::Create($businessClosedOutputPath)
try {
    foreach ($part in $businessClosedParts) {
        $input = [System.IO.File]::OpenRead($part.FullName)
        try {
            $input.CopyTo($businessClosedOutput)
        }
        finally {
            $input.Dispose()
        }
    }
}
finally {
    $businessClosedOutput.Dispose()
}

$businessClosedActualSha256 =
    (Get-FileHash -LiteralPath $businessClosedOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($businessClosedActualSha256 -ne $businessClosedExpectedSha256) {
    throw "The assembled Business Closed video did not match the uploaded MP4."
}

$businessClosedVideo = Get-Item -LiteralPath $businessClosedOutputPath
Write-Host "Business Closed video assembled and verified: $($businessClosedVideo.Length) bytes."
