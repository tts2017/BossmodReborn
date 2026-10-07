# Rewrites module type names in BossModReborn's saved rotation presets from the upstream modules to their [Custom]
# copies (Custom/Rotation). Only modules that have a Custom copy are renamed; everything else is left as is.
# The original file is copied to a timestamped backup next to it before anything is written.
#
# Run with the game closed: the plugin keeps the presets in memory and may write them back on exit.
#
#   pwsh -File tools/preset_migration/Convert-PresetsToCustom.ps1 [-Path <presets.db.json>]

param(
    [string]$Path = (Join-Path $env:APPDATA 'XIVLauncher\pluginConfigs\BossModReborn\autorot\presets.db.json')
)

$ErrorActionPreference = 'Stop'

$map = [ordered]@{
    'BossMod.Autorotation.xan.BLM'                      = 'BossMod.Autorotation.xan.Custom.BLM'
    'BossMod.Autorotation.xan.BST'                      = 'BossMod.Autorotation.xan.Custom.BST'
    'BossMod.Autorotation.xan.DRG'                      = 'BossMod.Autorotation.xan.Custom.DRG'
    'BossMod.Autorotation.xan.MNK'                      = 'BossMod.Autorotation.xan.Custom.MNK'
    'BossMod.Autorotation.xan.NIN'                      = 'BossMod.Autorotation.xan.Custom.NIN'
    'BossMod.Autorotation.xan.RPR'                      = 'BossMod.Autorotation.xan.Custom.RPR'
    'BossMod.Autorotation.xan.SAM'                      = 'BossMod.Autorotation.xan.Custom.SAM'
    'BossMod.Autorotation.xan.VPR'                      = 'BossMod.Autorotation.xan.Custom.VPR'
    'BossMod.Autorotation.xan.MCH'                      = 'BossMod.Autorotation.xan.Custom.MCH'
    'BossMod.Autorotation.xan.DRK'                      = 'BossMod.Autorotation.xan.Custom.DRK'
    'BossMod.Autorotation.xan.GNB'                      = 'BossMod.Autorotation.xan.Custom.GNB'
    'BossMod.Autorotation.xan.PLD'                      = 'BossMod.Autorotation.xan.Custom.PLD'
    'BossMod.Autorotation.xan.TankAI'                   = 'BossMod.Autorotation.xan.Custom.TankAI'
    'BossMod.Autorotation.xan.VariantAI'                = 'BossMod.Autorotation.xan.Custom.VariantAI'
    'BossMod.Autorotation.Standard.xan.Utility.ThirdEye' = 'BossMod.Autorotation.Standard.xan.Utility.Custom.ThirdEye'
    'BossMod.Autorotation.akechi.AkechiBLM'             = 'BossMod.Autorotation.akechi.Custom.AkechiBLM'
    'BossMod.Autorotation.akechi.AkechiDRG'             = 'BossMod.Autorotation.akechi.Custom.AkechiDRG'
    'BossMod.Autorotation.akechi.AkechiMCH'             = 'BossMod.Autorotation.akechi.Custom.AkechiMCH'
    'BossMod.Autorotation.akechi.AkechiSCH'             = 'BossMod.Autorotation.akechi.Custom.AkechiSCH'
    'BossMod.Autorotation.akechi.AkechiDRK'             = 'BossMod.Autorotation.akechi.Custom.AkechiDRK'
    'BossMod.Autorotation.akechi.AkechiGNB'             = 'BossMod.Autorotation.akechi.Custom.AkechiGNB'
    'BossMod.Autorotation.akechi.AkechiPLD'             = 'BossMod.Autorotation.akechi.Custom.AkechiPLD'
    'BossMod.Autorotation.VeynWAR'                      = 'BossMod.Autorotation.Custom.VeynWAR'
    'BossMod.Autorotation.ClassGNBUtility'              = 'BossMod.Autorotation.Custom.ClassGNBUtility'
    'BossMod.Autorotation.ClassNINUtility'              = 'BossMod.Autorotation.Custom.ClassNINUtility'
    'BossMod.Autorotation.ClassSGEUtility'              = 'BossMod.Autorotation.Custom.ClassSGEUtility'
}

if (-not (Test-Path -LiteralPath $Path)) { throw "Preset file not found: $Path" }

$backup = "$Path.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
Copy-Item -LiteralPath $Path -Destination $backup
Write-Host "Backup: $backup"

$db = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json

$presetsChanged = 0
$modulesRenamed = 0
foreach ($preset in $db.payload) {
    if ($null -eq $preset.Modules) { continue }
    $modules = [ordered]@{}
    $changed = $false
    foreach ($prop in $preset.Modules.PSObject.Properties) {
        $name = $prop.Name
        # rename only when the preset does not already contain the Custom module (a duplicate key would make the file unreadable)
        if ($map.Contains($name) -and -not $preset.Modules.PSObject.Properties[$map[$name]]) {
            $name = $map[$name]
            $changed = $true
            $modulesRenamed++
        }
        $modules[$name] = $prop.Value
    }
    if ($changed) {
        $preset.Modules = [pscustomobject]$modules
        $presetsChanged++
        Write-Host "  $($preset.Name)"
    }
}

$db | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
Write-Host "Presets changed: $presetsChanged, modules renamed: $modulesRenamed"
