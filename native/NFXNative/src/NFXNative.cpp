// NFXNative: NatureFX's own D3D12 ray tracing engine, loaded by the C# mod (Rendering/NativeEngine.cs).
//
// Unity's player in CarX reports no inline ray tracing (DXR 1.1) although the GPU and the D3D12 runtime have it,
// so the engine drives Unity's D3D12 device itself: its own shaders (compiled with DXC), acceleration structures
// and command lists.
//
// Stage 1: find the device, report what it supports, trace one inline ray against a test triangle.
// Stage 2: build the scene from Unity's meshes (their vertex / index buffers, one BLAS per mesh and sub mesh, one
// TLAS over the instances) on an own queue, and check it with a test ray the mod compares with a physics raycast.
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <mutex>
#include <algorithm>
#include <vector>
#include <map>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

#include "RayQueryTest.h"  // g_RayQueryTest: shaders/RayQueryTest.hlsl as DXIL
#include "SceneRay.h"      // g_SceneRay: shaders/SceneRay.hlsl as DXIL
#include "SceneRays.h"     // g_SceneRays: shaders/SceneRays.hlsl as DXIL
#include "SunShadow.h"     // g_SunShadow: shaders/SunShadow.hlsl as DXIL
#include "Ambient.h"       // g_Ambient: shaders/Ambient.hlsl as DXIL
#include "LocalShadows.h"  // g_LocalShadows: shaders/LocalShadows.hlsl as DXIL
#include "Shafts.h"        // g_Shafts: shaders/Shafts.hlsl as DXIL
#include "Surface.h"       // g_Surface: shaders/Surface.hlsl as DXIL
#include "Reflection.h"    // g_Reflection: shaders/Reflection.hlsl as DXIL
#include "GI.h"            // g_GI: shaders/GI.hlsl as DXIL
#include "GICacheResolve.h"  // g_GICacheResolve: shaders/GICacheResolve.hlsl as DXIL
#include "Glass.h"            // g_Glass: shaders/Glass.hlsl as DXIL
#include "SkyTransmittance.h" // g_SkyTransmittance: shaders/SkyTransmittance.hlsl (Sky.hlsli)
#include "SkyMultiScatter.h"  // g_SkyMultiScatter
#include "SkyView.h"          // g_SkyView
#include "CloudNoise.h"       // g_CloudNoise: the clouds' noise and type map (Sky.hlsli)
#include "Clouds.h"           // g_Clouds: the cloud layer
#include "CloudShadow.h"      // g_CloudShadow: the clouds' shadow map
#include "SkyAmbient.h"       // g_SkyAmbient: the sky's light for the ambient probe
#include "Fog.h"              // g_Fog: the fog and haze (phase 2)
#include "FogVolume.h"        // g_FogVolume: the 3D fog's scattering per froxel
#include "SkyCube.h"          // g_SkyCube: the engine's sky as HDRP's cube map
#include "LampMask.h"         // g_LampMask: each lamp's own visibility (roadmap A4)
#include "SunMap.h"           // g_SunMap: the sun's ray traced shadow map for transparent surfaces
#include "GroundMap.h"        // g_GroundMap: the ground seen from above (ground blend, painted dirt, debris)
#include "SanitizeIndices.h"  // g_SanitizeIndices: shaders/SanitizeIndices.hlsl as DXIL
#include "ExtractUvs.h"       // g_ExtractUvs: shaders/ExtractUvs.hlsl as DXIL
// the same traces as DXR 1.0 libraries (NFX_PIPELINE: TraceRay in a ray tracing pipeline), g_<name>Lib
#include "SunShadowLib.h"
#include "AmbientLib.h"
#include "LocalShadowsLib.h"
#include "ShaftsLib.h"
#include "SurfaceLib.h"
#include "ReflectionLib.h"
#include "GILib.h"
#include "GICacheResolveLib.h"
#include "GlassLib.h"
#include "SkyTransmittanceLib.h"
#include "SkyMultiScatterLib.h"
#include "SkyViewLib.h"
#include "CloudNoiseLib.h"
#include "CloudsLib.h"
#include "CloudShadowLib.h"
#include "SkyAmbientLib.h"
#include "FogLib.h"
#include "FogVolumeLib.h"
#include "SkyCubeLib.h"
#include "LampMaskLib.h"
#include "SunMapLib.h"
#include "GroundMapLib.h"
#include "CopyRaw.h"          // g_CopyRaw: shaders/CopyRaw.hlsl as DXIL
#include "WindDisplace.h"     // g_WindDisplace: shaders/WindDisplace.hlsl (foliage swayed by the wind)
#include "BakeAlpha.h"        // g_BakeAlpha: shaders/BakeAlpha.hlsl as DXIL
#include "BakeOmm.h"          // g_BakeOmm: shaders/BakeOmm.hlsl (opacity micromaps from the alpha masks)
#include "SunShadow619.h"     // g_SunShadow619: shaders/SunShadow.hlsl for the DXR 1.2 device (SM 6.9, alpha masks)
#include "NrdSigmaPack.h"     // g_NrdSigmaPack: shaders/NrdSigmaPack.hlsl (SIGMA's inputs, Nrd.inl)
#include "NrdSigmaUnpack.h"   // g_NrdSigmaUnpack: shaders/NrdSigmaUnpack.hlsl (SIGMA's shadow back)
#include "NrdReblurPack.h"    // g_NrdReblurPack: shaders/NrdReblurPack.hlsl (REBLUR's inputs, Nrd.inl)
#include "NrdReblurUnpack.h"  // g_NrdReblurUnpack: shaders/NrdReblurUnpack.hlsl (REBLUR's ambient light back)
#include "NrdSpecPack.h"      // g_NrdSpecPack: shaders/NrdSpecPack.hlsl (REBLUR_SPECULAR's inputs)
#include "NrdSpecUnpack.h"    // g_NrdSpecUnpack: shaders/NrdSpecUnpack.hlsl (the reflections back)

using Microsoft::WRL::ComPtr;

#define NFX_EXPORT extern "C" UNITY_INTERFACE_EXPORT

namespace {

IUnityInterfaces* g_unity = nullptr;
IUnityGraphicsD3D12v7* g_unityD3D12 = nullptr;
bool g_pluginLoadCalled = false;
ComPtr<ID3D12Device5> g_device;
std::string g_deviceSource = "none";
// Set when the GPU stopped (device removed, or a wait timed out): the engine does nothing more this session
std::atomic<bool> g_dead{ false };
HRESULT g_removedReason = S_OK;

void WriteHangReport();  // the breadcrumbs into NFXNative-gpu-hang.txt (defined with them below)

bool CheckDevice() {
	HRESULT reason = g_device ? g_device->GetDeviceRemovedReason() : S_OK;
	if (FAILED(reason)) {
		g_removedReason = reason;
		g_dead = true;
		WriteHangReport();
	}
	return !g_dead;
}

// GPU breadcrumbs, to name a hang's culprit: every list the engine records writes its number into memory the CPU can
// still read after the device is lost, when the GPU starts it (slot 0) and when it is through (slot 1). A hung GPU
// leaves slot 0 ahead of slot 1: that list never finished. The tags are the lists' names by number.
struct Crumbs {
	ComPtr<ID3D12Resource> buffer;
	volatile UINT* cpu = nullptr;
	std::string tags[256];
	UINT next = 0;

	bool Create(ID3D12Device* device) {
		if (buffer || device == nullptr) {
			return buffer != nullptr;
		}
		D3D12_HEAP_PROPERTIES heap = {};
		heap.Type = D3D12_HEAP_TYPE_CUSTOM;
		heap.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
		heap.MemoryPoolPreference = D3D12_MEMORY_POOL_L0;
		D3D12_RESOURCE_DESC desc = {};
		desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
		desc.Width = 256;
		desc.Height = 1;
		desc.DepthOrArraySize = 1;
		desc.MipLevels = 1;
		desc.SampleDesc.Count = 1;
		desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
		void* mapped = nullptr;
		if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&buffer))) ||
		    FAILED(buffer->Map(0, nullptr, &mapped)) || mapped == nullptr) {
			buffer.Reset();
			return false;
		}
		cpu = static_cast<volatile UINT*>(mapped);
		cpu[0] = cpu[1] = 0;
		return true;
	}

	UINT Begin(ID3D12GraphicsCommandList2* list, const std::string& tag) {
		if (!buffer || list == nullptr) {
			return 0;
		}
		UINT id = ++next;
		tags[id & 255] = tag;
		D3D12_WRITEBUFFERIMMEDIATE_PARAMETER at = { buffer->GetGPUVirtualAddress(), id };
		D3D12_WRITEBUFFERIMMEDIATE_MODE mode = D3D12_WRITEBUFFERIMMEDIATE_MODE_MARKER_IN;
		list->WriteBufferImmediate(1, &at, &mode);
		return id;
	}

	void End(ID3D12GraphicsCommandList2* list, UINT id) {
		if (!buffer || list == nullptr || id == 0) {
			return;
		}
		D3D12_WRITEBUFFERIMMEDIATE_PARAMETER at = { buffer->GetGPUVirtualAddress() + 4, id };
		D3D12_WRITEBUFFERIMMEDIATE_MODE mode = D3D12_WRITEBUFFERIMMEDIATE_MODE_MARKER_OUT;
		list->WriteBufferImmediate(1, &at, &mode);
	}

	std::string Report(const char* name) const {
		if (!buffer || cpu == nullptr) {
			return std::string(name) + ": no breadcrumbs\n";
		}
		UINT begun = cpu[0], done = cpu[1];
		std::string r = std::string(name) + ": last started " + std::to_string(begun) + " (" + tags[begun & 255] + "), last finished " +
		                std::to_string(done) + " (" + tags[done & 255] + "), recorded " + std::to_string(next) + "\n";
		r += begun > done ? "  -> the GPU stopped inside " + tags[begun & 255] + "\n" : std::string("  -> every started list finished on this queue\n");
		r += "  the last lists recorded:";
		for (UINT i = next > 16 ? next - 15 : 1; i <= next; ++i) {
			r += " " + std::to_string(i) + "=" + tags[i & 255];
		}
		return r + "\n";
	}
};
Crumbs g_crumbs;        // Unity's queue (the render thread's lists)
Crumbs g_rtCrumbs;      // the DXR 1.2 device's trace queue (RtDevice.inl)
Crumbs g_gpuCrumbs;     // the engine's own queue: scene builds (g_gpu)
std::string g_crumbTag = "render";  // the next render thread list's name

// Once, when the device is found lost: where each queue stopped, next to NFXNative.dll (the game dies soon after,
// and Unity's own log only says the device was removed)
void WriteHangReport() {
	static std::atomic<bool> written{ false };
	if (written.exchange(true)) {
		return;
	}
	HMODULE self = nullptr;
	wchar_t path[MAX_PATH] = {};
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&WriteHangReport), &self);
	DWORD length = GetModuleFileNameW(self, path, MAX_PATH);
	std::wstring file(path, length);
	file = file.substr(0, file.find_last_of(L"\\/") + 1) + L"NFXNative-gpu-hang.txt";
	FILE* f = _wfopen(file.c_str(), L"w");
	if (f == nullptr) {
		return;
	}
	SYSTEMTIME t;
	GetLocalTime(&t);
	fprintf(f, "NFXNative: the GPU stopped (device removed, reason 0x%08x) at %04d-%02d-%02d %02d:%02d:%02d\n", static_cast<unsigned>(g_removedReason), t.wYear,
	        t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
	fputs(g_crumbs.Report("Unity's queue").c_str(), f);
	fputs(g_rtCrumbs.Report("DXR 1.2 trace queue").c_str(), f);
	fputs(g_gpuCrumbs.Report("the engine's own queue (scene builds)").c_str(), f);
	fclose(f);
}


// ------------------------------------------------------------------ helpers

int HighestShaderModel(ID3D12Device* device) {
	for (int sm = 0x69; sm >= 0x60; --sm) {
		D3D12_FEATURE_DATA_SHADER_MODEL data = { static_cast<D3D_SHADER_MODEL>(sm) };
		if (SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &data, sizeof(data)))) {
			return data.HighestShaderModel;
		}
	}
	return 0;
}

std::string AdapterName(ID3D12Device* device) {
	ComPtr<IDXGIFactory4> factory;
	if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
		return "?";
	}
	ComPtr<IDXGIAdapter1> adapter;
	if (FAILED(factory->EnumAdapterByLuid(device->GetAdapterLuid(), IID_PPV_ARGS(&adapter)))) {
		return "?";
	}
	DXGI_ADAPTER_DESC1 desc = {};
	adapter->GetDesc1(&desc);
	char name[128] = {};
	WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof(name) - 1, nullptr, nullptr);
	return name;
}

ComPtr<ID3D12Resource> CreateBuffer(UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state) {
	D3D12_HEAP_PROPERTIES props = {};
	props.Type = heap;
	D3D12_RESOURCE_DESC desc = {};
	desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	desc.Width = size < 256 ? 256 : size;
	desc.Height = 1;
	desc.DepthOrArraySize = 1;
	desc.MipLevels = 1;
	desc.SampleDesc.Count = 1;
	desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
	desc.Flags = flags;
	ComPtr<ID3D12Resource> buffer;
	if (FAILED(g_device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr, IID_PPV_ARGS(&buffer)))) {
		return nullptr;
	}
	return buffer;
}

D3D12_RESOURCE_BARRIER UavBarrier(ID3D12Resource* resource) {
	D3D12_RESOURCE_BARRIER b = {};
	b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
	b.UAV.pResource = resource;
	return b;
}

D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to) {
	D3D12_RESOURCE_BARRIER b = {};
	b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
	b.Transition.pResource = resource;
	b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
	b.Transition.StateBefore = from;
	b.Transition.StateAfter = to;
	return b;
}

double NowMs() {
	LARGE_INTEGER frequency, counter;
	QueryPerformanceFrequency(&frequency);
	QueryPerformanceCounter(&counter);
	return counter.QuadPart * 1000.0 / frequency.QuadPart;
}

// An own direct queue with one command list, used synchronously (record, submit, wait). Unity's queue is not
// reachable without its plugin interface; the scene builds need no ordering with Unity's frame.
struct Gpu {
	ComPtr<ID3D12CommandQueue> queue;
	ComPtr<ID3D12CommandAllocator> allocator;
	ComPtr<ID3D12GraphicsCommandList4> list;
	ComPtr<ID3D12Fence> fence;
	UINT64 fenceValue = 0;
	HANDLE event = nullptr;

	bool Create() {
		if (g_dead) {
			return false;
		}
		if (queue) {
			return true;
		}
		D3D12_COMMAND_QUEUE_DESC desc = {};
		desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
		if (FAILED(g_device->CreateCommandQueue(&desc, IID_PPV_ARGS(&queue))) ||
		    FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator))) ||
		    FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list))) ||
		    FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)))) {
			queue.Reset();
			return false;
		}
		list->Close();
		event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
		return true;
	}

	void Begin(const char* tag = "scene") {
		// a build that failed half way (no memory for a buffer) returned with the list open: closed first, the
		// allocator can be reset and the list starts empty (Reset drops what was recorded)
		list->Close();
		allocator->Reset();
		list->Reset(allocator.Get(), nullptr);
		g_gpuCrumbs.Create(g_device.Get());
		crumb = g_gpuCrumbs.Begin(list.Get(), tag);
	}
	UINT crumb = 0;

	bool SubmitAndWait(DWORD timeoutMs = 10000) {
		g_gpuCrumbs.End(list.Get(), crumb);
		crumb = 0;
		if (FAILED(list->Close())) {
			return false;
		}
		ID3D12CommandList* lists[] = { list.Get() };
		queue->ExecuteCommandLists(1, lists);
		queue->Signal(fence.Get(), ++fenceValue);
		fence->SetEventOnCompletion(fenceValue, event);
		if (WaitForSingleObject(event, timeoutMs) != WAIT_OBJECT_0) {
			g_dead = true;  // a hung GPU: leave it alone from now on
			WriteHangReport();
			return false;
		}
		return CheckDevice();
	}
};
Gpu g_gpu;

// A compute pipeline with a root signature of t0 (scene), u0 (output), optional root constants at b0 and an
// optional second buffer (root parameters in that order); extra: 0 none, 1 read only at t1, 2 read write at u1
struct ComputePipeline {
	ComPtr<ID3D12RootSignature> root;
	ComPtr<ID3D12PipelineState> pso;

	bool Create(const void* dxil, size_t size, UINT constants, int extra = 0, ID3D12Device* device = nullptr) {
		if (pso) {
			return true;
		}
		device = device != nullptr ? device : g_device.Get();
		D3D12_ROOT_PARAMETER params[4] = {};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		params[2].Constants.Num32BitValues = constants > 0 ? constants : 1;
		params[3].ParameterType = extra == 2 ? D3D12_ROOT_PARAMETER_TYPE_UAV : D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[3].Descriptor.ShaderRegister = 1;
		D3D12_ROOT_SIGNATURE_DESC rootDesc = {};
		rootDesc.NumParameters = extra != 0 ? 4 : constants > 0 ? 3 : 2;
		rootDesc.pParameters = params;
		ComPtr<ID3DBlob> blob, error;
		if (FAILED(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) ||
		    FAILED(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root)))) {
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC desc = {};
		desc.pRootSignature = root.Get();
		desc.CS = { dxil, size };
		return SUCCEEDED(device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&pso)));
	}
};
ComputePipeline g_rayQueryTest;
ComputePipeline g_sceneRay;
ComputePipeline g_sceneRays;
ComputePipeline g_sanitize;
ComputePipeline g_extractUvs;

// Shader reload without a restart: compiled shaders (<name>.cso) the build drops in kino/mods/NatureFX/NativeShaders
// override the embedded ones of the same name. NFX_Native_LoadShaders reads a folder on the main thread; render event
// 8 takes them in on the render thread (between traces) and makes the pipelines again.
std::map<std::string, std::vector<char>> g_shaderFiles, g_shaderFilesPending;
std::mutex g_shaderFilesMutex;
std::atomic<bool> g_shaderFilesReady{ false };
std::string g_shaderStatus = "embedded shaders";

D3D12_SHADER_BYTECODE ShaderCode(const char* name, const void* embedded, size_t size) {
	auto it = g_shaderFiles.find(name);
	if (it != g_shaderFiles.end() && !it->second.empty()) {
		return { it->second.data(), it->second.size() };
	}
	return { embedded, size };
}

// The trace pipelines (NFXCommon.hlsli): t0 scene, u0 result, b0 frame constants, u1 surface, t2 instance records,
// t3 ambient probe, t4 screen (colour and depth), a linear wrap sampler, and the descriptor heap indexed directly by
// the shaders (shader model 6.6)
enum TraceKind {
	kTraceSun = 0, kTraceAmbient = 1, kTraceLocal = 2, kTraceShafts = 3, kTraceSurface = 4, kTraceReflection = 5,
	kTraceGI = 6, kTraceCacheResolve = 7, kTraceGlass = 8,
	// the sky (Sky.hlsli): its tables, no rays
	kTraceSkyTransmittance = 9, kTraceSkyMultiScatter = 10, kTraceSkyView = 11, kTraceCloudNoise = 12, kTraceClouds = 13,
	kTraceCloudShadow = 14, kTraceSkyAmbient = 15, kTraceFog = 16, kTraceFogVolume = 17, kTraceSkyCube = 18, kTraceLampMask = 19, kTraceSunMap = 20, kTraceGroundMap = 21, kTraceKinds = 22
};
// GI's world space radiance cache (NFXCommon.hlsli: CacheEntries x CacheEntryBytes), u2 of every trace
constexpr UINT64 kCacheBytes = (1ull << 19) * 48;
ComPtr<ID3D12Resource> g_cache;
ComPtr<ID3D12RootSignature> g_traceRoot;
ComPtr<ID3D12PipelineState> g_tracePso[kTraceKinds];
// DXR 1.0: the traces as ray tracing pipelines (state objects) and their shader tables (ray generation, miss, hit
// group records, kTableStride apart); used while g_pipeline is set (NFX_Native_SetPipeline)
ComPtr<ID3D12StateObject> g_traceState[kTraceKinds];
ComPtr<ID3D12Resource> g_traceTables;
constexpr UINT kTableStride = 64;  // D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT
std::atomic<bool> g_pipeline{ false };

// The payload of NFXCommon.hlsli's NfxPayload: t, instance, primitive, barycentrics, object to world 3x4
constexpr UINT kPayloadBytes = 4 + 4 + 4 + 8 + 48;

bool CreateTraceStateObjects() {
	if (g_traceState[kTraceKinds - 1]) {
		return true;
	}
	const D3D12_SHADER_BYTECODE libraries[kTraceKinds] = {
		ShaderCode("SunShadowLib", g_SunShadowLib, sizeof(g_SunShadowLib)), ShaderCode("AmbientLib", g_AmbientLib, sizeof(g_AmbientLib)),
		ShaderCode("LocalShadowsLib", g_LocalShadowsLib, sizeof(g_LocalShadowsLib)), ShaderCode("ShaftsLib", g_ShaftsLib, sizeof(g_ShaftsLib)),
		ShaderCode("SurfaceLib", g_SurfaceLib, sizeof(g_SurfaceLib)), ShaderCode("ReflectionLib", g_ReflectionLib, sizeof(g_ReflectionLib)),
		ShaderCode("GILib", g_GILib, sizeof(g_GILib)), ShaderCode("GICacheResolveLib", g_GICacheResolveLib, sizeof(g_GICacheResolveLib)),
		ShaderCode("GlassLib", g_GlassLib, sizeof(g_GlassLib)),
		ShaderCode("SkyTransmittanceLib", g_SkyTransmittanceLib, sizeof(g_SkyTransmittanceLib)),
		ShaderCode("SkyMultiScatterLib", g_SkyMultiScatterLib, sizeof(g_SkyMultiScatterLib)), ShaderCode("SkyViewLib", g_SkyViewLib, sizeof(g_SkyViewLib)),
		ShaderCode("CloudNoiseLib", g_CloudNoiseLib, sizeof(g_CloudNoiseLib)), ShaderCode("CloudsLib", g_CloudsLib, sizeof(g_CloudsLib)),
		ShaderCode("CloudShadowLib", g_CloudShadowLib, sizeof(g_CloudShadowLib)), ShaderCode("SkyAmbientLib", g_SkyAmbientLib, sizeof(g_SkyAmbientLib)),
		ShaderCode("FogLib", g_FogLib, sizeof(g_FogLib)), ShaderCode("FogVolumeLib", g_FogVolumeLib, sizeof(g_FogVolumeLib)),
		ShaderCode("SkyCubeLib", g_SkyCubeLib, sizeof(g_SkyCubeLib)), ShaderCode("LampMaskLib", g_LampMaskLib, sizeof(g_LampMaskLib)),
		ShaderCode("SunMapLib", g_SunMapLib, sizeof(g_SunMapLib)), ShaderCode("GroundMapLib", g_GroundMapLib, sizeof(g_GroundMapLib))
	};
	// every kind: 3 records (ray generation, miss, hit group), one table row
	if (!g_traceTables) {
		g_traceTables = CreateBuffer(static_cast<UINT64>(kTableStride) * 3 * kTraceKinds, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE,
		                             D3D12_RESOURCE_STATE_GENERIC_READ);
		if (!g_traceTables) {
			return false;
		}
	}
	unsigned char* tables = nullptr;
	if (FAILED(g_traceTables->Map(0, nullptr, reinterpret_cast<void**>(&tables))) || tables == nullptr) {
		return false;
	}
	bool ok = true;
	for (int k = 0; k < kTraceKinds && ok; ++k) {
		if (g_traceState[k]) {
			continue;
		}
		D3D12_EXPORT_DESC exports[4] = { { L"NfxRayGen", nullptr, D3D12_EXPORT_FLAG_NONE }, { L"NfxAnyHit", nullptr, D3D12_EXPORT_FLAG_NONE },
		                                 { L"NfxClosestHit", nullptr, D3D12_EXPORT_FLAG_NONE }, { L"NfxMiss", nullptr, D3D12_EXPORT_FLAG_NONE } };
		D3D12_DXIL_LIBRARY_DESC library = { libraries[k], 4, exports };
		D3D12_HIT_GROUP_DESC hitGroup = { L"NfxHitGroup", D3D12_HIT_GROUP_TYPE_TRIANGLES, L"NfxAnyHit", L"NfxClosestHit", nullptr };
		D3D12_RAYTRACING_SHADER_CONFIG shaderConfig = { kPayloadBytes, 8 };
		D3D12_GLOBAL_ROOT_SIGNATURE globalRoot = { g_traceRoot.Get() };
		D3D12_RAYTRACING_PIPELINE_CONFIG pipelineConfig = { 1 };
		D3D12_STATE_SUBOBJECT subobjects[] = {
			{ D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &library },
			{ D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hitGroup },
			{ D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &shaderConfig },
			{ D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &globalRoot },
			{ D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pipelineConfig },
		};
		D3D12_STATE_OBJECT_DESC desc = { D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, _countof(subobjects), subobjects };
		ComPtr<ID3D12StateObjectProperties> props;
		if (FAILED(g_device->CreateStateObject(&desc, IID_PPV_ARGS(&g_traceState[k]))) || FAILED(g_traceState[k].As(&props))) {
			g_traceState[k].Reset();
			ok = false;
			break;
		}
		unsigned char* row = tables + static_cast<size_t>(k) * kTableStride * 3;
		memcpy(row, props->GetShaderIdentifier(L"NfxRayGen"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
		memcpy(row + kTableStride, props->GetShaderIdentifier(L"NfxMiss"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
		memcpy(row + kTableStride * 2, props->GetShaderIdentifier(L"NfxHitGroup"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
	}
	g_traceTables->Unmap(0, nullptr);
	return ok;
}

bool CreateTracePipelines() {
	if (g_tracePso[kTraceKinds - 1]) {
		return true;
	}
	if (!g_traceRoot) {
		D3D12_ROOT_PARAMETER1 params[8] = {};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
		params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[3].Descriptor.ShaderRegister = 1;
		params[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[4].Descriptor.ShaderRegister = 2;
		params[5].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[5].Descriptor.ShaderRegister = 3;
		params[6].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[6].Descriptor.ShaderRegister = 4;
		params[7].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[7].Descriptor.ShaderRegister = 2;
		for (auto& p : params) {
			p.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
		}
		D3D12_STATIC_SAMPLER_DESC sampler = {};
		sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
		sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
		sampler.MaxLOD = D3D12_FLOAT32_MAX;
		sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
		D3D12_VERSIONED_ROOT_SIGNATURE_DESC desc = {};
		desc.Version = D3D_ROOT_SIGNATURE_VERSION_1_1;
		desc.Desc_1_1.NumParameters = 8;
		desc.Desc_1_1.pParameters = params;
		desc.Desc_1_1.NumStaticSamplers = 1;
		desc.Desc_1_1.pStaticSamplers = &sampler;
		desc.Desc_1_1.Flags = D3D12_ROOT_SIGNATURE_FLAG_CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED;
		ComPtr<ID3DBlob> blob, error;
		if (FAILED(D3D12SerializeVersionedRootSignature(&desc, &blob, &error)) ||
		    FAILED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_traceRoot)))) {
			return false;
		}
	}
	const D3D12_SHADER_BYTECODE code[kTraceKinds] = {
		ShaderCode("SunShadow", g_SunShadow, sizeof(g_SunShadow)), ShaderCode("Ambient", g_Ambient, sizeof(g_Ambient)),
		ShaderCode("LocalShadows", g_LocalShadows, sizeof(g_LocalShadows)), ShaderCode("Shafts", g_Shafts, sizeof(g_Shafts)),
		ShaderCode("Surface", g_Surface, sizeof(g_Surface)), ShaderCode("Reflection", g_Reflection, sizeof(g_Reflection)),
		ShaderCode("GI", g_GI, sizeof(g_GI)), ShaderCode("GICacheResolve", g_GICacheResolve, sizeof(g_GICacheResolve)),
		ShaderCode("Glass", g_Glass, sizeof(g_Glass)),
		ShaderCode("SkyTransmittance", g_SkyTransmittance, sizeof(g_SkyTransmittance)),
		ShaderCode("SkyMultiScatter", g_SkyMultiScatter, sizeof(g_SkyMultiScatter)), ShaderCode("SkyView", g_SkyView, sizeof(g_SkyView)),
		ShaderCode("CloudNoise", g_CloudNoise, sizeof(g_CloudNoise)), ShaderCode("Clouds", g_Clouds, sizeof(g_Clouds)),
		ShaderCode("CloudShadow", g_CloudShadow, sizeof(g_CloudShadow)), ShaderCode("SkyAmbient", g_SkyAmbient, sizeof(g_SkyAmbient)),
		ShaderCode("Fog", g_Fog, sizeof(g_Fog)), ShaderCode("FogVolume", g_FogVolume, sizeof(g_FogVolume)),
		ShaderCode("SkyCube", g_SkyCube, sizeof(g_SkyCube)), ShaderCode("LampMask", g_LampMask, sizeof(g_LampMask)),
		ShaderCode("SunMap", g_SunMap, sizeof(g_SunMap)), ShaderCode("GroundMap", g_GroundMap, sizeof(g_GroundMap))
	};
	for (int k = 0; k < kTraceKinds; ++k) {
		if (g_tracePso[k]) {
			continue;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC desc = {};
		desc.pRootSignature = g_traceRoot.Get();
		desc.CS = code[k];
		if (FAILED(g_device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&g_tracePso[k])))) {
			return false;
		}
	}
	// the DXR 1.0 pipelines too (failing them only keeps the inline traces)
	CreateTraceStateObjects();
	return true;
}

// ------------------------------------------------------------------ buffer pools
// The geometries' BLAS and their own data (indices, positions, normals, UVs) live in a few large buffers (64 MB
// chunks) at 256 byte aligned offsets instead of one committed buffer each: a committed buffer takes at least 64 KB,
// and a small mesh's BLAS or UVs are a few KB (thousands of them on a map: hundreds of MB lost to the rounding).
// BLAS chunks stay in the acceleration structure state for good. Data chunks are written (unordered access) while a
// scene build fills them and read (non pixel shader resource) otherwise: the build moves the whole chunk between the
// two, after WaitRenderIdle when a chunk the frames read gets new data (BuildScene). Spans are freed only after
// WaitRenderIdle (with the retired geometries), so no frame in flight still reads what is reused; a chunk with nothing
// left in it is released.
struct PoolSpan {
	int chunk = -1;
	UINT64 offset = 0;
	UINT64 bytes = 0;  // taken (aligned)
	UINT64 size = 0;   // asked for
	ID3D12Resource* resource = nullptr;  // the chunk's buffer (held by the pool)
	D3D12_GPU_VIRTUAL_ADDRESS address = 0;
	explicit operator bool() const { return chunk >= 0; }
	D3D12_GPU_VIRTUAL_ADDRESS Address() const { return address; }
	ID3D12Resource* Resource() const { return resource; }
};

struct BufferPool {
	static constexpr UINT64 kChunkBytes = 64ull << 20;
	static constexpr UINT64 kAlign = 256;  // D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT; covers raw views (16)
	D3D12_RESOURCE_STATES createState;
	explicit BufferPool(D3D12_RESOURCE_STATES state) : createState(state) {}
	struct Chunk {
		ComPtr<ID3D12Resource> buffer;
		UINT64 size = 0, top = 0, used = 0;
		std::vector<std::pair<UINT64, UINT64>> free;  // (offset, bytes) below top, sorted, merged
		D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;  // what the GPU last left it in (data chunks)
	};
	std::vector<Chunk> chunks;

	static UINT64 Align(UINT64 v) { return (v + kAlign - 1) & ~(kAlign - 1); }

	PoolSpan Make(int c, UINT64 offset, UINT64 bytes, UINT64 size) {
		auto& ch = chunks[c];
		ch.used += bytes;
		PoolSpan span;
		span.chunk = c;
		span.offset = offset;
		span.bytes = bytes;
		span.size = size;
		span.resource = ch.buffer.Get();
		span.address = ch.buffer->GetGPUVirtualAddress() + offset;
		return span;
	}

	// onlyIn: when not COMMON, only chunks in that state are used (a data build that could not wait for the frames
	// in flight writes to new chunks only: they are created in it)
	PoolSpan Allocate(UINT64 size, D3D12_RESOURCE_STATES onlyIn = D3D12_RESOURCE_STATE_COMMON) {
		UINT64 bytes = Align(size == 0 ? kAlign : size);
		// a freed hole (first fit), then the end of a chunk
		for (size_t c = 0; c < chunks.size(); ++c) {
			auto& ch = chunks[c];
			if (!ch.buffer || (onlyIn != D3D12_RESOURCE_STATE_COMMON && ch.state != onlyIn)) {
				continue;
			}
			for (size_t k = 0; k < ch.free.size(); ++k) {
				if (ch.free[k].second >= bytes) {
					UINT64 offset = ch.free[k].first;
					ch.free[k].first += bytes;
					ch.free[k].second -= bytes;
					if (ch.free[k].second == 0) {
						ch.free.erase(ch.free.begin() + static_cast<std::ptrdiff_t>(k));
					}
					return Make(static_cast<int>(c), offset, bytes, size);
				}
			}
			if (ch.size - ch.top >= bytes) {
				UINT64 offset = ch.top;
				ch.top += bytes;
				return Make(static_cast<int>(c), offset, bytes, size);
			}
		}
		// a new chunk (one of its own for a buffer larger than a chunk; a smaller chunk when the budget is tight)
		UINT64 chunkSize = bytes > kChunkBytes ? bytes : kChunkBytes;
		ComPtr<ID3D12Resource> buffer = CreateBuffer(chunkSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, createState);
		if (!buffer && chunkSize > bytes) {
			chunkSize = bytes > (16ull << 20) ? bytes : (16ull << 20);
			buffer = CreateBuffer(chunkSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, createState);
		}
		if (!buffer) {
			return PoolSpan();
		}
		int slot = -1;
		for (size_t c = 0; c < chunks.size(); ++c) {
			if (!chunks[c].buffer) {
				slot = static_cast<int>(c);
				break;
			}
		}
		if (slot < 0) {
			chunks.emplace_back();
			slot = static_cast<int>(chunks.size() - 1);
		}
		auto& ch = chunks[slot];
		ch = Chunk();
		ch.buffer = buffer;
		ch.size = chunkSize;
		ch.top = bytes;
		ch.state = createState;
		return Make(slot, 0, bytes, size);
	}

	// only once nothing in flight can read it (after WaitRenderIdle)
	void Free(PoolSpan& span) {
		if (span.chunk < 0 || span.chunk >= static_cast<int>(chunks.size()) || !chunks[span.chunk].buffer) {
			span = PoolSpan();
			return;
		}
		auto& ch = chunks[span.chunk];
		ch.used = ch.used > span.bytes ? ch.used - span.bytes : 0;
		if (ch.used == 0) {
			ch = Chunk();  // empty: the whole chunk goes
			span = PoolSpan();
			return;
		}
		auto it = ch.free.begin();
		while (it != ch.free.end() && it->first < span.offset) {
			++it;
		}
		it = ch.free.insert(it, { span.offset, span.bytes });
		// merge with the next and the previous hole
		auto next = it + 1;
		if (next != ch.free.end() && it->first + it->second == next->first) {
			it->second += next->second;
			ch.free.erase(next);
		}
		if (it != ch.free.begin()) {
			auto prev = it - 1;
			if (prev->first + prev->second == it->first) {
				prev->second += it->second;
				ch.free.erase(it);
			}
		}
		// a hole at the end gives the space back to the top
		if (!ch.free.empty() && ch.free.back().first + ch.free.back().second == ch.top) {
			ch.top = ch.free.back().first;
			ch.free.pop_back();
		}
		span = PoolSpan();
	}

	void Reset() { chunks.clear(); }

	UINT64 Used() const {
		UINT64 n = 0;
		for (const auto& ch : chunks) {
			n += ch.used;
		}
		return n;
	}
	UINT64 Reserved() const {
		UINT64 n = 0;
		for (const auto& ch : chunks) {
			n += ch.buffer ? ch.size : 0;
		}
		return n;
	}
	int Chunks() const {
		int n = 0;
		for (const auto& ch : chunks) {
			n += ch.buffer ? 1 : 0;
		}
		return n;
	}
};
BufferPool g_blasPool(D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
BufferPool g_dataPool(D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

// Vertex data shared between geometries: Unity's sub meshes of one mesh read the same vertex buffer from the same
// place (base vertex 0 for most), and each registered the whole mesh's vertices again, one copy per sub mesh (the
// data pool held several times the meshes' size). One copy per (source address, stride, format, vertex count) now,
// counted by the geometries using it (g_spanRefs, every data span: a span goes back to the pool when its count is 0
// and its last geometry retires). An entry leaves g_sharedData as soon as its count is 0 (NFX_Native_ReleaseGeometry),
// so a new buffer Unity makes at the same address is never matched with old data.
struct SharedKey {
	D3D12_GPU_VIRTUAL_ADDRESS source;
	UINT stride;
	int format;
	UINT count;
	bool operator<(const SharedKey& o) const {
		if (source != o.source) return source < o.source;
		if (stride != o.stride) return stride < o.stride;
		if (format != o.format) return format < o.format;
		return count < o.count;
	}
};
std::map<SharedKey, PoolSpan> g_sharedData;
std::map<std::pair<int, UINT64>, int> g_spanRefs;  // (chunk, offset) of a data span: the geometries using it

void RefSpan(const PoolSpan& span) {
	if (span) {
		++g_spanRefs[{ span.chunk, span.offset }];
	}
}

// One geometry less uses it; at 0 nobody can be given it any more (its share entry goes; the space is freed with the
// geometry's retirement, FreeRetiredSpan)
void UnrefSpan(const PoolSpan& span) {
	if (!span) {
		return;
	}
	auto it = g_spanRefs.find({ span.chunk, span.offset });
	if (it == g_spanRefs.end() || --it->second > 0) {
		return;
	}
	for (auto s = g_sharedData.begin(); s != g_sharedData.end();) {
		s = s->second.chunk == span.chunk && s->second.offset == span.offset ? g_sharedData.erase(s) : std::next(s);
	}
}

// A retired geometry's span: back to the pool if no geometry uses it any more (once: the count's entry goes with it)
void FreeRetiredSpan(PoolSpan& span) {
	if (!span) {
		return;
	}
	auto it = g_spanRefs.find({ span.chunk, span.offset });
	if (it != g_spanRefs.end() && it->second <= 0) {
		g_spanRefs.erase(it);
		g_dataPool.Free(span);
	}
	span = PoolSpan();
}

// A geometry's own data: a span of g_dataPool, or a buffer of its own (NFX_Native_AddGeometryData's upload buffers)
struct DataSpan {
	ComPtr<ID3D12Resource> owned;
	PoolSpan pooled;
	explicit operator bool() const { return owned || pooled; }
	D3D12_GPU_VIRTUAL_ADDRESS Address() const { return owned ? owned->GetGPUVirtualAddress() : pooled.Address(); }
	ID3D12Resource* Resource() const { return owned ? owned.Get() : pooled.Resource(); }
	UINT64 Offset() const { return owned ? 0 : pooled.offset; }
	UINT64 Size() const { return owned ? owned->GetDesc().Width : pooled.size; }
	static DataSpan Own(const ComPtr<ID3D12Resource>& buffer) {
		DataSpan d;
		d.owned = buffer;
		return d;
	}
	// (a pooled span goes back with g_dataPool.Free; this only forgets it)
	void Reset() {
		owned.Reset();
		pooled = PoolSpan();
	}
};

// ------------------------------------------------------------------ scene

// One Unity mesh / sub mesh: its triangles in the mesh's space, built once into a BLAS
struct Geometry {
	D3D12_RAYTRACING_GEOMETRY_DESC desc = {};
	ComPtr<ID3D12Resource> vertexSource;  // Unity's buffers, held until the BLAS is built
	ComPtr<ID3D12Resource> indexSource;
	PoolSpan blas;  // in g_blasPool
	UINT64 triangles = 0;
	// read from Unity's index buffer: its indices are copied into ownIndices first, bad triangles made degenerate
	bool sanitize = false;
	D3D12_GPU_VIRTUAL_ADDRESS sourceIndices = 0;
	bool sourceIs16 = false;
	DataSpan ownIndices;
	bool released = false;  // NFX_Native_ReleaseGeometry: its mesh is gone
	// what the trace shaders read at a hit (own buffers, bindless descriptors): indices, positions (the hit's
	// normal) and the first UV set (its colour, the alpha test)
	D3D12_GPU_VIRTUAL_ADDRESS sourcePositions = 0;  // in Unity's vertex buffer, extracted at build
	UINT positionStride = 0;
	int positionFormat = 0;                   // 3: three 32 bit floats, 4: four 16 bit floats
	DataSpan ownPositions;                    // float3 per vertex
	D3D12_GPU_VIRTUAL_ADDRESS sourceNormals = 0;  // in Unity's vertex buffer, extracted at build
	UINT normalStride = 0;
	int normalFormat = 0;                     // 3: three 32 bit floats, 4: four 16 bit floats
	ComPtr<ID3D12Resource> normalSource;      // Unity's vertex buffer, held until the normals are extracted
	DataSpan ownNormals;                      // float3 per vertex
	D3D12_GPU_VIRTUAL_ADDRESS sourceUvs = 0;  // in Unity's vertex buffer (base vertex applied), extracted at build
	UINT uvStride = 0;
	int uvFormat = 0;                         // 1: two 32 bit floats, 2: two 16 bit floats
	ComPtr<ID3D12Resource> uvSource;          // Unity's vertex buffer, held until the UVs are extracted
	DataSpan ownUvs;                          // float2 per vertex
	UINT indexDescriptor = ~0u;
	UINT positionDescriptor = ~0u;
	UINT normalDescriptor = ~0u;
	UINT uvDescriptor = ~0u;
};

// A material: its colour texture (colour, alpha) and how it is mapped, base colour, metalness, smoothness
struct Material {
	ComPtr<ID3D12Resource> texture;
	UINT descriptor = ~0u;
	float cutoff = 0.5f;
	float alphaScale = 1.0f;
	float st[4] = { 1.0f, 1.0f, 0.0f, 0.0f };
	float color[3] = { 0.5f, 0.5f, 0.5f };
	float metallic = 0.0f;
	float smoothness = 0.5f;
	ComPtr<ID3D12Resource> mask;     // HDRP mask map (r metallic, a smoothness), remapped
	UINT maskDescriptor = ~0u;
	float remap[4] = { 0.0f, 1.0f, 0.0f, 1.0f };
	UINT flags = 0;                  // 1 clear coat, 2 thin card
	ComPtr<ID3D12Resource> emissionMap;
	UINT emissionDescriptor = ~0u;
	float emission[3] = { 0.0f, 0.0f, 0.0f };  // linear, intensity included (HDRP's _EmissiveColor)
};
std::vector<Material> g_materials;
std::vector<Geometry> g_geometries;
// Released between two scene builds: kept alive until the next BuildScene, since g_resolved and g_records still
// point at their BLAS, own buffers and textures and the render thread builds its TLAS from them every frame
// (freed at once, a frame traced a BLAS that was gone: device removed on a map change)
std::vector<Geometry> g_retiredGeometries;
std::vector<Material> g_retiredMaterials;
std::vector<D3D12_RAYTRACING_INSTANCE_DESC> g_instances;
// The instances with their BLAS addresses, as the render thread rebuilds its TLAS from them every frame; moving
// ones get new transforms (NFX_Native_UpdateTransforms). Guarded by g_sceneMutex, as are the geometries.
std::vector<D3D12_RAYTRACING_INSTANCE_DESC> g_resolved;
std::mutex g_sceneMutex;
bool WaitRenderIdle();  // until the GPU finished the render thread's work (defined with it below); false: it did not within the time
// the DXR 1.2 device's copy of the scene (RtDevice.inl)
int MirrorScene();
void ClearRtScene();
void ReleaseRtGeometry(int id);
extern std::vector<D3D12_RAYTRACING_INSTANCE_DESC> g_rtResolved;
ComPtr<ID3D12Resource> g_instanceBuffer;
UINT64 g_instanceCapacity = 0;
ComPtr<ID3D12Resource> g_tlas;
UINT64 g_tlasCapacity = 0;
ComPtr<ID3D12Resource> g_scratch;
UINT64 g_scratchCapacity = 0;
// BLAS compaction (BuildScene): each new BLAS reports its compacted size here (8 bytes each), read back, and is copied
// into a span of that size; the build's own span goes back to the pool. Typically 40 to 60 % smaller.
constexpr D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS kBlasFlags =
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION;
ComPtr<ID3D12Resource> g_compactSizes;     // default heap, unordered access
UINT64 g_compactSizesCapacity = 0;
ComPtr<ID3D12Resource> g_compactReadback;  // readback heap
UINT64 g_compactReadbackCapacity = 0;
std::string g_sceneStats = "no scene";
std::string g_sceneStatsWind;
std::vector<int> g_instanceGeometry;  // per entry of g_instances
std::vector<bool> g_instanceMoving;   // per entry of g_instances: flag bit 3 (a car's part)
std::vector<int> g_instanceMaterial;
// Per instance (InstanceID) 64 bytes the shadow shader reads for the alpha test: index and UV descriptors, texture
// descriptor (~0: solid), cutoff, alpha scale, texture scale / offset
ComPtr<ID3D12Resource> g_records;
UINT64 g_recordCapacity = 0;

// The engine's shader visible descriptor heap, indexed directly by the shadow shader
ComPtr<ID3D12DescriptorHeap> g_heap;
UINT g_heapNext = 0;
// slots given back (the retired geometries' and materials' views, after WaitRenderIdle: no record points at them any
// more), handed out before new ones. Without it the heap only grew and filled after some map changes.
std::vector<UINT> g_heapFree;
UINT g_heapStep = 0;
UINT g_descriptorsRefused = 0;  // allocations past the end of the heap (reported in the scene stats)
constexpr UINT kHeapSize = 1 << 16;

UINT AllocateDescriptor(D3D12_CPU_DESCRIPTOR_HANDLE* handle) {
	if (!g_heap) {
		D3D12_DESCRIPTOR_HEAP_DESC desc = {};
		desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		desc.NumDescriptors = kHeapSize;
		desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
		if (FAILED(g_device->CreateDescriptorHeap(&desc, IID_PPV_ARGS(&g_heap)))) {
			return ~0u;
		}
		g_heapStep = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
	}
	UINT index;
	if (!g_heapFree.empty()) {
		index = g_heapFree.back();
		g_heapFree.pop_back();
	} else if (g_heapNext >= kHeapSize) {
		// full even with what was given back: materials go without textures (opaque alpha tests, grey hits)
		++g_descriptorsRefused;
		return ~0u;
	} else {
		index = g_heapNext++;
	}
	*handle = g_heap->GetCPUDescriptorHandleForHeapStart();
	handle->ptr += static_cast<SIZE_T>(index) * g_heapStep;
	return index;
}

// Gives a slot back (only once nothing in flight or in the records points at it)
void FreeDescriptor(UINT& index) {
	if (index != ~0u && index < g_heapNext) {
		g_heapFree.push_back(index);
	}
	index = ~0u;
}

// A raw (byte address) buffer view of a whole buffer
UINT RawBufferDescriptor(ID3D12Resource* buffer) {
	D3D12_CPU_DESCRIPTOR_HANDLE handle;
	UINT index = AllocateDescriptor(&handle);
	if (index == ~0u) {
		return index;
	}
	D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
	desc.Format = DXGI_FORMAT_R32_TYPELESS;
	desc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
	desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
	desc.Buffer.NumElements = static_cast<UINT>(buffer->GetDesc().Width / 4);
	desc.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
	g_device->CreateShaderResourceView(buffer, &desc, handle);
	return index;
}

// A raw view of a geometry's own data: its span of a pool chunk (FirstElement in 32 bit words: the span's offset is
// 256 byte aligned, past the 16 a raw view needs), or its own buffer
UINT RawBufferDescriptor(const DataSpan& data) {
	if (data.owned) {
		return RawBufferDescriptor(data.owned.Get());
	}
	D3D12_CPU_DESCRIPTOR_HANDLE handle;
	UINT index = AllocateDescriptor(&handle);
	if (index == ~0u) {
		return index;
	}
	D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
	desc.Format = DXGI_FORMAT_R32_TYPELESS;
	desc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
	desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
	desc.Buffer.FirstElement = data.pooled.offset / 4;
	desc.Buffer.NumElements = static_cast<UINT>((data.pooled.size + 3) / 4);
	desc.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
	g_device->CreateShaderResourceView(data.Resource(), &desc, handle);
	return index;
}

// A view of one of Unity's 2D textures (typeless formats read as sRGB or linear as the texture is meant); ~0 when
// it is not a plain 2D texture
UINT TextureDescriptor(ID3D12Resource* texture, bool srgb) {
	D3D12_RESOURCE_DESC rd = texture->GetDesc();
	if (rd.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || rd.DepthOrArraySize != 1 || rd.SampleDesc.Count != 1) {
		return ~0u;
	}
	DXGI_FORMAT format = rd.Format;
	switch (format) {
		case DXGI_FORMAT_R8G8B8A8_TYPELESS: format = srgb ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB : DXGI_FORMAT_R8G8B8A8_UNORM; break;
		case DXGI_FORMAT_B8G8R8A8_TYPELESS: format = srgb ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM; break;
		case DXGI_FORMAT_BC1_TYPELESS: format = srgb ? DXGI_FORMAT_BC1_UNORM_SRGB : DXGI_FORMAT_BC1_UNORM; break;
		case DXGI_FORMAT_BC2_TYPELESS: format = srgb ? DXGI_FORMAT_BC2_UNORM_SRGB : DXGI_FORMAT_BC2_UNORM; break;
		case DXGI_FORMAT_BC3_TYPELESS: format = srgb ? DXGI_FORMAT_BC3_UNORM_SRGB : DXGI_FORMAT_BC3_UNORM; break;
		case DXGI_FORMAT_BC7_TYPELESS: format = srgb ? DXGI_FORMAT_BC7_UNORM_SRGB : DXGI_FORMAT_BC7_UNORM; break;
		case DXGI_FORMAT_R16G16B16A16_TYPELESS: format = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
		case DXGI_FORMAT_BC4_TYPELESS: format = DXGI_FORMAT_BC4_UNORM; break;
		case DXGI_FORMAT_BC5_TYPELESS: format = DXGI_FORMAT_BC5_UNORM; break;
		case DXGI_FORMAT_BC6H_TYPELESS: format = DXGI_FORMAT_BC6H_UF16; break;
		case DXGI_FORMAT_R8_TYPELESS: format = DXGI_FORMAT_R8_UNORM; break;
		case DXGI_FORMAT_R8G8_TYPELESS: format = DXGI_FORMAT_R8G8_UNORM; break;
		case DXGI_FORMAT_R16_TYPELESS: format = DXGI_FORMAT_R16_UNORM; break;
		case DXGI_FORMAT_R16G16_TYPELESS: format = DXGI_FORMAT_R16G16_FLOAT; break;
		case DXGI_FORMAT_R32_TYPELESS: format = DXGI_FORMAT_R32_FLOAT; break;
		case DXGI_FORMAT_R32G32_TYPELESS: format = DXGI_FORMAT_R32G32_FLOAT; break;
		case DXGI_FORMAT_R32G32B32A32_TYPELESS: format = DXGI_FORMAT_R32G32B32A32_FLOAT; break;
		case DXGI_FORMAT_R10G10B10A2_TYPELESS: format = DXGI_FORMAT_R10G10B10A2_UNORM; break;
		case DXGI_FORMAT_B8G8R8X8_TYPELESS: format = srgb ? DXGI_FORMAT_B8G8R8X8_UNORM_SRGB : DXGI_FORMAT_B8G8R8X8_UNORM; break;
		// any other typeless format (depth, odd layouts) has no view this code knows: "no texture" rather than a
		// view D3D12 rejects without saying so
		case DXGI_FORMAT_R32G32B32_TYPELESS: case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
		case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_R24_UNORM_X8_TYPELESS: case DXGI_FORMAT_X24_TYPELESS_G8_UINT:
		case DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
			return ~0u;
		default: break;
	}
	D3D12_CPU_DESCRIPTOR_HANDLE handle;
	UINT index = AllocateDescriptor(&handle);
	if (index == ~0u) {
		return index;
	}
	D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
	desc.Format = format;
	desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
	desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
	desc.Texture2D.MipLevels = static_cast<UINT>(-1);
	g_device->CreateShaderResourceView(texture, &desc, handle);
	return index;
}

bool EnsureBuffer(ComPtr<ID3D12Resource>& buffer, UINT64& capacity, UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state) {
	if (buffer && capacity >= size) {
		return true;
	}
	capacity = size + size / 4;
	buffer = CreateBuffer(capacity, heap, flags, state);
	return buffer != nullptr;
}

}  // namespace

// ------------------------------------------------------------------ Unity plugin interface

// Called by Unity when its plugin loader loads the DLL (DllImport through Unity); not when the DLL is loaded some
// other way, which NFX_Native_Info reports.
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unity) {
	g_pluginLoadCalled = true;
	g_unity = unity;
	g_unityD3D12 = unity->Get<IUnityGraphicsD3D12v7>();
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
	g_unityD3D12 = nullptr;
	g_unity = nullptr;
}

// ------------------------------------------------------------------ device

// Finds Unity's D3D12 device: from Unity's plugin interface when it was handed over, else from any D3D12
// resource of Unity's (a texture's native pointer). Returns 1 when a DXR capable device (ID3D12Device5) was found.
NFX_EXPORT int NFX_Native_Init(void* unityResource) {
	if (g_device) {
		return 1;
	}
	ComPtr<ID3D12Device> device;
	if (g_unityD3D12 != nullptr && g_unityD3D12->GetDevice() != nullptr) {
		device = g_unityD3D12->GetDevice();
		g_deviceSource = "Unity plugin interface";
	} else if (unityResource != nullptr) {
		auto* resource = static_cast<ID3D12Resource*>(unityResource);
		if (FAILED(resource->GetDevice(IID_PPV_ARGS(&device)))) {
			return 0;
		}
		g_deviceSource = "texture native pointer";
	}
	if (!device || FAILED(device.As(&g_device))) {
		return 0;
	}
	return 1;
}

// Status strings the render thread writes (DLSS, NRD, shader reload) and the main thread reads through the
// NFX_Native_*Status exports: one lock for both, since a std::string being reassigned while it is copied out read
// freed memory (the DLSS status changed every frame)
std::mutex g_statusMutex;

static void SetStatus(std::string& status, const std::string& value) {
	std::lock_guard<std::mutex> lock(g_statusMutex);
	if (status != value) {
		status = value;
	}
}

static int CopyOut(const std::string& s, char* buffer, int size) {
	std::lock_guard<std::mutex> lock(g_statusMutex);
	int n = static_cast<int>(s.size()) < size - 1 ? static_cast<int>(s.size()) : size - 1;
	if (n > 0) {
		memcpy(buffer, s.data(), n);
	}
	if (size > 0) {
		buffer[n < 0 ? 0 : n] = 0;
	}
	return n;
}

// Text report of the device (UTF-8, zero terminated). Returns the length written.
NFX_EXPORT int NFX_Native_Info(char* buffer, int size) {
	std::string s;
	s += "plugin interface: ";
	s += g_pluginLoadCalled ? (g_unityD3D12 != nullptr ? "yes (D3D12v7)" : "yes (no D3D12v7)") : "no";
	s += ", device from: " + g_deviceSource;
	// features the C# side checks before relying on them (an older DLL lacks them)
	s += ", features: own-glass lamp-mask dlss dlss-rr sun-map ground-map ground-cover";
	if (g_device) {
		D3D12_FEATURE_DATA_D3D12_OPTIONS options = {};
		g_device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &options, sizeof(options));
		D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
		g_device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5));
		int sm = HighestShaderModel(g_device.Get());
		char line[256];
		snprintf(line, sizeof(line), ", adapter: %s, ray tracing tier: %d.%d, shader model: %d.%d, resource binding tier: %d",
		         AdapterName(g_device.Get()).c_str(), options5.RaytracingTier / 10, options5.RaytracingTier % 10,
		         sm >> 4, sm & 15, static_cast<int>(options.ResourceBindingTier));
		s += line;
	}
	if (g_dead) {
		char line[96];
		snprintf(line, sizeof(line), ", GPU stopped (device removed reason 0x%08lX)", static_cast<unsigned long>(g_removedReason));
		s += line;
	}
	return CopyOut(s, buffer, size);
}

// Whether Unity handed over its plugin interface (1) or not (0)
NFX_EXPORT int NFX_Native_PluginInterface() {
	return g_pluginLoadCalled && g_unityD3D12 != nullptr ? 1 : 0;
}

// Traces one inline ray (DXR 1.1) down onto a test triangle with an own BLAS / TLAS, compute pipeline and command
// queue, and waits for it. Returns 2 hit (works), 1 miss, or a negative stage that failed.
NFX_EXPORT int NFX_Native_RayQueryTest() {
	if (!g_device) {
		return -1;
	}
	D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
	g_device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5));
	if (options5.RaytracingTier < D3D12_RAYTRACING_TIER_1_1) {
		return -2;
	}
	if (!g_gpu.Create()) {
		return -3;
	}

	// a 20 m triangle at y = 0, centred under the ray
	const float vertices[] = { -10.0f, 0.0f, -10.0f, 10.0f, 0.0f, -10.0f, 0.0f, 0.0f, 10.0f };
	auto vb = CreateBuffer(sizeof(vertices), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	if (!vb) {
		return -4;
	}
	void* mapped = nullptr;
	vb->Map(0, nullptr, &mapped);
	memcpy(mapped, vertices, sizeof(vertices));
	vb->Unmap(0, nullptr);

	D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
	geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
	geometry.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
	geometry.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
	geometry.Triangles.VertexCount = 3;
	geometry.Triangles.VertexBuffer.StartAddress = vb->GetGPUVirtualAddress();
	geometry.Triangles.VertexBuffer.StrideInBytes = 12;

	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS blasInputs = {};
	blasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
	blasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	blasInputs.NumDescs = 1;
	blasInputs.pGeometryDescs = &geometry;
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO blasInfo = {};
	g_device->GetRaytracingAccelerationStructurePrebuildInfo(&blasInputs, &blasInfo);

	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tlasInputs = {};
	tlasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	tlasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	tlasInputs.NumDescs = 1;
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tlasInfo = {};
	g_device->GetRaytracingAccelerationStructurePrebuildInfo(&tlasInputs, &tlasInfo);

	UINT64 scratchSize = blasInfo.ScratchDataSizeInBytes > tlasInfo.ScratchDataSizeInBytes ? blasInfo.ScratchDataSizeInBytes : tlasInfo.ScratchDataSizeInBytes;
	auto blas = CreateBuffer(blasInfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
	auto tlas = CreateBuffer(tlasInfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
	auto scratch = CreateBuffer(scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	auto scratch2 = CreateBuffer(scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	auto instances = CreateBuffer(sizeof(D3D12_RAYTRACING_INSTANCE_DESC), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	auto output = CreateBuffer(4, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	auto readback = CreateBuffer(4, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
	if (!blas || !tlas || !scratch || !scratch2 || !instances || !output || !readback) {
		return -5;
	}

	D3D12_RAYTRACING_INSTANCE_DESC instance = {};
	instance.Transform[0][0] = instance.Transform[1][1] = instance.Transform[2][2] = 1.0f;
	instance.InstanceMask = 0xFF;
	instance.AccelerationStructure = blas->GetGPUVirtualAddress();
	instances->Map(0, nullptr, &mapped);
	memcpy(mapped, &instance, sizeof(instance));
	instances->Unmap(0, nullptr);
	tlasInputs.InstanceDescs = instances->GetGPUVirtualAddress();

	if (!g_rayQueryTest.Create(g_RayQueryTest, sizeof(g_RayQueryTest), 0)) {
		return -7;
	}

	g_gpu.Begin();
	auto* list = g_gpu.list.Get();
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
	build.Inputs = blasInputs;
	build.DestAccelerationStructureData = blas->GetGPUVirtualAddress();
	build.ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress();
	list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
	auto barrier = UavBarrier(blas.Get());
	list->ResourceBarrier(1, &barrier);
	build.Inputs = tlasInputs;
	build.DestAccelerationStructureData = tlas->GetGPUVirtualAddress();
	build.ScratchAccelerationStructureData = scratch2->GetGPUVirtualAddress();
	list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
	barrier = UavBarrier(tlas.Get());
	list->ResourceBarrier(1, &barrier);

	list->SetComputeRootSignature(g_rayQueryTest.root.Get());
	list->SetPipelineState(g_rayQueryTest.pso.Get());
	list->SetComputeRootShaderResourceView(0, tlas->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	list->Dispatch(1, 1, 1);
	auto toCopy = Transition(output.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
	list->ResourceBarrier(1, &toCopy);
	list->CopyBufferRegion(readback.Get(), 0, output.Get(), 0, 4);
	if (!g_gpu.SubmitAndWait(5000)) {
		return -9;
	}

	UINT32 result = 0;
	D3D12_RANGE range = { 0, 4 };
	if (FAILED(readback->Map(0, &range, &mapped)) || mapped == nullptr) {
		return -10;
	}
	memcpy(&result, mapped, 4);
	D3D12_RANGE none = { 0, 0 };
	readback->Unmap(0, &none);
	return static_cast<int>(result);
}

#include "Wind.inl"

// ------------------------------------------------------------------ scene API

// Drops every geometry and instance (a new map)
NFX_EXPORT void NFX_Native_ClearScene() {
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	WaitRenderIdle();  // frames in flight still trace the old BLAS
	g_retiredGeometries.clear();
	g_retiredMaterials.clear();
	g_geometries.clear();
	g_blasPool.Reset();
	g_dataPool.Reset();
	g_sharedData.clear();
	g_spanRefs.clear();
	// the build time TLAS points at BLAS just freed: rebuilt with the next scene (the render thread's own TLAS is made
	// from g_resolved every frame, empty now: nothing is traced until then)
	g_tlas.Reset();
	g_tlasCapacity = 0;
	g_instances.clear();
	g_resolved.clear();
	g_instanceGeometry.clear();
	g_instanceMaterial.clear();
	g_materials.clear();
	g_windBlas.clear();
	g_instanceWind.clear();
	g_instanceSways.clear();
	g_heapNext = 0;
	g_heapFree.clear();
	g_sceneStats = "no scene";
	ClearRtScene();
}

// Registers one mesh / sub mesh from Unity's own GPU buffers (Mesh.GetNativeVertexBufferPtr / IndexBufferPtr).
// vertexAddressOffset: bytes to the first vertex's position (base vertex * stride + attribute offset).
// positionFormat: DXGI format of the position (R32G32B32_FLOAT 6, R16G16B16A16_FLOAT 10). indexFormat: 16 or 32.
// Returns the geometry id for NFX_Native_SetInstances, or -1.
// uvBuffer / uvAddressOffset / uvStride / uvFormat (1: float2, 2: half2): the first UV set, for alpha tested
// geometry (opaque 0); none when uvFormat is 0.
NFX_EXPORT int NFX_Native_AddGeometry(void* vertexBuffer, UINT64 vertexAddressOffset, UINT vertexStride, UINT vertexCount, int positionFormat,
                                      void* indexBuffer, UINT64 indexAddressOffset, UINT indexCount, int indexFormat, int opaque,
                                      void* uvBuffer, UINT64 uvAddressOffset, UINT uvStride, int uvFormat,
                                      void* normalBuffer, UINT64 normalAddressOffset, UINT normalStride, int normalFormat) {
	if (!g_device || g_dead || vertexBuffer == nullptr || indexBuffer == nullptr || indexCount < 3 || vertexCount == 0) {
		return -1;
	}
	Geometry g;
	g.vertexSource = static_cast<ID3D12Resource*>(vertexBuffer);
	g.indexSource = static_cast<ID3D12Resource*>(indexBuffer);
	// what is read must lie inside Unity's buffers (a sub allocated or dynamic buffer would read past them and
	// stop the GPU)
	D3D12_RESOURCE_DESC vd = g.vertexSource->GetDesc();
	D3D12_RESOURCE_DESC id = g.indexSource->GetDesc();
	UINT64 positionSize = positionFormat == DXGI_FORMAT_R32G32B32_FLOAT ? 12 : 8;
	if (vd.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER || id.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER ||
	    vertexAddressOffset + static_cast<UINT64>(vertexCount - 1) * vertexStride + positionSize > vd.Width ||
	    indexAddressOffset + static_cast<UINT64>(indexCount) * (indexFormat == 16 ? 2 : 4) > id.Width) {
		return -2;
	}
	g.desc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
	g.desc.Flags = opaque ? D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE : D3D12_RAYTRACING_GEOMETRY_FLAG_NONE;
	auto& t = g.desc.Triangles;
	t.VertexFormat = static_cast<DXGI_FORMAT>(positionFormat);
	t.VertexCount = vertexCount;
	t.VertexBuffer.StartAddress = g.vertexSource->GetGPUVirtualAddress() + vertexAddressOffset;
	t.VertexBuffer.StrideInBytes = vertexStride;
	t.IndexFormat = DXGI_FORMAT_R32_UINT;  // the sanitized copy (NFX_Native_BuildScene)
	t.IndexCount = indexCount - indexCount % 3;
	g.sanitize = true;
	g.sourceIndices = g.indexSource->GetGPUVirtualAddress() + indexAddressOffset;
	g.sourceIs16 = indexFormat == 16;
	g.triangles = t.IndexCount / 3;
	g.sourcePositions = t.VertexBuffer.StartAddress;
	g.positionStride = vertexStride;
	g.positionFormat = positionFormat == DXGI_FORMAT_R32G32B32_FLOAT ? 3 : 4;
	// normalFormat: 3 three floats, 4 four halves; 0 none (the geometric normal is used)
	if (normalBuffer != nullptr && normalFormat >= 3) {
		g.normalSource = static_cast<ID3D12Resource*>(normalBuffer);
		D3D12_RESOURCE_DESC nd = g.normalSource->GetDesc();
		if (nd.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER &&
		    normalAddressOffset + static_cast<UINT64>(vertexCount - 1) * normalStride + (normalFormat == 3 ? 12 : 8) <= nd.Width) {
			g.sourceNormals = g.normalSource->GetGPUVirtualAddress() + normalAddressOffset;
			g.normalStride = normalStride;
			g.normalFormat = normalFormat;
		} else {
			g.normalSource.Reset();
		}
	}
	if (uvBuffer != nullptr && uvFormat > 0) {
		g.uvSource = static_cast<ID3D12Resource*>(uvBuffer);
		D3D12_RESOURCE_DESC ud = g.uvSource->GetDesc();
		if (ud.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER &&
		    uvAddressOffset + static_cast<UINT64>(vertexCount - 1) * uvStride + (uvFormat == 1 ? 8 : 4) <= ud.Width) {
			g.sourceUvs = g.uvSource->GetGPUVirtualAddress() + uvAddressOffset;
			g.uvStride = uvStride;
			g.uvFormat = uvFormat;
		} else {
			g.uvSource.Reset();
		}
	}
	g_geometries.push_back(g);
	return static_cast<int>(g_geometries.size() - 1);
}

// Registers one mesh / sub mesh from a copy of its data (meshes the CPU can read: deformable car bodies, baked
// skinned meshes, whose GPU buffers Unity keeps in forms the engine cannot read safely). positions: xyz floats,
// indices: 32 bit, already relative to the first position. Returns the geometry id, or -1.
// uvs: xy floats per vertex, for alpha tested geometry (opaque 0); may be null.
// normals: xyz floats per vertex; may be null.
NFX_EXPORT int NFX_Native_AddGeometryData(const float* positions, int vertexCount, const int* indices, int indexCount, int opaque, const float* uvs,
                                          const float* normals) {
	if (!g_device || g_dead || positions == nullptr || indices == nullptr || vertexCount <= 0 || indexCount < 3) {
		return -1;
	}
	for (int i = 0; i < indexCount; ++i) {
		if (indices[i] < 0 || indices[i] >= vertexCount) {
			return -2;
		}
	}
	UINT64 vertexBytes = static_cast<UINT64>(vertexCount) * 12;
	UINT64 indexBytes = static_cast<UINT64>(indexCount) * 4;
	Geometry g;
	g.vertexSource = CreateBuffer(vertexBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	g.indexSource = CreateBuffer(indexBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	void* mapped = nullptr;
	if (!g.vertexSource || !g.indexSource || FAILED(g.vertexSource->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return -3;
	}
	memcpy(mapped, positions, vertexBytes);
	g.vertexSource->Unmap(0, nullptr);
	mapped = nullptr;
	if (FAILED(g.indexSource->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return -3;
	}
	memcpy(mapped, indices, indexBytes);
	g.indexSource->Unmap(0, nullptr);
	g.desc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
	g.desc.Flags = opaque ? D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE : D3D12_RAYTRACING_GEOMETRY_FLAG_NONE;
	auto& t = g.desc.Triangles;
	t.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
	t.VertexCount = static_cast<UINT>(vertexCount);
	t.VertexBuffer.StartAddress = g.vertexSource->GetGPUVirtualAddress();
	t.VertexBuffer.StrideInBytes = 12;
	t.IndexFormat = DXGI_FORMAT_R32_UINT;
	t.IndexCount = static_cast<UINT>(indexCount - indexCount % 3);
	t.IndexBuffer = g.indexSource->GetGPUVirtualAddress();
	g.triangles = t.IndexCount / 3;
	g.ownIndices = DataSpan::Own(g.indexSource);
	g.ownPositions = DataSpan::Own(g.vertexSource);
	if (normals != nullptr) {
		ComPtr<ID3D12Resource> buffer = CreateBuffer(vertexBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		mapped = nullptr;
		if (buffer && SUCCEEDED(buffer->Map(0, nullptr, &mapped)) && mapped != nullptr) {
			memcpy(mapped, normals, vertexBytes);
			buffer->Unmap(0, nullptr);
			g.ownNormals = DataSpan::Own(buffer);
		}
	}
	if (uvs != nullptr) {
		UINT64 uvBytes = static_cast<UINT64>(vertexCount) * 8;
		ComPtr<ID3D12Resource> buffer = CreateBuffer(uvBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		mapped = nullptr;
		if (buffer && SUCCEEDED(buffer->Map(0, nullptr, &mapped)) && mapped != nullptr) {
			memcpy(mapped, uvs, uvBytes);
			buffer->Unmap(0, nullptr);
			g.ownUvs = DataSpan::Own(buffer);
		}
	}
	g_geometries.push_back(g);
	return static_cast<int>(g_geometries.size() - 1);
}

// Replaces the instances: transforms are 3x4 row major object to world matrices (12 floats each), geometry ids
// from NFX_Native_AddGeometry, flags bit 0 = alpha tested (non opaque), bit 1 = glass: instance mask 2, seen only by
// the glass trace (Glass.hlsl); everything else is mask 1, what the other traces see (NFX_SCENE_MASK). Bit 2: swayed by
// the wind (NatureFX/Foliage, Wind.inl). Bit 3: moves (a car's part): its record says so (NFX_RECORD_MOVING).
NFX_EXPORT void NFX_Native_SetInstances(const float* transforms, const int* geometries, const int* flags, const int* materials, int count) {
	g_instances.clear();
	g_instances.reserve(count);
	g_instanceGeometry.clear();
	g_instanceMaterial.clear();
	g_instanceSways.clear();
	g_instanceMoving.clear();
	for (int i = 0; i < count; ++i) {
		int id = geometries[i];
		if (id < 0 || id >= static_cast<int>(g_geometries.size()) || g_geometries[id].released) {
			continue;
		}
		D3D12_RAYTRACING_INSTANCE_DESC d = {};
		memcpy(d.Transform, transforms + i * 12, sizeof(d.Transform));
		d.InstanceID = static_cast<UINT>(i);
		// 2 glass (its own trace only); else the scene, and 16 the ground, 32 the road among it (GroundMap.hlsl)
		d.InstanceMask = (flags[i] & 2) ? 0x02 : static_cast<UINT>(0x01 | ((flags[i] & 16) ? 0x04 : 0) | ((flags[i] & 32) ? 0x08 : 0) | ((flags[i] & 5) ? 0x10 : 0));
		d.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE | ((flags[i] & 1) ? D3D12_RAYTRACING_INSTANCE_FLAG_NONE : D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE);
		// geometry index for now; the BLAS address is filled in at build time
		d.AccelerationStructure = static_cast<UINT64>(id);
		g_instances.push_back(d);
		g_instanceGeometry.push_back(id);
		g_instanceMaterial.push_back(materials[i]);
		g_instanceSways.push_back((flags[i] & 4) != 0);
		g_instanceMoving.push_back((flags[i] & 8) != 0);
	}
}

// A material: Unity colour texture (Texture.GetNativeTexturePtr; may be null), whether it holds sRGB colour, alpha
// cutoff, alpha scale (base colour alpha), texture scale and offset, base colour (linear), metalness, smoothness.
// Returns its id, or -1.
NFX_EXPORT int NFX_Native_AddMaterial(void* texture, int srgb, float cutoff, float alphaScale, float scaleX, float scaleY, float offsetX, float offsetY,
                                      float r, float g, float b, float metallic, float smoothness,
                                      void* mask, float metallicMin, float metallicMax, float smoothnessMin, float smoothnessMax, int flags,
                                      void* emissionMap, int emissionSrgb, float er, float eg, float eb) {
	if (!g_device || g_dead) {
		return -1;
	}
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	Material m;
	if (texture != nullptr) {
		m.texture = static_cast<ID3D12Resource*>(texture);
		m.descriptor = TextureDescriptor(m.texture.Get(), srgb != 0);
		if (m.descriptor == ~0u) {
			m.texture.Reset();
		}
	}
	m.color[0] = r;
	m.color[1] = g;
	m.color[2] = b;
	m.metallic = metallic;
	m.smoothness = smoothness;
	if (mask != nullptr) {
		m.mask = static_cast<ID3D12Resource*>(mask);
		m.maskDescriptor = TextureDescriptor(m.mask.Get(), false);
		if (m.maskDescriptor == ~0u) {
			m.mask.Reset();
		}
	}
	m.remap[0] = metallicMin;
	m.remap[1] = metallicMax;
	m.remap[2] = smoothnessMin;
	m.remap[3] = smoothnessMax;
	m.flags = static_cast<UINT>(flags);
	if (emissionMap != nullptr && (er > 0.0f || eg > 0.0f || eb > 0.0f)) {
		m.emissionMap = static_cast<ID3D12Resource*>(emissionMap);
		m.emissionDescriptor = TextureDescriptor(m.emissionMap.Get(), emissionSrgb != 0);
		if (m.emissionDescriptor == ~0u) {
			m.emissionMap.Reset();
		}
	}
	m.emission[0] = er;
	m.emission[1] = eg;
	m.emission[2] = eb;
	m.cutoff = cutoff;
	m.alphaScale = alphaScale;
	m.st[0] = scaleX;
	m.st[1] = scaleY;
	m.st[2] = offsetX;
	m.st[3] = offsetY;
	g_materials.push_back(m);
	return static_cast<int>(g_materials.size() - 1);
}

// Its mesh is gone (a new map): the BLAS and buffers are freed with the next scene build, once nothing points at
// them any more (g_retiredGeometries)
NFX_EXPORT void NFX_Native_ReleaseGeometry(int id) {
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	if (id < 0 || id >= static_cast<int>(g_geometries.size())) {
		return;
	}
	for (const DataSpan* d : { &g_geometries[id].ownIndices, &g_geometries[id].ownPositions, &g_geometries[id].ownNormals, &g_geometries[id].ownUvs }) {
		UnrefSpan(d->pooled);
	}
	g_retiredGeometries.push_back(std::move(g_geometries[id]));
	Geometry released;
	released.released = true;
	g_geometries[id] = released;
	ReleaseRtGeometry(id);
}

// Its material or texture is gone: its textures are held until the next scene build (g_retiredMaterials)
NFX_EXPORT void NFX_Native_ReleaseMaterial(int id) {
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	if (id < 0 || id >= static_cast<int>(g_materials.size())) {
		return;
	}
	g_retiredMaterials.push_back(std::move(g_materials[id]));
	g_materials[id] = Material();
}

// Builds the BLAS of every new geometry and the TLAS over the instances on the engine's queue, and waits.
// Returns 1, or a negative stage that failed.
NFX_EXPORT int NFX_Native_BuildScene() {
	if (!g_device) {
		return -1;
	}
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	if (!g_gpu.Create()) {
		return -2;
	}
	// indices read from Unity's buffers are copied and checked into the engine's own buffers first
	if (!g_sanitize.Create(g_SanitizeIndices, sizeof(g_SanitizeIndices), 4, 1) ||
	    !g_extractUvs.Create(g_ExtractUvs, sizeof(g_ExtractUvs), 4, 1)) {
		return -8;
	}
	// Scratch for every build of this list, allocated before recording: growing it between builds released the
	// buffer the builds recorded before still used once the list ran (GPU hang, device removed)
	std::vector<D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO> prebuild(g_geometries.size());
	UINT64 scratchSize = 0;
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		auto& g = g_geometries[i];
		if (g.released || g.blas) {
			continue;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		inputs.Flags = kBlasFlags;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = 1;
		inputs.pGeometryDescs = &g.desc;
		g_device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuild[i]);
		scratchSize = prebuild[i].ScratchDataSizeInBytes > scratchSize ? prebuild[i].ScratchDataSizeInBytes : scratchSize;
	}
	{
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
		inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = static_cast<UINT>(g_instances.size());
		D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
		g_device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
		scratchSize = info.ScratchDataSizeInBytes > scratchSize ? info.ScratchDataSizeInBytes : scratchSize;
	}
	if (!EnsureBuffer(g_scratch, g_scratchCapacity, scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)) {
		return -3;
	}

	double start = NowMs();
	g_gpu.Begin();
	auto* list = g_gpu.list.Get();

	// The own data goes into g_dataPool's chunks. A chunk the frames read gets new data only after they are done
	// (it is unordered access while written); if they do not finish in time, only new chunks are written.
	bool newData = false;
	for (const auto& g : g_geometries) {
		if (!g.blas && !g.released && ((g.sanitize && !g.ownIndices) || (!g.ownPositions && g.sourcePositions != 0) ||
		                               (!g.ownNormals && g.sourceNormals != 0) || (!g.ownUvs && g.sourceUvs != 0) ||
		                               g.ownIndices.owned || g.ownPositions.owned || g.ownNormals.owned || g.ownUvs.owned)) {
			newData = true;
			break;
		}
	}
	bool reuseChunks = !newData || WaitRenderIdle();
	std::vector<int> written;  // the data chunks written in this list (unordered access until the barrier below)
	auto allocateData = [&](UINT64 size) -> DataSpan {
		DataSpan d;
		d.pooled = g_dataPool.Allocate(size, reuseChunks ? D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		if (!d.pooled) {
			return d;
		}
		RefSpan(d.pooled);
		int c = d.pooled.chunk;
		if (std::find(written.begin(), written.end(), c) == written.end()) {
			written.push_back(c);
			auto& ch = g_dataPool.chunks[c];
			if (ch.state != D3D12_RESOURCE_STATE_UNORDERED_ACCESS) {
				D3D12_RESOURCE_BARRIER b = Transition(ch.buffer.Get(), ch.state, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
				list->ResourceBarrier(1, &b);
			}
		}
		return d;
	};
	for (auto& g : g_geometries) {
		if (g.blas || g.released || !g.sanitize || g.ownIndices) {
			continue;
		}
		UINT triangles = static_cast<UINT>(g.triangles);
		g.ownIndices = allocateData(static_cast<UINT64>(triangles) * 12);
		if (!g.ownIndices) {
			return -9;
		}
		D3D12_GPU_VIRTUAL_ADDRESS aligned = g.sourceIndices & ~static_cast<D3D12_GPU_VIRTUAL_ADDRESS>(3);
		UINT constants[4] = { triangles, static_cast<UINT>(g.sourceIndices - aligned), g.sourceIs16 ? 1u : 0u, g.desc.Triangles.VertexCount };
		list->SetComputeRootSignature(g_sanitize.root.Get());
		list->SetPipelineState(g_sanitize.pso.Get());
		list->SetComputeRootShaderResourceView(0, aligned);
		list->SetComputeRootUnorderedAccessView(1, g.ownIndices.Address());
		list->SetComputeRoot32BitConstants(2, 4, constants, 0);
		list->SetComputeRootShaderResourceView(3, aligned);
		list->Dispatch((triangles + 63) / 64, 1, 1);
		g.desc.Triangles.IndexBuffer = g.ownIndices.Address();
		g.desc.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
		g.desc.Triangles.IndexCount = triangles * 3;
	}
	// positions and UVs out of Unity's vertex buffers, for the trace shaders' hit shading
	auto extract = [&](D3D12_GPU_VIRTUAL_ADDRESS source, UINT vertices, UINT stride, int format, DataSpan& target) -> bool {
		UINT64 size = static_cast<UINT64>(vertices) * (format >= 3 ? 12 : 8);
		target = allocateData(size);
		if (!target) {
			return false;
		}
		D3D12_GPU_VIRTUAL_ADDRESS aligned = source & ~static_cast<D3D12_GPU_VIRTUAL_ADDRESS>(3);
		UINT constants[4] = { vertices, static_cast<UINT>(source - aligned), stride, static_cast<UINT>(format) };
		list->SetComputeRootSignature(g_extractUvs.root.Get());
		list->SetPipelineState(g_extractUvs.pso.Get());
		list->SetComputeRootShaderResourceView(0, aligned);
		list->SetComputeRootUnorderedAccessView(1, target.Address());
		list->SetComputeRoot32BitConstants(2, 4, constants, 0);
		list->SetComputeRootShaderResourceView(3, aligned);
		list->Dispatch((vertices + 63) / 64, 1, 1);
		return true;
	};
	// the same vertex stream another geometry already has (a sub mesh of the same mesh): shared, not copied again
	auto extractShared = [&](D3D12_GPU_VIRTUAL_ADDRESS source, UINT vertices, UINT stride, int format, DataSpan& target) -> bool {
		SharedKey key{ source, stride, format, vertices };
		auto found = g_sharedData.find(key);
		if (found != g_sharedData.end()) {
			target = DataSpan();
			target.pooled = found->second;
			RefSpan(target.pooled);
			return true;
		}
		if (!extract(source, vertices, stride, format, target)) {
			return false;
		}
		g_sharedData[key] = target.pooled;
		return true;
	};
	for (auto& g : g_geometries) {
		if (g.blas || g.released) {
			continue;
		}
		UINT vertices = g.desc.Triangles.VertexCount;
		if (!g.ownPositions && g.sourcePositions != 0 && !extractShared(g.sourcePositions, vertices, g.positionStride, g.positionFormat, g.ownPositions)) {
			return -10;
		}
		if (!g.ownNormals && g.sourceNormals != 0 && !extractShared(g.sourceNormals, vertices, g.normalStride, g.normalFormat, g.ownNormals)) {
			return -10;
		}
		if (!g.ownUvs && g.sourceUvs != 0 && !extractShared(g.sourceUvs, vertices, g.uvStride, g.uvFormat, g.ownUvs)) {
			return -10;
		}
	}
	// NFX_Native_AddGeometryData's meshes (the CPU's copy, in upload buffers the GPU read over PCIe at every hit): into
	// the pool too, as plain copies (ExtractUvs' formats 3 and 1 copy whole words); the BLAS below is built from the
	// pool's copy. The upload buffers live until the list ran (uploadsHeld).
	std::vector<ComPtr<ID3D12Resource>> uploadsHeld;
	auto toPool = [&](DataSpan& data, UINT count, UINT stride, int format) -> bool {
		if (!data.owned) {
			return true;
		}
		ComPtr<ID3D12Resource> upload = data.owned;
		uploadsHeld.push_back(upload);
		DataSpan pooled;
		if (!extract(upload->GetGPUVirtualAddress(), count, stride, format, pooled)) {
			return true;  // no room in the pool: it stays in its upload buffer (data still holds it), as before
		}
		data = pooled;
		return true;
	};
	for (auto& g : g_geometries) {
		if (g.blas || g.released) {
			continue;
		}
		UINT vertices = g.desc.Triangles.VertexCount;
		bool indicesOwned = static_cast<bool>(g.ownIndices.owned), positionsOwned = static_cast<bool>(g.ownPositions.owned);
		// indices: three 32 bit words a triangle, copied as one "vertex" of format 3 each
		toPool(g.ownIndices, static_cast<UINT>(g.triangles), 12, 3);
		toPool(g.ownPositions, vertices, 12, 3);
		toPool(g.ownNormals, vertices, 12, 3);
		toPool(g.ownUvs, vertices, 8, 1);
		if (indicesOwned && g.ownIndices.pooled) {
			g.desc.Triangles.IndexBuffer = g.ownIndices.Address();
		}
		if (positionsOwned && g.ownPositions.pooled) {
			g.desc.Triangles.VertexBuffer.StartAddress = g.ownPositions.Address();
		}
	}

	// the chunks written, whole, to be read (the BLAS builds below, the traces)
	if (!written.empty()) {
		std::vector<D3D12_RESOURCE_BARRIER> toRead;
		for (int c : written) {
			toRead.push_back(Transition(g_dataPool.chunks[c].buffer.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
		}
		list->ResourceBarrier(static_cast<UINT>(toRead.size()), toRead.data());
	}

	// BLAS: each new geometry, one after the other through a shared scratch buffer; each reports its compacted size
	int newBlas = 0;
	for (size_t gi = 0; gi < g_geometries.size(); ++gi) {
		if (!g_geometries[gi].released && !g_geometries[gi].blas && prebuild[gi].ResultDataMaxSizeInBytes > 0) {
			++newBlas;
		}
	}
	bool compact = newBlas > 0 &&
	               EnsureBuffer(g_compactSizes, g_compactSizesCapacity, static_cast<UINT64>(newBlas) * 8, D3D12_HEAP_TYPE_DEFAULT,
	                            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS) &&
	               EnsureBuffer(g_compactReadback, g_compactReadbackCapacity, static_cast<UINT64>(newBlas) * 8, D3D12_HEAP_TYPE_READBACK,
	                            D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
	std::vector<size_t> compactIds;  // the geometries whose new BLAS report their size, in that order
	int built = 0;
	UINT64 blasBytes = 0;
	for (size_t gi = 0; gi < g_geometries.size(); ++gi) {
		auto& g = g_geometries[gi];
		if (g.released) {
			continue;
		}
		if (g.blas) {
			blasBytes += g.blas.bytes;
			continue;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		inputs.Flags = kBlasFlags;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = 1;
		inputs.pGeometryDescs = &g.desc;
		const auto& info = prebuild[gi];
		if (info.ResultDataMaxSizeInBytes == 0) {
			continue;
		}
		g.blas = g_blasPool.Allocate(info.ResultDataMaxSizeInBytes);
		if (!g.blas) {
			return -4;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs = inputs;
		build.DestAccelerationStructureData = g.blas.Address();
		build.ScratchAccelerationStructureData = g_scratch->GetGPUVirtualAddress();
		if (compact) {
			D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC post = {};
			post.InfoType = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE;
			post.DestBuffer = g_compactSizes->GetGPUVirtualAddress() + compactIds.size() * 8;
			list->BuildRaytracingAccelerationStructure(&build, 1, &post);
			compactIds.push_back(gi);
		} else {
			list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		}
		// (the whole chunk: the next build reuses the scratch, and a later one may sit next to this BLAS)
		D3D12_RESOURCE_BARRIER b[2] = { UavBarrier(g.blas.Resource()), UavBarrier(g_scratch.Get()) };
		list->ResourceBarrier(2, b);
		blasBytes += info.ResultDataMaxSizeInBytes;
		++built;
	}

	// Compaction: run what is recorded (the data, the builds, their sizes copied to the readback buffer), then copy
	// each new BLAS into a span of its compacted size. The TLAS below takes the compacted ones; the build's spans were
	// never in a TLAS the frames trace and go back to the pool once the copies ran.
	std::vector<PoolSpan> uncompacted;
	if (compact && !compactIds.empty()) {
		UINT64 bytes = compactIds.size() * 8;
		D3D12_RESOURCE_BARRIER toCopy[2] = { UavBarrier(g_compactSizes.Get()),
		                                     Transition(g_compactSizes.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE) };
		list->ResourceBarrier(2, toCopy);
		list->CopyBufferRegion(g_compactReadback.Get(), 0, g_compactSizes.Get(), 0, bytes);
		D3D12_RESOURCE_BARRIER back = Transition(g_compactSizes.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		list->ResourceBarrier(1, &back);
		if (!g_gpu.SubmitAndWait()) {
			return -12;
		}
		// the list ran: the data chunks it wrote are readable now
		for (int c : written) {
			g_dataPool.chunks[c].state = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
		}
		written.clear();
		g_gpu.Begin("scene compact");
		list = g_gpu.list.Get();
		void* mapped = nullptr;
		D3D12_RANGE readRange = { 0, static_cast<SIZE_T>(bytes) };
		if (SUCCEEDED(g_compactReadback->Map(0, &readRange, &mapped)) && mapped != nullptr) {
			const UINT64* sizes = static_cast<const UINT64*>(mapped);
			bool copied = false;
			for (size_t k = 0; k < compactIds.size(); ++k) {
				auto& g = g_geometries[compactIds[k]];
				UINT64 size = sizes[k];
				// only when it saves something (and the size makes sense)
				if (size == 0 || BufferPool::Align(size) >= g.blas.bytes) {
					continue;
				}
				PoolSpan compacted = g_blasPool.Allocate(size);
				if (!compacted) {
					continue;  // no room: the BLAS stays as built
				}
				list->CopyRaytracingAccelerationStructure(compacted.Address(), g.blas.Address(),
				                                          D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE_COMPACT);
				uncompacted.push_back(g.blas);
				g.blas = compacted;  // (not "small": windows.h defines it as char)
				copied = true;
			}
			D3D12_RANGE none = { 0, 0 };
			g_compactReadback->Unmap(0, &none);
			if (copied) {
				D3D12_RESOURCE_BARRIER all = UavBarrier(nullptr);  // every copy done before the TLAS build reads them
				list->ResourceBarrier(1, &all);
			}
		}
		blasBytes = 0;
		for (const auto& g : g_geometries) {
			blasBytes += !g.released && g.blas ? g.blas.bytes : 0;
		}
	}

	// TLAS over the instances, with each instance's BLAS address
	UINT64 triangles = 0;
	std::vector<D3D12_RAYTRACING_INSTANCE_DESC> instances = g_instances;
	for (auto& d : instances) {
		const auto& g = g_geometries[static_cast<size_t>(d.AccelerationStructure)];
		d.AccelerationStructure = g.blas ? g.blas.Address() : 0;
		triangles += g.triangles;
	}
	UINT64 instanceBytes = instances.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
	if (!EnsureBuffer(g_instanceBuffer, g_instanceCapacity, instanceBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ)) {
		return -5;
	}
	if (!instances.empty()) {
		void* mapped = nullptr;
		if (FAILED(g_instanceBuffer->Map(0, nullptr, &mapped)) || mapped == nullptr) {
			return -5;
		}
		memcpy(mapped, instances.data(), instanceBytes);
		g_instanceBuffer->Unmap(0, nullptr);
	}
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tlasInputs = {};
	tlasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	tlasInputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
	tlasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	tlasInputs.NumDescs = static_cast<UINT>(instances.size());
	tlasInputs.InstanceDescs = g_instanceBuffer->GetGPUVirtualAddress();
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tlasInfo = {};
	g_device->GetRaytracingAccelerationStructurePrebuildInfo(&tlasInputs, &tlasInfo);
	// the scratch is large enough already (above); the TLAS is only used by this queue, which waits for its work
	if (tlasInfo.ScratchDataSizeInBytes > g_scratchCapacity ||
	    !EnsureBuffer(g_tlas, g_tlasCapacity, tlasInfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)) {
		return -6;
	}
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
	build.Inputs = tlasInputs;
	build.DestAccelerationStructureData = g_tlas->GetGPUVirtualAddress();
	build.ScratchAccelerationStructureData = g_scratch->GetGPUVirtualAddress();
	list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
	auto barrier = UavBarrier(g_tlas.Get());
	list->ResourceBarrier(1, &barrier);
	if (!g_gpu.SubmitAndWait()) {
		return -7;
	}
	// the list ran: the data chunks it wrote are readable now (until then their state stays what the GPU has)
	for (int c : written) {
		g_dataPool.chunks[c].state = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
	}
	// the BLAS as built, before compaction: copied, and in no TLAS any frame traces
	for (auto& span : uncompacted) {
		g_blasPool.Free(span);
	}
	// built: Unity's buffers are no longer needed; the engine's own copies stay for the trace shaders
	int alphaInstances = 0;
	for (auto& g : g_geometries) {
		g.vertexSource.Reset();
		g.indexSource.Reset();
		g.uvSource.Reset();
		g.normalSource.Reset();
		if (g.released) {
			continue;
		}
		if (g.indexDescriptor == ~0u && g.ownIndices) {
			g.indexDescriptor = RawBufferDescriptor(g.ownIndices);
		}
		if (g.positionDescriptor == ~0u && g.ownPositions) {
			g.positionDescriptor = RawBufferDescriptor(g.ownPositions);
		}
		if (g.uvDescriptor == ~0u && g.ownUvs) {
			g.uvDescriptor = RawBufferDescriptor(g.ownUvs);
		}
		if (g.normalDescriptor == ~0u && g.ownNormals) {
			g.normalDescriptor = RawBufferDescriptor(g.ownNormals);
		}
	}

	// instance records for the alpha test (the render thread's frames in flight read the old ones: wait for them)
	UINT records = 1;
	for (const auto& d : g_instances) {
		records = d.InstanceID + 1 > records ? d.InstanceID + 1 : records;
	}
	// 128 bytes (32 words) per instance, laid out as Record in NFXCommon.hlsli
	constexpr size_t kRecordWords = 32;
	std::vector<UINT32> data(static_cast<size_t>(records) * kRecordWords, 0);
	const Material plain;
	for (size_t i = 0; i < data.size(); i += kRecordWords) {
		data[i] = data[i + 1] = data[i + 2] = data[i + 5] = data[i + 6] = data[i + 16] = data[i + 22] = ~0u;
	}
	for (size_t i = 0; i < g_instances.size(); ++i) {
		const auto& g = g_geometries[g_instanceGeometry[i]];
		int material = g_instanceMaterial[i];
		const auto& m = material >= 0 && material < static_cast<int>(g_materials.size()) ? g_materials[material] : plain;
		UINT32* r = &data[static_cast<size_t>(g_instances[i].InstanceID) * kRecordWords];
		r[0] = g.indexDescriptor;
		r[1] = g.uvDescriptor;
		r[2] = m.descriptor;
		memcpy(&r[3], &m.cutoff, 4);
		memcpy(&r[4], &m.alphaScale, 4);
		r[5] = g.positionDescriptor;
		memcpy(&r[7], &m.metallic, 4);
		memcpy(&r[8], m.st, 16);
		memcpy(&r[12], m.color, 12);
		memcpy(&r[15], &m.smoothness, 4);
		r[6] = g.normalDescriptor;
		r[16] = m.maskDescriptor;
		// thin: an alpha tested instance (leaf, grass, fence card)
		r[17] = m.flags | ((g_instances[i].Flags & D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE) == 0 ? 2u : 0u) |
		        (i < g_instanceMoving.size() && g_instanceMoving[i] ? 4u : 0u);
		memcpy(&r[18], m.remap, 16);
		r[22] = m.emissionDescriptor;
		memcpy(&r[24], m.emission, 12);
		if ((g_instances[i].Flags & D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE) == 0 && m.descriptor != ~0u) {
			++alphaInstances;
		}
	}
	WaitRenderIdle();
	// nothing traces the old scene now (the render thread waits for g_sceneMutex): what was released since the
	// last build goes, its BLAS space back to the pool
	// (the records written below were made from the live geometries and materials only)
	for (auto& g : g_retiredGeometries) {
		g_blasPool.Free(g.blas);
		FreeRetiredSpan(g.ownIndices.pooled);
		FreeRetiredSpan(g.ownPositions.pooled);
		FreeRetiredSpan(g.ownNormals.pooled);
		FreeRetiredSpan(g.ownUvs.pooled);
		FreeDescriptor(g.indexDescriptor);
		FreeDescriptor(g.positionDescriptor);
		FreeDescriptor(g.uvDescriptor);
		FreeDescriptor(g.normalDescriptor);
	}
	for (auto& m : g_retiredMaterials) {
		FreeDescriptor(m.descriptor);
		FreeDescriptor(m.maskDescriptor);
		FreeDescriptor(m.emissionDescriptor);
	}
	g_retiredGeometries.clear();
	g_retiredMaterials.clear();
	if (!EnsureBuffer(g_records, g_recordCapacity, data.size() * 4, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ)) {
		return -11;
	}
	void* recordMap = nullptr;
	if (FAILED(g_records->Map(0, nullptr, &recordMap)) || recordMap == nullptr) {
		return -11;
	}
	memcpy(recordMap, data.data(), data.size() * 4);
	g_records->Unmap(0, nullptr);
	// swaying foliage: its own updatable BLAS (Wind.inl)
	if (BuildWindBlas()) {
		for (size_t i = 0; i < instances.size() && i < g_instanceWind.size(); ++i) {
			if (g_instanceWind[i] >= 0) {
				instances[i].AccelerationStructure = g_windBlas[g_instanceWind[i]].blas->GetGPUVirtualAddress();
			}
		}
	}
	g_resolved = instances;
	char windStats[64];
	snprintf(windStats, sizeof(windStats), ", %zu swaying with the wind", g_windBlas.size());
	g_sceneStatsWind = windStats;

	char stats[256];
	snprintf(stats, sizeof(stats), "%zu geometries (%d new), %zu instances (%d alpha tested), %.1f M triangles, BLAS %.1f MB, TLAS %.1f MB, build %.1f ms",
	         g_geometries.size(), built, instances.size(), alphaInstances, triangles / 1e6, blasBytes / (1024.0 * 1024.0),
	         tlasInfo.ResultDataMaxSizeInBytes / (1024.0 * 1024.0), NowMs() - start);
	g_sceneStats = stats;
	snprintf(stats, sizeof(stats), ", BLAS pool %.1f of %.1f MB in %d chunks, data pool %.1f of %.1f MB in %d chunks",
	         g_blasPool.Used() / (1024.0 * 1024.0), g_blasPool.Reserved() / (1024.0 * 1024.0), g_blasPool.Chunks(),
	         g_dataPool.Used() / (1024.0 * 1024.0), g_dataPool.Reserved() / (1024.0 * 1024.0), g_dataPool.Chunks());
	g_sceneStats += stats;
	if (g_descriptorsRefused > 0) {
		snprintf(stats, sizeof(stats), ", DESCRIPTOR HEAP FULL (%u refused, %u of %u used)", g_descriptorsRefused, g_heapNext - static_cast<UINT>(g_heapFree.size()), kHeapSize);
		g_sceneStats += stats;
	}
	// the render thread's work uses these pipelines: create them here, on the main thread
	g_sceneRay.Create(g_SceneRay, sizeof(g_SceneRay), 8);
	CreateTracePipelines();
	// the DXR 1.2 device takes the new part of the scene over (if it runs)
	MirrorScene();
	return 1;
}

NFX_EXPORT int NFX_Native_SceneStats(char* buffer, int size) {
	return CopyOut(g_sceneStats + g_sceneStatsWind, buffer, size);
}

// One ray against the built scene (absolute world space). Returns the hit distance, -1 for no hit, or < -1 when
// the ray could not be traced.
NFX_EXPORT float NFX_Native_TraceTestRay(float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance) {
	if (!g_device || !g_tlas || !g_gpu.Create()) {
		return -2.0f;
	}
	if (!g_sceneRay.Create(g_SceneRay, sizeof(g_SceneRay), 8)) {
		return -3.0f;
	}
	static ComPtr<ID3D12Resource> output, readback;
	if (!output) {
		output = CreateBuffer(4, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		readback = CreateBuffer(4, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
		if (!output || !readback) {
			return -4.0f;
		}
	}
	float constants[8] = { ox, oy, oz, maxDistance, dx, dy, dz, 0.0f };
	g_gpu.Begin();
	auto* list = g_gpu.list.Get();
	list->SetComputeRootSignature(g_sceneRay.root.Get());
	list->SetPipelineState(g_sceneRay.pso.Get());
	list->SetComputeRootShaderResourceView(0, g_tlas->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	list->SetComputeRoot32BitConstants(2, 8, constants, 0);
	list->Dispatch(1, 1, 1);
	auto toCopy = Transition(output.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
	list->ResourceBarrier(1, &toCopy);
	list->CopyBufferRegion(readback.Get(), 0, output.Get(), 0, 4);
	auto back = Transition(output.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	list->ResourceBarrier(1, &back);
	if (!g_gpu.SubmitAndWait(5000)) {
		return -5.0f;
	}
	float t = 0.0f;
	void* mapped = nullptr;
	D3D12_RANGE range = { 0, 4 };
	if (FAILED(readback->Map(0, &range, &mapped)) || mapped == nullptr) {
		return -6.0f;
	}
	memcpy(&t, mapped, 4);
	D3D12_RANGE none = { 0, 0 };
	readback->Unmap(0, &none);
	return t;
}

// Many rays at once against the built scene (absolute world space). rays: 8 floats each (origin xyz, max distance,
// direction xyz, unused). Writes the hit distance (-1: none) and the index of the instance hit (-1: none) per ray.
// Returns 1, or a negative stage that failed.
NFX_EXPORT int NFX_Native_TraceTestRays(const float* rays, int count, float* distances, int* instances) {
	if (!g_device || !g_tlas || count <= 0 || !g_gpu.Create()) {
		return -1;
	}
	if (!g_sceneRays.Create(g_SceneRays, sizeof(g_SceneRays), 1, 1)) {
		return -2;
	}
	UINT64 inBytes = static_cast<UINT64>(count) * 32;
	UINT64 outBytes = static_cast<UINT64>(count) * 8;
	auto input = CreateBuffer(inBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	auto output = CreateBuffer(outBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	auto readback = CreateBuffer(outBytes, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
	if (!input || !output || !readback) {
		return -3;
	}
	void* mapped = nullptr;
	if (FAILED(input->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return -3;
	}
	memcpy(mapped, rays, inBytes);
	input->Unmap(0, nullptr);

	g_gpu.Begin();
	auto* list = g_gpu.list.Get();
	list->SetComputeRootSignature(g_sceneRays.root.Get());
	list->SetPipelineState(g_sceneRays.pso.Get());
	list->SetComputeRootShaderResourceView(0, g_tlas->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	UINT n = static_cast<UINT>(count);
	list->SetComputeRoot32BitConstants(2, 1, &n, 0);
	list->SetComputeRootShaderResourceView(3, input->GetGPUVirtualAddress());
	list->Dispatch((n + 63) / 64, 1, 1);
	auto toCopy = Transition(output.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
	list->ResourceBarrier(1, &toCopy);
	list->CopyBufferRegion(readback.Get(), 0, output.Get(), 0, outBytes);
	if (!g_gpu.SubmitAndWait(5000)) {
		return -4;
	}
	D3D12_RANGE range = { 0, static_cast<SIZE_T>(outBytes) };
	if (FAILED(readback->Map(0, &range, &mapped)) || mapped == nullptr) {
		return -5;
	}
	const UINT32* words = static_cast<const UINT32*>(mapped);
	for (int i = 0; i < count; ++i) {
		memcpy(&distances[i], &words[i * 2], 4);
		instances[i] = static_cast<int>(words[i * 2 + 1]);
	}
	D3D12_RANGE none = { 0, 0 };
	readback->Unmap(0, &none);
	return 1;
}

// ------------------------------------------------------------------ Unity's command queue
// Unity does not hand its plugin interface to this DLL, so its command queue (needed to put the engine's work in
// order with Unity's frame) is caught once: ID3D12CommandQueue::ExecuteCommandLists is swapped in the D3D12
// runtime's queue vtable (in memory, nothing on disk) until Unity's next submission on its direct queue, which is
// recorded, and the original entry is put back at once.

namespace {

using ExecuteCommandListsFn = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
// IUnknown 3, ID3D12Object 4, ID3D12DeviceChild 1, then UpdateTileMappings, CopyTileMappings, ExecuteCommandLists
constexpr int kExecuteCommandListsSlot = 10;
ExecuteCommandListsFn g_originalExecute = nullptr;
void** g_queueVtable = nullptr;
std::atomic<bool> g_hookInstalled{ false };
std::atomic<ID3D12CommandQueue*> g_unityQueue{ nullptr };
ID3D12Device* g_deviceBase = nullptr;

bool WriteSlot(void** vtable, void* function) {
	DWORD old = 0;
	if (!VirtualProtect(&vtable[kExecuteCommandListsSlot], sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) {
		return false;
	}
	InterlockedExchangePointer(&vtable[kExecuteCommandListsSlot], function);
	VirtualProtect(&vtable[kExecuteCommandListsSlot], sizeof(void*), old, &old);
	FlushInstructionCache(GetCurrentProcess(), &vtable[kExecuteCommandListsSlot], sizeof(void*));
	return true;
}

void RemoveHook() {
	bool expected = true;
	if (g_hookInstalled.compare_exchange_strong(expected, false)) {
		WriteSlot(g_queueVtable, reinterpret_cast<void*>(g_originalExecute));
	}
}

void STDMETHODCALLTYPE HookedExecuteCommandLists(ID3D12CommandQueue* queue, UINT count, ID3D12CommandList* const* lists) {
	if (g_unityQueue.load() == nullptr && queue != g_gpu.queue.Get()) {
		D3D12_COMMAND_QUEUE_DESC desc = queue->GetDesc();
		ComPtr<ID3D12Device> device;
		if (desc.Type == D3D12_COMMAND_LIST_TYPE_DIRECT && SUCCEEDED(queue->GetDevice(IID_PPV_ARGS(&device))) && device.Get() == g_deviceBase) {
			ID3D12CommandQueue* none = nullptr;
			if (g_unityQueue.compare_exchange_strong(none, queue)) {
				queue->AddRef();
				RemoveHook();
			}
		}
	}
	g_originalExecute(queue, count, lists);
}

// Work the engine puts on Unity's queue from Unity's render thread (render events), apart from g_gpu
struct RenderThreadGpu {
	// submissions in flight: every effect on makes about 10 trace events a frame, with 2 or 3 frames queued; with 12 the
	// traces of a busy frame were skipped (their results stayed Prepare's defaults: lights and reflections flickered)
	static constexpr int kSlots = 64;
	ComPtr<ID3D12CommandAllocator> allocators[kSlots];
	ComPtr<ID3D12GraphicsCommandList4> list;
	ComPtr<ID3D12Fence> fence;
	std::atomic<UINT64> submitted{ 0 };  // written by the render thread, read by WaitRenderIdle on the main thread
	int frame = 0;

	bool Create() {
		if (list) {
			return true;
		}
		for (auto& a : allocators) {
			if (FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)))) {
				return false;
			}
		}
		if (FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr, IID_PPV_ARGS(&list))) ||
		    FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)))) {
			list.Reset();
			return false;
		}
		list->Close();
		return true;
	}

	// The next allocator, once the GPU is done with what it recorded kSlots submissions ago
	ID3D12GraphicsCommandList4* Begin() {
		if (submitted >= kSlots && fence->GetCompletedValue() < submitted - (kSlots - 1)) {
			return nullptr;  // the GPU is that far behind: skip rather than stall Unity's render thread
		}
		auto& allocator = allocators[frame % kSlots];
		list->Close();  // a list left open by an earlier failure would make both resets fail
		allocator->Reset();
		list->Reset(allocator.Get(), nullptr);
		g_crumbs.Create(g_device.Get());
		crumb = g_crumbs.Begin(list.Get(), g_crumbTag);
		g_crumbTag = "render";
		return list.Get();
	}
	UINT crumb = 0;

	void Submit(ID3D12CommandQueue* queue) {
		g_crumbs.End(list.Get(), crumb);
		crumb = 0;
		list->Close();
		ID3D12CommandList* lists[] = { list.Get() };
		queue->ExecuteCommandLists(1, lists);
		queue->Signal(fence.Get(), ++submitted);
		++frame;
	}
};
RenderThreadGpu g_render;
std::atomic<int> g_queueTest{ 0 };  // 0 not run, 1 submitted, < 0 failed

// Render event 1: one test ray dispatched on Unity's queue, signalled on the engine's fence
void QueueTest() {
	if (g_dead) {
		g_queueTest = -3;
		return;
	}
	auto* queue = g_unityQueue.load();
	if (queue == nullptr || !g_tlas || !g_sceneRay.pso || !g_render.Create()) {
		g_queueTest = -1;
		return;
	}
	static ComPtr<ID3D12Resource> output;
	if (!output) {
		output = CreateBuffer(4, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	}
	g_crumbTag = "queue test";
	auto* list = g_render.Begin();
	if (list == nullptr || !output) {
		g_queueTest = -2;
		return;
	}
	float constants[8] = { 0.0f, 1000.0f, 0.0f, 10.0f, 0.0f, -1.0f, 0.0f, 0.0f };
	list->SetComputeRootSignature(g_sceneRay.root.Get());
	list->SetPipelineState(g_sceneRay.pso.Get());
	list->SetComputeRootShaderResourceView(0, g_tlas->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	list->SetComputeRoot32BitConstants(2, 8, constants, 0);
	list->Dispatch(1, 1, 1);
	g_render.Submit(queue);
	g_queueTest = 1;
}

// The render thread's own TLAS, rebuilt every frame from g_resolved (moving cars), with an instance upload buffer
// per frame in flight
// TLAS builds (one per frame) in flight: as many as the render thread may have submissions queued (Begin lets it run
// kSlots ahead), so an upload buffer is never written while a queued build still reads it
constexpr int kInstanceSlots = RenderThreadGpu::kSlots;
ComPtr<ID3D12Resource> g_renderTlas, g_renderScratch, g_renderInstances[kInstanceSlots];
UINT64 g_renderTlasCapacity = 0, g_renderScratchCapacity = 0, g_renderInstanceCapacity[kInstanceSlots] = {};
UINT32 g_renderTlasFrame = ~0u;
int g_renderTlasBuilds = 0;
ComPtr<ID3D12Resource> g_frameConstants;  // a kConstantSlot slot per submission in flight
constexpr UINT kConstantSlot = 8448;  // the Frame constants (2100 floats: 52 + 4 x 128 lights x 4), rounded up to 256 bytes
// buffers the render thread outgrew, with the submission after which the GPU no longer uses them
std::vector<std::pair<ComPtr<ID3D12Resource>, UINT64>> g_renderRetired;

// EnsureBuffer for buffers frames in flight may still use: the old one is kept until they are done
bool EnsureRenderBuffer(ComPtr<ID3D12Resource>& buffer, UINT64& capacity, UINT64 size, D3D12_RESOURCE_STATES state) {
	if (buffer && capacity >= size) {
		return true;
	}
	if (buffer) {
		g_renderRetired.emplace_back(buffer, g_render.submitted);
	}
	buffer.Reset();
	return EnsureBuffer(buffer, capacity, size, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, state);
}

void ReleaseRetired() {
	UINT64 completed = g_render.fence ? g_render.fence->GetCompletedValue() : 0;
	for (size_t i = 0; i < g_renderRetired.size();) {
		if (g_renderRetired[i].second <= completed) {
			g_renderRetired.erase(g_renderRetired.begin() + i);
		} else {
			++i;
		}
	}
}
std::atomic<int> g_traceFrames{ 0 };
std::atomic<int> g_traceSkipped{ 0 };

// Data of render event 3, filled by the mod per trace (NativeEngine.Trace)
struct TraceEventData {
	ID3D12Resource* surface;  // NFX_NativeBridge Prepare output
	ID3D12Resource* output;   // the result, read back by Unpack
	ID3D12Resource* probe;    // HDRP's diffuse ambient probe (7 float4)
	ID3D12Resource* screen;   // reflections: camera colour and depth per full resolution pixel (else null)
	UINT32 kind;              // TraceKind
	UINT32 width, height;
	UINT32 unityFrame;        // Time.frameCount: the TLAS is built once per Unity frame (the passes count frames apart)
	float constants[2100];    // the Frame constant buffer of NFXCommon.hlsli (FrameIndex at 11)
};
static_assert(sizeof(TraceEventData::constants) <= kConstantSlot, "the Frame constants outgrew their slot");

// The DXR 1.2 device's trace (RtDevice.inl); false when Unity's device is to trace it
bool TraceRt(const TraceEventData* data, ID3D12CommandQueue* queue);

// Builds g_renderTlas from g_resolved into the open list, once per frame. False when there is nothing to trace.
// g_sceneMutex held by the caller (Trace), from before the list is begun until it is submitted: the main thread's
// WaitRenderIdle only knows about submitted work, so a TLAS build recorded against BLAS it is about to free must not
// sit unsubmitted while it waits
bool RecordRenderTlas(ID3D12GraphicsCommandList4* list, UINT32 frame, D3D12_GPU_VIRTUAL_ADDRESS* records) {
	if (g_resolved.empty() || !g_records || !g_heap) {
		return false;
	}
	*records = g_records->GetGPUVirtualAddress();
	if (frame == g_renderTlasFrame && g_renderTlas) {
		return true;  // built by this frame's first trace
	}
	int slot = g_renderTlasBuilds % kInstanceSlots;
	UINT64 bytes = g_resolved.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
	if (!EnsureBuffer(g_renderInstances[slot], g_renderInstanceCapacity[slot], bytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ)) {
		return false;
	}
	void* mapped = nullptr;
	if (FAILED(g_renderInstances[slot]->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return false;
	}
	memcpy(mapped, g_resolved.data(), bytes);
	g_renderInstances[slot]->Unmap(0, nullptr);

	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
	inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
	inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	inputs.NumDescs = static_cast<UINT>(g_resolved.size());
	inputs.InstanceDescs = g_renderInstances[slot]->GetGPUVirtualAddress();
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
	g_device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
	ReleaseRetired();
	if (!EnsureRenderBuffer(g_renderScratch, g_renderScratchCapacity, info.ScratchDataSizeInBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS) ||
	    !EnsureRenderBuffer(g_renderTlas, g_renderTlasCapacity, info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)) {
		return false;
	}
	RecordWind(list, g_renderTlasBuilds, g_resolved);
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
	build.Inputs = inputs;
	build.DestAccelerationStructureData = g_renderTlas->GetGPUVirtualAddress();
	build.ScratchAccelerationStructureData = g_renderScratch->GetGPUVirtualAddress();
	list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
	auto barrier = UavBarrier(g_renderTlas.Get());
	list->ResourceBarrier(1, &barrier);
	g_renderTlasFrame = frame;
	++g_renderTlasBuilds;
	return true;
}

// Render event 3: one trace (sun shadows, ambient light, local light shadows, light shafts) on Unity's queue,
// between the mod's Prepare and Unpack dispatches. The surface and result buffers are Unity's and stay in the
// unordered access state Prepare left them in (the engine changes no resource state).
void Trace(const TraceEventData* data) {
	if (g_dead || !CheckDevice()) {
		++g_traceSkipped;
		return;
	}
	auto* queue = g_unityQueue.load();
	if (queue == nullptr || data == nullptr || data->surface == nullptr || data->output == nullptr || data->probe == nullptr ||
	    data->kind >= kTraceKinds || !g_tracePso[data->kind] || !g_render.Create()) {
		++g_traceSkipped;
		return;
	}
	if (!g_cache) {
		// zeroed at creation (committed resources start cleared): every slot empty
		g_cache = CreateBuffer(kCacheBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		if (!g_cache) {
			++g_traceSkipped;
			return;
		}
	}
	if (!g_frameConstants) {
		g_frameConstants = CreateBuffer(static_cast<UINT64>(kConstantSlot) * RenderThreadGpu::kSlots, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		if (!g_frameConstants) {
			++g_traceSkipped;
			return;
		}
	}
	if (TraceRt(data, queue)) {
		++g_traceFrames;
		return;
	}
	g_crumbTag = "trace " + std::to_string(data->kind);
	// the scene lock from here to the submit (see RecordRenderTlas): ReleaseGeometry / BuildScene wait for the GPU
	// under it and then free BLAS, so nothing referencing them may be half recorded meanwhile
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	auto* list = g_render.Begin();
	if (list == nullptr) {
		++g_traceSkipped;
		return;
	}
	UINT32 frame;
	frame = data->unityFrame;
	D3D12_GPU_VIRTUAL_ADDRESS records = 0;
	if (!RecordRenderTlas(list, frame, &records)) {
		list->Close();  // nothing recorded worth submitting; the result keeps Prepare's defaults
		++g_traceSkipped;
		return;
	}
	// this submission's constants: its slot was last used kSlots submissions ago, which Begin waited for
	UINT slot = g_render.frame % RenderThreadGpu::kSlots;
	void* mapped = nullptr;
	if (FAILED(g_frameConstants->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		list->Close();
		++g_traceSkipped;
		return;
	}
	memcpy(static_cast<char*>(mapped) + static_cast<size_t>(slot) * kConstantSlot, data->constants, sizeof(data->constants));
	g_frameConstants->Unmap(0, nullptr);

	ID3D12DescriptorHeap* heaps[] = { g_heap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	list->SetComputeRootSignature(g_traceRoot.Get());
	list->SetPipelineState(g_tracePso[data->kind].Get());
	list->SetComputeRootShaderResourceView(0, g_renderTlas->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, data->output->GetGPUVirtualAddress());
	list->SetComputeRootConstantBufferView(2, g_frameConstants->GetGPUVirtualAddress() + static_cast<UINT64>(slot) * kConstantSlot);
	list->SetComputeRootUnorderedAccessView(3, data->surface->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(4, records);
	list->SetComputeRootShaderResourceView(5, data->probe->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(6, (data->screen != nullptr ? data->screen : data->surface)->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(7, g_cache->GetGPUVirtualAddress());
	if (g_pipeline && g_traceState[data->kind]) {
		// DXR 1.0: the same trace as a ray generation shader over width x height (the root arguments above are the
		// global root signature's)
		list->SetPipelineState1(g_traceState[data->kind].Get());
		D3D12_GPU_VIRTUAL_ADDRESS row = g_traceTables->GetGPUVirtualAddress() + static_cast<UINT64>(data->kind) * kTableStride * 3;
		D3D12_DISPATCH_RAYS_DESC rays = {};
		rays.RayGenerationShaderRecord = { row, kTableStride };
		rays.MissShaderTable = { row + kTableStride, kTableStride, kTableStride };
		rays.HitGroupTable = { row + kTableStride * 2, kTableStride, kTableStride };
		rays.Width = data->width;
		rays.Height = data->height;
		rays.Depth = 1;
		list->DispatchRays(&rays);
	} else {
		list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
	}
	D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
	list->ResourceBarrier(1, &barrier);
	g_render.Submit(queue);
	++g_traceFrames;
}

void ReloadShaders();  // render event 8 (end of this file)

void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
	if (eventId == 1) {
		QueueTest();
	} else if (eventId == 8) {
		ReloadShaders();
	}
}

// NRD after a trace (Nrd.inl)
struct NrdEventData;
void NrdSigma(const NrdEventData* data);
void NrdReblur(const NrdEventData* data);
void NrdReflection(const NrdEventData* data);
// DLAA in place of TAA (Dlss.inl)
struct DlssEventData;
void Dlss(const DlssEventData* data);

void UNITY_INTERFACE_API OnRenderEventAndData(int eventId, void* data) {
	if (eventId == 3) {
		Trace(static_cast<const TraceEventData*>(data));
	} else if (eventId == 5) {
		NrdSigma(static_cast<const NrdEventData*>(data));
	} else if (eventId == 6) {
		NrdReblur(static_cast<const NrdEventData*>(data));
	} else if (eventId == 7) {
		NrdReflection(static_cast<const NrdEventData*>(data));
	} else if (eventId == 9) {
		Dlss(static_cast<const DlssEventData*>(data));
	}
}

}  // namespace

namespace {
bool WaitRenderIdle() {
	if (!g_render.fence || g_render.submitted == 0) {
		return true;
	}
	bool done = true;
	if (g_render.fence->GetCompletedValue() < g_render.submitted) {
		HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
		g_render.fence->SetEventOnCompletion(g_render.submitted, event);
		// a GPU that is this far behind (a resize at 4K evicting memory) must not have what it still uses freed:
		// callers that free resources on a false wait keep them (DlssResize)
		done = WaitForSingleObject(event, 5000) == WAIT_OBJECT_0;
		CloseHandle(event);
	}
	return done;
}

}  // namespace

// Starts catching Unity's direct queue (see above). Returns 1 when the hook is in place or the queue is known.
NFX_EXPORT int NFX_Native_CaptureQueue() {
	if (g_unityQueue.load() != nullptr) {
		return 1;
	}
	if (!g_device || !g_gpu.Create()) {
		return -1;
	}
	if (g_hookInstalled.load()) {
		return 1;
	}
	ComPtr<ID3D12Device> base;
	g_device.As(&base);
	g_deviceBase = base.Get();
	g_queueVtable = *reinterpret_cast<void***>(g_gpu.queue.Get());
	g_originalExecute = reinterpret_cast<ExecuteCommandListsFn>(g_queueVtable[kExecuteCommandListsSlot]);
	g_hookInstalled = true;
	if (!WriteSlot(g_queueVtable, reinterpret_cast<void*>(&HookedExecuteCommandLists))) {
		g_hookInstalled = false;
		return -2;
	}
	return 1;
}

// 2 Unity's queue known, 1 still waiting for it, 0 not started. giveUp removes the hook.
NFX_EXPORT int NFX_Native_QueueStatus(int giveUp) {
	if (g_unityQueue.load() != nullptr) {
		return 2;
	}
	if (giveUp) {
		RemoveHook();
		return 0;
	}
	return g_hookInstalled.load() ? 1 : 0;
}

// Render event callback for CommandBuffer.IssuePluginEvent (event 1: queue test)
NFX_EXPORT void* NFX_Native_RenderEventFunc() {
	return reinterpret_cast<void*>(&OnRenderEvent);
}

// Render event callback for CommandBuffer.IssuePluginEventAndData (event 3: a trace, data TraceEventData)
NFX_EXPORT void* NFX_Native_RenderEventAndDataFunc() {
	return reinterpret_cast<void*>(&OnRenderEventAndData);
}

// Moving instances: new 3x4 row major transforms (12 floats each) for the instance indices given
NFX_EXPORT void NFX_Native_UpdateTransforms(const int* indices, const float* transforms, int count) {
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	for (int i = 0; i < count; ++i) {
		int index = indices[i];
		if (index >= 0 && index < static_cast<int>(g_resolved.size())) {
			memcpy(g_resolved[index].Transform, transforms + i * 12, sizeof(g_resolved[index].Transform));
		}
		if (index >= 0 && index < static_cast<int>(g_rtResolved.size())) {
			memcpy(g_rtResolved[index].Transform, transforms + i * 12, sizeof(g_rtResolved[index].Transform));
		}
	}
}

// How the traces run: 1 DXR 1.0 (TraceRay in ray tracing pipelines), 0 DXR 1.1 (inline ray queries). Returns 1 when
// the pipelines exist (else the inline traces keep running).
NFX_EXPORT int NFX_Native_SetPipeline(int pipeline) {
	g_pipeline = pipeline != 0 && g_traceState[kTraceKinds - 1] != nullptr;
	return g_traceState[kTraceKinds - 1] ? 1 : 0;
}

// Whether the sun shadows can run (Unity's queue known, scene built, pipeline ready): 1 / 0
NFX_EXPORT int NFX_Native_SunReady() {
	return !g_dead && g_unityQueue.load() != nullptr && g_tracePso[kTraceKinds - 1] && !g_resolved.empty() ? 1 : 0;
}

// Frames the sun shadows were traced and skipped since the start
NFX_EXPORT void NFX_Native_SunCounters(int* traced, int* skipped) {
	*traced = g_traceFrames.load();
	*skipped = g_traceSkipped.load();
}

// Queue test: 2 the work submitted on Unity's queue finished, 1 submitted and pending, 0 not run, < 0 failed
NFX_EXPORT int NFX_Native_QueueTestResult() {
	int state = g_queueTest.load();
	if (state != 1) {
		return state;
	}
	return g_render.fence && g_render.fence->GetCompletedValue() >= g_render.submitted ? 2 : 1;
}

// ------------------------------------------------------------------ DXR 1.2 probe

// The game's runtime (Agility SDK 1.610) stops at Shader Model 6.8. Can the engine reach DXR 1.2 (shader execution
// reordering, opacity micromaps) through an independent device on Agility SDK 1.619, in this process, and hand its
// results to Unity's device? Dxr12Probe.h's checks on a device for Unity's adapter, then the micromap test's output
// goes through a buffer and a fence shared by both devices and Unity's device reads it back.
namespace {
std::string g_probeLog;
void ProbeAppend(const char* format, ...) {
	char line[512];
	va_list args;
	va_start(args, format);
	vsnprintf(line, sizeof(line), format, args);
	va_end(args);
	g_probeLog += line;
}
}  // namespace
#define ProbeLog(...) ProbeAppend(__VA_ARGS__)
#include "Dxr12Probe.h"

namespace {
// CLSID_D3D12SDKConfiguration (d3d12.h declares it without INITGUID here)
const GUID kSdkConfiguration = { 0x7cda6aca, 0xa03e, 0x49c8, { 0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce } };

bool ProbeInterop(dxr12::Gpu& g, ID3D12Resource* tlas, ID3D12RootSignature* root) {
	// the buffer lives on Unity's device; the 1.619 device opens it
	D3D12_HEAP_PROPERTIES hp = {};
	hp.Type = D3D12_HEAP_TYPE_DEFAULT;
	D3D12_RESOURCE_DESC rd = {};
	rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	rd.Width = 256;
	rd.Height = rd.DepthOrArraySize = rd.MipLevels = 1;
	rd.SampleDesc.Count = 1;
	rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
	rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
	ComPtr<ID3D12Resource> unityBuffer, probeBuffer;
	HANDLE bufferHandle = nullptr, fenceHandle = nullptr;
	if (FAILED(g_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&unityBuffer))) ||
	    FAILED(g_device->CreateSharedHandle(unityBuffer.Get(), nullptr, GENERIC_ALL, nullptr, &bufferHandle))) {
		ProbeLog("FAIL shared buffer on Unity's device\n");
		return false;
	}
	HRESULT hr = g.device->OpenSharedHandle(bufferHandle, IID_PPV_ARGS(&probeBuffer));
	CloseHandle(bufferHandle);
	if (FAILED(hr)) {
		ProbeLog("FAIL the 1.619 device cannot open Unity's buffer (hr 0x%08lX)\n", static_cast<unsigned long>(hr));
		return false;
	}
	// the fence lives on the 1.619 device; Unity's device opens it
	ComPtr<ID3D12Fence> probeFence, unityFence;
	if (FAILED(g.device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&probeFence))) ||
	    FAILED(g.device->CreateSharedHandle(probeFence.Get(), nullptr, GENERIC_ALL, nullptr, &fenceHandle))) {
		ProbeLog("FAIL shared fence on the 1.619 device\n");
		return false;
	}
	hr = g_device->OpenSharedHandle(fenceHandle, IID_PPV_ARGS(&unityFence));
	CloseHandle(fenceHandle);
	if (FAILED(hr)) {
		ProbeLog("FAIL Unity's device cannot open the 1.619 fence (hr 0x%08lX)\n", static_cast<unsigned long>(hr));
		return false;
	}

	// the 1.619 device traces into the shared buffer and signals the shared fence
	if (!dxr12::TestOmm(g, tlas, root, probeBuffer.Get())) {
		ProbeLog("FAIL micromap test into the shared buffer\n");
		return false;
	}
	g.queue->Signal(probeFence.Get(), 1);

	// Unity's device waits for that fence on the GPU, then copies the buffer out
	ComPtr<ID3D12CommandQueue> queue;
	ComPtr<ID3D12CommandAllocator> allocator;
	ComPtr<ID3D12GraphicsCommandList> list;
	ComPtr<ID3D12Fence> done;
	D3D12_COMMAND_QUEUE_DESC qd = {};
	qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
	hp.Type = D3D12_HEAP_TYPE_READBACK;
	rd.Flags = D3D12_RESOURCE_FLAG_NONE;
	ComPtr<ID3D12Resource> readback;
	if (FAILED(g_device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue))) ||
	    FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator))) ||
	    FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list))) ||
	    FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&done))) ||
	    FAILED(g_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)))) {
		ProbeLog("FAIL command objects on Unity's device\n");
		return false;
	}
	list->CopyBufferRegion(readback.Get(), 0, unityBuffer.Get(), 0, 16);
	list->Close();
	queue->Wait(unityFence.Get(), 1);
	ID3D12CommandList* lists[] = { list.Get() };
	queue->ExecuteCommandLists(1, lists);
	queue->Signal(done.Get(), 1);
	HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	done->SetEventOnCompletion(1, e);
	bool finished = WaitForSingleObject(e, 5000) == WAIT_OBJECT_0;
	CloseHandle(e);
	if (!finished) {
		ProbeLog("FAIL Unity's device did not get past the shared fence\n");
		return false;
	}
	struct { float t; UINT candidates; } r[2] = {};
	void* mapped = nullptr;
	if (FAILED(readback->Map(0, nullptr, &mapped))) {
		return false;
	}
	memcpy(r, mapped, sizeof(r));
	readback->Unmap(0, nullptr);
	ProbeLog("  Unity's device read: t %.3f / %.3f, candidates %u / %u (expect -1 / 1, 0 / 0)\n", r[0].t, r[1].t, r[0].candidates, r[1].candidates);
	return r[0].t < 0.0f && r[1].t > 0.99f && r[1].t < 1.01f && r[0].candidates == 0 && r[1].candidates == 0;
}
}  // namespace

// sdkPath: the folder with Agility SDK 1.619's D3D12Core.dll (absolute, ending in a slash). Writes the report;
// returns 1 when everything passed, 0 when something failed, < 0 when it could not start.
NFX_EXPORT int NFX_Native_Dxr12Probe(const char* sdkPath, char* buffer, int size) {
	g_probeLog.clear();
	if (!g_device || g_dead) {
		ProbeLog("FAIL no Unity device\n");
		CopyOut(g_probeLog, buffer, size);
		return -1;
	}
	bool ok = false;
	{
		ComPtr<ID3D12SDKConfiguration1> config;
		ComPtr<ID3D12DeviceFactory> factory;
		ComPtr<IDXGIFactory4> dxgi;
		ComPtr<IDXGIAdapter1> adapter;
		HRESULT hr = S_OK;
		if (FAILED(hr = D3D12GetInterface(kSdkConfiguration, IID_PPV_ARGS(&config)))) {
			ProbeLog("FAIL D3D12GetInterface(SDK configuration 1) hr 0x%08lX\n", static_cast<unsigned long>(hr));
		} else if (FAILED(hr = config->CreateDeviceFactory(619, sdkPath, IID_PPV_ARGS(&factory)))) {
			ProbeLog("FAIL CreateDeviceFactory(619, %s) hr 0x%08lX\n", sdkPath, static_cast<unsigned long>(hr));
		} else if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&dxgi))) ||
		           FAILED(dxgi->EnumAdapterByLuid(g_device->GetAdapterLuid(), IID_PPV_ARGS(&adapter)))) {
			ProbeLog("FAIL Unity's adapter\n");
		} else {
			ProbeLog("OK   device factory for SDK 619 next to the game's runtime\n");
			dxr12::Gpu g;
			std::vector<ComPtr<ID3D12Resource>> keep;
			ComPtr<ID3D12Resource> tlas;
			ComPtr<ID3D12RootSignature> root;
			if (dxr12::ProbeAll(factory.Get(), adapter.Get(), g, keep, tlas, root)) {
				bool omm = dxr12::TestOmm(g, tlas.Get(), root.Get());
				ProbeLog("%s opacity micromaps (inline ray query)\n", omm ? "OK  " : "FAIL");
				bool ser = dxr12::TestSer(g, tlas.Get(), root.Get());
				ProbeLog("%s shader execution reordering (ray generation shader)\n", ser ? "OK  " : "FAIL");
				bool interop = ProbeInterop(g, tlas.Get(), root.Get());
				ProbeLog("%s shared buffer and fence between the 1.619 device and Unity's\n", interop ? "OK  " : "FAIL");
				ok = omm && ser && interop;
			}
		}
	}
	ProbeLog(ok ? "DXR 1.2: all checks passed\n" : "DXR 1.2: some checks failed\n");
	CopyOut(g_probeLog, buffer, size);
	return ok ? 1 : 0;
}

#include "RtDevice.inl"
#include "Nrd.inl"
#include "Dlss.inl"

namespace {
// Render event 8: the shader files NFX_Native_LoadShaders read take the embedded ones' place, and every trace pipeline
// and NRD pack / unpack kernel is made again from them (on the render thread, so no trace is recorded meanwhile; the
// GPU is waited for, so no list still uses the old ones)
void ReloadShaders() {
	if (!g_shaderFilesReady.exchange(false)) {
		return;
	}
	WaitRenderIdle();
	std::lock_guard<std::mutex> scene(g_sceneMutex);
	{
		std::lock_guard<std::mutex> lock(g_shaderFilesMutex);
		g_shaderFiles.swap(g_shaderFilesPending);
		g_shaderFilesPending.clear();
	}
	for (int k = 0; k < kTraceKinds; ++k) {
		g_tracePso[k].Reset();
		g_traceState[k].Reset();
	}
	bool ok = CreateTracePipelines();
	NrdDropKernels();
	DlssDropKernels();
	char line[192];
	snprintf(line, sizeof(line), "%zu shader files in use; trace pipelines %s", g_shaderFiles.size(),
	         ok ? "made again" : "failed (a shader file is broken: traces stop until the next reload)");
	SetStatus(g_shaderStatus, line);
}
}  // namespace

// Reads every <name>.cso of the folder (main thread); render event 8 then takes them in. Returns how many.
NFX_EXPORT int NFX_Native_LoadShaders(const char* folder) {
	std::map<std::string, std::vector<char>> files;
	WIN32_FIND_DATAA found = {};
	std::string base = std::string(folder) + "\\";
	HANDLE find = FindFirstFileA((base + "*.cso").c_str(), &found);
	if (find != INVALID_HANDLE_VALUE) {
		do {
			std::string name = found.cFileName;
			FILE* f = nullptr;
			if (fopen_s(&f, (base + name).c_str(), "rb") != 0 || f == nullptr) {
				continue;
			}
			fseek(f, 0, SEEK_END);
			long length = ftell(f);
			fseek(f, 0, SEEK_SET);
			std::vector<char> bytes(length > 0 ? static_cast<size_t>(length) : 0);
			size_t read = bytes.empty() ? 0 : fread(bytes.data(), 1, bytes.size(), f);
			fclose(f);
			if (read == bytes.size() && !bytes.empty()) {
				files[name.substr(0, name.size() - 4)] = std::move(bytes);
			}
		} while (FindNextFileA(find, &found));
		FindClose(find);
	}
	int count = static_cast<int>(files.size());
	{
		std::lock_guard<std::mutex> lock(g_shaderFilesMutex);
		g_shaderFilesPending = std::move(files);
	}
	g_shaderFilesReady = true;
	return count;
}

NFX_EXPORT int NFX_Native_ShaderStatus(char* buffer, int size) {
	return CopyOut(g_shaderStatus, buffer, size);
}

// The game's video memory use as the OS counts it for this process (MB), and the budget it may use; 0 when unknown.
// Lets the mod show what the settings cost and find memory that is not given back.
NFX_EXPORT int NFX_Native_GpuMemory(UINT64* usedMb, UINT64* budgetMb) {
	*usedMb = 0;
	*budgetMb = 0;
	if (!g_device) {
		return 0;
	}
	ComPtr<IDXGIFactory4> dxgi;
	ComPtr<IDXGIAdapter3> adapter;
	if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->EnumAdapterByLuid(g_device->GetAdapterLuid(), IID_PPV_ARGS(&adapter)))) {
		return 0;
	}
	DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
	if (FAILED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) {
		return 0;
	}
	*usedMb = info.CurrentUsage >> 20;
	*budgetMb = info.Budget >> 20;
	return 1;
}

// ------------------------------------------------------------------ memory (the debug page, DebugPage.cs)
namespace {
// What one resource takes in video memory: a buffer its width rounded to 64 KB (a committed resource's granularity),
// a texture as the device lays it out
UINT64 ResourceBytes(ID3D12Resource* resource) {
	if (resource == nullptr) {
		return 0;
	}
	D3D12_RESOURCE_DESC desc = resource->GetDesc();
	if (desc.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER) {
		return (desc.Width + 0xFFFFull) & ~0xFFFFull;
	}
	ComPtr<ID3D12Device> device;
	if (FAILED(resource->GetDevice(IID_PPV_ARGS(&device)))) {
		return 0;
	}
	return device->GetResourceAllocationInfo(0, 1, &desc).SizeInBytes;
}
}  // namespace

// The engine's own video memory by kind, in bytes, into out (as many as capacity holds); returns how many there are.
// Order (keep in step with DebugPage.cs NativeMemoryNames): BLAS pool reserved, BLAS pool used, data pool reserved,
// data pool used, upload copies (meshes still in upload buffers), scratch and TLAS (scene builds), render TLAS
// (per frame), records and tables, radiance cache, swaying foliage, NRD, DXR 1.2 device. DLSS's own memory (NGX) is
// not visible from here.
NFX_EXPORT int NFX_Native_MemoryStats(unsigned long long* out, int capacity) {
	constexpr int kCount = 12;
	if (out == nullptr || capacity <= 0) {
		return kCount;
	}
	UINT64 v[kCount] = {};
	{
		std::lock_guard<std::mutex> lock(g_sceneMutex);
		v[0] = g_blasPool.Reserved();
		v[1] = g_blasPool.Used();
		v[2] = g_dataPool.Reserved();
		v[3] = g_dataPool.Used();
		for (const auto& g : g_geometries) {
			for (const DataSpan* d : { &g.ownIndices, &g.ownPositions, &g.ownNormals, &g.ownUvs }) {
				v[4] += d->owned ? ResourceBytes(d->owned.Get()) : 0;
			}
		}
		v[5] = ResourceBytes(g_scratch.Get()) + ResourceBytes(g_tlas.Get()) + ResourceBytes(g_instanceBuffer.Get()) +
		       ResourceBytes(g_compactSizes.Get()) + ResourceBytes(g_compactReadback.Get());
		v[6] = ResourceBytes(g_renderTlas.Get()) + ResourceBytes(g_renderScratch.Get());
		for (const auto& r : g_renderInstances) {
			v[6] += ResourceBytes(r.Get());
		}
		v[7] = ResourceBytes(g_records.Get()) + ResourceBytes(g_traceTables.Get()) + ResourceBytes(g_frameConstants.Get());
		v[8] = ResourceBytes(g_cache.Get());
		v[9] = ResourceBytes(g_windScratch.Get()) + ResourceBytes(g_windFrames.Get());
		for (const auto& w : g_windBlas) {
			v[9] += ResourceBytes(w.positions.Get()) + ResourceBytes(w.blas.Get());
		}
		for (const NrdContext* c : { &g_nrdSun, &g_nrdAmbient, &g_nrdLocal, &g_nrdReflection }) {
			for (const auto& t : c->permanent) {
				v[10] += ResourceBytes(t.resource.Get());
			}
			for (const auto& t : c->transient) {
				v[10] += ResourceBytes(t.resource.Get());
			}
			for (const auto& t : c->in) {
				v[10] += ResourceBytes(t.resource.Get());
			}
			for (const auto& t : c->out) {
				v[10] += ResourceBytes(t.resource.Get());
			}
		}
		v[10] += ResourceBytes(g_nrdConstants.Get());
		for (const auto& c : g_rtChunks) {
			v[11] += ResourceBytes(c.rt.Get());  // the shared chunks: counted once (the Unity side is the same memory)
		}
		for (const auto& r : g_rtGeometries) {
			v[11] += ResourceBytes(r.blas.Get()) + ResourceBytes(r.ommData.Get()) + ResourceBytes(r.ommDescs.Get()) +
			         ResourceBytes(r.ommIndices.Get()) + ResourceBytes(r.ommArray.Get());
		}
		for (const auto& w : g_rtWind) {
			v[11] += ResourceBytes(w.positions.Get()) + ResourceBytes(w.blas.Get());
		}
		v[11] += ResourceBytes(g_rt.tlas.Get()) + ResourceBytes(g_rt.scratch.Get()) + ResourceBytes(g_rt.buildScratch.Get()) +
		         ResourceBytes(g_rt.records.Get()) + ResourceBytes(g_rtWindScratch.Get());
		for (int k = 0; k < 2; ++k) {
			v[11] += ResourceBytes(g_rt.inRt[k].Get()) + ResourceBytes(g_rt.outRt[k].Get());
		}
	}
	int n = capacity < kCount ? capacity : kCount;
	for (int i = 0; i < n; ++i) {
		out[i] = v[i];
	}
	return kCount;
}
