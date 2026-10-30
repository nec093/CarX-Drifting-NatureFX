// NFXNative: NVIDIA Real-Time Denoisers (NRD, static library from %USERPROFILE%\NatureFX-Deps\NRD) run on Unity's
// queue without NRI, included at the end of NFXNative.cpp. SIGMA denoises the sun shadows (render event 5), REBLUR the
// ambient light or GI (render event 6: two REBLUR_DIFFUSE, the light bounced in and the share of the sky seen), and
// with an instance of its own the lamp shadows (event 6, mode 2: the blocked light and the light emissive stand ins add);
// REBLUR_SPECULAR the reflections (render event 7).
//
// Right after a trace: a pack shader turns Unity's guides (PrepareNrdGuide) and the trace's result into NRD's input
// textures, NRD's dispatches run (one shared root signature per instance: a root CBV, an SRV table, a UAV table, the
// static samplers), an unpack shader writes the denoised signal back into the trace's result buffer for Unpack. Every
// texture is the engine's own (Unity's resource states are never touched); NRD's descriptors live in a heap of their
// own, used as a ring, and their constants in a ring in an upload buffer.
#include "NRD.h"

namespace {

struct NrdEventData {
	ID3D12Resource* guide;    // PrepareNrdGuide: 2 float4 per pixel
	ID3D12Resource* surface;  // the trace's surfaces (sun: 2 float4 per pixel, ambient: 3)
	ID3D12Resource* result;   // the trace's result (sun: float2 per pixel, ambient: float4), denoised in place
	UINT32 width, height;
	UINT32 frameIndex;
	UINT32 reset;             // 1: history dropped (camera cut, F8)
	float tanSunRadius;
	float sunDirection[3];    // towards the sun
	float viewToClip[16], viewToClipPrev[16];    // column major, not jittered
	float worldToView[16], worldToViewPrev[16];  // column major
	float denoisingRange;
	float timeDelta;          // ms
	UINT32 mode;              // event 6: 1 GI (two signals), 0 the one bounce ambient light, 2 the lamp shadows
	UINT32 pad;
};
static_assert(sizeof(NrdEventData) == 328, "NrdEventData: NativeEngine.IssueNrd writes 328 bytes");

struct NrdTexture {
	ComPtr<ID3D12Resource> resource;
	D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
	DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
};

// A pack or unpack kernel: pack has t0..t2 root SRVs, a UAV table (uavs) and 4 constants; unpack an SRV table (srvs),
// the next t a root SRV, u0 a root UAV and 4 constants
struct NrdKernel {
	ComPtr<ID3D12RootSignature> root;
	ComPtr<ID3D12PipelineState> pso;
};

// One NRD instance with its pipelines, pools and the textures it reads and writes (in / out: per identifier)
struct NrdContext {
	const char* name = "";
	nrd::Instance* instance = nullptr;
	bool failed = false;
	std::string status = "not started";
	ComPtr<ID3D12RootSignature> root;
	std::vector<ComPtr<ID3D12PipelineState>> pipelines;
	UINT srvMax = 0, uavMax = 0;
	UINT width = 0, height = 0;
	std::vector<NrdTexture> permanent, transient;
	NrdTexture normalRoughness, viewZ, motion;
	NrdTexture in[2], out[2];
	NrdKernel pack, unpack;
	int runs = 0;
};
NrdContext g_nrdSun, g_nrdAmbient, g_nrdLocal, g_nrdReflection;

// shared by both: the descriptor ring, the constant ring, textures replaced on a resize
constexpr UINT kNrdDescriptors = 16384;
ComPtr<ID3D12DescriptorHeap> g_nrdHeap;
UINT g_nrdHeapStep = 0, g_nrdHeapNext = 0;
constexpr UINT64 kNrdConstantBytes = 4ull << 20;
ComPtr<ID3D12Resource> g_nrdConstants;
UINT8* g_nrdConstantsMapped = nullptr;
UINT64 g_nrdConstantsNext = 0;
std::vector<std::pair<ComPtr<ID3D12Resource>, UINT64>> g_nrdRetired;

DXGI_FORMAT NrdFormat(nrd::Format f) {
	switch (f) {
	case nrd::Format::R8_UNORM: return DXGI_FORMAT_R8_UNORM;
	case nrd::Format::R8_SNORM: return DXGI_FORMAT_R8_SNORM;
	case nrd::Format::R8_UINT: return DXGI_FORMAT_R8_UINT;
	case nrd::Format::R8_SINT: return DXGI_FORMAT_R8_SINT;
	case nrd::Format::RG8_UNORM: return DXGI_FORMAT_R8G8_UNORM;
	case nrd::Format::RG8_SNORM: return DXGI_FORMAT_R8G8_SNORM;
	case nrd::Format::RG8_UINT: return DXGI_FORMAT_R8G8_UINT;
	case nrd::Format::RG8_SINT: return DXGI_FORMAT_R8G8_SINT;
	case nrd::Format::RGBA8_UNORM: return DXGI_FORMAT_R8G8B8A8_UNORM;
	case nrd::Format::RGBA8_SNORM: return DXGI_FORMAT_R8G8B8A8_SNORM;
	case nrd::Format::RGBA8_UINT: return DXGI_FORMAT_R8G8B8A8_UINT;
	case nrd::Format::RGBA8_SINT: return DXGI_FORMAT_R8G8B8A8_SINT;
	case nrd::Format::RGBA8_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
	case nrd::Format::R16_UNORM: return DXGI_FORMAT_R16_UNORM;
	case nrd::Format::R16_SNORM: return DXGI_FORMAT_R16_SNORM;
	case nrd::Format::R16_UINT: return DXGI_FORMAT_R16_UINT;
	case nrd::Format::R16_SINT: return DXGI_FORMAT_R16_SINT;
	case nrd::Format::R16_SFLOAT: return DXGI_FORMAT_R16_FLOAT;
	case nrd::Format::RG16_UNORM: return DXGI_FORMAT_R16G16_UNORM;
	case nrd::Format::RG16_SNORM: return DXGI_FORMAT_R16G16_SNORM;
	case nrd::Format::RG16_UINT: return DXGI_FORMAT_R16G16_UINT;
	case nrd::Format::RG16_SINT: return DXGI_FORMAT_R16G16_SINT;
	case nrd::Format::RG16_SFLOAT: return DXGI_FORMAT_R16G16_FLOAT;
	case nrd::Format::RGBA16_UNORM: return DXGI_FORMAT_R16G16B16A16_UNORM;
	case nrd::Format::RGBA16_SNORM: return DXGI_FORMAT_R16G16B16A16_SNORM;
	case nrd::Format::RGBA16_UINT: return DXGI_FORMAT_R16G16B16A16_UINT;
	case nrd::Format::RGBA16_SINT: return DXGI_FORMAT_R16G16B16A16_SINT;
	case nrd::Format::RGBA16_SFLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
	case nrd::Format::R32_UINT: return DXGI_FORMAT_R32_UINT;
	case nrd::Format::R32_SINT: return DXGI_FORMAT_R32_SINT;
	case nrd::Format::R32_SFLOAT: return DXGI_FORMAT_R32_FLOAT;
	case nrd::Format::RG32_UINT: return DXGI_FORMAT_R32G32_UINT;
	case nrd::Format::RG32_SINT: return DXGI_FORMAT_R32G32_SINT;
	case nrd::Format::RG32_SFLOAT: return DXGI_FORMAT_R32G32_FLOAT;
	case nrd::Format::RGB32_UINT: return DXGI_FORMAT_R32G32B32_UINT;
	case nrd::Format::RGB32_SINT: return DXGI_FORMAT_R32G32B32_SINT;
	case nrd::Format::RGB32_SFLOAT: return DXGI_FORMAT_R32G32B32_FLOAT;
	case nrd::Format::RGBA32_UINT: return DXGI_FORMAT_R32G32B32A32_UINT;
	case nrd::Format::RGBA32_SINT: return DXGI_FORMAT_R32G32B32A32_SINT;
	case nrd::Format::RGBA32_SFLOAT: return DXGI_FORMAT_R32G32B32A32_FLOAT;
	case nrd::Format::R10_G10_B10_A2_UNORM: return DXGI_FORMAT_R10G10B10A2_UNORM;
	case nrd::Format::R10_G10_B10_A2_UINT: return DXGI_FORMAT_R10G10B10A2_UINT;
	case nrd::Format::R11_G11_B10_UFLOAT: return DXGI_FORMAT_R11G11B10_FLOAT;
	case nrd::Format::R9_G9_B9_E5_UFLOAT: return DXGI_FORMAT_R9G9B9E5_SHAREDEXP;
	default: return DXGI_FORMAT_UNKNOWN;
	}
}

bool NrdCreateTexture(NrdTexture& t, DXGI_FORMAT format, UINT width, UINT height) {
	if (t.resource) {
		g_nrdRetired.emplace_back(t.resource, g_render.submitted);
		t.resource.Reset();
	}
	D3D12_HEAP_PROPERTIES props = {};
	props.Type = D3D12_HEAP_TYPE_DEFAULT;
	D3D12_RESOURCE_DESC desc = {};
	desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
	desc.Width = width > 0 ? width : 1;
	desc.Height = height > 0 ? height : 1;
	desc.DepthOrArraySize = 1;
	desc.MipLevels = 1;
	desc.Format = format;
	desc.SampleDesc.Count = 1;
	desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
	t.format = format;
	t.state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
	return SUCCEEDED(g_device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &desc, t.state, nullptr, IID_PPV_ARGS(&t.resource)));
}

bool NrdSerialize(const D3D12_ROOT_SIGNATURE_DESC& desc, ComPtr<ID3D12RootSignature>& root) {
	ComPtr<ID3DBlob> blob, error;
	return SUCCEEDED(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) &&
	       SUCCEEDED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root)));
}

bool NrdPso(ID3D12RootSignature* root, const void* dxil, size_t size, ComPtr<ID3D12PipelineState>& pso) {
	D3D12_COMPUTE_PIPELINE_STATE_DESC d = {};
	d.pRootSignature = root;
	d.CS = { dxil, size };
	return SUCCEEDED(g_device->CreateComputePipelineState(&d, IID_PPV_ARGS(&pso)));
}

bool NrdPackKernel(NrdKernel& k, UINT uavs, const void* dxil, size_t size) {
	D3D12_DESCRIPTOR_RANGE range = { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, uavs, 0, 0, 0 };
	D3D12_ROOT_PARAMETER p[5] = {};
	for (int i = 0; i < 3; ++i) {
		p[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		p[i].Descriptor.ShaderRegister = i;
	}
	p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
	p[3].DescriptorTable = { 1, &range };
	p[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
	p[4].Constants.Num32BitValues = 4;
	D3D12_ROOT_SIGNATURE_DESC d = { 5, p, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
	return NrdSerialize(d, k.root) && NrdPso(k.root.Get(), dxil, size, k.pso);
}

bool NrdUnpackKernel(NrdKernel& k, UINT srvs, const void* dxil, size_t size) {
	D3D12_DESCRIPTOR_RANGE range = { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, srvs, 0, 0, 0 };
	D3D12_ROOT_PARAMETER p[4] = {};
	p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
	p[0].DescriptorTable = { 1, &range };
	p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
	p[1].Descriptor.ShaderRegister = srvs;
	p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
	p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
	p[3].Constants.Num32BitValues = 4;
	D3D12_ROOT_SIGNATURE_DESC d = { 4, p, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
	return NrdSerialize(d, k.root) && NrdPso(k.root.Get(), dxil, size, k.pso);
}

// The shared rings (render thread, once)
bool NrdShared() {
	if (g_nrdHeap) {
		return true;
	}
	D3D12_DESCRIPTOR_HEAP_DESC hd = {};
	hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
	hd.NumDescriptors = kNrdDescriptors;
	hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
	ComPtr<ID3D12DescriptorHeap> heap;
	if (FAILED(g_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap)))) {
		return false;
	}
	g_nrdConstants = CreateBuffer(kNrdConstantBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	void* mapped = nullptr;
	if (!g_nrdConstants || FAILED(g_nrdConstants->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return false;
	}
	g_nrdConstantsMapped = static_cast<UINT8*>(mapped);
	g_nrdHeapStep = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
	g_nrdHeap = heap;
	return true;
}

// The instance with its denoisers (identifier = index), its pipelines and the shared root signature (render thread,
// once). Fails for good: the status says why.
bool NrdCreate(NrdContext& c, const nrd::Denoiser* denoisers, UINT count) {
	if (c.instance) {
		return true;
	}
	if (c.failed) {
		return false;
	}
	c.failed = true;  // until everything below is made
	if (!NrdShared()) {
		c.status = "NRD descriptor heap or constant ring could not be made";
		return false;
	}
	std::vector<nrd::DenoiserDesc> descs(count);
	for (UINT i = 0; i < count; ++i) {
		descs[i] = { i, denoisers[i] };
	}
	nrd::InstanceCreationDesc creation = {};
	creation.denoisers = descs.data();
	creation.denoisersNum = count;
	if (nrd::CreateInstance(creation, c.instance) != nrd::Result::SUCCESS) {
		c.instance = nullptr;
		c.status = "NRD instance could not be made";
		return false;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*c.instance);
	c.srvMax = desc->descriptorPoolDesc.perSetTexturesMaxNum;
	c.uavMax = desc->descriptorPoolDesc.perSetStorageTexturesMaxNum;
	D3D12_DESCRIPTOR_RANGE ranges[2] = {};
	ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
	ranges[0].NumDescriptors = c.srvMax > 0 ? c.srvMax : 1;
	ranges[0].BaseShaderRegister = desc->resourcesBaseRegisterIndex;
	ranges[0].RegisterSpace = desc->resourcesSpaceIndex;
	ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
	ranges[1].NumDescriptors = c.uavMax > 0 ? c.uavMax : 1;
	ranges[1].BaseShaderRegister = desc->resourcesBaseRegisterIndex;
	ranges[1].RegisterSpace = desc->resourcesSpaceIndex;
	D3D12_ROOT_PARAMETER params[3] = {};
	params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
	params[0].Descriptor.ShaderRegister = desc->constantBufferRegisterIndex;
	params[0].Descriptor.RegisterSpace = desc->constantBufferAndSamplersSpaceIndex;
	params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
	params[1].DescriptorTable = { 1, &ranges[0] };
	params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
	params[2].DescriptorTable = { 1, &ranges[1] };
	std::vector<D3D12_STATIC_SAMPLER_DESC> samplers(desc->samplersNum);
	for (UINT i = 0; i < desc->samplersNum; ++i) {
		auto& s = samplers[i];
		s.Filter = desc->samplers[i] == nrd::Sampler::LINEAR_CLAMP ? D3D12_FILTER_MIN_MAG_MIP_LINEAR : D3D12_FILTER_MIN_MAG_MIP_POINT;
		s.AddressU = s.AddressV = s.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
		s.MaxLOD = D3D12_FLOAT32_MAX;
		s.ShaderRegister = desc->samplersBaseRegisterIndex + i;
		s.RegisterSpace = desc->constantBufferAndSamplersSpaceIndex;
		s.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
	}
	D3D12_ROOT_SIGNATURE_DESC rootDesc = { 3, params, static_cast<UINT>(samplers.size()), samplers.data(), D3D12_ROOT_SIGNATURE_FLAG_NONE };
	if (!NrdSerialize(rootDesc, c.root)) {
		c.status = "NRD root signature could not be made";
		return false;
	}
	for (UINT i = 0; i < desc->pipelinesNum; ++i) {
		const auto& p = desc->pipelines[i];
		ComPtr<ID3D12PipelineState> state;
		if (p.computeShaderDXIL.bytecode == nullptr || !NrdPso(c.root.Get(), p.computeShaderDXIL.bytecode, static_cast<size_t>(p.computeShaderDXIL.size), state)) {
			char line[128];
			snprintf(line, sizeof(line), "NRD pipeline %u (%s) could not be made", i, p.shaderIdentifier);
			c.status = line;
			return false;
		}
		c.pipelines.push_back(state);
	}
	c.failed = false;
	c.status = "ready";
	return true;
}

// The pools and the input / output textures at this size (render thread); inFormat / outFormat: the signals'
bool NrdResize(NrdContext& c, UINT width, UINT height, DXGI_FORMAT inFormat, DXGI_FORMAT outFormat, int signals) {
	if (c.width == width && c.height == height && c.out[0].resource) {
		return true;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*c.instance);
	auto make = [&](std::vector<NrdTexture>& pool, const nrd::TextureDesc* descs, UINT count) {
		pool.resize(count);
		for (UINT i = 0; i < count; ++i) {
			UINT f = descs[i].downsampleFactor > 0 ? descs[i].downsampleFactor : 1;
			if (!NrdCreateTexture(pool[i], NrdFormat(descs[i].format), (width + f - 1) / f, (height + f - 1) / f)) {
				return false;
			}
		}
		return true;
	};
	bool ok = make(c.permanent, desc->permanentPool, desc->permanentPoolSize) && make(c.transient, desc->transientPool, desc->transientPoolSize) &&
	          NrdCreateTexture(c.normalRoughness, DXGI_FORMAT_R10G10B10A2_UNORM, width, height) &&
	          NrdCreateTexture(c.viewZ, DXGI_FORMAT_R32_FLOAT, width, height) &&
	          NrdCreateTexture(c.motion, DXGI_FORMAT_R16G16_FLOAT, width, height);
	for (int i = 0; i < signals && ok; ++i) {
		ok = NrdCreateTexture(c.in[i], inFormat, width, height) && NrdCreateTexture(c.out[i], outFormat, width, height);
	}
	if (!ok) {
		c.status = "NRD textures could not be made";
		c.failed = true;
		return false;
	}
	c.width = width;
	c.height = height;
	return true;
}

NrdTexture* NrdResource(NrdContext& c, nrd::Identifier id, const nrd::ResourceDesc& r) {
	UINT slot = id < 2 ? id : 0;
	switch (r.type) {
	case nrd::ResourceType::IN_MV: return &c.motion;
	case nrd::ResourceType::IN_NORMAL_ROUGHNESS: return &c.normalRoughness;
	case nrd::ResourceType::IN_VIEWZ: return &c.viewZ;
	case nrd::ResourceType::IN_PENUMBRA: return &c.in[0];
	case nrd::ResourceType::OUT_SHADOW_TRANSLUCENCY: return &c.out[0];
	case nrd::ResourceType::IN_DIFF_RADIANCE_HITDIST: return &c.in[slot];
	case nrd::ResourceType::OUT_DIFF_RADIANCE_HITDIST: return &c.out[slot];
	case nrd::ResourceType::IN_SPEC_RADIANCE_HITDIST: return &c.in[0];
	case nrd::ResourceType::OUT_SPEC_RADIANCE_HITDIST: return &c.out[0];
	case nrd::ResourceType::TRANSIENT_POOL: return r.indexInPool < c.transient.size() ? &c.transient[r.indexInPool] : nullptr;
	case nrd::ResourceType::PERMANENT_POOL: return r.indexInPool < c.permanent.size() ? &c.permanent[r.indexInPool] : nullptr;
	default: return nullptr;
	}
}

// count contiguous descriptors from the ring (wrapping to its start when they would not fit)
D3D12_CPU_DESCRIPTOR_HANDLE NrdDescriptors(UINT count, D3D12_GPU_DESCRIPTOR_HANDLE* gpu) {
	if (g_nrdHeapNext + count > kNrdDescriptors) {
		g_nrdHeapNext = 0;
	}
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_nrdHeap->GetCPUDescriptorHandleForHeapStart();
	cpu.ptr += static_cast<SIZE_T>(g_nrdHeapNext) * g_nrdHeapStep;
	*gpu = g_nrdHeap->GetGPUDescriptorHandleForHeapStart();
	gpu->ptr += static_cast<UINT64>(g_nrdHeapNext) * g_nrdHeapStep;
	g_nrdHeapNext += count;
	return cpu;
}

D3D12_CPU_DESCRIPTOR_HANDLE NrdAt(D3D12_CPU_DESCRIPTOR_HANDLE base, UINT i) {
	base.ptr += static_cast<SIZE_T>(i) * g_nrdHeapStep;
	return base;
}

void NrdView(D3D12_CPU_DESCRIPTOR_HANDLE handle, NrdTexture* t, bool write) {
	if (write) {
		D3D12_UNORDERED_ACCESS_VIEW_DESC d = {};
		d.Format = t != nullptr ? t->format : DXGI_FORMAT_R16_FLOAT;
		d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
		g_device->CreateUnorderedAccessView(t != nullptr ? t->resource.Get() : nullptr, nullptr, &d, handle);
	} else {
		D3D12_SHADER_RESOURCE_VIEW_DESC d = {};
		d.Format = t != nullptr ? t->format : DXGI_FORMAT_R16_FLOAT;
		d.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
		d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
		d.Texture2D.MipLevels = 1;
		g_device->CreateShaderResourceView(t != nullptr ? t->resource.Get() : nullptr, &d, handle);
	}
}

void NrdTransition(std::vector<D3D12_RESOURCE_BARRIER>& barriers, NrdTexture* t, D3D12_RESOURCE_STATES state) {
	if (t != nullptr && t->resource && t->state != state) {
		barriers.push_back(Transition(t->resource.Get(), t->state, state));
		t->state = state;
	}
}

D3D12_GPU_VIRTUAL_ADDRESS NrdConstants(const void* data, UINT size) {
	UINT64 aligned = (static_cast<UINT64>(size) + 255) & ~255ull;
	if (g_nrdConstantsNext + aligned > kNrdConstantBytes) {
		g_nrdConstantsNext = 0;
	}
	memcpy(g_nrdConstantsMapped + g_nrdConstantsNext, data, size);
	D3D12_GPU_VIRTUAL_ADDRESS address = g_nrdConstants->GetGPUVirtualAddress() + g_nrdConstantsNext;
	g_nrdConstantsNext += aligned;
	return address;
}

void NrdFlush(ID3D12GraphicsCommandList4* list, std::vector<D3D12_RESOURCE_BARRIER>& barriers, bool uav) {
	if (uav) {
		barriers.push_back(UavBarrier(nullptr));
	}
	if (!barriers.empty()) {
		list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
		barriers.clear();
	}
}

// The frees of earlier resizes, then the list to record into (null: skip this time)
ID3D12GraphicsCommandList4* NrdBegin(const NrdEventData* data) {
	if (g_dead || g_unityQueue.load() == nullptr || data == nullptr || data->guide == nullptr || data->surface == nullptr ||
	    data->result == nullptr || data->width == 0 || data->height == 0 || !g_render.Create()) {
		return nullptr;
	}
	UINT64 completed = g_render.fence->GetCompletedValue();
	for (size_t i = 0; i < g_nrdRetired.size();) {
		if (g_nrdRetired[i].second <= completed) {
			g_nrdRetired.erase(g_nrdRetired.begin() + i);
		} else {
			++i;
		}
	}
	return g_render.Begin();
}

// Packs the inputs: the guides, normal / view Z / motion and the context's signal inputs into a UAV table
void NrdPack(ID3D12GraphicsCommandList4* list, NrdContext& c, const NrdEventData* data, int signals, const UINT constants[4]) {
	std::vector<D3D12_RESOURCE_BARRIER> barriers;
	NrdTexture* outputs[5] = { &c.normalRoughness, &c.viewZ, &c.motion, &c.in[0], &c.in[1] };
	UINT count = 3 + signals;
	for (UINT i = 0; i < count; ++i) {
		NrdTransition(barriers, outputs[i], D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	}
	NrdFlush(list, barriers, false);
	D3D12_GPU_DESCRIPTOR_HANDLE table;
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = NrdDescriptors(count, &table);
	for (UINT i = 0; i < count; ++i) {
		NrdView(NrdAt(cpu, i), outputs[i], true);
	}
	list->SetComputeRootSignature(c.pack.root.Get());
	list->SetPipelineState(c.pack.pso.Get());
	list->SetComputeRootShaderResourceView(0, data->guide->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(1, data->surface->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(2, data->result->GetGPUVirtualAddress());
	list->SetComputeRootDescriptorTable(3, table);
	list->SetComputeRoot32BitConstants(4, 4, constants, 0);
	list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
}

// The context's dispatches for these identifiers, after the common settings and each identifier's own
bool NrdRun(ID3D12GraphicsCommandList4* list, NrdContext& c, const NrdEventData* data, bool resized, const nrd::Identifier* ids,
            const void* const* settings, UINT count, uint32_t* dispatched) {
	nrd::CommonSettings common = {};
	memcpy(common.viewToClipMatrix, data->viewToClip, sizeof(common.viewToClipMatrix));
	memcpy(common.viewToClipMatrixPrev, data->viewToClipPrev, sizeof(common.viewToClipMatrixPrev));
	memcpy(common.worldToViewMatrix, data->worldToView, sizeof(common.worldToViewMatrix));
	memcpy(common.worldToViewMatrixPrev, data->worldToViewPrev, sizeof(common.worldToViewMatrixPrev));
	common.motionVectorScale[0] = 1.0f;
	common.motionVectorScale[1] = 1.0f;
	common.motionVectorScale[2] = 0.0f;
	common.resourceSize[0] = common.resourceSizePrev[0] = common.rectSize[0] = common.rectSizePrev[0] = static_cast<uint16_t>(data->width);
	common.resourceSize[1] = common.resourceSizePrev[1] = common.rectSize[1] = common.rectSizePrev[1] = static_cast<uint16_t>(data->height);
	common.denoisingRange = data->denoisingRange;
	common.timeDeltaBetweenFrames = data->timeDelta;
	common.frameIndex = data->frameIndex;
	common.accumulationMode = data->reset || resized || c.runs == 0 ? nrd::AccumulationMode::CLEAR_AND_RESTART : nrd::AccumulationMode::CONTINUE;
	if (nrd::SetCommonSettings(*c.instance, common) != nrd::Result::SUCCESS) {
		c.status = "NRD common settings were refused";
		return false;
	}
	for (UINT i = 0; i < count; ++i) {
		if (nrd::SetDenoiserSettings(*c.instance, ids[i], settings[i]) != nrd::Result::SUCCESS) {
			c.status = "NRD denoiser settings were refused";
			return false;
		}
	}
	const nrd::DispatchDesc* dispatches = nullptr;
	uint32_t dispatchCount = 0;
	if (nrd::GetComputeDispatches(*c.instance, ids, count, dispatches, dispatchCount) != nrd::Result::SUCCESS) {
		c.status = "NRD dispatches could not be had";
		return false;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*c.instance);
	std::vector<D3D12_RESOURCE_BARRIER> barriers;
	D3D12_GPU_VIRTUAL_ADDRESS constants = 0;
	list->SetComputeRootSignature(c.root.Get());
	for (uint32_t d = 0; d < dispatchCount; ++d) {
		const auto& dispatch = dispatches[d];
		const auto& pipeline = desc->pipelines[dispatch.pipelineIndex];
		// the dispatch's resources, range by range: SRVs into one table, UAVs into the other (unused slots null)
		D3D12_GPU_DESCRIPTOR_HANDLE srvTable, uavTable;
		D3D12_CPU_DESCRIPTOR_HANDLE srvCpu = NrdDescriptors(c.srvMax > 0 ? c.srvMax : 1, &srvTable);
		D3D12_CPU_DESCRIPTOR_HANDLE uavCpu = NrdDescriptors(c.uavMax > 0 ? c.uavMax : 1, &uavTable);
		UINT srvs = 0, uavs = 0, next = 0;
		for (uint32_t r = 0; r < pipeline.resourceRangesNum; ++r) {
			bool write = pipeline.resourceRanges[r].descriptorType == nrd::DescriptorType::STORAGE_TEXTURE;
			for (uint32_t k = 0; k < pipeline.resourceRanges[r].descriptorsNum && next < dispatch.resourcesNum; ++k, ++next) {
				NrdTexture* t = NrdResource(c, dispatch.identifier, dispatch.resources[next]);
				NrdView(NrdAt(write ? uavCpu : srvCpu, write ? uavs++ : srvs++), t, write);
				NrdTransition(barriers, t, write ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
			}
		}
		for (UINT k = srvs; k < c.srvMax; ++k) {
			NrdView(NrdAt(srvCpu, k), nullptr, false);
		}
		for (UINT k = uavs; k < c.uavMax; ++k) {
			NrdView(NrdAt(uavCpu, k), nullptr, true);
		}
		NrdFlush(list, barriers, true);
		if (dispatch.constantBufferDataSize > 0 && (!dispatch.constantBufferDataMatchesPreviousDispatch || constants == 0)) {
			constants = NrdConstants(dispatch.constantBufferData, dispatch.constantBufferDataSize);
		}
		list->SetPipelineState(c.pipelines[dispatch.pipelineIndex].Get());
		list->SetComputeRootConstantBufferView(0, constants);
		list->SetComputeRootDescriptorTable(1, srvTable);
		list->SetComputeRootDescriptorTable(2, uavTable);
		list->Dispatch(dispatch.gridWidth, dispatch.gridHeight, 1);
	}
	*dispatched = dispatchCount;
	return true;
}

// Writes the context's outputs back into the trace's result: an SRV table of the outputs, the surfaces, the result
void NrdUnpack(ID3D12GraphicsCommandList4* list, NrdContext& c, const NrdEventData* data, int signals, const UINT constants[4]) {
	std::vector<D3D12_RESOURCE_BARRIER> barriers;
	for (int i = 0; i < signals; ++i) {
		NrdTransition(barriers, &c.out[i], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
	}
	NrdFlush(list, barriers, true);
	D3D12_GPU_DESCRIPTOR_HANDLE table;
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = NrdDescriptors(static_cast<UINT>(signals), &table);
	for (int i = 0; i < signals; ++i) {
		NrdView(NrdAt(cpu, static_cast<UINT>(i)), &c.out[i], false);
	}
	list->SetComputeRootSignature(c.unpack.root.Get());
	list->SetPipelineState(c.unpack.pso.Get());
	list->SetComputeRootDescriptorTable(0, table);
	list->SetComputeRootShaderResourceView(1, data->surface->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(2, data->result->GetGPUVirtualAddress());
	list->SetComputeRoot32BitConstants(3, 4, constants, 0);
	list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
	D3D12_RESOURCE_BARRIER done = UavBarrier(nullptr);
	list->ResourceBarrier(1, &done);
}

void NrdStatusLine(NrdContext& c, const char* what, const NrdEventData* data, uint32_t dispatched) {
	if (++c.runs == 1 || c.status == "ready") {
		char line[128];
		snprintf(line, sizeof(line), "%s, %ux%u, %u dispatches", what, data->width, data->height, dispatched);
		c.status = line;
	}
}

// Render event 5: SIGMA over the sun trace that ran just before
void NrdSigma(const NrdEventData* data) {
	static const nrd::Denoiser kSigma[] = { nrd::Denoiser::SIGMA_SHADOW };
	auto& c = g_nrdSun;
	if (!c.pack.pso && !c.failed &&
	    (!NrdPackKernel(c.pack, 4, g_NrdSigmaPack, sizeof(g_NrdSigmaPack)) || !NrdUnpackKernel(c.unpack, 1, g_NrdSigmaUnpack, sizeof(g_NrdSigmaUnpack)))) {
		c.failed = true;
		c.status = "SIGMA pack / unpack pipelines could not be made";
	}
	auto* list = c.pack.pso ? NrdBegin(data) : nullptr;
	if (list == nullptr) {
		return;
	}
	bool resized = c.width != data->width || c.height != data->height;
	if (!NrdCreate(c, kSigma, 1) || !NrdResize(c, data->width, data->height, DXGI_FORMAT_R16_FLOAT, DXGI_FORMAT_R16_FLOAT, 1)) {
		list->Close();
		return;
	}
	ID3D12DescriptorHeap* heaps[] = { g_nrdHeap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	UINT packConstants[4] = { data->width, data->height, 0, 0 };
	memcpy(&packConstants[2], &data->tanSunRadius, 4);
	NrdPack(list, c, data, 1, packConstants);
	nrd::SigmaSettings sigma = {};
	memcpy(sigma.lightDirection, data->sunDirection, sizeof(sigma.lightDirection));
	nrd::Identifier id = 0;
	const void* settings[] = { &sigma };
	uint32_t dispatched = 0;
	if (!NrdRun(list, c, data, resized, &id, settings, 1, &dispatched)) {
		list->Close();
		return;
	}
	UINT unpackConstants[4] = { data->width, data->height, 0, 0 };
	NrdUnpack(list, c, data, 1, unpackConstants);
	g_render.Submit(g_unityQueue.load());
	NrdStatusLine(c, "SIGMA on the sun shadows", data, dispatched);
}

// Render event 6: REBLUR over the ambient light, GI or lamp shadow trace that ran just before (GI and lamps: two
// signals)
void NrdReblur(const NrdEventData* data) {
	static const nrd::Denoiser kReblur[] = { nrd::Denoiser::REBLUR_DIFFUSE, nrd::Denoiser::REBLUR_DIFFUSE };
	auto& c = data != nullptr && data->mode == 2 ? g_nrdLocal : g_nrdAmbient;
	if (!c.pack.pso && !c.failed &&
	    (!NrdPackKernel(c.pack, 5, g_NrdReblurPack, sizeof(g_NrdReblurPack)) || !NrdUnpackKernel(c.unpack, 2, g_NrdReblurUnpack, sizeof(g_NrdReblurUnpack)))) {
		c.failed = true;
		c.status = "REBLUR pack / unpack pipelines could not be made";
	}
	auto* list = c.pack.pso ? NrdBegin(data) : nullptr;
	if (list == nullptr) {
		return;
	}
	bool resized = c.width != data->width || c.height != data->height;
	if (!NrdCreate(c, kReblur, 2) || !NrdResize(c, data->width, data->height, DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R16G16B16A16_FLOAT, 2)) {
		list->Close();
		return;
	}
	ID3D12DescriptorHeap* heaps[] = { g_nrdHeap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	bool two = data->mode == 1 || data->mode == 2;
	UINT constants[4] = { data->width, data->height, data->mode, 0 };
	NrdPack(list, c, data, 2, constants);
	// one ray a pixel (GI, ambient light) or two lights (lamps): the fast history (6 frames by default), which the
	// long one is clamped to, followed the noise and the image flickered on a moving car. A longer fast history and
	// a looser clamp; the long history and the anti-lag stay REBLUR's.
	nrd::ReblurSettings reblur = {};
	reblur.maxFastAccumulatedFrameNum = 10;
	reblur.fastHistoryClampingSigmaScale = 2.5f;
	nrd::Identifier ids[2] = { 0, 1 };
	const void* settings[] = { &reblur, &reblur };
	uint32_t dispatched = 0;
	if (!NrdRun(list, c, data, resized, ids, settings, two ? 2 : 1, &dispatched)) {
		list->Close();
		return;
	}
	NrdUnpack(list, c, data, 2, constants);
	g_render.Submit(g_unityQueue.load());
	NrdStatusLine(c, data->mode == 2 ? "REBLUR on the lamp shadows" : data->mode == 1 ? "REBLUR on the GI (light and sky)" : "REBLUR on the ambient light",
	              data, dispatched);
}

// Render event 7: REBLUR_SPECULAR over the reflection trace that ran just before
void NrdReflection(const NrdEventData* data) {
	static const nrd::Denoiser kSpecular[] = { nrd::Denoiser::REBLUR_SPECULAR };
	auto& c = g_nrdReflection;
	if (!c.pack.pso && !c.failed &&
	    (!NrdPackKernel(c.pack, 4, g_NrdSpecPack, sizeof(g_NrdSpecPack)) || !NrdUnpackKernel(c.unpack, 1, g_NrdSpecUnpack, sizeof(g_NrdSpecUnpack)))) {
		c.failed = true;
		c.status = "REBLUR specular pack / unpack pipelines could not be made";
	}
	auto* list = c.pack.pso ? NrdBegin(data) : nullptr;
	if (list == nullptr) {
		return;
	}
	bool resized = c.width != data->width || c.height != data->height;
	if (!NrdCreate(c, kSpecular, 1) || !NrdResize(c, data->width, data->height, DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R16G16B16A16_FLOAT, 1)) {
		list->Close();
		return;
	}
	ID3D12DescriptorHeap* heaps[] = { g_nrdHeap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	UINT constants[4] = { data->width, data->height, 0, 0 };
	NrdPack(list, c, data, 1, constants);
	// what a reflection shows changes as fast as the car moves: REBLUR's defaults (30 frames, stabilized over 63)
	// ghosted and lagged behind. A short history, quicker to throw away (anti-lag), and the smoothest surfaces
	// (car paint's varnish, mirrors, wet roads) responsive down to a couple of frames.
	nrd::ReblurSettings reblur = {};
	reblur.maxAccumulatedFrameNum = 12;
	reblur.maxFastAccumulatedFrameNum = 2;
	reblur.maxStabilizedFrameNum = 6;
	reblur.antilagSettings.luminanceSensitivity = 2.0f;
	reblur.responsiveAccumulationSettings.roughnessThreshold = 0.2f;
	reblur.responsiveAccumulationSettings.minAccumulatedFrameNum = 2;
	nrd::Identifier id = 0;
	const void* settings[] = { &reblur };
	uint32_t dispatched = 0;
	if (!NrdRun(list, c, data, resized, &id, settings, 1, &dispatched)) {
		list->Close();
		return;
	}
	NrdUnpack(list, c, data, 1, constants);
	g_render.Submit(g_unityQueue.load());
	NrdStatusLine(c, "REBLUR on the reflections", data, dispatched);
}

}  // namespace

// What the NRD stages are doing (for the mod's status lines): the sun's, then the ambient light's
NFX_EXPORT int NFX_Native_NrdStatus(char* buffer, int size) {
	return CopyOut("sun: " + g_nrdSun.status + "; ambient: " + g_nrdAmbient.status + "; lamps: " + g_nrdLocal.status +
	               "; reflections: " + g_nrdReflection.status, buffer, size);
}
