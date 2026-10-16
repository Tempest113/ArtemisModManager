# Drives the real app against a throw-away sandbox to check the Logs page (D56): a log shows what the game writes while it is open (and keeps the scroll
# position of someone who scrolled up), and "Clean up old logs" sends leftovers to the Recycle Bin and nothing else. Prints PASS or FAIL per check.
# Build first: dotnet build manager/src/Artemis.App -o <folder>, then set ARTEMIS_EXE to <folder>\Artemis.exe (or build to the default output).
. "$PSScriptRoot\Ui.ps1"
$root = Join-Path $script:scratch 'logbox'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = Join-Path $root 'data'; $user = Join-Path $root 'user'; $game = Join-Path $root 'game'
New-Item -ItemType Directory -Force "$user\mod", "$user\logs", "$user\modloader\plugins", "$data\playsets", $game | Out-Null
$btn = [System.Windows.Automation.ControlType]::Button
$failed = 0
function Check($what, $ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed++ } }
function Dialog-Window { $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window); $script:win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
function Wait-For($block, $tries = 40) { for ($i = 0; $i -lt $tries; $i++) { $r = & $block; if ($r) { return $r }; Start-Sleep -Milliseconds 250 } }
function Log-Text { (Find-El 'Log text').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Log-Scroll { (Find-El 'Log text').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticalScrollPercent }
function Age($path, $days) { (Get-Item $path).LastWriteTimeUtc = (Get-Date).AddDays(-$days).ToUniversalTime() }

'{ "gameId": "stellaris", "rawVersion": "v4.5.2", "version": "Cygnus v4.5.2", "modsCompatibilityVersion": "4.5", "exePath": "./stellaris.exe", "exeArgs": [], "gameDataPath": "" }' | Set-Content "$game\launcher-settings.json"
'x' | Set-Content "$game\stellaris.exe"
(@{ gameDir = $game; activePlaysetId = 'p1' } | ConvertTo-Json) | Set-Content "$data\settings.json"
'{ "id": "p1", "name": "Test", "mods": [] }' | Set-Content "$data\playsets\p1.json"

# 300 lines in error.log (enough to scroll), the game's other logs, a stale log from some other tool, Orion's log, an old loader's log, a plugin that is not installed
(1..300 | ForEach-Object { '[10:00:{0:D2}][a.cpp:{1}]: line {1}' -f ($_ % 60), $_ }) | Set-Content "$user\logs\error.log"
'[10:00:00][b.cpp:1]: game' | Set-Content "$user\logs\game.log"
'stale' | Set-Content "$user\logs\stellarstellaris-debug.log"; Age "$user\logs\stellarstellaris-debug.log" 400
'orion' | Set-Content "$user\modloader\orion.log"
'old loader' | Set-Content "$user\modloader\modloader.log"
'gone' | Set-Content "$user\modloader\plugins\Gone.log"

Start-App $data $user
Click-El (Find-Typed 'Logs' ([System.Windows.Automation.ControlType]::RadioButton))
Check 'the error log is shown first, with its end in view' (Wait-For { (Log-Text) -match 'line 300' })
Check 'the view is scrolled to the end' ((Log-Scroll) -gt 95)

Add-Content "$user\logs\error.log" '[10:05:00][c.cpp:9]: appended live while open'
Check 'a line the game writes appears without refreshing' ($null -ne (Wait-For { (Log-Text) -match 'appended live while open' }))
Check 'the view follows the end' ((Log-Scroll) -gt 95)

(Find-El 'Log text').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 0)
Start-Sleep -Milliseconds 300
Add-Content "$user\logs\error.log" '[10:06:00][c.cpp:10]: second line while reading higher up'
Check 'the second line appears too' ($null -ne (Wait-For { (Log-Text) -match 'second line while reading' }))
Check 'a reader who scrolled up is left where they are' ((Log-Scroll) -lt 5)

# a new log appearing in the folder shows up in the list
'[10:07:00][d.cpp:1]: new' | Set-Content "$user\logs\setup.log"
Check 'a log that appears is listed' ($null -ne (Wait-For { Find-El 'setup.log' }))
Check 'the chosen log keeps its place' ((Log-Text) -match 'second line while reading')

Click-El (Find-El 'Clean up old logs')
$dialog = Wait-For { Dialog-Window }
$text = (Dump-Names 200) -join "`n"
Check 'the list names the stale log and the old loader and the plugin that is not installed' (($text -match 'stellarstellaris-debug') -and ($text -match 'modloader.log') -and ($text -match 'Gone.log'))
Check 'the logs of the latest run and Orion are not in the list' (($text -notmatch 'error.log\s+\(') -and ($text -notmatch 'orion.log\s+\('))
Click-El (Find-Typed 'Recycle' $btn $dialog); Start-Sleep -Milliseconds 2500
# the sandbox lives in the temp folder, so what was sent away is in the user's Recycle Bin; look it up, then take our own test files out again
$bin = (New-Object -ComObject Shell.Application).Namespace(10)
$binned = @($bin.Items() | Where-Object { "$($_.ExtendedProperty('System.Recycle.DeletedFrom'))" -like '*logbox*' })
Check 'the leftovers went to the Recycle Bin' ((($binned | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) } | Sort-Object) -join ',') -eq 'Gone,modloader,stellarstellaris-debug')
Check 'they are gone from the folders' (-not ((Test-Path "$user\logs\stellarstellaris-debug.log") -or (Test-Path "$user\modloader\modloader.log") -or (Test-Path "$user\modloader\plugins\Gone.log")))
Check 'the logs of the latest run and Orion stayed' ((Test-Path "$user\logs\error.log") -and (Test-Path "$user\logs\game.log") -and (Test-Path "$user\modloader\orion.log"))
Check 'no backup folder is made' (-not (Test-Path "$data\backup\logs"))
Check 'recycled logs leave the list' (($null -eq (Find-El 'stellarstellaris-debug.log')) -and ($null -ne (Find-El 'error.log')))
foreach ($item in $binned) {
  $leaf = Split-Path $item.Path -Leaf
  Remove-Item -LiteralPath $item.Path -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath (Join-Path (Split-Path $item.Path) ('$I' + $leaf.Substring(2))) -Force -ErrorAction SilentlyContinue
}
Shot 'LogsSmoke.png'
Stop-App
if ($failed -gt 0) { "$failed check(s) failed"; exit 1 } else { 'All checks passed' }
