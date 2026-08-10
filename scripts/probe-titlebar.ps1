# Checks, from outside the IDE, whether the Cockpit button made it into the title bar.
# UI Automation sees what the user sees, which is the only honest way to verify a button
# that was grafted onto the shell's own window.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$devenv = Get-Process devenv -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $devenv) { 'devenv is not running.'; return }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($devenv.MainWindowHandle)
if (-not $root) { 'The main window has no automation element yet.'; return }

$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)

$named = @()
foreach ($b in $buttons) {
    $name = $b.Current.Name
    if ($name) { $named += $name }
}

"Buttons in the window: $($buttons.Count)"
if ($named -contains 'Tootega Cockpit') { "[ ok ] 'Tootega Cockpit' is there." }
else { "[fail] No 'Tootega Cockpit' button." }

'Neighbours in the title bar area:'
$named | Where-Object { $_ -match 'Copilot|Cockpit|Feedback|account|Minimi|Maximi|Close|Restore|Search' } |
    Select-Object -Unique | ForEach-Object { "   $_" }
