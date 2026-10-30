# Runs a program on all but -FreeThreads logical CPUs, at below normal priority, and waits for it.
# Child processes started afterwards (Unity's UnityShaderCompiler.exe workers) inherit the CPU set, so a
# long shader build leaves those threads to the rest of the PC.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File run_limited.ps1 -Exe <path> [-FreeThreads 2]
#
# The program's arguments come from the NFX_RUN_ARGS environment variable (one Windows command line), which
# avoids PowerShell treating Unity's "-batchmode" style arguments as its own parameters.
param(
	[Parameter(Mandatory = $true)][string]$Exe,
	[int]$FreeThreads = 2
)

$n = [Environment]::ProcessorCount
$use = [Math]::Max(1, $n - $FreeThreads)
$mask = [Int64](([Int64]1 -shl $use) - 1)

$p = Start-Process -FilePath $Exe -ArgumentList $env:NFX_RUN_ARGS -PassThru -NoNewWindow
$null = $p.Handle # keep the handle so ExitCode is available after exit
$p.ProcessorAffinity = [IntPtr]$mask
$p.PriorityClass = 'BelowNormal'
Write-Host "Running on $use of $n logical CPUs ($($n - $use) left free), below normal priority"

$p.WaitForExit()
exit $p.ExitCode
