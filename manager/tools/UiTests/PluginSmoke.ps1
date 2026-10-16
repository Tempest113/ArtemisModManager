# Drives the plugin features of the real app against a throw-away sandbox (a pretend game folder, two mods, no real files touched):
# notices, the Plugins page, approving, a setting, the crash marker, Create a mod. Prints PASS or FAIL per check.
# Build first: dotnet build manager/src/Artemis.App   (or set ARTEMIS_EXE)
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'plugbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$user\modloader", "$data\playsets", $game | Out-Null
$btn = [System.Windows.Automation.ControlType]::Button; $menu = [System.Windows.Automation.ControlType]::MenuItem
$edit = [System.Windows.Automation.ControlType]::Edit; $radio = [System.Windows.Automation.ControlType]::RadioButton
$check = [System.Windows.Automation.ControlType]::CheckBox
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }
function Dialog-Window { $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window); $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
function Wait-For($block, $tries = 40) { for ($i = 0; $i -lt $tries; $i++) { $r = & $block; if ($r) { return $r }; Start-Sleep -Milliseconds 250 } }

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
Copy-Item "$env:SystemRoot\System32\hostname.exe" "$game\stellaris.exe"   # any real program: Orion's build id comes from its headers
'not a real dll' | Set-Content "$game\winmm.dll"
$orionFiles = Join-Path $PSScriptRoot '..\..\..\orion\build'               # built by orion\build.bat; the install steps are skipped without it
$haveOrion = (Test-Path "$orionFiles\winmm.dll") -and (Test-Path "$orionFiles\orion.dll")
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"
function New-Mod($key, $name, $files) {
  New-Item -ItemType Directory -Force "$user\mod\$key" | Out-Null
  "name=`"$name`"`nversion=`"1.0`"`nsupported_version=`"v4.*`"`n" | Set-Content "$user\mod\$key\descriptor.mod"
  "name=`"$name`"`nversion=`"1.0`"`nsupported_version=`"v4.*`"`npath=`"mod/$key`"`n" | Set-Content "$user\mod\$key.mod"
  foreach ($f in $files.Keys) { $p = Join-Path "$user\mod\$key" $f; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; $files[$f] | Set-Content $p }
}
New-Mod 'fleet' 'Fleet Mod' @{ 'modloader.json' = '{ "id": "demo.fleet", "name": "Fleet Plugin", "version": "1", "apiVersion": 1, "files": ["orion/f.dll"], "settings": [ { "id": "speed", "type": "float", "name": "Build speed", "default": 1.5, "min": 0.5, "max": 4 } ] }'; 'orion\f.dll' = 'MZ' }
New-Mod 'plain' 'Plain Mod' @{ 'gfx\a.dds' = 'x' }
'{ "id": "p1", "name": "Test", "mods": [ { "key": "fleet" }, { "key": "plain" } ] }' | Set-Content "$data\playsets\p1.json"
'{ "mod": "fleet", "id": "demo.fleet", "file": "orion/f.dll" }' | Set-Content "$user\modloader\loading.json"

Start-App $data $user @('--orion-dir', "`"$((Resolve-Path $orionFiles -ErrorAction SilentlyContinue).Path)`"")
Check 'a winmm.dll that is not ours is reported' ($null -ne (Wait-For { Find-Typed 'Show the folder' $btn }))
Check 'a plugin the game died loading is reported' ($null -ne (Wait-For { Find-Typed 'Switch the plugin off here' $btn }))
Click-El (Find-Typed 'Switch the plugin off here' $btn); Start-Sleep -Milliseconds 600
Check 'switching it off is saved in the playset and clears the marker' ((Get-Content "$data\playsets\p1.json" -Raw) -match '"pluginOff": true' -and -not (Test-Path "$user\modloader\loading.json"))

Click-El (Find-Typed 'Plugins' $radio)
Click-El (Wait-For { Find-Typed 'Fleet Plugin' ([System.Windows.Automation.ControlType]::ListItem) })
Click-El (Wait-For { Find-Typed 'Approve…' $btn })
$dialog = Wait-For { Dialog-Window }
Click-El (Find-Typed 'Approve' $btn $dialog); Start-Sleep -Milliseconds 800
Check 'approving writes trust.json with the file hash' ((Get-Content "$data\trust.json" -Raw -ErrorAction SilentlyContinue) -match '"orion/f.dll": "[0-9a-f]{64}"')

Click-El (Find-Typed 'Run this plugin in the current playset' $check); Start-Sleep -Milliseconds 400     # it was switched off above: back on
$speed = Wait-For { Find-Typed 'Build speed' $edit }
$speed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('2.5')
(Find-Typed 'Load plugins' $check).SetFocus(); Start-Sleep -Milliseconds 400                              # leaving the box commits the value
Click-El (Find-Typed 'Launch options' $btn)
Click-El (Wait-For { Find-Typed 'Save the load order without launching' $menu }); Start-Sleep -Milliseconds 800
$export = Get-Content "$data\plugin-settings.json" -Raw -ErrorAction SilentlyContinue
Check 'saving writes plugin-settings.json with the chosen value and the plugin on' ($export -match '"speed": 2.5' -and $export -match '"enabled": true')

if ($haveOrion) {
  # Orion's panel: refuses to touch a winmm.dll that isn't ours; installs once that is out of the way; allows the build; removes everything of ours.
  Check 'the Plugins page says another winmm.dll is in the way and offers no install' ($null -ne (Find-El 'Another winmm.dll is in the Stellaris folder.') -and $null -eq (Find-Typed 'Install Orion…' $btn))
  Remove-Item "$game\winmm.dll"
  Click-El (Find-Typed 'Mods' $radio); Click-El (Find-Typed 'Plugins' $radio)                              # coming back to the page looks at the folder again
  Click-El (Wait-For { Find-Typed 'Install Orion…' $btn })
  Click-El (Find-Typed 'Install' $btn (Wait-For { Dialog-Window })); Start-Sleep -Milliseconds 800
  $stub = Get-Item "$game\winmm.dll" -ErrorAction SilentlyContinue
  Check 'installing puts the stub in the game folder, saying Artemis as its company' ($null -ne $stub -and $stub.VersionInfo.CompanyName -like 'Artemis*')
  Check 'installing puts the core in a version folder and names it in current.txt' ((Test-Path "$data\orion\current.txt") -and @(Get-ChildItem "$data\orion\*\orion.dll").Count -eq 1)
  Check 'the page then says Orion is installed' ($null -ne (Wait-For { Find-El 'Orion 0.1.0 is installed.' }))
  Shot 'PluginsOrion.png'

  Click-El (Find-Typed 'Allow plugins on this build…' $btn)
  Click-El (Find-Typed 'Allow' $btn (Wait-For { Dialog-Window })); Start-Sleep -Milliseconds 600
  $build = Get-ChildItem "$data\orion\builds\*.json" -ErrorAction SilentlyContinue | Select-Object -First 1
  Check 'allowing the build writes its file, marked as the user''s own, with no addresses' ($null -ne $build -and (Get-Content $build -Raw) -match '"source":"user"' -and (Get-Content $build -Raw) -match '"addresses":\{\}')
  Check 'the page then offers to take the permission back' ($null -ne (Find-Typed 'Stop allowing plugins on this build' $btn))

  Click-El (Find-Typed 'Remove Orion' $btn)
  Click-El (Find-Typed 'Remove' $btn (Wait-For { Dialog-Window })); Start-Sleep -Milliseconds 800
  Check 'removing takes out the stub and the pointer but keeps the allowed build' (-not (Test-Path "$game\winmm.dll") -and -not (Test-Path "$data\orion\current.txt") -and (Test-Path $build.FullName))
}

Click-El (Find-Typed 'Mods' $radio)
Click-El (Wait-For { Find-Typed 'Add a mod' $btn })
Click-El (Wait-For { Find-Typed 'Create a mod…' $menu })
$dialog = Wait-For { Dialog-Window }
$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $edit))).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Created By Test')
Click-El (Find-Typed 'OK' $btn $dialog); Start-Sleep -Milliseconds 1200
Check 'Create a mod makes the folder and the .mod file' ((Test-Path "$user\mod\Created By Test\descriptor.mod") -and (Test-Path "$user\mod\Created By Test.mod"))
Shot 'PluginSmoke.png'
Stop-App
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
