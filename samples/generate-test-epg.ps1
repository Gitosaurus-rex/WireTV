<#
.SYNOPSIS
  Generates samples/test-epg.xml, an XMLTV guide matching samples/test-playlist.m3u.

.DESCRIPTION
  The guide is anchored to the current date, so re-run this when the generated file
  has gone stale. It exists to exercise the TV guide without needing a real IPTV
  subscription: the channel ids match the tvg-id values in the test playlist.
#>

param(
    # Length of each programme slot. Shorter slots make a denser guide, which is
    # handy for exercising scrolling in the guide window.
    [int] $SlotMinutes = 45,

    [int] $Days = 3
)

$ErrorActionPreference = 'Stop'

$outputPath = Join-Path $PSScriptRoot 'test-epg.xml'
$days = $Days

# Channel id -> display name and the programme titles it cycles through.
$channels = [ordered]@{
    'bipbop' = @{
        Name     = 'Apple BipBop'
        Icon     = 'https://example.org/logos/bipbop.png'
        Titles   = @('Test Pattern Live', 'Colour Bars', 'Timecode Hour', 'Engineering Feed')
        Category = 'Reference'
    }
    'bbb' = @{
        Name     = 'Big Buck Bunny'
        Icon     = 'https://example.org/logos/bbb.png'
        Titles   = @('Big Buck Bunny', 'Making Of', 'Animation Shorts', 'Blender Open Movies')
        Category = 'Animation'
    }
    'tears' = @{
        Name     = 'Tears of Steel'
        Icon     = $null
        Titles   = @('Tears of Steel', 'Behind the VFX', 'Sci-Fi Double Bill', 'Director Commentary')
        Category = 'Science fiction'
    }
    'sintel' = @{
        Name     = 'Sintel'
        Icon     = $null
        Titles   = @('Sintel', 'The Dragon Hunt', 'Concept Art Review', 'Score and Sound')
        Category = 'Fantasy'
    }
}

function Format-XmltvStamp([datetime] $moment) {
    $offset = [System.TimeZoneInfo]::Local.GetUtcOffset($moment)
    $sign = if ($offset.Ticks -lt 0) { '-' } else { '+' }
    '{0} {1}{2:00}{3:00}' -f $moment.ToString('yyyyMMddHHmmss'), $sign,
        [Math]::Abs($offset.Hours), [Math]::Abs($offset.Minutes)
}

function Escape-Xml([string] $text) {
    if ($null -eq $text) { return '' }
    $text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$builder.AppendLine('<tv generator-info-name="WireTv sample generator">')

foreach ($id in $channels.Keys) {
    $channel = $channels[$id]
    [void]$builder.AppendLine("  <channel id=""$id"">")
    [void]$builder.AppendLine("    <display-name>$(Escape-Xml $channel.Name)</display-name>")
    if ($channel.Icon) {
        [void]$builder.AppendLine("    <icon src=""$(Escape-Xml $channel.Icon)"" />")
    }
    [void]$builder.AppendLine('  </channel>')
}

# Start at midnight yesterday so "what is on now" always has an entry.
$start = (Get-Date).Date.AddDays(-1)
$slotMinutes = $SlotMinutes

foreach ($id in $channels.Keys) {
    $channel = $channels[$id]
    $cursor = $start
    $index = 0
    $episode = 0

    while ($cursor -lt $start.AddDays($days)) {
        $stop = $cursor.AddMinutes($slotMinutes)
        $title = $channel.Titles[$index % $channel.Titles.Count]

        [void]$builder.AppendLine(
            "  <programme start=""$(Format-XmltvStamp $cursor)"" stop=""$(Format-XmltvStamp $stop)"" channel=""$id"">")
        [void]$builder.AppendLine("    <title lang=""en"">$(Escape-Xml $title)</title>")
        [void]$builder.AppendLine("    <sub-title lang=""en"">Part $($index % 6 + 1)</sub-title>")
        [void]$builder.AppendLine(
            "    <desc lang=""en"">$(Escape-Xml "$title on $($channel.Name), starting at $($cursor.ToString('HH:mm')) on $($cursor.ToString('dddd d MMMM')).")</desc>")
        [void]$builder.AppendLine("    <category lang=""en"">$(Escape-Xml $channel.Category)</category>")
        [void]$builder.AppendLine("    <episode-num system=""xmltv_ns"">0.$episode.</episode-num>")
        [void]$builder.AppendLine('  </programme>')

        $cursor = $stop
        $index++
        $episode++
    }
}

[void]$builder.AppendLine('</tv>')

Set-Content -Path $outputPath -Value $builder.ToString() -Encoding UTF8

$programmeCount = ([regex]::Matches($builder.ToString(), '<programme ')).Count
Write-Host "Wrote $outputPath"
Write-Host "  $($channels.Count) channels, $programmeCount programmes, $days days from $($start.ToString('yyyy-MM-dd'))"
