// NFXNative: NVIDIA Real-Time Denoisers (NRD, static library from %USERPROFILE%\NatureFX-Deps\NRD) run on Unity's
// queue without NRI, included at the end of NFXNative.cpp. Stage 1: SIGMA denoises the sun shadows.
//
// Render event 5, right after a sun trace: NrdSigmaPack.hlsl turns Unity's guides (PrepareNrdGuide) and the trace's
// result into NRD's input textures, NRD's dispatches run (one shared root signature: a root CBV, an SRV table, a UAV
// table, the static samplers), NrdSigmaUnpack.hlsl writes the denoised shadow back into the trace's result buffer for
// Unpack. Every texture is the engine's own (Unity's resource states are never touched); NRD's descriptors live in a
// heap of their own, used as a ring.
#include "NRD.h"

namespace {

struct NrdEventData {
	ID3D12Resource* guide;    // PrepareNrdGuide: 2 float4 per pixel
	ID3D12Resource* surface;  // the sun trace's surfaces (2 float4 per pixel)
	ID3D12Resource* result;   // the sun trace's result (float2 per pixel), denoised in place
	UINT32 width, height;
	UINT32 frameIndex;
	UINT32 reset;             // 1: history dropped (camera cut, F8)
	float tanSunRadius;
	float sunDirection[3];    // towards the sun
	float viewToClip[16], viewToClipPrev[16];    // column major, not jittered
	float worldToView[16], worldToViewPrev[16];  // column major
	float denoisingRange;
	float timeDelta;          // ms
};

struct NrdTexture {
	ComPtr<ID3D12Resource> resource;
	D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
	DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
};

struct NrdRunner {
	nrd::Instance* instance = nullptr;
	bool failed = false;
	std::string status = "not started";
	ComPtr<ID3D12RootSignature> root;
	std::vector<ComPtr<ID3D12PipelineState>> pipelines;
	UINT srvMax = 0, uavMax = 0;
	// pack: t0 t1 t2 root, u0..u3 table, b0 4 constants; unpack: t0 table, t1 root, u0 root, b0 2 constants
	ComPtr<ID3D12RootSignature> packRoot, unpackRoot;
	ComPtr<ID3D12PipelineState> packPso, unpackPso;
	UINT width = 0, height = 0;
	std::vector<NrdTexture> permanent, transient;
	NrdTexture normalRoughness, viewZ, motion, penumbra, shadow;
	// shader visible ring of descriptors, and a ring of constant data in an upload buffer
	static constexpr UINT kDescriptors = 16384;
	ComPtr<ID3D12DescriptorHeap> heap;
	UINT heapStep = 0, heapNext = 0;
	static constexpr UINT64 kConstantBytes = 4ull << 20;
	ComPtr<ID3D12Resource> constants;
	UINT8* constantsMapped = nullptr;
	UINT64 constantsNext = 0;
	D3D12_GPU_VIRTUAL_ADDRESS lastConstants = 0;
	// textures replaced on a resize, with the submission after which they are free
	std::vector<std::pair<ComPtr<ID3D12Resource>, UINT64>> retired;
	int runs = 0;
};
NrdRunner g_nrd;

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
		g_nrd.retired.emplace_back(t.resource, g_render.submitted);
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

// The instance, its pipelines and the shared root signature (render thread, once)
bool NrdCreate() {
	if (g_nrd.instance) {
		return true;
	}
	if (g_nrd.failed) {
		return false;
	}
	g_nrd.failed = true;  // until everything below is made
	nrd::DenoiserDesc denoiser = { 0, nrd::Denoiser::SIGMA_SHADOW };
	nrd::InstanceCreationDesc creation = {};
	creation.denoisers = &denoiser;
	creation.denoisersNum = 1;
	if (nrd::CreateInstance(creation, g_nrd.instance) != nrd::Result::SUCCESS) {
		g_nrd.instance = nullptr;
		g_nrd.status = "NRD instance could not be made";
		return false;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*g_nrd.instance);
	g_nrd.srvMax = desc->descriptorPoolDesc.perSetTexturesMaxNum;
	g_nrd.uavMax = desc->descriptorPoolDesc.perSetStorageTexturesMaxNum;

	// shared root signature: b(cb) root CBV, t(base..) SRV table, u(base..) UAV table, static samplers
	D3D12_DESCRIPTOR_RANGE ranges[2] = {};
	ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
	ranges[0].NumDescriptors = g_nrd.srvMax > 0 ? g_nrd.srvMax : 1;
	ranges[0].BaseShaderRegister = desc->resourcesBaseRegisterIndex;
	ranges[0].RegisterSpace = desc->resourcesSpaceIndex;
	ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
	ranges[1].NumDescriptors = g_nrd.uavMax > 0 ? g_nrd.uavMax : 1;
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
	ComPtr<ID3DBlob> blob, error;
	if (FAILED(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) ||
	    FAILED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_nrd.root)))) {
		g_nrd.status = "NRD root signature could not be made";
		return false;
	}
	for (UINT i = 0; i < desc->pipelinesNum; ++i) {
		const auto& p = desc->pipelines[i];
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_nrd.root.Get();
		pso.CS = { p.computeShaderDXIL.bytecode, static_cast<SIZE_T>(p.computeShaderDXIL.size) };
		ComPtr<ID3D12PipelineState> state;
		if (p.computeShaderDXIL.bytecode == nullptr || FAILED(g_device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&state)))) {
			char line[128];
			snprintf(line, sizeof(line), "NRD pipeline %u could not be made", i);
			g_nrd.status = line;
			return false;
		}
		g_nrd.pipelines.push_back(state);
	}

	// pack and unpack
	{
		D3D12_DESCRIPTOR_RANGE uavs = { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 4, 0, 0, 0 };
		D3D12_ROOT_PARAMETER p[5] = {};
		for (int i = 0; i < 3; ++i) {
			p[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
			p[i].Descriptor.ShaderRegister = i;
		}
		p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		p[3].DescriptorTable = { 1, &uavs };
		p[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		p[4].Constants.Num32BitValues = 4;
		D3D12_ROOT_SIGNATURE_DESC d = { 5, p, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
		blob.Reset();
		if (FAILED(D3D12SerializeRootSignature(&d, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) ||
		    FAILED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_nrd.packRoot)))) {
			g_nrd.status = "NRD pack root signature could not be made";
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_nrd.packRoot.Get();
		pso.CS = { g_NrdSigmaPack, sizeof(g_NrdSigmaPack) };
		if (FAILED(g_device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&g_nrd.packPso)))) {
			g_nrd.status = "NRD pack pipeline could not be made";
			return false;
		}
	}
	{
		D3D12_DESCRIPTOR_RANGE srv = { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, 0 };
		D3D12_ROOT_PARAMETER p[4] = {};
		p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
		p[0].DescriptorTable = { 1, &srv };
		p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		p[1].Descriptor.ShaderRegister = 1;
		p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		p[3].Constants.Num32BitValues = 2;
		D3D12_ROOT_SIGNATURE_DESC d = { 4, p, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
		blob.Reset();
		if (FAILED(D3D12SerializeRootSignature(&d, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) ||
		    FAILED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_nrd.unpackRoot)))) {
			g_nrd.status = "NRD unpack root signature could not be made";
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_nrd.unpackRoot.Get();
		pso.CS = { g_NrdSigmaUnpack, sizeof(g_NrdSigmaUnpack) };
		if (FAILED(g_device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&g_nrd.unpackPso)))) {
			g_nrd.status = "NRD unpack pipeline could not be made";
			return false;
		}
	}

	D3D12_DESCRIPTOR_HEAP_DESC hd = {};
	hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
	hd.NumDescriptors = NrdRunner::kDescriptors;
	hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
	if (FAILED(g_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&g_nrd.heap)))) {
		g_nrd.status = "NRD descriptor heap could not be made";
		return false;
	}
	g_nrd.heapStep = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
	g_nrd.constants = CreateBuffer(NrdRunner::kConstantBytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	void* mapped = nullptr;
	if (!g_nrd.constants || FAILED(g_nrd.constants->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		g_nrd.status = "NRD constant buffer could not be made";
		return false;
	}
	g_nrd.constantsMapped = static_cast<UINT8*>(mapped);
	g_nrd.failed = false;
	g_nrd.status = "ready";
	return true;
}

// NRD's pools and the input / output textures at this size (render thread)
bool NrdResize(UINT width, UINT height) {
	if (g_nrd.width == width && g_nrd.height == height && g_nrd.shadow.resource) {
		return true;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*g_nrd.instance);
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
	bool ok = make(g_nrd.permanent, desc->permanentPool, desc->permanentPoolSize) && make(g_nrd.transient, desc->transientPool, desc->transientPoolSize) &&
	          NrdCreateTexture(g_nrd.normalRoughness, DXGI_FORMAT_R10G10B10A2_UNORM, width, height) &&
	          NrdCreateTexture(g_nrd.viewZ, DXGI_FORMAT_R32_FLOAT, width, height) &&
	          NrdCreateTexture(g_nrd.motion, DXGI_FORMAT_R16G16_FLOAT, width, height) &&
	          NrdCreateTexture(g_nrd.penumbra, DXGI_FORMAT_R16_FLOAT, width, height) &&
	          NrdCreateTexture(g_nrd.shadow, DXGI_FORMAT_R16_FLOAT, width, height);
	if (!ok) {
		g_nrd.status = "NRD textures could not be made";
		g_nrd.failed = true;
		return false;
	}
	g_nrd.width = width;
	g_nrd.height = height;
	return true;
}

NrdTexture* NrdResource(const nrd::ResourceDesc& r) {
	switch (r.type) {
	case nrd::ResourceType::IN_MV: return &g_nrd.motion;
	case nrd::ResourceType::IN_NORMAL_ROUGHNESS: return &g_nrd.normalRoughness;
	case nrd::ResourceType::IN_VIEWZ: return &g_nrd.viewZ;
	case nrd::ResourceType::IN_PENUMBRA: return &g_nrd.penumbra;
	case nrd::ResourceType::OUT_SHADOW_TRANSLUCENCY: return &g_nrd.shadow;
	case nrd::ResourceType::TRANSIENT_POOL: return r.indexInPool < g_nrd.transient.size() ? &g_nrd.transient[r.indexInPool] : nullptr;
	case nrd::ResourceType::PERMANENT_POOL: return r.indexInPool < g_nrd.permanent.size() ? &g_nrd.permanent[r.indexInPool] : nullptr;
	default: return nullptr;
	}
}

// count contiguous descriptors from the ring (wrapping to its start when they would not fit)
D3D12_CPU_DESCRIPTOR_HANDLE NrdDescriptors(UINT count, D3D12_GPU_DESCRIPTOR_HANDLE* gpu) {
	if (g_nrd.heapNext + count > NrdRunner::kDescriptors) {
		g_nrd.heapNext = 0;
	}
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = g_nrd.heap->GetCPUDescriptorHandleForHeapStart();
	cpu.ptr += static_cast<SIZE_T>(g_nrd.heapNext) * g_nrd.heapStep;
	*gpu = g_nrd.heap->GetGPUDescriptorHandleForHeapStart();
	gpu->ptr += static_cast<UINT64>(g_nrd.heapNext) * g_nrd.heapStep;
	g_nrd.heapNext += count;
	return cpu;
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
	if (g_nrd.constantsNext + aligned > NrdRunner::kConstantBytes) {
		g_nrd.constantsNext = 0;
	}
	memcpy(g_nrd.constantsMapped + g_nrd.constantsNext, data, size);
	D3D12_GPU_VIRTUAL_ADDRESS address = g_nrd.constants->GetGPUVirtualAddress() + g_nrd.constantsNext;
	g_nrd.constantsNext += aligned;
	return address;
}

// Render event 5: SIGMA over the sun trace that ran just before
void NrdSigma(const NrdEventData* data) {
	auto* queue = g_unityQueue.load();
	if (g_dead || queue == nullptr || data == nullptr || data->guide == nullptr || data->surface == nullptr || data->result == nullptr ||
	    data->width == 0 || data->height == 0 || !g_render.Create() || !NrdCreate()) {
		return;
	}
	// textures replaced earlier are freed once the GPU has passed them
	UINT64 completed = g_render.fence->GetCompletedValue();
	for (size_t i = 0; i < g_nrd.retired.size();) {
		if (g_nrd.retired[i].second <= completed) {
			g_nrd.retired.erase(g_nrd.retired.begin() + i);
		} else {
			++i;
		}
	}
	bool resized = g_nrd.width != data->width || g_nrd.height != data->height;
	if (!NrdResize(data->width, data->height)) {
		return;
	}
	auto* list = g_render.Begin();
	if (list == nullptr) {
		return;
	}
	ID3D12DescriptorHeap* heaps[] = { g_nrd.heap.Get() };
	list->SetDescriptorHeaps(1, heaps);
	std::vector<D3D12_RESOURCE_BARRIER> barriers;

	// 1. pack the inputs
	NrdTexture* inputs[4] = { &g_nrd.normalRoughness, &g_nrd.viewZ, &g_nrd.motion, &g_nrd.penumbra };
	for (auto* t : inputs) {
		NrdTransition(barriers, t, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	}
	if (!barriers.empty()) {
		list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
		barriers.clear();
	}
	D3D12_GPU_DESCRIPTOR_HANDLE packTable;
	D3D12_CPU_DESCRIPTOR_HANDLE cpu = NrdDescriptors(4, &packTable);
	for (int i = 0; i < 4; ++i) {
		D3D12_CPU_DESCRIPTOR_HANDLE h = cpu;
		h.ptr += static_cast<SIZE_T>(i) * g_nrd.heapStep;
		NrdView(h, inputs[i], true);
	}
	UINT packConstants[4] = { data->width, data->height, 0, 0 };
	memcpy(&packConstants[2], &data->tanSunRadius, 4);
	list->SetComputeRootSignature(g_nrd.packRoot.Get());
	list->SetPipelineState(g_nrd.packPso.Get());
	list->SetComputeRootShaderResourceView(0, data->guide->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(1, data->surface->GetGPUVirtualAddress());
	list->SetComputeRootShaderResourceView(2, data->result->GetGPUVirtualAddress());
	list->SetComputeRootDescriptorTable(3, packTable);
	list->SetComputeRoot32BitConstants(4, 4, packConstants, 0);
	list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);

	// 2. NRD
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
	common.accumulationMode = data->reset || resized ? nrd::AccumulationMode::CLEAR_AND_RESTART : nrd::AccumulationMode::CONTINUE;
	nrd::SigmaSettings sigma = {};
	memcpy(sigma.lightDirection, data->sunDirection, sizeof(sigma.lightDirection));
	const nrd::DispatchDesc* dispatches = nullptr;
	uint32_t dispatchCount = 0;
	nrd::Identifier id = 0;
	if (nrd::SetCommonSettings(*g_nrd.instance, common) != nrd::Result::SUCCESS ||
	    nrd::SetDenoiserSettings(*g_nrd.instance, id, &sigma) != nrd::Result::SUCCESS ||
	    nrd::GetComputeDispatches(*g_nrd.instance, &id, 1, dispatches, dispatchCount) != nrd::Result::SUCCESS) {
		g_nrd.status = "NRD settings were refused";
		list->Close();
		return;
	}
	const nrd::InstanceDesc* desc = nrd::GetInstanceDesc(*g_nrd.instance);
	list->SetComputeRootSignature(g_nrd.root.Get());
	for (uint32_t d = 0; d < dispatchCount; ++d) {
		const auto& dispatch = dispatches[d];
		const auto& pipeline = desc->pipelines[dispatch.pipelineIndex];
		// the dispatch's resources, range by range: SRVs into one table, UAVs into the other (unused slots null)
		D3D12_GPU_DESCRIPTOR_HANDLE srvTable, uavTable;
		D3D12_CPU_DESCRIPTOR_HANDLE srvCpu = NrdDescriptors(g_nrd.srvMax > 0 ? g_nrd.srvMax : 1, &srvTable);
		D3D12_CPU_DESCRIPTOR_HANDLE uavCpu = NrdDescriptors(g_nrd.uavMax > 0 ? g_nrd.uavMax : 1, &uavTable);
		UINT srvs = 0, uavs = 0, next = 0;
		for (uint32_t r = 0; r < pipeline.resourceRangesNum; ++r) {
			bool write = pipeline.resourceRanges[r].descriptorType == nrd::DescriptorType::STORAGE_TEXTURE;
			for (uint32_t k = 0; k < pipeline.resourceRanges[r].descriptorsNum && next < dispatch.resourcesNum; ++k, ++next) {
				NrdTexture* t = NrdResource(dispatch.resources[next]);
				D3D12_CPU_DESCRIPTOR_HANDLE h = write ? uavCpu : srvCpu;
				h.ptr += static_cast<SIZE_T>(write ? uavs++ : srvs++) * g_nrd.heapStep;
				NrdView(h, t, write);
				NrdTransition(barriers, t, write ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
			}
		}
		for (UINT k = srvs; k < g_nrd.srvMax; ++k) {
			D3D12_CPU_DESCRIPTOR_HANDLE h = srvCpu;
			h.ptr += static_cast<SIZE_T>(k) * g_nrd.heapStep;
			NrdView(h, nullptr, false);
		}
		for (UINT k = uavs; k < g_nrd.uavMax; ++k) {
			D3D12_CPU_DESCRIPTOR_HANDLE h = uavCpu;
			h.ptr += static_cast<SIZE_T>(k) * g_nrd.heapStep;
			NrdView(h, nullptr, true);
		}
		barriers.push_back(UavBarrier(nullptr));
		list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
		barriers.clear();
		if (dispatch.constantBufferDataSize > 0 && (!dispatch.constantBufferDataMatchesPreviousDispatch || g_nrd.lastConstants == 0)) {
			g_nrd.lastConstants = NrdConstants(dispatch.constantBufferData, dispatch.constantBufferDataSize);
		}
		list->SetPipelineState(g_nrd.pipelines[dispatch.pipelineIndex].Get());
		list->SetComputeRootConstantBufferView(0, g_nrd.lastConstants);
		list->SetComputeRootDescriptorTable(1, srvTable);
		list->SetComputeRootDescriptorTable(2, uavTable);
		list->Dispatch(dispatch.gridWidth, dispatch.gridHeight, 1);
	}

	// 3. the denoised shadow back into the trace's result
	NrdTransition(barriers, &g_nrd.shadow, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
	barriers.push_back(UavBarrier(nullptr));
	list->ResourceBarrier(static_cast<UINT>(barriers.size()), barriers.data());
	barriers.clear();
	D3D12_GPU_DESCRIPTOR_HANDLE shadowTable;
	NrdView(NrdDescriptors(1, &shadowTable), &g_nrd.shadow, false);
	UINT unpackConstants[2] = { data->width, data->height };
	list->SetComputeRootSignature(g_nrd.unpackRoot.Get());
	list->SetPipelineState(g_nrd.unpackPso.Get());
	list->SetComputeRootDescriptorTable(0, shadowTable);
	list->SetComputeRootShaderResourceView(1, data->surface->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(2, data->result->GetGPUVirtualAddress());
	list->SetComputeRoot32BitConstants(3, 2, unpackConstants, 0);
	list->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
	D3D12_RESOURCE_BARRIER done = UavBarrier(nullptr);
	list->ResourceBarrier(1, &done);
	g_render.Submit(queue);
	if (++g_nrd.runs == 1 || g_nrd.status == "ready") {
		char line[128];
		snprintf(line, sizeof(line), "SIGMA on the sun shadows, %ux%u, %u dispatches", data->width, data->height, dispatchCount);
		g_nrd.status = line;
	}
}

}  // namespace

// What the NRD stage is doing (for the mod's status lines)
NFX_EXPORT int NFX_Native_NrdStatus(char* buffer, int size) {
	return CopyOut(g_nrd.status, buffer, size);
}
