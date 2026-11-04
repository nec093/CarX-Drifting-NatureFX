# Builds native/NFXNative (NatureFX's own D3D12 ray tracing engine) into kino/mods/NatureFX/NFXNative.dll.
#
#   powershell -File tools/build_native.ps1
#
# Needs Visual Studio's C++ tools (vcvars64.bat), the Windows SDK's dxc.exe and Unity's PluginAPI headers
# (from the Unity 2023.2 editor install). Env overrides: NFX_VS, NFX_DXC, NFX_UNITY_PLUGINAPI, GAME_DIR.
# -ShadersOnly: only the shaders, into kino/mods/NatureFX/NativeShaders (the running game takes them on Settings >
# Reload shaders); the DLL is neither built nor copied.
param([switch]$ShadersOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'native\NFXNative'
$out = Join-Path $src 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$vs = if ($env:NFX_VS) { $env:NFX_VS } else {
	& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
}
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$dxc = if ($env:NFX_DXC) { $env:NFX_DXC } else {
	(Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\dxc.exe" | Sort-Object FullName | Select-Object -Last 1).FullName
}
$pluginApi = if ($env:NFX_UNITY_PLUGINAPI) { $env:NFX_UNITY_PLUGINAPI } else { 'C:\Program Files\Unity\Hub\Editor\2023.2.20f1\Editor\Data\PluginAPI' }
$game = if ($env:GAME_DIR) { $env:GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online' }

# shaders: DXIL embedded as byte arrays (g_<name>)
foreach ($shader in @(@{ Name = 'RayQueryTest'; Profile = 'cs_6_5' }, @{ Name = 'SceneRay'; Profile = 'cs_6_5' }, @{ Name = 'SceneRays'; Profile = 'cs_6_5' }, @{ Name = 'SunShadow'; Profile = 'cs_6_6' }, @{ Name = 'SanitizeIndices'; Profile = 'cs_6_5' }, @{ Name = 'ExtractUvs'; Profile = 'cs_6_5' }, @{ Name = 'Ambient'; Profile = 'cs_6_6' }, @{ Name = 'LocalShadows'; Profile = 'cs_6_6' }, @{ Name = 'Shafts'; Profile = 'cs_6_6' }, @{ Name = 'Surface'; Profile = 'cs_6_6' }, @{ Name = 'Reflection'; Profile = 'cs_6_6' }, @{ Name = 'GI'; Profile = 'cs_6_6' }, @{ Name = 'GICacheResolve'; Profile = 'cs_6_6' }, @{ Name = 'Glass'; Profile = 'cs_6_6' }, @{ Name = 'SkyTransmittance'; Profile = 'cs_6_6' }, @{ Name = 'SkyMultiScatter'; Profile = 'cs_6_6' }, @{ Name = 'SkyView'; Profile = 'cs_6_6' }, @{ Name = 'CloudNoise'; Profile = 'cs_6_6' }, @{ Name = 'Clouds'; Profile = 'cs_6_6' }, @{ Name = 'CloudShadow'; Profile = 'cs_6_6' }, @{ Name = 'SkyAmbient'; Profile = 'cs_6_6' }, @{ Name = 'Fog'; Profile = 'cs_6_6' }, @{ Name = 'FogVolume'; Profile = 'cs_6_6' }, @{ Name = 'SkyCube'; Profile = 'cs_6_6' }, @{ Name = 'LampMask'; Profile = 'cs_6_6' }, @{ Name = 'SunMap'; Profile = 'cs_6_6' }, @{ Name = 'GroundMap'; Profile = 'cs_6_6' }, @{ Name = 'CopyRaw'; Profile = 'cs_6_5' }, @{ Name = 'BakeAlpha'; Profile = 'cs_6_6' }, @{ Name = 'BakeOmm'; Profile = 'cs_6_6' }, @{ Name = 'WindDisplace'; Profile = 'cs_6_5' })) {
	& $dxc -nologo -T $shader.Profile -E main -O3 -Vn "g_$($shader.Name)" -Fh (Join-Path $out "$($shader.Name).h") -Fo (Join-Path $out "$($shader.Name).cso") (Join-Path $src "shaders\$($shader.Name).hlsl")
	if ($LASTEXITCODE -ne 0) { throw "dxc failed on $($shader.Name)" }
}

# DXR 1.2 probe (native/Dxr12Probe): Shader Model 6.9 shaders from DXC 1.9, and the Agility SDK 1.619 headers ahead
# of the Windows SDK's (they declare its device factory and the DXR 1.2 API)
$agility = if ($env:NFX_AGILITY) { $env:NFX_AGILITY } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\agility-1.619.6' }
$dxc19 = if ($env:NFX_DXC19) { $env:NFX_DXC19 } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\dxc-1.9.2609\bin\x64\dxc.exe' }
$probe = Join-Path $repo 'native\Dxr12Probe'
& $dxc19 -nologo -T cs_6_9 -E main -O3 -Vn g_ProbeOmm -Fh (Join-Path $out 'ProbeOmm.h') (Join-Path $probe 'ProbeOmm.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on ProbeOmm' }
& $dxc19 -nologo -T lib_6_9 -O3 -Vn g_ProbeSer -Fh (Join-Path $out 'ProbeSer.h') (Join-Path $probe 'ProbeSer.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on ProbeSer' }
# the sun shadows on the DXR 1.2 device: Shader Model 6.9, alpha from the baked byte masks
& $dxc19 -nologo -T cs_6_9 -E main -O3 -D NFX_ALPHA_MASK -D NFX_NO_GLASS_SHADOWS -D NFX_OMM -Vn g_SunShadow619 -Fh (Join-Path $out 'SunShadow619.h') (Join-Path $src 'shaders\SunShadow.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'dxc failed on SunShadow619' }
$agilityInclude = Join-Path $agility 'build\native\include'

# NVIDIA Real-Time Denoisers (Nrd.inl): the static library built from %USERPROFILE%\NatureFX-Deps\NRD (cmake, NMake,
# NRD_STATIC_LIBRARY=ON, DXIL only, /MT) and NRD.hlsli for the pack / unpack shaders
$nrd = if ($env:NFX_NRD) { $env:NFX_NRD } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\NRD' }
foreach ($name in @('NrdSigmaPack', 'NrdSigmaUnpack', 'NrdReblurPack', 'NrdReblurUnpack', 'NrdSpecPack', 'NrdSpecUnpack')) {
	& $dxc -nologo -T cs_6_6 -E main -O3 -I (Join-Path $nrd 'Shaders') -Vn "g_$name" -Fh (Join-Path $out "$name.h") -Fo (Join-Path $out "$name.cso") (Join-Path $src "shaders\$name.hlsl")
	if ($LASTEXITCODE -ne 0) { throw "dxc failed on $name" }
}
$nrdInclude = Join-Path $nrd 'Include'

# NVIDIA DLSS (Dlss.inl): the NGX SDK from %USERPROFILE%\NatureFX-Deps\DLSS (github.com/NVIDIA/DLSS), its static
# library (/MT) and nvngx_dlss.dll next to NFXNative.dll
$dlss = if ($env:NFX_DLSS) { $env:NFX_DLSS } else { Join-Path $env:USERPROFILE 'NatureFX-Deps\DLSS' }
foreach ($name in @('DlssPack', 'DlssUnpack', 'DlssPackGuides')) {
	& $dxc -nologo -T cs_6_5 -E main -O3 -Vn "g_$name" -Fh (Join-Path $out "$name.h") -Fo (Join-Path $out "$name.cso") (Join-Path $src "shaders\$name.hlsl")
	if ($LASTEXITCODE -ne 0) { throw "dxc failed on $name" }
}
$dlssInclude = Join-Path $dlss 'include'

# the compiled shaders as files too: the engine prefers them to its embedded copies once reloaded (never locked, so
# they can be replaced while the game runs)
$dest = Join-Path $game 'kino\mods\NatureFX'
$shaderDest = Join-Path $dest 'NativeShaders'
New-Item -ItemType Directory -Force $shaderDest | Out-Null
Copy-Item -Force (Join-Path $out '*.cso') $shaderDest
if ($ShadersOnly) {
	Write-Host "[NFXNative] Shaders copied to $shaderDest (Developer > Reload shaders in the game)"
	return
}
$nrdLibs = "`"$(Join-Path $nrd '_Bin\NRD.lib')`" `"$(Join-Path $nrd '_Build\_deps\shadermake-build\ShaderMakeBlob.lib')`""

$cl = "cl /nologo /O2 /EHsc /std:c++17 /utf-8 /W3 /LD /I`"$agilityInclude`" /I`"$pluginApi`" /I`"$out`" /I`"$probe`" /I`"$nrdInclude`" /I`"$dlssInclude`" `"$src\src\NFXNative.cpp`" /Fo`"$out\\`" /link d3d12.lib dxgi.lib $nrdLibs `"$(Join-Path $dlss 'lib\Windows_x86_64\x64\nvsdk_ngx_s.lib')`" advapi32.lib version.lib user32.lib /OUT:`"$out\NFXNative.dll`" /IMPLIB:`"$out\NFXNative.lib`""
cmd /c "`"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw 'cl failed' }

# the 1.619 runtime the DXR 1.2 probe loads (kino/mods/NatureFX/D3D12/D3D12Core.dll); not the game's own D3D12 folder
New-Item -ItemType Directory -Force (Join-Path $dest 'D3D12') | Out-Null
Copy-Item -Force (Join-Path $agility 'build\native\bin\x64\D3D12Core.dll') (Join-Path $dest 'D3D12')
Copy-Item -Force (Join-Path $out 'NFXNative.dll') $dest
# DLSS's runtime (NGX loads it from NFXNative.dll's folder) and its licence
Copy-Item -Force (Join-Path $dlss 'lib\Windows_x86_64\rel\nvngx_dlss.dll') $dest
Copy-Item -Force (Join-Path $dlss 'lib\Windows_x86_64\rel\nvngx_dlssd.dll') $dest
Copy-Item -Force (Join-Path $dlss 'LICENSE.txt') (Join-Path $dest 'DLSS-LICENSE.txt')
Write-Host "[NFXNative] Built and copied to $dest"
