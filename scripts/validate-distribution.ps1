param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedOrion,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVencordVersion,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVencordCommit
)

$ErrorActionPreference = "Stop"

$resolvedZip = [IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $resolvedZip -PathType Leaf)) {
    throw "Release ZIP not found: $resolvedZip"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$required = @(
    # Keep this in sync with InstallationService.IntegrityFiles.
    "dist\package.json",
    "dist\patcher.js",
    "dist\preload.js",
    "dist\renderer.js",
    "dist\renderer.css",
    "dist\vencordDesktopMain.js",
    "dist\vencordDesktopPreload.js",
    "dist\vencordDesktopRenderer.js",
    "dist\vencordDesktopRenderer.css",
    "README.md",
    # ValidatePackage requires install.bat before any Discord mutation.
    "install.bat"
)

$archive = [IO.Compression.ZipFile]::OpenRead($resolvedZip)
try {
    $failures = New-Object System.Collections.Generic.List[string]

    foreach ($relative in $required) {
        $entry = $archive.GetEntry($relative)
        if ($null -eq $entry) {
            $failures.Add("missing: $relative")
            continue
        }
        if ($entry.Length -le 0) {
            $failures.Add("empty: $relative")
        }
    }

    $nestedDist = $archive.Entries | Where-Object { $_.FullName -like "dist\dist\*" } | Select-Object -First 1
    if ($null -ne $nestedDist) {
        $failures.Add("unexpected nested dist directory: $($nestedDist.FullName)")
    }

    $packageEntry = $archive.GetEntry("dist\package.json")
    if ($null -ne $packageEntry -and $packageEntry.Length -gt 0) {
        $reader = New-Object IO.StreamReader($packageEntry.Open())
        try {
            $packageText = $reader.ReadToEnd()
            try {
                $null = $packageText | ConvertFrom-Json
            }
            catch {
                $failures.Add("dist\package.json is not valid JSON")
            }
        }
        finally {
            $reader.Dispose()
        }
    }

    $readmeEntry = $archive.GetEntry("README.md")
    if ($null -ne $readmeEntry -and $readmeEntry.Length -gt 0) {
        $reader = New-Object IO.StreamReader($readmeEntry.Open())
        try {
            $readme = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        if ($readme -notmatch [regex]::Escape("OrionQuests v$ExpectedOrion")) {
            $failures.Add("README.md does not identify OrionQuests v$ExpectedOrion")
        }
        if ($readme -notmatch [regex]::Escape("version $ExpectedVencordVersion")) {
            $failures.Add("README.md does not identify Vencord version $ExpectedVencordVersion")
        }
        $shortCommit = if ($ExpectedVencordCommit.Length -ge 8) { $ExpectedVencordCommit.Substring(0, 8) } else { $ExpectedVencordCommit }
        if ($readme -notmatch [regex]::Escape($shortCommit)) {
            $failures.Add("README.md does not identify Vencord commit $shortCommit")
        }
    }

    if ($failures.Count -gt 0) {
        throw ("Release validation failed:`r`n - " + ($failures -join "`r`n - "))
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $resolvedZip -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $resolvedZip).Length
Write-Host "RELEASE_VALIDATION=PASS"
Write-Host "ZIP=$resolvedZip"
Write-Host "SIZE=$size"
Write-Host "SHA256=$hash"
