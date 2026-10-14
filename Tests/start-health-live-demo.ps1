param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld')
& (Join-Path $PSScriptRoot 'start-health-timeline.ps1') -GameDir $GameDir -Live
