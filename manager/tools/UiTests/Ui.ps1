Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
}
'@
[Win]::SetProcessDPIAware() | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
# Helpers for driving the real app through Windows UI Automation, so no test ever clicks into another program: buttons are invoked through
# UI Automation (they work with the window behind others), screenshots use PrintWindow, and a real mouse click or key press happens only
# if Artemis (or its dialog) is the window in front. Output (screenshots, the sandbox) goes to the temp folder, in artemis-uitests.
# ARTEMIS_EXE overrides which build is run. Never invoke Launch from a test: it writes the real dlc_load.json and starts the game.
$script:scratch = Join-Path ([IO.Path]::GetTempPath()) 'artemis-uitests'
New-Item -ItemType Directory -Force $script:scratch | Out-Null
$script:exe = if ($env:ARTEMIS_EXE) { $env:ARTEMIS_EXE } else { Join-Path $PSScriptRoot '..\..\src\Artemis.App\bin\Debug\net10.0-windows\Artemis.exe' }

function Start-App($data, $user, $extra = @()) {
  $env:ARTEMIS_NO_WELCOME = '1'
  $p = Start-Process $script:exe -ArgumentList (@('--data-dir', "`"$data`"", '--user-dir', "`"$user`"") + $extra) -PassThru
  $script:proc = $p
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { break }
  }
  $script:win = $AE::FromHandle($p.MainWindowHandle)
  [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 1500
}

function Find-El($name, $root = $script:win) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-All($name, $root = $script:win) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-El($el) {
  if (-not $el) { throw 'Click-El: element not found' }
  # UI Automation actions work without the window being in front, so they never touch another app
  foreach ($pattern in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern)) {
    $obj = $null
    if ($el.TryGetCurrentPattern($pattern, [ref]$obj)) {
      if ($pattern -eq [System.Windows.Automation.InvokePattern]::Pattern) { $obj.Invoke() }
      elseif ($pattern -eq [System.Windows.Automation.SelectionItemPattern]::Pattern) { $obj.Select() }
      else { $obj.Toggle() }
      Start-Sleep -Milliseconds 400
      return
    }
  }
  # a real mouse click only if Artemis is the window in front; otherwise it would land in whatever is on top
  if ([Win]::GetForegroundWindow() -ne $script:proc.MainWindowHandle) { throw 'Click-El: Artemis is not in front; refusing to click the mouse' }
  $r = $el.Current.BoundingRectangle
  if ($r.IsEmpty) { throw 'Click-El: element has no position' }
  $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
  [Win]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 80
  [Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 400
}

function Click-Name($name) {
  $el = Find-El $name
  if (-not $el) { throw "No element named '$name'" }
  Click-El $el
}

function Shot($file) {
  $r = $script:win.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [Win]::PrintWindow($script:proc.MainWindowHandle, $hdc, 2) | Out-Null    # PW_RENDERFULLCONTENT
  $g.ReleaseHdc($hdc)
  $bmp.Save((Join-Path $script:scratch $file), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

function ShotScreen($file, $x, $y, $w, $h) {   # for pop-up menus, which are separate windows
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path $script:scratch $file), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

function Stop-App {
  try { $script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
  $script:proc.WaitForExit(5000) | Out-Null
  if (-not $script:proc.HasExited) { $script:proc.Kill() }
}

function Dump-Names($max = 60) {
  $all = $script:win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $all | Where-Object { $_.Current.Name } | Select-Object -First $max | ForEach-Object { "{0} [{1}]" -f $_.Current.Name, $_.Current.ControlType.ProgrammaticName }
}

function Find-Typed($name, $type, $root = $script:win) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
}
