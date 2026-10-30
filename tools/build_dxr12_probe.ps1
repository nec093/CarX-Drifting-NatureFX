# Builds native/Dxr12Probe (does DXR 1.2 work through an Agility SDK 1.619 device factory?) and runs it.
#
#   powershell -File tools/build_dxr12_probe.ps1
#
# Needs Visual Studio's C++ tools, the Agility SDK 1.619 NuGet package (Microsoft.Direct3D.D3D12) and DXC 1.9
# unpacked outside the repo. Env overrides: NFX_VS, NFX_AGILITY (the package's root), NFX_DXC19 (its dxc.exe).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'native\Dxr12Probe'
$out = Join-Path $src 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$vs = if ($env:NFX_VS) { $env:NFX_VS } else {
	& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
}
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$agility = if ($env:NFX_AGILITY) { $env:NFX_AGILITY } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\agility-1.619.6' }
$dxc = if ($env:NFX_DXC19) { $env:NFX_DXC19 } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\dxc-1.9.2609\bin\x64\dxc.exe' }

& $dxc -nologo -T cs_6_9 -E main -O3 -Vn g_ProbeOmm -Fh (Join-Path $out 'ProbeOmm.h') (Join-Path $src 'ProbeOmm.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on ProbeOmm' }
& $dxc -nologo -T lib_6_9 -O3 -Vn g_ProbeSer -Fh (Join-Path $out 'ProbeSer.h') (Join-Path $src 'ProbeSer.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on ProbeSer' }
& $dxc -nologo -T cs_6_9 -E main -O3 -Vn g_ProbeOmmCheck -Fh (Join-Path $out 'ProbeOmmCheck.h') (Join-Path $src 'ProbeOmmCheck.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on ProbeOmmCheck' }
& $dxc -nologo -T cs_6_6 -E main -O3 -Vn g_BakeOmm -Fh (Join-Path $out 'BakeOmm.h') (Join-Path $repo 'native\NFXNative\shaders\BakeOmm.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on BakeOmm' }

# the Agility SDK's headers come before the Windows SDK's (they declare the 1.619 API)
$include = Join-Path $agility 'build\native\include'
$cl = "cl /nologo /O2 /EHsc /std:c++17 /utf-8 /W3 /I`"$include`" /I`"$out`" `"$src\Dxr12Probe.cpp`" /Fo`"$out\\`" /Fe`"$out\Dxr12Probe.exe`" /link d3d12.lib dxgi.lib"
cmd /c "`"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw 'cl failed' }

# the 1.619 runtime next to the probe, as NFXNative would carry it in the mod folder
$d3d12 = Join-Path $out 'D3D12'
New-Item -ItemType Directory -Force $d3d12 | Out-Null
Copy-Item -Force (Join-Path $agility 'build\native\bin\x64\D3D12Core.dll') $d3d12
& (Join-Path $out 'Dxr12Probe.exe') "$d3d12\"
