# Drives the real app against a throw-away sandbox to check the two mod lists (D57): the column headers, and a trophy (or a cracked one) beside each mod
# once the index has been read, for a mod that leaves achievements on (or turns them off). Prints PASS or FAIL per check.
# Build first: dotnet build manager/src/Artemis.App -o <folder>, then set ARTEMIS_EXE to <folder>\Artemis.exe (or build to the default output).
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'listbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$data\playsets", $game | Out-Null
$image = [System.Windows.Automation.ControlType]::Image
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }
function Wait-For($block, $tries = 60) { for ($i = 0; $i -lt $tries; $i++) { $r = & $block; if ($r) { return $r }; Start-Sleep -Milliseconds 250 } }
function Count-Typed($pattern) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $image)
  @($script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | Where-Object { $_.Current.Name -like $pattern }).Count
}

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
'x' | Set-Content "$game\stellaris.exe"
# the game's list of what its checksum covers: common and events, .txt files
"directory`n{`n    name = `"common`"`n    sub_directories = yes`n    file_extension = `".txt`"`n}`ndirectory`n{`n    name = `"events`"`n    sub_directories = yes`n    file_extension = `".txt`"`n}`n" | Set-Content "$game\checksum_manifest.txt"
function Mod($key, $name, $ver, $files) {
  New-Item -ItemType Directory -Force "$user\mod\$key" | Out-Null
  "name=`"$name`"`nversion=`"1`"`nsupported_version=`"$ver`"`n" | Set-Content "$user\mod\$key\descriptor.mod"
  "name=`"$name`"`nversion=`"1`"`nsupported_version=`"$ver`"`npath=`"mod/$key`"`n" | Set-Content "$user\mod\$key.mod"
  foreach ($f in $files) { $p = "$user\mod\$key\$f"; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; 'x' | Set-Content $p }
}
Mod 'cosmetic' 'Cosmetic Portraits Pack' 'v4.*' @('gfx/portraits/a.dds', 'localisation/english/a_l_english.yml')
Mod 'gameplay' 'Gameplay Overhaul With A Rather Long Name To Truncate' 'v4.*' @('common/technology/a.txt')
Mod 'old' 'Old Fixes' 'v3.*' @('events/e.txt', 'events/f.txt')
Mod 'ui' 'Interface Tweaks' 'v4.*' @('interface/a.gui')
$mods = @('cosmetic', 'gameplay', 'old', 'ui') | ForEach-Object { '{ "key": "' + $_ + '", "enabled": true }' }
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"
"{ `"id`": `"p1`", `"name`": `"Test`", `"mods`": [ $($mods -join ', ') ] }" | Set-Content "$data\playsets\p1.json"

Start-App $data $user
Check 'every mod gets a trophy once its files have been read (4 mods in each list)' ($null -ne (Wait-For { (Count-Typed 'Adds or changes*') -eq 8 }))
Check 'the two mods that leave achievements on have a trophy in each list' ((Count-Typed 'Adds or changes no file*') -eq 4)
Check 'the two that change checksum files have a cracked one in each list, saying how many files' ((Count-Typed 'Adds or changes 1 file the*') -eq 2 -and (Count-Typed 'Adds or changes 2 files the*') -eq 2)
Check 'each list has the trophy as a column header' ((Count-Typed 'Achievements') -eq 2)
Check 'the summary under the Launch button is gone' ($null -eq (Find-El 'Achievements: off (2 mods)') -and @(Find-All 'Achievements: off (2 mods)').Count -eq 0)
Check 'the version column says Game version' ($null -ne (Find-El 'Game version') -and $null -eq (Find-El 'Game'))
Check 'the position column says Number, not #' ($null -ne (Find-El 'Number') -and $null -eq (Find-El '#'))
Shot 'ListsSmoke.png'
Stop-App
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
