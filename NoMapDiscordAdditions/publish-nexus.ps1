# Packages the Thunderstore folder and uploads it as a new file version on
# Nexus Mods via the v3 Upload API (open beta).
# Excludes *.zip files and the Images/ folder (same contents as the
# Thunderstore package, so both hosts ship identical files).
#
# Auth: set $env:NEXUS_APIKEY, pass -ApiKey, or create a .nexus-apikey file
# next to this script containing your personal API key
# (https://next.nexusmods.com/settings/api-keys).
#
# Target: identified by the game domain + the mod id from the site URL
# (nexusmods.com/games/valheim/mods/3428 -> -GameDomain valheim -ModId 3428).
# The script resolves the mod's global id, then looks up its files: if the
# mod already has an active file it uploads a NEW VERSION of it; if the page
# has no files yet (fresh draft) it CREATES the first file. Pass -FileId to
# skip resolution and target a specific mod file directly.
#
# Dry-run by default -- builds the zip and prints what would upload, but does
# not touch Nexus. Pass -Publish to actually upload.
#
# Usage examples:
#   .\publish-nexus.ps1                     # dry run
#   .\publish-nexus.ps1 -Publish            # actually upload
#   .\publish-nexus.ps1 -Publish -Description "Hotfix for ..."

[CmdletBinding()]
param(
    [string]$ThunderstoreDir,
    [string]$ApiKey = $env:NEXUS_APIKEY,
    [string]$ApiKeyFile,
    [string]$GameDomain = 'valheim',
    [string]$ModId = '3428',
    [string]$FileId,
    [string]$Category = 'main',
    [string]$Description,
    [switch]$ArchiveExisting,
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
if (-not $ApiKeyFile)      { $ApiKeyFile      = Join-Path $ScriptDir '.nexus-apikey' }

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

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

if (-not $ApiKey -and (Test-Path -LiteralPath $ApiKeyFile)) {
    $ApiKey = (Get-Content -LiteralPath $ApiKeyFile -Raw).Trim()
}
if (-not $ApiKey -and -not $DryRun) {
    throw "No API key. Set `$env:NEXUS_APIKEY, pass -ApiKey, or create $ApiKeyFile"
}

$baseUrl = 'https://api.nexusmods.com/v3'
$authHeaders = @{
    'apikey'     = $ApiKey
    'User-Agent' = 'NoMapDiscordAdditions-publish-script'
}

# Resolve the target: global mod id, and which mod file (if any) to add a
# version to. No -FileId and no existing active file means this upload will
# create the mod page's first file.
$modGlobalId = $null
$createNew   = $false
if ($ApiKey) {
    Write-Host "Resolving mod $GameDomain/$ModId..."
    $modInfo = Invoke-RestMethod -Method Get -Uri "$baseUrl/games/$GameDomain/mods/$ModId" -Headers $authHeaders
    $modGlobalId = $modInfo.data.id
    Write-Host "  mod id : $modGlobalId (game-scoped $($modInfo.data.game_scoped_id))"

    if (-not $FileId) {
        $filesResp = Invoke-RestMethod -Method Get -Uri "$baseUrl/mods/$modGlobalId/files" -Headers $authHeaders
        $activeFiles = @($filesResp.data.mod_files | Where-Object { $_.is_active })
        if ($activeFiles.Count -eq 1) {
            $FileId = $activeFiles[0].id
            Write-Host "  file   : $($activeFiles[0].name) (id $FileId) -- will add version"
        }
        elseif ($activeFiles.Count -gt 1) {
            $match = @($activeFiles | Where-Object { $_.name -eq $modName })
            if ($match.Count -eq 1) {
                $FileId = $match[0].id
                Write-Host "  file   : $($match[0].name) (id $FileId) -- will add version"
            } else {
                $names = ($activeFiles | ForEach-Object { "$($_.name) (id $($_.id))" }) -join ', '
                throw "Mod has multiple active files ($names); pass -FileId to pick one"
            }
        }
        else {
            $createNew = $true
            Write-Host "  file   : none yet -- will create the mod's first file"
        }
    }
}

# Default description: the CHANGELOG.md section for this version, with the
# heaviest markdown stripped (Nexus renders file descriptions as BBCode/plain
# text, not markdown).
if (-not $Description) {
    $changelogPath = Join-Path $ThunderstoreDir 'CHANGELOG.md'
    if (Test-Path -LiteralPath $changelogPath) {
        $lines = Get-Content -LiteralPath $changelogPath
        $section = New-Object System.Collections.Generic.List[string]
        $inSection = $false
        foreach ($line in $lines) {
            if ($line -match '^##\s+(.+)$') {
                if ($inSection) { break }
                if ($Matches[1].Trim() -eq $version) { $inSection = $true }
                continue
            }
            if ($inSection) { [void]$section.Add($line) }
        }
        if ($section.Count -gt 0) {
            $text = ($section -join "`n").Trim()
            $text = $text -replace '(?m)^###\s+', ''      # sub-headings -> plain lines
            $text = $text -replace '\*\*([^*]+)\*\*', '$1' # bold
            $text = $text -replace '`([^`]+)`', '$1'       # inline code
            $Description = $text
        }
    }
}

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
    Write-Host "Dry run (no -Publish) -- would upload to Nexus Mods:"
    Write-Host "  mod         : $GameDomain/$ModId $(if ($modGlobalId) { "(global id $modGlobalId)" })"
    Write-Host "  action      : $(if ($createNew) { 'create first mod file' } elseif ($FileId) { "new version of file $FileId" } else { '<unresolved -- no API key>' })"
    Write-Host "  api key     : $(if ($ApiKey) { 'found' } else { '<missing>' })"
    Write-Host "  name        : $modName"
    Write-Host "  version     : $version"
    Write-Host "  category    : $Category"
    Write-Host "  archive old : $([bool]$ArchiveExisting)"
    Write-Host "  description : $(if ($Description) { "$($Description.Length) chars (from CHANGELOG.md)" } else { '<none>' })"
    Write-Host ""
    Write-Host "Re-run with -Publish to upload."
    if (-not $KeepZip) { Remove-Item -LiteralPath $zipPath -Force }
    return
}

try {
    $createBody = @{ filename = $zipName; size_bytes = [long]$zipSize } | ConvertTo-Json -Compress

    if ($zipSize -le 100MB) {
        Write-Host "Creating upload session..."
        $upload = (Invoke-RestMethod -Method Post -Uri "$baseUrl/uploads" `
            -Headers $authHeaders -ContentType 'application/json' -Body $createBody).data
        $uploadId = $upload.id
        Write-Host "  upload id : $uploadId"

        Write-Host "Uploading ($zipSize bytes)..."
        # The presigned URL signs content-disposition (attachment; filename=...)
        # and content-type; the PUT must send both or R2 rejects the signature.
        Invoke-WebRequest -Method Put -Uri $upload.presigned_url -InFile $zipPath `
            -ContentType 'application/octet-stream' -UseBasicParsing `
            -Headers @{ 'Content-Disposition' = "attachment; filename=`"$zipName`"" } | Out-Null
    }
    else {
        # Files over 100 MiB must use the S3-style multipart flow.
        Write-Host "Creating multipart upload..."
        $upload = (Invoke-RestMethod -Method Post -Uri "$baseUrl/uploads/multipart" `
            -Headers $authHeaders -ContentType 'application/json' -Body $createBody).data

        $uploadId    = $upload.id
        $partUrls    = @($upload.part_presigned_urls)
        $partSize    = [long]$upload.part_size_bytes
        $completeUrl = $upload.complete_presigned_url
        $partCount   = $partUrls.Count
        Write-Host "  upload id : $uploadId"
        Write-Host "  parts     : $partCount ($partSize bytes each)"

        $parts = New-Object System.Collections.Generic.List[object]
        $fileStream = [System.IO.File]::OpenRead($zipPath)
        try {
            for ($i = 0; $i -lt $partCount; $i++) {
                $partNum   = $i + 1
                $offset    = [long]$i * $partSize
                $remaining = $zipSize - $offset
                $thisSize  = [Math]::Min($partSize, $remaining)
                $buffer    = New-Object byte[] $thisSize
                $fileStream.Position = $offset
                [void]$fileStream.Read($buffer, 0, $thisSize)

                $tmp = [System.IO.Path]::GetTempFileName()
                [System.IO.File]::WriteAllBytes($tmp, $buffer)
                try {
                    Write-Host "Uploading part $partNum/$partCount ($thisSize bytes)..."
                    $resp = Invoke-WebRequest -Method Put -Uri $partUrls[$i] -InFile $tmp `
                        -ContentType 'application/octet-stream' -UseBasicParsing
                    $etag = $resp.Headers['ETag']
                    if ($etag -is [array]) { $etag = $etag[0] }
                    if (-not $etag) { throw "No ETag returned for part $partNum" }
                    $parts.Add(@{ ETag = $etag.Trim('"'); PartNumber = $partNum }) | Out-Null
                } finally {
                    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
                }
            }
        } finally {
            $fileStream.Dispose()
        }

        Write-Host "Completing multipart upload..."
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('<CompleteMultipartUpload>')
        foreach ($p in $parts) {
            [void]$sb.Append("<Part><PartNumber>$($p.PartNumber)</PartNumber><ETag>$($p.ETag)</ETag></Part>")
        }
        [void]$sb.Append('</CompleteMultipartUpload>')
        Invoke-WebRequest -Method Post -Uri $completeUrl -Body $sb.ToString() `
            -ContentType 'application/xml' -UseBasicParsing | Out-Null
    }

    Write-Host "Finalising upload..."
    Invoke-RestMethod -Method Post -Uri "$baseUrl/uploads/$uploadId/finalise" `
        -Headers $authHeaders | Out-Null

    Write-Host "Waiting for upload to become available..."
    $state = ''
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/uploads/$uploadId" -Headers $authHeaders
        $state = $status.data.state
        if ($state -eq 'available') { break }
        Start-Sleep -Seconds 2
    }
    if ($state -ne 'available') {
        throw "Upload $uploadId never became available (last state: $state)"
    }

    if ($createNew) {
        Write-Host "Creating mod file (first file on this mod page)..."
        $payload = @{
            upload_id                    = $uploadId
            mod_id                       = $modGlobalId
            name                         = $modName
            version                      = $version
            file_category                = $Category
            primary_mod_manager_download = $true
        }
        if ($Description) { $payload.description = $Description }
        # Send raw UTF-8 bytes: PS 5.1 encodes string bodies as Latin-1, which
        # turns non-ASCII changelog characters into invalid JSON server-side.
        $body = [System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 5 -Compress))

        $result = Invoke-RestMethod -Method Post -Uri "$baseUrl/mod-files" `
            -Headers $authHeaders -ContentType 'application/json; charset=utf-8' -Body $body

        Write-Host ""
        Write-Host "Uploaded successfully."
        if ($result.data.id) {
            Write-Host "  file id : $($result.data.id) (game-scoped $($result.data.game_scoped_id))"
        }
    }
    else {
        Write-Host "Creating file version..."
        $payload = @{
            upload_id             = $uploadId
            name                  = $modName
            version               = $version
            file_category         = $Category
            archive_existing_file = [bool]$ArchiveExisting
        }
        if ($Description) { $payload.description = $Description }
        $body = [System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 5 -Compress))

        $result = Invoke-RestMethod -Method Post -Uri "$baseUrl/mod-files/$FileId/versions" `
            -Headers $authHeaders -ContentType 'application/json; charset=utf-8' -Body $body

        Write-Host ""
        Write-Host "Uploaded successfully."
        if ($result.data.version.id) {
            Write-Host "  version id : $($result.data.version.id)"
        }
        if ($result.data.file.id) {
            Write-Host "  file id    : $($result.data.file.id)"
        }
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
