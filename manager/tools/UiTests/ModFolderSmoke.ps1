# Drives the real app against a throw-away sandbox to check what it does about the mod folder (D55): a mod folder with no .mod file gets one (also when it is nested),
# and a .mod file whose files are gone is reported and, on request, sent to the Recycle Bin. Prints PASS or FAIL per check.
# Build first: dotnet build manager/src/Artemis.App   (or set ARTEMIS_EXE)
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'modbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$data\playsets", $game | Out-Null
$btn = [System.Windows.Automation.ControlType]::Button
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }
function Dialog-Window { $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window); $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
function Wait-For($block, $tries = 40) { for ($i = 0; $i -lt $tries; $i++) { $r = & $block; if ($r) { return $r }; Start-Sleep -Milliseconds 250 } }

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
'x' | Set-Content "$game\stellaris.exe"
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"
'{ "id": "p1", "name": "Test", "mods": [] }' | Set-Content "$data\playsets\p1.json"

# a mod with its .mod file, left alone; a nested mod folder with no .mod file; a .mod file whose folder is gone
New-Item -ItemType Directory -Force "$user\mod\fine", "$user\mod\outer\inner\common" | Out-Null
"name=`"Fine Mod`"`nversion=`"1`"`nsupported_version=`"v4.*`"`n" | Set-Content "$user\mod\fine\descriptor.mod"
"name=`"Fine Mod`"`nversion=`"1`"`nsupported_version=`"v4.*`"`npath=`"mod/fine`"`n" | Set-Content "$user\mod\Fine.mod"
"name=`"Nested Mod`"`nversion=`"1`"`nsupported_version=`"v4.*`"`n" | Set-Content "$user\mod\outer\inner\descriptor.mod"
'x' | Set-Content "$user\mod\outer\inner\common\a.txt"
"name=`"Gone Mod`"`nversion=`"1`"`nsupported_version=`"v4.*`"`npath=`"mod/gone`"`n" | Set-Content "$user\mod\Gone.mod"

Start-App $data $user
$nested = "$user\mod\Nested Mod.mod"
Check 'a nested mod folder with no .mod file gets one, pointing at the folder' ((Test-Path $nested) -and (Get-Content $nested -Raw) -match 'path="mod/outer/inner"')
Check 'the mod folders that already had a .mod file get nothing more' (@(Get-ChildItem "$user\mod" -Filter *.mod).Count -eq 3)
Check 'the missing mod is reported' ($null -ne (Wait-For { Find-Typed 'Review…' $btn }))
Click-El (Find-Typed 'Review…' $btn)
$dialog = Wait-For { Dialog-Window }
Click-El (Find-Typed 'Recycle' $btn $dialog); Start-Sleep -Milliseconds 1200
# the sandbox lives in the temp folder, so what was sent away is in the user's Recycle Bin; look it up, then take our own test file out again
$bin = (New-Object -ComObject Shell.Application).Namespace(10)
$binned = @($bin.Items() | Where-Object { "$($_.ExtendedProperty('System.Recycle.DeletedFrom'))" -like '*modbox*' })
Check 'removing sends the leftover .mod file to the Recycle Bin' (-not (Test-Path "$user\mod\Gone.mod") -and $binned.Count -eq 1 -and $binned[0].Name -like 'Gone*')
Check 'no backup folder is made' (-not (Test-Path "$data\backup\descriptors"))
foreach ($item in $binned) {
  $leaf = Split-Path $item.Path -Leaf
  Remove-Item -LiteralPath $item.Path -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath (Join-Path (Split-Path $item.Path) ('$I' + $leaf.Substring(2))) -Force -ErrorAction SilentlyContinue
}
Check 'the mods that have their files are untouched' ((Test-Path "$user\mod\Fine.mod") -and (Test-Path $nested))
Check 'the report is gone once nothing is missing' ($null -eq (Find-Typed 'Review…' $btn))
Shot 'ModFolderSmoke.png'
Stop-App
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
