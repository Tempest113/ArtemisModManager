# Drives the real window against a throw-away sandbox to check adding and removing several mods at once: the right-click menu, the Enter key, and Ctrl+Z / Ctrl+Y
# (the whole batch is one save and one undo step). The result is read from the playset file on disk. Prints PASS or FAIL per check.
# Unlike the other scripts this one sends a real mouse click and real key presses, because a context menu and key bindings can't be reached through UI Automation.
# It does so only while Artemis is the window in front, and stops with an error otherwise: leave the machine alone while it runs (about 15 seconds).
# Build first: dotnet build manager/src/Artemis.App   (or set ARTEMIS_EXE)
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'bulkbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$data\playsets", $game | Out-Null
$menuItem = [System.Windows.Automation.ControlType]::MenuItem
$listItem = [System.Windows.Automation.ControlType]::ListItem
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
'x' | Set-Content "$game\stellaris.exe"
function New-Mod($key, $name) {
  New-Item -ItemType Directory -Force "$user\mod\$key" | Out-Null
  "name=`"$name`"`nversion=`"1.0`"`nsupported_version=`"v4.*`"`n" | Set-Content "$user\mod\$key\descriptor.mod"
  "name=`"$name`"`nversion=`"1.0`"`nsupported_version=`"v4.*`"`npath=`"mod/$key`"`n" | Set-Content "$user\mod\$key.mod"
}
New-Mod 'base' 'Base Mod'
1..40 | ForEach-Object { New-Mod ('m{0:D2}' -f $_) ('Sample Mod {0:D2}' -f $_) }
'{ "id": "p1", "name": "Test", "mods": [ { "key": "base" } ] }' | Set-Content "$data\playsets\p1.json"
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"

function Count-Mods { try { @((Get-Content "$data\playsets\p1.json" -Raw | ConvertFrom-Json).mods).Count } catch { -1 } }
function Wait-Count($n, $ms = 6000) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { if ((Count-Mods) -eq $n) { return $sw.ElapsedMilliseconds }; Start-Sleep -Milliseconds 50 }; return -1 }
function Front { [Win]::SetForegroundWindow($script:proc.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300; [Win]::GetForegroundWindow() -eq $script:proc.MainWindowHandle }
function Row($name) { Find-Typed $name $listItem }
function Press($keys) { if (-not (Front)) { throw 'Artemis is not in front; refusing to send keys' }; [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 300 }
function Select-Rows($names) {   # the first row, then Shift+Down: a real keyboard selection of consecutive rows (focus stays in the list for Enter)
  (Row $names[0]).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
  (Row $names[0]).SetFocus(); Start-Sleep -Milliseconds 200
  Press ('+{DOWN ' + ($names.Count - 1) + '}')
}
function Right-Click-Row($name) {
  if (-not (Front)) { throw 'Artemis is not in front; refusing to use the mouse' }
  $r = (Row $name).Current.BoundingRectangle
  [Win]::SetCursorPos([int]($r.X + $r.Width * 0.3), [int]($r.Y + $r.Height / 2)) | Out-Null; Start-Sleep -Milliseconds 120
  [Win]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [Win]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 600   # right button down, up
}
function Menu-Click($text) {   # a pop-up menu is a window of its own: look from the desktop
  $el = $null
  for ($i = 0; $i -lt 20 -and -not $el; $i++) { $el = Find-Typed $text $menuItem $AE::RootElement; if (-not $el) { Start-Sleep -Milliseconds 150 } }
  if (-not $el) { throw "menu item '$text' not found" }
  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 200
}

$five = 1..5 | ForEach-Object { 'Sample Mod {0:D2}' -f $_ }
Start-App $data $user
try {
  Check 'the window comes to the front, so real input can be used' (Front)
  Check 'start: the playset holds just the base mod' ((Count-Mods) -eq 1)

  Select-Rows $five
  Right-Click-Row $five[2]
  Menu-Click 'Add to playset'
  Check 'right-click > Add to playset puts all 5 selected mods in the playset' ((Wait-Count 6) -ge 0)
  Press '^z'
  Check 'one Ctrl+Z takes all 5 back out' ((Wait-Count 1) -ge 0)
  Press '^y'
  Check 'Ctrl+Y puts all 5 back' ((Wait-Count 6) -ge 0)
  Press '^z'
  Check 'Ctrl+Z again: back to just the base mod' ((Wait-Count 1) -ge 0)

  Select-Rows $five
  Press '{ENTER}'
  Check 'Enter adds all 5 selected mods' ((Wait-Count 6) -ge 0)
  Select-Rows $five
  Press '{ENTER}'
  Check 'Enter again, with all of them in, takes all 5 out' ((Wait-Count 1) -ge 0)
  Press '^z'
  Check 'one Ctrl+Z puts those 5 back' ((Wait-Count 6) -ge 0)

  Select-Rows $five
  Right-Click-Row $five[1]
  Menu-Click 'Remove from playset'
  Check 'right-click > Remove from playset takes all 5 out' ((Wait-Count 1) -ge 0)
  Shot 'BulkPlaysetSmoke.png'
}
finally { Stop-App }
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
