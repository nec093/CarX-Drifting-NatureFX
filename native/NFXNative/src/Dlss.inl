// NFXNative: NVIDIA DLSS (NGX, static library and headers from %USERPROFILE%\NatureFX-Deps\DLSS) in place of HDRP's TAA:
// DLAA at the render resolution, or upscaling from a lower one to the display's (Dlaa.cs), included at the end of NFXNative.cpp after Nrd.inl (whose descriptor ring, texture and kernel helpers it
// shares). Render event 9, on Unity's queue like the traces.
//
// Unity's textures are never handed over (their resource states are Unity's): NFX_NativeBridge PrepareDlss packs the
// camera's colour (pre-exposed HDR), its motion to the previous frame and its depth into one buffer (a uint4 per
// pixel: colour as 4 halves, motion as 2 halves, depth), DlssPack.hlsl spreads it over the engine's own textures,
// DLSS evaluates into its own output (the display resolution), DlssUnpack.hlsl writes the result into the output
// buffer (the same buffer for DLAA, the colour replaced) and UnpackDlss puts it into PostPass's colour.
// Ray Reconstruction (DLSS-RR, nvngx_dlssd.dll): the same, with the noisy sun shadows and reflections left in the
// colour (RTShadowPass, RTReflectionPass) and its guides: a second buffer (diffuse and specular albedo, the world
// normal) that DlssPackGuides.hlsl spreads over textures of its own, the roughness in the colour's alpha, the camera's
// matrices. nvngx_dlss.dll is looked for next to NFXNative.dll (the mod folder).
#include "nvsdk_ngx_helpers.h"
#include "nvsdk_ngx_helpers_dlssd_d3d.h"
#include "DlssPackGuides.h"  // g_DlssPackGuides: shaders/DlssPackGuides.hlsl
#include "DlssPack.h"    // g_DlssPack: shaders/DlssPack.hlsl
#include "DlssUnpack.h"  // g_DlssUnpack: shaders/DlssUnpack.hlsl

namespace {

struct DlssEventData {
	ID3D12Resource* buffer;   // PrepareDlss: a uint4 per render pixel
	ID3D12Resource* output;   // a uint4 per display pixel, the colour in its x, y (DLAA: buffer itself)
	UINT32 width, height;     // the render resolution
	UINT32 outWidth, outHeight;
	float jitterX, jitterY;   // the projection's offset this frame, in render pixels (DLSS's convention)
	UINT32 reset;             // 1: history dropped (camera cut)
	float timeDelta;          // ms
	UINT32 quality;           // NVSDK_NGX_PerfQuality_Value (DLAA 5)
	UINT32 rayReconstruction; // 1: DLSS-RR with the guides
	ID3D12Resource* guides;   // a uint4 per render pixel (rayReconstruction 1)
	float worldToView[16];    // the camera without the jitter, column major (rayReconstruction 1)
	float viewToClip[16];
};
static_assert(sizeof(DlssEventData) == 192, "DlssEventData: NativeEngine.Dlss writes 192 bytes");

struct DlssState {
	bool initialised = false, failed = false;
	std::string status = "not started";
	NVSDK_NGX_Parameter* params = nullptr;
	NVSDK_NGX_Handle* feature = nullptr;
	UINT width = 0, height = 0, outWidth = 0, outHeight = 0, quality = 0;
	bool rr = false;                 // the feature is DLSS-RR
	bool rrAvailable = false;
	NrdTexture color, motion, depth, output;
	NrdTexture diffuse, specular, normals;  // Ray Reconstruction's guides
	NrdKernel pack, unpack, packGuides;
	int runs = 0;
};
DlssState g_dlss;

// the folder NFXNative.dll was loaded from (nvngx_dlss.dll and NGX's logs there)
std::wstring DlssFolder() {
	HMODULE self = nullptr;
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                   reinterpret_cast<LPCWSTR>(&DlssFolder), &self);
	wchar_t path[MAX_PATH] = {};
	GetModuleFileNameW(self, path, MAX_PATH);
	std::wstring folder = path;
	size_t slash = folder.find_last_of(L"\\/");
	return slash == std::wstring::npos ? L"." : folder.substr(0, slash);
}

// NGX's own log lines (its warnings about the inputs it is given) in NFX_ngx.log next to NFXNative.dll, started
// afresh each run; NGX may call this from any thread
std::mutex g_dlssLogMutex;
FILE* g_dlssLog = nullptr;

void NVSDK_CONV DlssLog(const char* message, NVSDK_NGX_Logging_Level level, NVSDK_NGX_Feature feature) {
	std::lock_guard<std::mutex> lock(g_dlssLogMutex);
	if (g_dlssLog == nullptr || message == nullptr) {
		return;
	}
	fprintf(g_dlssLog, "[%d] %s", static_cast<int>(feature), message);
	size_t length = strlen(message);
	if (length == 0 || message[length - 1] != '\n') {
		fputc('\n', g_dlssLog);
	}
	fflush(g_dlssLog);
}

// NGX on Unity's device (render thread, once). Fails for good: the status says why.
bool DlssInit() {
	auto& d = g_dlss;
	if (d.initialised) {
		return true;
	}
	if (d.failed) {
		return false;
	}
	d.failed = true;  // until everything below is done
	static std::wstring folder = DlssFolder();
	static const wchar_t* paths[] = { folder.c_str() };
	NVSDK_NGX_FeatureCommonInfo info = {};
	info.PathListInfo.Path = paths;
	info.PathListInfo.Length = 1;
	{
		std::lock_guard<std::mutex> lock(g_dlssLogMutex);
		if (g_dlssLog == nullptr) {
			_wfopen_s(&g_dlssLog, (folder + L"\\NFX_ngx.log").c_str(), L"w");
		}
	}
	info.LoggingInfo.LoggingCallback = &DlssLog;
	info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
	info.LoggingInfo.DisableOtherLoggingSinks = true;
	NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init_with_ProjectID("6f3b8a2e-4c1d-4e7a-9b5f-2d8c0e1a7f43", NVSDK_NGX_ENGINE_TYPE_CUSTOM, "NatureFX",
	                                                        folder.c_str(), g_device.Get(), &info);
	if (NVSDK_NGX_FAILED(r)) {
		char line[128];
		snprintf(line, sizeof(line), "NGX could not start (0x%08x: not an NVIDIA RTX GPU, or the driver is too old)", static_cast<unsigned>(r));
		d.status = line;
		return false;
	}
	if (NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_GetCapabilityParameters(&d.params)) || d.params == nullptr) {
		d.status = "NGX's capability parameters could not be had";
		NVSDK_NGX_D3D12_Shutdown1(g_device.Get());
		return false;
	}
	int available = 0, needsDriver = 0;
	NVSDK_NGX_Parameter_GetI(d.params, NVSDK_NGX_Parameter_SuperSampling_Available, &available);
	NVSDK_NGX_Parameter_GetI(d.params, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsDriver);
	if (!available) {
		d.status = needsDriver ? "DLSS needs a newer NVIDIA driver" : "DLSS is not available (nvngx_dlss.dll missing next to NFXNative.dll, or the GPU has no tensor cores)";
		NVSDK_NGX_D3D12_DestroyParameters(d.params);
		d.params = nullptr;
		NVSDK_NGX_D3D12_Shutdown1(g_device.Get());
		return false;
	}
	int rrAvailable = 0;
	NVSDK_NGX_Parameter_GetI(d.params, NVSDK_NGX_Parameter_SuperSamplingDenoising_Available, &rrAvailable);
	d.rrAvailable = rrAvailable != 0;
	d.initialised = true;
	d.failed = false;
	d.status = "ready";
	return true;
}

// The textures and the feature at these sizes and quality; the feature is made again on a change (it records into
// the list)
bool DlssResize(ID3D12GraphicsCommandList4* list, const DlssEventData* data) {
	auto& d = g_dlss;
	UINT width = data->width, height = data->height, outWidth = data->outWidth, outHeight = data->outHeight;
	bool rr = data->rayReconstruction != 0 && d.rrAvailable;
	if (d.feature && d.width == width && d.height == height && d.outWidth == outWidth && d.outHeight == outHeight && d.quality == data->quality &&
	    d.rr == rr) {
		return true;
	}
	if (d.feature) {
		// the old feature may still be in flight: wait for the GPU (a resize is rare)
		WaitRenderIdle();
		NVSDK_NGX_D3D12_ReleaseFeature(d.feature);
		d.feature = nullptr;
	}
	if (!NrdCreateTexture(d.color, DXGI_FORMAT_R16G16B16A16_FLOAT, width, height) ||
	    !NrdCreateTexture(d.motion, DXGI_FORMAT_R16G16_FLOAT, width, height) ||
	    !NrdCreateTexture(d.depth, DXGI_FORMAT_R32_FLOAT, width, height) ||
	    !NrdCreateTexture(d.output, DXGI_FORMAT_R16G16B16A16_FLOAT, outWidth, outHeight) ||
	    (rr && (!NrdCreateTexture(d.diffuse, DXGI_FORMAT_R16G16B16A16_FLOAT, width, height) ||
	            !NrdCreateTexture(d.specular, DXGI_FORMAT_R16G16B16A16_FLOAT, width, height) ||
	            !NrdCreateTexture(d.normals, DXGI_FORMAT_R16G16B16A16_FLOAT, width, height)))) {
		d.status = "DLSS textures could not be made";
		return false;
	}
	NVSDK_NGX_DLSS_Create_Params create = {};
	create.Feature.InWidth = width;
	create.Feature.InHeight = height;
	create.Feature.InTargetWidth = outWidth;
	create.Feature.InTargetHeight = outHeight;
	create.Feature.InPerfQualityValue = static_cast<NVSDK_NGX_PerfQuality_Value>(data->quality);
	// HDR (pre-exposed by HDRP's exposure), the motion without the jitter (PrepareDlss works it out from the camera's
	// unjittered matrices), HDRP's reversed depth, DLSS's own exposure on the pre-exposed colour
	create.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_DepthInverted |
	                              NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
	NVSDK_NGX_Result r;
	if (rr) {
		// the roughness in the normals' w, HDRP's (reversed) hardware depth
		NVSDK_NGX_DLSSD_Create_Params rrCreate = {};
		rrCreate.InDenoiseMode = NVSDK_NGX_DLSS_Denoise_Mode_DLUnified;
		rrCreate.InRoughnessMode = NVSDK_NGX_DLSS_Roughness_Mode_Packed;
		rrCreate.InUseHWDepth = NVSDK_NGX_DLSS_Depth_Type_HW;
		rrCreate.InWidth = width;
		rrCreate.InHeight = height;
		rrCreate.InTargetWidth = outWidth;
		rrCreate.InTargetHeight = outHeight;
		rrCreate.InPerfQualityValue = create.Feature.InPerfQualityValue;
		rrCreate.InFeatureCreateFlags = create.InFeatureCreateFlags;
		r = NGX_D3D12_CREATE_DLSSD_EXT(list, 1, 1, &d.feature, d.params, &rrCreate);
	} else {
		r = NGX_D3D12_CREATE_DLSS_EXT(list, 1, 1, &d.feature, d.params, &create);
	}
	if (NVSDK_NGX_FAILED(r)) {
		char line[96];
		snprintf(line, sizeof(line), "the DLSS%s feature could not be made (0x%08x)", rr ? "-RR" : "", static_cast<unsigned>(r));
		d.status = line;
		d.feature = nullptr;
		return false;
	}
	d.width = width;
	d.height = height;
	d.outWidth = outWidth;
	d.outHeight = outHeight;
	d.quality = data->quality;
	d.rr = rr;
	d.runs = 0;
	return true;
}

void Dlss(const DlssEventData* data) {
	auto& d = g_dlss;
	if (g_dead || g_unityQueue.load() == nullptr || data == nullptr || data->buffer == nullptr || data->output == nullptr || data->width == 0 ||
	    data->height == 0 || data->outWidth < data->width || data->outHeight < data->height ||
	    !g_render.Create() || !NrdShared() || !DlssInit()) {
		return;
	}
	if (!d.pack.pso && (!NrdPackKernel(d.pack, 3, NrdCode("DlssPack", g_DlssPack, sizeof(g_DlssPack))) ||
	                    !NrdUnpackKernel(d.unpack, 1, NrdCode("DlssUnpack", g_DlssUnpack, sizeof(g_DlssUnpack))) ||
	                    !NrdPackKernel(d.packGuides, 3, NrdCode("DlssPackGuides", g_DlssPackGuides, sizeof(g_DlssPackGuides))))) {
		d.status = "DLSS pack / unpack pipelines could not be made";
		return;
	}
	UINT64 completed = g_render.fence->GetCompletedValue();
	for (size_t i = 0; i < g_nrdRetired.size();) {
		if (g_nrdRetired[i].second <= completed) {
			g_nrdRetired.erase(g_nrdRetired.begin() + i);
		} else {
			++i;
		}
	}
	auto* list = g_render.Begin();
	if (list == nullptr) {
		return;
	}
	bool rr = data->rayReconstruction != 0 && d.rrAvailable && data->guides != nullptr;
	bool resized = d.width != data->width || d.height != data->height || d.outWidth != data->outWidth || d.outHeight != data->outHeight ||
	               d.quality != data->quality || d.rr != rr || !d.feature;
	if (!DlssResize(list, data)) {
		list->Close();
		return;
	}
	std::vector<D3D12_RESOURCE_BARRIER> barriers;
	UINT constants[4] = { data->width, data->height, 0, 0 };

	// the buffer over the three inputs
	NrdTexture* inputs[3] = { &d.color, &d.motion, &d.depth };
	for (auto* t : inputs) {
		NrdTransition(barriers, t, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	}
	NrdFlush(list, barriers, false);
	ID3D12DescriptorHeap* heaps[] = { g_nrdHeap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	D3D12_GPU_DESCRIPTOR_HANDLE table;
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = NrdDescriptors(3, &table);
	for (UINT i = 0; i < 3; ++i) {
		NrdView(NrdAt(cpu, i), inputs[i], true);
	}
	list->SetComputeRootSignature(d.pack.root.Get());
	list->SetPipelineState(d.pack.pso.Get());
	for (UINT i = 0; i < 3; ++i) {
		list->SetComputeRootShaderResourceView(i, data->buffer->GetGPUVirtualAddress());
	}
	list->SetComputeRootDescriptorTable(3, table);
	list->SetComputeRoot32BitConstants(4, 4, constants, 0);
	list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
	// Ray Reconstruction's guides: the guide buffer (and the roughness from the colour's alpha) over three more
	NrdTexture* guides[3] = { &d.diffuse, &d.specular, &d.normals };
	if (rr) {
		for (auto* t : guides) {
			NrdTransition(barriers, t, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		}
		NrdFlush(list, barriers, false);
		cpu = NrdDescriptors(3, &table);
		for (UINT i = 0; i < 3; ++i) {
			NrdView(NrdAt(cpu, i), guides[i], true);
		}
		list->SetComputeRootSignature(d.packGuides.root.Get());
		list->SetPipelineState(d.packGuides.pso.Get());
		list->SetComputeRootShaderResourceView(0, data->guides->GetGPUVirtualAddress());
		list->SetComputeRootShaderResourceView(1, data->buffer->GetGPUVirtualAddress());
		list->SetComputeRootShaderResourceView(2, data->buffer->GetGPUVirtualAddress());
		list->SetComputeRootDescriptorTable(3, table);
		list->SetComputeRoot32BitConstants(4, 4, constants, 0);
		list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
	}

	// DLSS
	for (auto* t : inputs) {
		NrdTransition(barriers, t, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
	}
	if (rr) {
		for (auto* t : guides) {
			NrdTransition(barriers, t, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
		}
	}
	NrdTransition(barriers, &d.output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	NrdFlush(list, barriers, true);
	NVSDK_NGX_D3D12_DLSS_Eval_Params eval = {};
	eval.Feature.pInColor = d.color.resource.Get();
	eval.Feature.pInOutput = d.output.resource.Get();
	eval.pInDepth = d.depth.resource.Get();
	eval.pInMotionVectors = d.motion.resource.Get();
	eval.InJitterOffsetX = data->jitterX;
	eval.InJitterOffsetY = data->jitterY;
	eval.InRenderSubrectDimensions = { data->width, data->height };
	eval.InReset = data->reset || resized || d.runs == 0 ? 1 : 0;
	eval.InMVScaleX = 1.0f;  // PrepareDlss writes the motion in pixels already
	eval.InMVScaleY = 1.0f;
	eval.InPreExposure = 1.0f;
	eval.InExposureScale = 1.0f;
	eval.InFrameTimeDeltaInMsec = data->timeDelta;
	NVSDK_NGX_Result r;
	if (rr) {
		NVSDK_NGX_D3D12_DLSSD_Eval_Params rrEval = {};
		rrEval.pInColor = eval.Feature.pInColor;
		rrEval.pInOutput = eval.Feature.pInOutput;
		rrEval.pInDepth = eval.pInDepth;
		rrEval.pInMotionVectors = eval.pInMotionVectors;
		rrEval.pInDiffuseAlbedo = d.diffuse.resource.Get();
		rrEval.pInSpecularAlbedo = d.specular.resource.Get();
		rrEval.pInNormals = d.normals.resource.Get();
		rrEval.pInRoughness = d.normals.resource.Get();  // packed: its w
		rrEval.InJitterOffsetX = eval.InJitterOffsetX;
		rrEval.InJitterOffsetY = eval.InJitterOffsetY;
		rrEval.InRenderSubrectDimensions = eval.InRenderSubrectDimensions;
		rrEval.InReset = eval.InReset;
		rrEval.InMVScaleX = 1.0f;
		rrEval.InMVScaleY = 1.0f;
		rrEval.InPreExposure = 1.0f;
		rrEval.InExposureScale = 1.0f;
		rrEval.InFrameTimeDeltaInMsec = data->timeDelta;
		rrEval.pInWorldToViewMatrix = const_cast<float*>(data->worldToView);
		rrEval.pInViewToClipMatrix = const_cast<float*>(data->viewToClip);
		r = NGX_D3D12_EVALUATE_DLSSD_EXT(list, d.feature, d.params, &rrEval);
	} else {
		r = NGX_D3D12_EVALUATE_DLSS_EXT(list, d.feature, d.params, &eval);
	}
	if (NVSDK_NGX_FAILED(r)) {
		char line[96];
		snprintf(line, sizeof(line), "DLSS evaluation failed (0x%08x)", static_cast<unsigned>(r));
		d.status = line;
		// what was recorded still runs (the pack); the buffer keeps the colour as it was
		g_render.Submit(g_unityQueue.load());
		return;
	}

	// the result into the output buffer (NGX set heaps and a root signature of its own)
	NrdTransition(barriers, &d.output, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
	NrdFlush(list, barriers, true);
	list->SetDescriptorHeaps(1, heaps);
	cpu = NrdDescriptors(1, &table);
	NrdView(cpu, &d.output, false);
	list->SetComputeRootSignature(d.unpack.root.Get());
	list->SetPipelineState(d.unpack.pso.Get());
	list->SetComputeRootDescriptorTable(0, table);
	list->SetComputeRootShaderResourceView(1, data->output->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(2, data->output->GetGPUVirtualAddress());
	UINT outConstants[4] = { data->outWidth, data->outHeight, 0, 0 };
	list->SetComputeRoot32BitConstants(3, 4, outConstants, 0);
	list->Dispatch((data->outWidth + 7) / 8, (data->outHeight + 7) / 8, 1);
	D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
	list->ResourceBarrier(1, &barrier);
	g_render.Submit(g_unityQueue.load());
	++d.runs;
	char line[128];
	const char* rrNote = rr ? ", Ray Reconstruction" : data->rayReconstruction && !d.rrAvailable ? ", Ray Reconstruction not available" : "";
	if (data->outWidth == data->width && data->outHeight == data->height) {
		snprintf(line, sizeof(line), "DLAA at %ux%u%s (%d frames)", data->width, data->height, rrNote, d.runs);
	} else {
		snprintf(line, sizeof(line), "DLSS %ux%u to %ux%u%s (%d frames)", data->width, data->height, data->outWidth, data->outHeight, rrNote, d.runs);
	}
	d.status = line;
}

// The pack / unpack kernels dropped (render thread, shader reload): made again from the shader files on next use
void DlssDropKernels() {
	g_dlss.pack = NrdKernel();
	g_dlss.unpack = NrdKernel();
	g_dlss.packGuides = NrdKernel();
}

}  // namespace

// What DLSS is doing (for the mod's status line)
NFX_EXPORT int NFX_Native_DlssStatus(char* buffer, int size) {
	return CopyOut(g_dlss.status, buffer, size);
}

// What DLSS can do on this GPU: -1 not known yet (NGX starts with the first DLSS frame), else 1 super resolution, 2 Ray
// Reconstruction
NFX_EXPORT int NFX_Native_DlssCaps() {
	if (!g_dlss.initialised) {
		return g_dlss.failed ? 0 : -1;
	}
	return 1 | (g_dlss.rrAvailable ? 2 : 0);
}
