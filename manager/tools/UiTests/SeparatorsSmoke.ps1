# Drives the real app against a throw-away sandbox to check that a separator's group of mods can be folded away in the load order (D59): a group saved as
# collapsed starts folded, the chevron folds and unfolds, the state is saved in the playset, and finding a hidden mod opens its group. Prints PASS or FAIL per check.
# Build first: dotnet build manager/src/Artemis.App -o <folder>, then set ARTEMIS_EXE to <folder>\Artemis.exe (or build to the default output).
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'sepbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$data\playsets", $game | Out-Null
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }
function Wait-For($block, $tries = 40) { for ($i = 0; $i -lt $tries; $i++) { $r = & $block; if ($r) { return $r }; Start-Sleep -Milliseconds 250 } }
$list = [System.Windows.Automation.ControlType]::List
$btn = [System.Windows.Automation.ControlType]::Button
function Order-List { Find-Typed 'Load order' $list }
function Shown($name) { $null -ne (Find-El $name (Order-List)) }
function Chevrons { @((Order-List).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Fold or unfold this group')))) }

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
'x' | Set-Content "$game\stellaris.exe"
foreach ($k in 'a', 'b', 'c', 'd', 'e') {
  $name = "Mod $($k.ToUpper())"
  New-Item -ItemType Directory -Force "$user\mod\$k" | Out-Null
  "name=`"$name`"`nversion=`"1`"`nsupported_version=`"v4.*`"`n" | Set-Content "$user\mod\$k\descriptor.mod"
  "name=`"$name`"`nversion=`"1`"`nsupported_version=`"v4.*`"`npath=`"mod/$k`"`n" | Set-Content "$user\mod\$k.mod"
}
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"
# a, [Fixes: b c], [Gameplay, folded: d e]
'{ "id": "p1", "name": "Test", "mods": [ { "key": "a" }, { "separator": "Fixes" }, { "key": "b" }, { "key": "c" }, { "separator": "Gameplay", "collapsed": true }, { "key": "d" }, { "key": "e" } ] }' | Set-Content "$data\playsets\p1.json"

Start-App $data $user
Check 'the load order lists the mods and both separators' ($null -ne (Wait-For { (Shown 'Mod C') -and (Shown 'Gameplay') }))
Check 'a group saved as folded starts folded: its mods are not shown' (-not (Shown 'Mod D') -and -not (Shown 'Mod E'))
Check 'the mods above it and in the open group are shown' ((Shown 'Mod A') -and (Shown 'Mod B') -and (Shown 'Mod C'))
Check 'the folded separator still shows how many mods it holds' ($null -ne (Find-El '2' (Order-List)))

$chev = Chevrons
Check 'each separator has a chevron' ($chev.Count -eq 2)
Click-El $chev[1]
Check 'unfolding the group shows its mods' ($null -ne (Wait-For { (Shown 'Mod D') -and (Shown 'Mod E') }))
Click-El (Chevrons)[0]
Check 'folding the first group hides its mods and leaves the rest' ($null -ne (Wait-For { -not (Shown 'Mod B') }) -and -not (Shown 'Mod C') -and (Shown 'Mod A') -and (Shown 'Mod D'))
Start-Sleep -Milliseconds 500
$saved = Get-Content "$data\playsets\p1.json" -Raw
Check 'the folded state is saved in the playset: only the first group' ((([regex]::Matches($saved, '"collapsed"')).Count -eq 1) -and ($saved -match '"separator": "Fixes",\s*"collapsed": true'))

# finding a mod inside the folded group opens the group
$search = Find-Typed 'Search enabled mods' ([System.Windows.Automation.ControlType]::Edit)
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Mod C')
Start-Sleep -Milliseconds 500
Click-El (Find-Typed 'Next match' $btn)
Check 'finding a mod in a folded group opens it' ($null -ne (Wait-For { (Shown 'Mod B') -and (Shown 'Mod C') }))
Shot 'SeparatorsSmoke.png'
Stop-App
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
