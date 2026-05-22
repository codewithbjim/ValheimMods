# Packages the Thunderstore folder and submits it to thunderstore.io.
# Excludes *.zip files and the Images/ folder.
#
# Auth: set $env:THUNDERSTORE_TOKEN, pass -Token, or create a .thunderstore-token
# file next to this script containing the bearer token.
#
# Dry-run by default -- builds the zip and prints what would submit, but does
# not upload. Pass -Publish to actually submit to thunderstore.io.
#
# Usage examples:
#   .\publish-thunderstore.ps1              # dry run
#   .\publish-thunderstore.ps1 -Publish     # actually submit
#   .\publish-thunderstore.ps1 -Publish -Token "tss_xxx"
#   .\publish-thunderstore.ps1 -Categories mods,misc,client-side

[CmdletBinding()]
param(
    [string]$ThunderstoreDir,
    [string]$Token = $env:THUNDERSTORE_TOKEN,
    [string]$TokenFile,
    [string]$Team = 'virtualbjorn',
    [string]$Community = 'valheim',
    [string[]]$Categories = @(
        'Mods',
        'Misc',
        'Server-side',
        'Client-side',
        'Bog Witch Update',
        'AI Generated'
    ),
    [bool]$Nsfw = $false,
    [switch]$KeepZip,
    [switch]$Publish
)

$DryRun = -not $Publish

# $PSScriptRoot can be empty when the script is invoked via -Command, piped to
# iex, or dot-sourced from an interactive session. Fall back through other
# invocation hints, then the current directory.
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $ScriptDir -and $PSCommandPath) {
    $ScriptDir = Split-Path -Parent $PSCommandPath
}
if (-not $ScriptDir) { $ScriptDir = (Get-Location).Path }

if (-not $ThunderstoreDir) { $ThunderstoreDir = Join-Path $ScriptDir 'Thunderstore' }
if (-not $TokenFile)       { $TokenFile       = Join-Path $ScriptDir '.thunderstore-token' }

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function ConvertTo-Slug([string]$value) {
    $s = $value.ToLowerInvariant().Trim()
    $s = ($s -replace '[^a-z0-9]+', '-').Trim('-')
    return $s
}

if (-not (Test-Path -LiteralPath $ThunderstoreDir)) {
    throw "Thunderstore folder not found: $ThunderstoreDir"
}

$manifestPath = Join-Path $ThunderstoreDir 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "manifest.json not found at $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$modName  = $manifest.name
$version  = $manifest.version_number
if (-not $modName -or -not $version) {
    throw "manifest.json is missing name or version_number"
}

if (-not $Token -and (Test-Path -LiteralPath $TokenFile)) {
    $Token = (Get-Content -LiteralPath $TokenFile -Raw).Trim()
}
if (-not $Token -and -not $DryRun) {
    throw "No API token. Set `$env:THUNDERSTORE_TOKEN, pass -Token, or create $TokenFile"
}

$categorySlugs = @($Categories | ForEach-Object { ConvertTo-Slug $_ } | Where-Object { $_ })

$zipName = "$modName-$version.zip"
$zipPath = Join-Path $ThunderstoreDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Write-Host "Packaging $modName v$version"
Write-Host "  source : $ThunderstoreDir"
Write-Host "  output : $zipPath"

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$rootFull = (Resolve-Path -LiteralPath $ThunderstoreDir).Path.TrimEnd('\','/')
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    Get-ChildItem -LiteralPath $ThunderstoreDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($rootFull.Length + 1)
        $relFwd = $rel -replace '\\', '/'

        if ($_.Extension -ieq '.zip') { return }
        if ($relFwd -match '^Images/') { return }

        Write-Host "  + $relFwd"
        $entry = $zip.CreateEntry($relFwd, [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        try {
            $fs = [System.IO.File]::OpenRead($_.FullName)
            try { $fs.CopyTo($entryStream) } finally { $fs.Dispose() }
        } finally {
            $entryStream.Dispose()
        }
    }
} finally {
    $zip.Dispose()
}

$zipSize = (Get-Item -LiteralPath $zipPath).Length
Write-Host ("Created {0} ({1:N0} bytes)" -f $zipName, $zipSize)

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run (no -Publish) -- would submit:"
    Write-Host "  team       : $Team"
    Write-Host "  community  : $Community"
    Write-Host "  categories : $($categorySlugs -join ', ')"
    Write-Host "  nsfw       : $Nsfw"
    Write-Host ""
    Write-Host "Re-run with -Publish to upload."
    if (-not $KeepZip) { Remove-Item -LiteralPath $zipPath -Force }
    return
}

$baseUrl = 'https://thunderstore.io'
$authHeaders = @{ 'Authorization' = "Bearer $Token" }

try {
    Write-Host "Initiating upload..."
    $initBody = @{ filename = $zipName; file_size_bytes = $zipSize } | ConvertTo-Json -Compress
    $init = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/api/experimental/usermedia/initiate-upload/" `
        -Headers $authHeaders -ContentType 'application/json' -Body $initBody

    $mediaUuid   = $init.user_media.uuid
    $uploadUrls  = @($init.upload_urls | Sort-Object part_number)
    $partCount   = $uploadUrls.Count
    Write-Host "  media uuid : $mediaUuid"
    Write-Host "  parts      : $partCount"

    $parts = New-Object System.Collections.Generic.List[object]
    if ($partCount -eq 1) {
        $u = $uploadUrls[0]
        Write-Host "Uploading part 1/$partCount ($zipSize bytes)..."
        $resp = Invoke-WebRequest -Method Put -Uri $u.url -InFile $zipPath `
            -ContentType 'application/octet-stream' -UseBasicParsing
        $etag = $resp.Headers['ETag']
        if ($etag -is [array]) { $etag = $etag[0] }
        $parts.Add(@{ ETag = $etag; PartNumber = [int]$u.part_number }) | Out-Null
    }
    else {
        # Open the zip only for the multi-part path. Holding a FileShare.Read
        # handle here while the single-part branch calls Invoke-WebRequest
        # -InFile triggers a sharing violation ("used by another process"),
        # because -InFile opens the same file for ReadWrite.
        $fileStream = [System.IO.File]::OpenRead($zipPath)
        try {
            $partSize = [Math]::Ceiling($zipSize / $partCount)
            foreach ($u in $uploadUrls) {
                $partNum   = [int]$u.part_number
                $offset    = ($partNum - 1) * $partSize
                $remaining = $zipSize - $offset
                $thisSize  = [Math]::Min($partSize, $remaining)
                $buffer    = New-Object byte[] $thisSize
                $fileStream.Position = $offset
                [void]$fileStream.Read($buffer, 0, $thisSize)

                $tmp = [System.IO.Path]::GetTempFileName()
                [System.IO.File]::WriteAllBytes($tmp, $buffer)
                try {
                    Write-Host "Uploading part $partNum/$partCount ($thisSize bytes)..."
                    $resp = Invoke-WebRequest -Method Put -Uri $u.url -InFile $tmp `
                        -ContentType 'application/octet-stream' -UseBasicParsing
                    $etag = $resp.Headers['ETag']
                    if ($etag -is [array]) { $etag = $etag[0] }
                    $parts.Add(@{ ETag = $etag; PartNumber = $partNum }) | Out-Null
                } finally {
                    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
                }
            }
        } finally {
            $fileStream.Dispose()
        }
    }

    Write-Host "Finishing upload..."
    $finishBody = @{ parts = $parts } | ConvertTo-Json -Depth 5 -Compress
    Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/api/experimental/usermedia/$mediaUuid/finish-upload/" `
        -Headers $authHeaders -ContentType 'application/json' -Body $finishBody | Out-Null

    Write-Host "Submitting package..."
    $submitPayload = @{
        author_name          = $Team
        communities          = @($Community)
        community_categories = @{ $Community = $categorySlugs }
        has_nsfw_content     = [bool]$Nsfw
        upload_uuid          = $mediaUuid
    }
    $submitBody = $submitPayload | ConvertTo-Json -Depth 5 -Compress

    $submission = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/api/experimental/submission/submit/" `
        -Headers $authHeaders -ContentType 'application/json' -Body $submitBody

    Write-Host ""
    Write-Host "Submitted successfully."
    if ($submission.package_version.full_name) {
        Write-Host "  package  : $($submission.package_version.full_name)"
    }
    if ($submission.package_version.website_url) {
        Write-Host "  page     : $($submission.package_version.website_url)"
    } elseif ($submission.package_version.download_url) {
        Write-Host "  download : $($submission.package_version.download_url)"
    }
}
catch {
    $err = $_
    $msg = $err.Exception.Message
    if ($err.ErrorDetails -and $err.ErrorDetails.Message) {
        $msg = "$msg`n$($err.ErrorDetails.Message)"
    } elseif ($err.Exception.Response) {
        try {
            $stream = $err.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $body = $reader.ReadToEnd()
            if ($body) { $msg = "$msg`n$body" }
        } catch {}
    }
    Write-Error "Upload failed: $msg"
    throw
}
finally {
    if (-not $KeepZip -and (Test-Path -LiteralPath $zipPath)) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    }
}
