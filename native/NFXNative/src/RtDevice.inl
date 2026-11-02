// NFXNative: the DXR 1.2 device (sun shadows), included at the end of NFXNative.cpp.
//
// Stage 1 of moving the engine to DXR 1.2: an independent device on Agility SDK 1.619 (Shader Model 6.9) traces the
// sun shadows. It cannot read Unity's buffers or textures, so the scene goes over once, through buffers shared by
// both devices: every geometry's indices, positions and UVs (copied by CopyRaw.hlsl on Unity's device) and every
// alpha tested material's alpha as a byte mask (BakeAlpha.hlsl). The 1.619 device builds its own BLAS and records
// from them, and per frame its own TLAS. A trace: Unity's queue copies the Prepare output into a shared buffer and
// signals a shared fence; the 1.619 queue waits for it, traces, signals a second fence. Unity's queue takes the
// result one trace later: waiting on the other device within the frame cost 5 ms (the wait is resolved through the
// OS, and the GPU idles meanwhile), so it copies the previous trace's result into the mod's buffer for Unpack and the
// shadow pass reads that one frame old result at the pixels' previous positions. The CPU never waits.
namespace {

struct RtChunk {  // a shared buffer the scene's data is sub allocated from
	ComPtr<ID3D12Resource> unity, rt;
	UINT64 size = 0, used = 0;
};
struct RtGeometry {
	bool mirrored = false;
	int chunk = -1;
	UINT64 indexOffset = 0, positionOffset = 0, uvOffset = 0;
	UINT indexDescriptor = ~0u, positionDescriptor = ~0u, uvDescriptor = ~0u;
	ComPtr<ID3D12Resource> blas;
	// opacity micromaps (alpha tested geometry, BakeOmm.hlsl): their data, per triangle descriptions and indices,
	// and the micromap array the BLAS links
	ComPtr<ID3D12Resource> ommData, ommDescs, ommIndices, ommArray;
	bool ommTried = false;  // micromaps were attempted (not built again if that failed)
};
struct RtMaterial {
	bool baked = false;
	int chunk = -1;
	UINT64 offset = 0;
	UINT width = 0, height = 0;
	UINT descriptor = ~0u;
};

struct RtDevice {
	ComPtr<ID3D12Device5> device;
	ComPtr<ID3D12CommandQueue> queue;
	// scene builds: one list used synchronously
	ComPtr<ID3D12CommandAllocator> buildAllocator;
	ComPtr<ID3D12GraphicsCommandList4> buildList;
	ComPtr<ID3D12Fence> buildFence;
	UINT64 buildValue = 0;
	HANDLE buildEvent = nullptr;
	// traces from the render thread
	static constexpr int kSlots = 6;
	ComPtr<ID3D12CommandAllocator> allocators[kSlots];
	ComPtr<ID3D12GraphicsCommandList4> list;
	ComPtr<ID3D12Resource> instances[kSlots];
	UINT64 instanceCapacity[kSlots] = {};
	ComPtr<ID3D12Resource> constants;  // kConstantSlot bytes per slot
	ComPtr<ID3D12Resource> tlas, scratch;
	UINT64 tlasCapacity = 0, scratchCapacity = 0;
	std::vector<std::pair<ComPtr<ID3D12Resource>, UINT64>> retired;  // outgrown, with the trace after which they are free
	// Unity -> 1.619 (input ready) made on Unity's device, 1.619 -> Unity (result ready) made on the 1.619 device; each
	// opened on the other device
	ComPtr<ID3D12Fence> toRtUnity, toRtRt, toUnityRt, toUnityUnity;
	UINT64 serial = 0;
	// what one trace hands over: Prepare's output and the result, as seen by both devices; two of each (the result
	// of one trace is read during the next)
	ComPtr<ID3D12Resource> inUnity[2], inRt[2], outUnity[2], outRt[2];
	UINT64 inSize = 0, outSize = 0;
	UINT64 lastTraced = 0;  // serial of the last trace that ran on this device
	// the engine's descriptor heap on this device
	ComPtr<ID3D12DescriptorHeap> heap;
	UINT heapNext = 0, heapStep = 0;
	ComPtr<ID3D12RootSignature> root;
	ComPtr<ID3D12PipelineState> sun;
	ComPtr<ID3D12Resource> records;
	UINT64 recordCapacity = 0;
	ComPtr<ID3D12Resource> buildScratch;
	UINT64 buildScratchCapacity = 0;
	UINT32 tlasFrame = ~0u;
	int traces = 0;
};
RtDevice g_rt;
std::atomic<bool> g_rtActive{ false };  // traces go to the 1.619 device (set by the mod)
std::atomic<bool> g_rtReady{ false };   // its scene is mirrored and the pipeline made
std::atomic<bool> g_rtOmm{ true };      // opacity micromaps in use (off: every instance disables them, for comparing)
int g_rtOmmGeometries = 0;
UINT64 g_rtOmmBytes = 0;
// BakeOmm: t0 indices, t1 UVs, t2 the mask, u0 the micromap data, 12 root constants
ComPtr<ID3D12RootSignature> g_ommRoot;
ComPtr<ID3D12PipelineState> g_ommPso;
// level 4 (256 micro-triangles, 64 bytes) a triangle; level 3 for geometry with very many triangles
constexpr UINT kOmmLevel = 4;
constexpr UINT64 kOmmLevel4MaxTriangles = 200000;
std::string g_rtStatus = "not started";
std::vector<RtChunk> g_rtChunks;
std::vector<RtGeometry> g_rtGeometries;
std::vector<RtMaterial> g_rtMaterials;
std::vector<D3D12_RAYTRACING_INSTANCE_DESC> g_rtResolved;  // g_resolved with the 1.619 BLAS addresses
ComputePipeline g_copyRaw;
// BakeAlpha: u0, 5 root constants, a linear wrap sampler, the heap indexed directly
ComPtr<ID3D12RootSignature> g_bakeRoot;
ComPtr<ID3D12PipelineState> g_bakePso;
// The swaying foliage on this device (Wind.inl's g_windBlas mirrored, same indices): WindDisplace.hlsl runs here too,
// from this device's still copy of the geometry, and updates this device's BLAS, once a frame before the TLAS
struct RtWind {
	int geometry = -1;
	float transform[12] = {};
	ComPtr<ID3D12Resource> positions;  // swayed, object space float3 (unordered access)
	ComPtr<ID3D12Resource> blas;
	UINT64 updateScratch = 0;
	D3D12_RAYTRACING_GEOMETRY_DESC desc = {};
};
std::vector<RtWind> g_rtWind;
ComputePipeline g_rtWindDisplace;
ComPtr<ID3D12Resource> g_rtWindScratch, g_rtWindFrames;
UINT64 g_rtWindScratchCapacity = 0;

ComPtr<ID3D12Resource> RtBuffer(ID3D12Device* device, UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state,
                                D3D12_HEAP_FLAGS heapFlags = D3D12_HEAP_FLAG_NONE) {
	D3D12_HEAP_PROPERTIES props = {};
	props.Type = heap;
	D3D12_RESOURCE_DESC desc = {};
	desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	desc.Width = size < 256 ? 256 : size;
	desc.Height = desc.DepthOrArraySize = desc.MipLevels = 1;
	desc.SampleDesc.Count = 1;
	desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
	desc.Flags = flags;
	ComPtr<ID3D12Resource> buffer;
	if (FAILED(device->CreateCommittedResource(&props, heapFlags, &desc, state, nullptr, IID_PPV_ARGS(&buffer)))) {
		return nullptr;
	}
	return buffer;
}

// A buffer on Unity's device both devices use (default heap, unordered access), opened on the 1.619 device
bool SharedBuffer(UINT64 size, ComPtr<ID3D12Resource>& unity, ComPtr<ID3D12Resource>& rt) {
	unity = RtBuffer(g_device.Get(), size, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON,
	                 D3D12_HEAP_FLAG_SHARED);
	HANDLE handle = nullptr;
	if (!unity || FAILED(g_device->CreateSharedHandle(unity.Get(), nullptr, GENERIC_ALL, nullptr, &handle))) {
		return false;
	}
	HRESULT hr = g_rt.device->OpenSharedHandle(handle, IID_PPV_ARGS(&rt));
	CloseHandle(handle);
	return SUCCEEDED(hr);
}

// A fence made on one device and opened on the other
bool SharedFence(ID3D12Device* owner, ID3D12Device* other, ComPtr<ID3D12Fence>& mine, ComPtr<ID3D12Fence>& theirs) {
	HANDLE handle = nullptr;
	if (FAILED(owner->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&mine))) ||
	    FAILED(owner->CreateSharedHandle(mine.Get(), nullptr, GENERIC_ALL, nullptr, &handle))) {
		return false;
	}
	HRESULT hr = other->OpenSharedHandle(handle, IID_PPV_ARGS(&theirs));
	CloseHandle(handle);
	return SUCCEEDED(hr);
}

// Space in the shared scene buffers: (chunk, offset), 256 byte aligned
bool RtAllocate(UINT64 bytes, int* chunk, UINT64* offset) {
	bytes = (bytes + 255) & ~255ull;
	for (size_t i = 0; i < g_rtChunks.size(); ++i) {
		if (g_rtChunks[i].used + bytes <= g_rtChunks[i].size) {
			*chunk = static_cast<int>(i);
			*offset = g_rtChunks[i].used;
			g_rtChunks[i].used += bytes;
			return true;
		}
	}
	RtChunk c;
	c.size = bytes > (64ull << 20) ? bytes : (64ull << 20);
	if (!SharedBuffer(c.size, c.unity, c.rt)) {
		return false;
	}
	c.used = bytes;
	g_rtChunks.push_back(c);
	*chunk = static_cast<int>(g_rtChunks.size() - 1);
	*offset = 0;
	return true;
}

// A raw view of part of a shared scene buffer in the 1.619 device's heap
UINT RtRawDescriptor(ID3D12Resource* buffer, UINT64 offset, UINT64 bytes) {
	if (g_rt.heapNext >= kHeapSize) {
		return ~0u;
	}
	UINT index = g_rt.heapNext++;
	D3D12_CPU_DESCRIPTOR_HANDLE handle = g_rt.heap->GetCPUDescriptorHandleForHeapStart();
	handle.ptr += static_cast<SIZE_T>(index) * g_rt.heapStep;
	D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
	desc.Format = DXGI_FORMAT_R32_TYPELESS;
	desc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
	desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
	desc.Buffer.FirstElement = offset / 4;
	desc.Buffer.NumElements = static_cast<UINT>((bytes + 3) / 4);
	desc.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
	g_rt.device->CreateShaderResourceView(buffer, &desc, handle);
	return index;
}

bool RtSubmitBuild() {
	if (FAILED(g_rt.buildList->Close())) {
		return false;
	}
	ID3D12CommandList* lists[] = { g_rt.buildList.Get() };
	g_rt.queue->ExecuteCommandLists(1, lists);
	g_rt.queue->Signal(g_rt.buildFence.Get(), ++g_rt.buildValue);
	g_rt.buildFence->SetEventOnCompletion(g_rt.buildValue, g_rt.buildEvent);
	if (WaitForSingleObject(g_rt.buildEvent, 10000) != WAIT_OBJECT_0) {
		g_rtStatus = "the DXR 1.2 device stopped during a build";
		g_rtActive = false;
		g_rtReady = false;
		return false;
	}
	return true;
}

// Until the 1.619 device finished every trace submitted to it. Unity's queue being idle is not enough: it takes a
// trace's result only during the next trace, so the last one may still run.
void RtWaitIdle() {
	if (!g_rt.toUnityRt || g_rt.serial == 0 || g_rt.toUnityRt->GetCompletedValue() >= g_rt.serial) {
		return;
	}
	HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	g_rt.toUnityRt->SetEventOnCompletion(g_rt.serial, done);
	WaitForSingleObject(done, 5000);
	CloseHandle(done);
}

D3D12_VERSIONED_ROOT_SIGNATURE_DESC HeapIndexedRoot(D3D12_ROOT_PARAMETER1* params, UINT count, D3D12_STATIC_SAMPLER_DESC* sampler) {
	sampler->Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
	sampler->AddressU = sampler->AddressV = sampler->AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
	sampler->MaxLOD = D3D12_FLOAT32_MAX;
	sampler->ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
	D3D12_VERSIONED_ROOT_SIGNATURE_DESC desc = {};
	desc.Version = D3D_ROOT_SIGNATURE_VERSION_1_1;
	desc.Desc_1_1.NumParameters = count;
	desc.Desc_1_1.pParameters = params;
	desc.Desc_1_1.NumStaticSamplers = 1;
	desc.Desc_1_1.pStaticSamplers = sampler;
	desc.Desc_1_1.Flags = D3D12_ROOT_SIGNATURE_FLAG_CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED;
	return desc;
}

bool CreateRtPipelines() {
	if (!g_copyRaw.Create(g_CopyRaw, sizeof(g_CopyRaw), 4)) {
		return false;
	}
	if (!g_rtWindDisplace.Create(g_WindDisplace, sizeof(g_WindDisplace), 28, 1, g_rt.device.Get())) {
		return false;
	}
	if (!g_bakePso) {
		D3D12_ROOT_PARAMETER1 params[2] = {};
		params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		params[1].Constants.Num32BitValues = 5;
		D3D12_STATIC_SAMPLER_DESC sampler = {};
		auto desc = HeapIndexedRoot(params, 2, &sampler);
		ComPtr<ID3DBlob> blob, error;
		if (FAILED(D3D12SerializeVersionedRootSignature(&desc, &blob, &error)) ||
		    FAILED(g_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_bakeRoot)))) {
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_bakeRoot.Get();
		pso.CS = { g_BakeAlpha, sizeof(g_BakeAlpha) };
		if (FAILED(g_device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&g_bakePso)))) {
			return false;
		}
	}
	if (!g_ommPso) {
		D3D12_ROOT_PARAMETER params[5] = {};
		for (int i = 0; i < 3; ++i) {
			params[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
			params[i].Descriptor.ShaderRegister = i;
		}
		params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
		params[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
		params[4].Constants.Num32BitValues = 12;
		D3D12_ROOT_SIGNATURE_DESC desc = { 5, params, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
		ComPtr<ID3DBlob> blob, error;
		if (FAILED(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error)) ||
		    FAILED(g_rt.device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_ommRoot)))) {
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_ommRoot.Get();
		pso.CS = { g_BakeOmm, sizeof(g_BakeOmm) };
		if (FAILED(g_rt.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&g_ommPso)))) {
			return false;
		}
	}
	if (!g_rt.sun) {
		// the trace root signature of NFXCommon.hlsli, on the 1.619 device
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
		D3D12_STATIC_SAMPLER_DESC sampler = {};
		auto desc = HeapIndexedRoot(params, 8, &sampler);
		ComPtr<ID3DBlob> blob, error;
		if (FAILED(D3D12SerializeVersionedRootSignature(&desc, &blob, &error)) ||
		    FAILED(g_rt.device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_rt.root)))) {
			return false;
		}
		D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
		pso.pRootSignature = g_rt.root.Get();
		pso.CS = { g_SunShadow619, sizeof(g_SunShadow619) };
		if (FAILED(g_rt.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&g_rt.sun)))) {
			return false;
		}
	}
	return true;
}

// The swaying foliage's BLAS on this device, after the still ones were built, with both devices idle: from this
// device's still copy of the geometry, updatable and without opacity micromaps (an update would keep the micromaps
// baked for the still leaves; the shadows alpha test these hits in the shader instead). Those that kept their geometry
// and place are reused. Points the swaying instances of resolved at them.
bool BuildRtWind(std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& resolved) {
	std::vector<RtWind> next(g_windBlas.size());
	std::vector<size_t> toBuild;
	for (size_t k = 0; k < g_windBlas.size(); ++k) {
		const auto& w = g_windBlas[k];
		for (auto& old : g_rtWind) {
			if (old.blas && old.geometry == w.geometry && memcmp(old.transform, w.transform, sizeof(old.transform)) == 0) {
				next[k] = old;
				old.geometry = -1;  // taken
				break;
			}
		}
		if (next[k].blas) {
			continue;
		}
		const auto& g = g_geometries[w.geometry];
		const auto& rg = g_rtGeometries[w.geometry];
		if (g.released || !rg.mirrored || rg.chunk < 0) {
			continue;
		}
		auto* chunk = g_rtChunks[rg.chunk].rt.Get();
		auto& r = next[k];
		r.geometry = w.geometry;
		memcpy(r.transform, w.transform, sizeof(r.transform));
		r.desc.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
		r.desc.Flags = g.desc.Flags;
		auto& t = r.desc.Triangles;
		t.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
		t.VertexCount = g.desc.Triangles.VertexCount;
		t.VertexBuffer = { chunk->GetGPUVirtualAddress() + rg.positionOffset, 12 };
		t.IndexFormat = DXGI_FORMAT_R32_UINT;
		t.IndexCount = static_cast<UINT>(g.triangles * 3);
		t.IndexBuffer = chunk->GetGPUVirtualAddress() + rg.indexOffset;
		toBuild.push_back(k);
	}
	g_rtWind.clear();  // the ones not taken are free: both devices are idle

	auto inputsOf = [](const D3D12_RAYTRACING_GEOMETRY_DESC* desc) {
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = 1;
		inputs.pGeometryDescs = desc;
		return inputs;
	};
	UINT64 scratchSize = 0;
	for (size_t k : toBuild) {
		auto& r = next[k];
		auto inputs = inputsOf(&r.desc);
		D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
		g_rt.device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
		scratchSize = info.ScratchDataSizeInBytes > scratchSize ? info.ScratchDataSizeInBytes : scratchSize;
		r.updateScratch = info.UpdateScratchDataSizeInBytes;
		r.positions = RtBuffer(g_rt.device.Get(), static_cast<UINT64>(r.desc.Triangles.VertexCount) * 12, D3D12_HEAP_TYPE_DEFAULT,
		                       D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		r.blas = RtBuffer(g_rt.device.Get(), info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                  D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
		if (!r.positions || !r.blas) {
			return false;
		}
	}
	if (!toBuild.empty()) {
		if (g_rt.buildScratchCapacity < scratchSize) {
			g_rt.buildScratch = RtBuffer(g_rt.device.Get(), scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
			                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
			g_rt.buildScratchCapacity = g_rt.buildScratch ? scratchSize : 0;
			if (!g_rt.buildScratch) {
				return false;
			}
		}
		g_rt.buildList->Close();  // left open by an earlier failure: closed, both resets succeed
		g_rt.buildAllocator->Reset();
		g_rt.buildList->Reset(g_rt.buildAllocator.Get(), nullptr);
		for (size_t k : toBuild) {
			auto& r = next[k];
			D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
			build.Inputs = inputsOf(&r.desc);
			build.DestAccelerationStructureData = r.blas->GetGPUVirtualAddress();
			build.ScratchAccelerationStructureData = g_rt.buildScratch->GetGPUVirtualAddress();
			g_rt.buildList->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
			D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
			g_rt.buildList->ResourceBarrier(1, &b);
		}
		if (!RtSubmitBuild()) {
			return false;
		}
		// from now on the updates read the swayed copy
		for (size_t k : toBuild) {
			next[k].desc.Triangles.VertexBuffer.StartAddress = next[k].positions->GetGPUVirtualAddress();
		}
	}
	g_rtWind = next;
	UINT64 updateScratch = 0;
	for (const auto& r : g_rtWind) {
		updateScratch = r.updateScratch > updateScratch ? r.updateScratch : updateScratch;
	}
	if (updateScratch > g_rtWindScratchCapacity) {
		g_rtWindScratch = RtBuffer(g_rt.device.Get(), updateScratch, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                           D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		g_rtWindScratchCapacity = g_rtWindScratch ? updateScratch : 0;
		if (!g_rtWindScratch) {
			return false;
		}
	}
	for (size_t i = 0; i < resolved.size() && i < g_instanceWind.size(); ++i) {
		int k = g_instanceWind[i];
		if (k >= 0 && k < static_cast<int>(g_rtWind.size()) && g_rtWind[k].blas) {
			resolved[i].AccelerationStructure = g_rtWind[k].blas->GetGPUVirtualAddress();
		}
	}
	return true;
}

// Once a frame, before this device's TLAS (as RecordWind on Unity's): sway every wind BLAS here and update it.
// g_sceneMutex held.
void RecordRtWind(ID3D12GraphicsCommandList4* rl, int slot) {
	if (g_rtWind.empty() || !g_windActive || !g_rtWindDisplace.pso || !g_rtWindScratch) {
		return;
	}
	if (!g_rtWindFrames) {
		g_rtWindFrames = RtBuffer(g_rt.device.Get(), static_cast<UINT64>(kWindFrameBytes) * RtDevice::kSlots, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE,
		                          D3D12_RESOURCE_STATE_GENERIC_READ);
		if (!g_rtWindFrames) {
			return;
		}
	}
	void* mapped = nullptr;
	if (FAILED(g_rtWindFrames->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return;
	}
	{
		std::lock_guard<std::mutex> lock(g_windMutex);
		memcpy(static_cast<char*>(mapped) + static_cast<size_t>(slot) * kWindFrameBytes, g_windFrame, sizeof(g_windFrame));
	}
	g_rtWindFrames->Unmap(0, nullptr);

	// 1. sway the vertices of every wind BLAS (its first instance's place, as it is now)
	rl->SetComputeRootSignature(g_rtWindDisplace.root.Get());
	rl->SetPipelineState(g_rtWindDisplace.pso.Get());
	rl->SetComputeRootShaderResourceView(3, g_rtWindFrames->GetGPUVirtualAddress() + static_cast<UINT64>(slot) * kWindFrameBytes);
	std::vector<bool> done(g_rtWind.size(), false);
	for (size_t i = 0; i < g_instanceWind.size() && i < g_rtResolved.size(); ++i) {
		int k = g_instanceWind[i];
		if (k < 0 || k >= static_cast<int>(g_rtWind.size()) || done[k] || !g_rtWind[k].blas) {
			continue;
		}
		done[k] = true;
		const auto& r = g_rtWind[k];
		const auto& rg = g_rtGeometries[r.geometry];
		if (rg.chunk < 0) {
			continue;
		}
		UINT vertices = r.desc.Triangles.VertexCount;
		float constants[28];
		WindConstants(g_rtResolved[i].Transform, vertices, constants);
		rl->SetComputeRootShaderResourceView(0, g_rtChunks[rg.chunk].rt->GetGPUVirtualAddress() + rg.positionOffset);
		rl->SetComputeRootUnorderedAccessView(1, r.positions->GetGPUVirtualAddress());
		rl->SetComputeRoot32BitConstants(2, 28, constants, 0);
		rl->Dispatch((vertices + 63) / 64, 1, 1);
	}
	// 2. the updates read the swayed vertices: unordered access -> shader resource, and back after
	std::vector<D3D12_RESOURCE_BARRIER> toRead, toWrite;
	for (const auto& r : g_rtWind) {
		if (r.blas) {
			toRead.push_back(Transition(r.positions.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
			toWrite.push_back(Transition(r.positions.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
		}
	}
	if (toRead.empty()) {
		return;
	}
	rl->ResourceBarrier(static_cast<UINT>(toRead.size()), toRead.data());
	for (auto& r : g_rtWind) {
		if (!r.blas) {
			continue;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD |
		                     D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE;
		build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		build.Inputs.NumDescs = 1;
		build.Inputs.pGeometryDescs = &r.desc;
		build.DestAccelerationStructureData = r.blas->GetGPUVirtualAddress();
		build.SourceAccelerationStructureData = r.blas->GetGPUVirtualAddress();
		build.ScratchAccelerationStructureData = g_rtWindScratch->GetGPUVirtualAddress();
		rl->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
		rl->ResourceBarrier(1, &b);
	}
	rl->ResourceBarrier(static_cast<UINT>(toWrite.size()), toWrite.data());
}

// Takes the engine's scene over to the 1.619 device: what is new since the last time. Main thread, g_sceneMutex held,
// after NFX_Native_BuildScene (the engine's own copies of the geometry exist then).
int MirrorScene() {
	if (!g_rt.device || g_dead) {
		return -1;
	}
	if (!CreateRtPipelines() || !g_gpu.Create()) {
		g_rtStatus = "pipelines could not be made";
		return -2;
	}
	double start = NowMs();
	g_rtGeometries.resize(g_geometries.size());
	g_rtMaterials.resize(g_materials.size());

	// materials whose alpha the shadows test: those of alpha tested instances with a colour texture
	std::vector<bool> alphaTested(g_materials.size(), false);
	for (size_t i = 0; i < g_instances.size(); ++i) {
		int m = g_instanceMaterial[i];
		if ((g_instances[i].Flags & D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE) == 0 && m >= 0 && m < static_cast<int>(g_materials.size())) {
			alphaTested[m] = true;
		}
	}

	// 1. Unity's device: the geometry copies and alpha masks into the shared buffers
	g_gpu.Begin();
	auto* list = g_gpu.list.Get();
	ID3D12DescriptorHeap* heaps[] = { g_heap.Get() };
	auto copy = [&](D3D12_GPU_VIRTUAL_ADDRESS source, UINT64 bytes, int chunk, UINT64 offset) {
		UINT words = static_cast<UINT>(bytes / 4);
		UINT constants[4] = { words, 0, 0, 0 };
		list->SetComputeRootSignature(g_copyRaw.root.Get());
		list->SetPipelineState(g_copyRaw.pso.Get());
		list->SetComputeRootShaderResourceView(0, source);
		list->SetComputeRootUnorderedAccessView(1, g_rtChunks[chunk].unity->GetGPUVirtualAddress() + offset);
		list->SetComputeRoot32BitConstants(2, 4, constants, 0);
		list->Dispatch((words + 255) / 256, 1, 1);
	};
	int newGeometries = 0, newMasks = 0;
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		const auto& g = g_geometries[i];
		auto& r = g_rtGeometries[i];
		if (r.mirrored || r.chunk >= 0 || g.released || !g.blas || !g.ownIndices || !g.ownPositions) {
			continue;
		}
		UINT64 indexBytes = g.triangles * 12;
		UINT64 positionBytes = static_cast<UINT64>(g.desc.Triangles.VertexCount) * 12;
		UINT64 uvBytes = g.ownUvs ? static_cast<UINT64>(g.desc.Triangles.VertexCount) * 8 : 0;
		UINT64 total = ((indexBytes + 255) & ~255ull) + ((positionBytes + 255) & ~255ull) + ((uvBytes + 255) & ~255ull);
		UINT64 base = 0;
		if (!RtAllocate(total, &r.chunk, &base)) {
			g_rtStatus = "no memory for the shared scene";
			return -3;
		}
		r.indexOffset = base;
		r.positionOffset = r.indexOffset + ((indexBytes + 255) & ~255ull);
		r.uvOffset = r.positionOffset + ((positionBytes + 255) & ~255ull);
		copy(g.ownIndices.Address(), indexBytes, r.chunk, r.indexOffset);
		copy(g.ownPositions.Address(), positionBytes, r.chunk, r.positionOffset);
		if (uvBytes > 0) {
			copy(g.ownUvs.Address(), uvBytes, r.chunk, r.uvOffset);
		}
		++newGeometries;
	}
	for (size_t i = 0; i < g_materials.size(); ++i) {
		const auto& m = g_materials[i];
		auto& r = g_rtMaterials[i];
		if (r.baked || r.chunk >= 0 || !alphaTested[i] || !m.texture || m.descriptor == ~0u) {
			continue;
		}
		// about mip 2 of the texture, as the shadow rays sample it, at most 512 texels a side
		D3D12_RESOURCE_DESC td = m.texture->GetDesc();
		UINT width = static_cast<UINT>(td.Width), height = td.Height;
		int level = 2;
		while ((width >> level) > 512 || (height >> level) > 512) {
			++level;
		}
		r.width = ((width >> level) + 3) & ~3u;
		r.width = r.width < 4 ? 4 : r.width;
		r.height = (height >> level) > 0 ? (height >> level) : 1;
		if (!RtAllocate(static_cast<UINT64>(r.width) * r.height, &r.chunk, &r.offset)) {
			g_rtStatus = "no memory for the alpha masks";
			return -3;
		}
		float mip = static_cast<float>(level);
		UINT constants[5] = { m.descriptor, r.width, r.height, static_cast<UINT>(r.offset), 0 };
		memcpy(&constants[4], &mip, 4);
		list->SetDescriptorHeaps(1, heaps);
		list->SetComputeRootSignature(g_bakeRoot.Get());
		list->SetPipelineState(g_bakePso.Get());
		list->SetComputeRootUnorderedAccessView(0, g_rtChunks[r.chunk].unity->GetGPUVirtualAddress());
		list->SetComputeRoot32BitConstants(1, 5, constants, 0);
		list->Dispatch((r.width / 4 + 7) / 8, (r.height + 7) / 8, 1);
		++newMasks;
	}
	if (!g_gpu.SubmitAndWait()) {
		g_rtStatus = "Unity's device stopped copying the scene";
		return -4;
	}

	// 2. the 1.619 device: views, micromaps for alpha tested geometry, BLAS (scratch for all builds sized before
	// recording). A geometry's micromaps come from the alpha mask of the first alpha tested instance using it.
	std::vector<int> ommMaterial(g_geometries.size(), -1);
	for (size_t i = 0; i < g_instances.size(); ++i) {
		int gi = g_instanceGeometry[i], m = g_instanceMaterial[i];
		if ((g_instances[i].Flags & D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_OPAQUE) == 0 && ommMaterial[gi] < 0 &&
		    m >= 0 && m < static_cast<int>(g_rtMaterials.size()) && g_rtMaterials[m].chunk >= 0) {
			ommMaterial[gi] = m;
		}
	}
	// geometry mirrored before its material turned alpha tested (the shader rebuild swaps materials after the map
	// loads) gets its BLAS again, with micromaps; traces in flight still use the old one
	bool rebuild = false;
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		auto& r = g_rtGeometries[i];
		if (r.mirrored && !r.ommTried && ommMaterial[i] >= 0 && g_geometries[i].ownUvs && !g_geometries[i].released) {
			r.mirrored = false;
			rebuild = true;
		}
	}
	if (rebuild) {
		WaitRenderIdle();
		RtWaitIdle();
	}
	std::vector<D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC> triangleDescs(g_geometries.size());
	std::vector<D3D12_RAYTRACING_GEOMETRY_OMM_LINKAGE_DESC> linkages(g_geometries.size());
	std::vector<D3D12_RAYTRACING_OPACITY_MICROMAP_HISTOGRAM_ENTRY> histograms(g_geometries.size());
	std::vector<D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_DESC> arrays(g_geometries.size());
	std::vector<D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO> ommPrebuild(g_geometries.size());
	std::vector<UINT> ommLevel(g_geometries.size(), 0);
	std::vector<D3D12_RAYTRACING_GEOMETRY_DESC> descs(g_geometries.size());
	std::vector<D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO> prebuild(g_geometries.size());
	UINT64 scratchSize = 0;
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		const auto& g = g_geometries[i];
		auto& r = g_rtGeometries[i];
		if (r.mirrored || r.chunk < 0 || g.released) {
			continue;
		}
		auto* buffer = g_rtChunks[r.chunk].rt.Get();
		UINT64 positionBytes = static_cast<UINT64>(g.desc.Triangles.VertexCount) * 12;
		if (r.indexDescriptor == ~0u) {  // (a BLAS built again keeps its views)
			r.indexDescriptor = RtRawDescriptor(buffer, r.indexOffset, g.triangles * 12);
			r.positionDescriptor = RtRawDescriptor(buffer, r.positionOffset, positionBytes);
			r.uvDescriptor = g.ownUvs ? RtRawDescriptor(buffer, r.uvOffset, static_cast<UINT64>(g.desc.Triangles.VertexCount) * 8) : ~0u;
		}
		auto& t = triangleDescs[i];
		t.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
		t.VertexCount = g.desc.Triangles.VertexCount;
		t.VertexBuffer.StartAddress = buffer->GetGPUVirtualAddress() + r.positionOffset;
		t.VertexBuffer.StrideInBytes = 12;
		t.IndexFormat = DXGI_FORMAT_R32_UINT;
		t.IndexCount = static_cast<UINT>(g.triangles * 3);
		t.IndexBuffer = buffer->GetGPUVirtualAddress() + r.indexOffset;
		auto& d = descs[i];
		d.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
		d.Flags = g.desc.Flags;
		d.Triangles = t;
		// alpha tested with UVs and a mask: 4 state micromaps, one per triangle
		if (ommMaterial[i] >= 0 && g.ownUvs && g.triangles > 0) {
			r.ommTried = true;
			UINT level = g.triangles > kOmmLevel4MaxTriangles ? kOmmLevel - 1 : kOmmLevel;
			UINT triangles = static_cast<UINT>(g.triangles);
			UINT64 bytesPerTriangle = (1ull << (2 * level)) / 4;
			r.ommData = RtBuffer(g_rt.device.Get(), triangles * bytesPerTriangle, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
			                     D3D12_RESOURCE_STATE_UNORDERED_ACCESS);  // committed: zeroed (transparent)
			std::vector<D3D12_RAYTRACING_OPACITY_MICROMAP_DESC> perTriangle(triangles);
			std::vector<UINT> indices(triangles);
			for (UINT k = 0; k < triangles; ++k) {
				perTriangle[k].ByteOffset = static_cast<UINT>(k * bytesPerTriangle);
				perTriangle[k].SubdivisionLevel = level;
				perTriangle[k].Format = D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_4_STATE;
				indices[k] = k;
			}
			r.ommDescs = RtBuffer(g_rt.device.Get(), perTriangle.size() * sizeof(perTriangle[0]), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE,
			                      D3D12_RESOURCE_STATE_GENERIC_READ);
			r.ommIndices = RtBuffer(g_rt.device.Get(), indices.size() * 4, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
			void* mapped = nullptr;
			bool ok = r.ommData && r.ommDescs && r.ommIndices;
			if (ok && SUCCEEDED(r.ommDescs->Map(0, nullptr, &mapped)) && mapped != nullptr) {
				memcpy(mapped, perTriangle.data(), perTriangle.size() * sizeof(perTriangle[0]));
				r.ommDescs->Unmap(0, nullptr);
			} else {
				ok = false;
			}
			mapped = nullptr;
			if (ok && SUCCEEDED(r.ommIndices->Map(0, nullptr, &mapped)) && mapped != nullptr) {
				memcpy(mapped, indices.data(), indices.size() * 4);
				r.ommIndices->Unmap(0, nullptr);
			} else {
				ok = false;
			}
			if (ok) {
				histograms[i] = { triangles, level, D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_4_STATE };
				auto& a = arrays[i];
				a.NumOmmHistogramEntries = 1;
				a.pOmmHistogram = &histograms[i];
				a.InputBuffer = r.ommData->GetGPUVirtualAddress();
				a.PerOmmDescs = { r.ommDescs->GetGPUVirtualAddress(), sizeof(D3D12_RAYTRACING_OPACITY_MICROMAP_DESC) };
				D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS ommInputs = {};
				ommInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_OPACITY_MICROMAP_ARRAY;
				ommInputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
				ommInputs.NumDescs = 1;
				ommInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
				ommInputs.pOpacityMicromapArrayDesc = &a;
				g_rt.device->GetRaytracingAccelerationStructurePrebuildInfo(&ommInputs, &ommPrebuild[i]);
				if (ommPrebuild[i].ResultDataMaxSizeInBytes > 0) {
					r.ommArray = RtBuffer(g_rt.device.Get(), ommPrebuild[i].ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
					                      D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
				}
			}
			if (r.ommArray) {
				ommLevel[i] = level;
				scratchSize = ommPrebuild[i].ScratchDataSizeInBytes > scratchSize ? ommPrebuild[i].ScratchDataSizeInBytes : scratchSize;
				auto& link = linkages[i];
				link.OpacityMicromapIndexBuffer = { r.ommIndices->GetGPUVirtualAddress(), 4 };
				link.OpacityMicromapIndexFormat = DXGI_FORMAT_R32_UINT;
				link.OpacityMicromapBaseLocation = 0;
				link.OpacityMicromapArray = r.ommArray->GetGPUVirtualAddress();
				d.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_OMM_TRIANGLES;
				d.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_NONE;
				d.OmmTriangles.pTriangles = &triangleDescs[i];
				d.OmmTriangles.pOmmLinkage = &linkages[i];
				g_rtOmmBytes += triangles * bytesPerTriangle + ommPrebuild[i].ResultDataMaxSizeInBytes;
				++g_rtOmmGeometries;
			} else {
				r.ommData.Reset();
				r.ommDescs.Reset();
				r.ommIndices.Reset();
			}
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = 1;
		inputs.pGeometryDescs = &d;
		g_rt.device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &prebuild[i]);
		scratchSize = prebuild[i].ScratchDataSizeInBytes > scratchSize ? prebuild[i].ScratchDataSizeInBytes : scratchSize;
	}
	for (size_t i = 0; i < g_materials.size(); ++i) {
		auto& r = g_rtMaterials[i];
		if (r.chunk >= 0 && !r.baked) {
			r.descriptor = RtRawDescriptor(g_rtChunks[r.chunk].rt.Get(), r.offset, static_cast<UINT64>(r.width) * r.height);
			r.baked = true;
		}
	}
	if (g_rt.buildScratchCapacity < scratchSize) {
		g_rt.buildScratch = RtBuffer(g_rt.device.Get(), scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		g_rt.buildScratchCapacity = g_rt.buildScratch ? scratchSize : 0;
		if (!g_rt.buildScratch) {
			return -5;
		}
	}
	g_rt.buildList->Close();  // left open by an earlier failure (no memory): closed, both resets succeed
	g_rt.buildAllocator->Reset();
	g_rt.buildList->Reset(g_rt.buildAllocator.Get(), nullptr);
	// micromaps: bake every new one, then build their arrays (each through the shared scratch, one after the other)
	std::vector<D3D12_RESOURCE_BARRIER> baked;
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		auto& r = g_rtGeometries[i];
		if (r.mirrored || !r.ommArray || ommLevel[i] == 0) {
			continue;
		}
		const auto& g = g_geometries[i];
		const auto& rm = g_rtMaterials[ommMaterial[i]];
		const auto& m = g_materials[ommMaterial[i]];
		UINT triangles = static_cast<UINT>(g.triangles);
		UINT threads = triangles << (2 * ommLevel[i]);
		UINT groups = (threads + 63) / 64;
		UINT groupsX = groups < 65535 ? groups : 65535;
		UINT groupsY = (groups + groupsX - 1) / groupsX;
		UINT constants[12] = { triangles, ommLevel[i], rm.width, rm.height };
		memcpy(&constants[4], m.st, 16);
		memcpy(&constants[8], &m.cutoff, 4);
		memcpy(&constants[9], &m.alphaScale, 4);
		constants[10] = groupsX;
		auto* chunk = g_rtChunks[r.chunk].rt.Get();
		g_rt.buildList->SetComputeRootSignature(g_ommRoot.Get());
		g_rt.buildList->SetPipelineState(g_ommPso.Get());
		g_rt.buildList->SetComputeRootShaderResourceView(0, chunk->GetGPUVirtualAddress() + r.indexOffset);
		g_rt.buildList->SetComputeRootShaderResourceView(1, chunk->GetGPUVirtualAddress() + r.uvOffset);
		g_rt.buildList->SetComputeRootShaderResourceView(2, g_rtChunks[rm.chunk].rt->GetGPUVirtualAddress() + rm.offset);
		g_rt.buildList->SetComputeRootUnorderedAccessView(3, r.ommData->GetGPUVirtualAddress());
		g_rt.buildList->SetComputeRoot32BitConstants(4, 12, constants, 0);
		g_rt.buildList->Dispatch(groupsX, groupsY, 1);
		baked.push_back(Transition(r.ommData.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
	}
	if (!baked.empty()) {
		g_rt.buildList->ResourceBarrier(static_cast<UINT>(baked.size()), baked.data());
	}
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		auto& r = g_rtGeometries[i];
		if (r.mirrored || !r.ommArray || ommLevel[i] == 0) {
			continue;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_OPACITY_MICROMAP_ARRAY;
		build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
		build.Inputs.NumDescs = 1;
		build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		build.Inputs.pOpacityMicromapArrayDesc = &arrays[i];
		build.DestAccelerationStructureData = r.ommArray->GetGPUVirtualAddress();
		build.ScratchAccelerationStructureData = g_rt.buildScratch->GetGPUVirtualAddress();
		g_rt.buildList->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
		g_rt.buildList->ResourceBarrier(1, &b);
	}
	for (size_t i = 0; i < g_geometries.size(); ++i) {
		auto& r = g_rtGeometries[i];
		if (r.mirrored || r.chunk < 0 || g_geometries[i].released || prebuild[i].ResultDataMaxSizeInBytes == 0) {
			continue;
		}
		r.blas = RtBuffer(g_rt.device.Get(), prebuild[i].ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                  D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
		if (!r.blas) {
			return -6;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
		build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		build.Inputs.NumDescs = 1;
		build.Inputs.pGeometryDescs = &descs[i];
		build.DestAccelerationStructureData = r.blas->GetGPUVirtualAddress();
		build.ScratchAccelerationStructureData = g_rt.buildScratch->GetGPUVirtualAddress();
		g_rt.buildList->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
		g_rt.buildList->ResourceBarrier(1, &b);
		r.mirrored = true;
	}
	if (!RtSubmitBuild()) {
		return -7;
	}

	// 3. records (as NFX_Native_BuildScene's, with this device's views and the alpha masks) and instances
	UINT recordCount = 1;
	for (const auto& d : g_instances) {
		recordCount = d.InstanceID + 1 > recordCount ? d.InstanceID + 1 : recordCount;
	}
	constexpr size_t kRecordWords = 32;
	std::vector<UINT32> data(static_cast<size_t>(recordCount) * kRecordWords, 0);
	for (size_t i = 0; i < data.size(); i += kRecordWords) {
		data[i] = data[i + 1] = data[i + 2] = data[i + 5] = data[i + 6] = data[i + 16] = data[i + 22] = ~0u;
	}
	std::vector<D3D12_RAYTRACING_INSTANCE_DESC> resolved = g_resolved;
	size_t usable = 0;
	for (size_t i = 0; i < g_instances.size() && i < resolved.size(); ++i) {
		const auto& rg = g_rtGeometries[g_instanceGeometry[i]];
		resolved[i].AccelerationStructure = rg.blas ? rg.blas->GetGPUVirtualAddress() : 0;
		usable += rg.blas ? 1 : 0;
		int material = g_instanceMaterial[i];
		UINT32* r = &data[static_cast<size_t>(g_instances[i].InstanceID) * kRecordWords];
		r[0] = rg.indexDescriptor;
		r[1] = rg.uvDescriptor;
		r[5] = rg.positionDescriptor;
		if (material >= 0 && material < static_cast<int>(g_materials.size())) {
			const auto& m = g_materials[material];
			const auto& rm = g_rtMaterials[material];
			r[2] = rm.baked ? rm.descriptor : ~0u;
			memcpy(&r[3], &m.cutoff, 4);
			memcpy(&r[4], &m.alphaScale, 4);
			memcpy(&r[7], &m.metallic, 4);
			memcpy(&r[8], m.st, 16);
			memcpy(&r[12], m.color, 12);
			memcpy(&r[15], &m.smoothness, 4);
			r[17] = m.flags;
			r[27] = rm.width | (rm.height << 16);
		}
	}
	// the traces read the records: replace them only when both devices are done with them
	WaitRenderIdle();
	RtWaitIdle();
	if (!BuildRtWind(resolved)) {
		g_rtStatus = "the swaying foliage's BLAS could not be built";
		return -9;
	}
	if (g_rt.recordCapacity < data.size() * 4) {
		g_rt.records = RtBuffer(g_rt.device.Get(), data.size() * 4 + 4096, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		g_rt.recordCapacity = g_rt.records ? data.size() * 4 + 4096 : 0;
		if (!g_rt.records) {
			return -8;
		}
	}
	void* mapped = nullptr;
	if (FAILED(g_rt.records->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return -8;
	}
	memcpy(mapped, data.data(), data.size() * 4);
	g_rt.records->Unmap(0, nullptr);
	g_rtResolved = resolved;
	g_rtReady = !g_rtResolved.empty();

	UINT64 shared = 0;
	for (const auto& c : g_rtChunks) {
		shared += c.used;
	}
	char line[256];
	snprintf(line, sizeof(line), "ready: %zu instances (%zu with geometry), %d geometries and %d alpha masks new, shared scene %.1f MB, "
	         "%d geometries with opacity micromaps (%.1f MB), %.1f ms",
	         g_rtResolved.size(), usable, newGeometries, newMasks, shared / (1024.0 * 1024.0), g_rtOmmGeometries, g_rtOmmBytes / (1024.0 * 1024.0),
	         NowMs() - start);
	g_rtStatus = line;
	return 1;
}

// The scene is dropped (a new map): the 1.619 device's copy goes too. g_sceneMutex held, render thread idle.
void ClearRtScene() {
	if (!g_rt.device) {
		return;
	}
	g_rtReady = false;
	RtWaitIdle();
	g_rtGeometries.clear();
	g_rtMaterials.clear();
	g_rtResolved.clear();
	g_rtWind.clear();
	g_rtChunks.clear();
	g_rt.heapNext = 0;
	g_rtOmmGeometries = 0;
	g_rtOmmBytes = 0;
	g_rtStatus = "device made, no scene yet";
}

// Its mesh is gone: its BLAS and views go once the scene is mirrored again. g_sceneMutex held, render thread idle.
void ReleaseRtGeometry(int id) {
	if (id >= 0 && id < static_cast<int>(g_rtGeometries.size())) {
		RtWaitIdle();
		g_rtGeometries[id] = RtGeometry();
		g_rtReady = false;  // its instances still point at the BLAS: no traces until the scene is mirrored again
	}
}

bool RtEnsureHandover(UINT64 inBytes, UINT64 outBytes) {
	if (g_rt.inSize < inBytes) {
		for (int i = 0; i < 2; ++i) {
			if (!SharedBuffer(inBytes, g_rt.inUnity[i], g_rt.inRt[i])) {
				return false;
			}
		}
		g_rt.inSize = inBytes;
		g_rt.lastTraced = 0;  // new buffers: no earlier result in them
	}
	if (g_rt.outSize < outBytes) {
		for (int i = 0; i < 2; ++i) {
			if (!SharedBuffer(outBytes, g_rt.outUnity[i], g_rt.outRt[i])) {
				return false;
			}
		}
		g_rt.outSize = outBytes;
		g_rt.lastTraced = 0;
	}
	return true;
}

// CopyRaw.hlsl over whole 32 bit words (4 words a thread, 64 threads a group). The sun trace's buffers are already
// packed by the bridge and SunShadow.hlsl (16 bytes in, 4 out per pixel): they are handed over as they are.
void RtCopyWords(ID3D12GraphicsCommandList* list, ID3D12Resource* source, ID3D12Resource* target, UINT words) {
	UINT constants[4] = { words, 0, 0, 0 };
	list->SetComputeRootSignature(g_copyRaw.root.Get());
	list->SetPipelineState(g_copyRaw.pso.Get());
	list->SetComputeRootShaderResourceView(0, source->GetGPUVirtualAddress());
	list->SetComputeRootUnorderedAccessView(1, target->GetGPUVirtualAddress());
	list->SetComputeRoot32BitConstants(2, 4, constants, 0);
	list->Dispatch((words + 255) / 256, 1, 1);
}

// TLAS of this frame on the 1.619 device into its open list (once per frame). g_sceneMutex held.
bool RtRecordTlas(ID3D12GraphicsCommandList4* rl, UINT32 frame, int slot, UINT64 serial) {
	if (frame == g_rt.tlasFrame && g_rt.tlas) {
		return true;
	}
	if (g_rtResolved.empty()) {
		return false;
	}
	UINT64 bytes = g_rtResolved.size() * sizeof(D3D12_RAYTRACING_INSTANCE_DESC);
	if (g_rt.instanceCapacity[slot] < bytes) {
		g_rt.instances[slot] = RtBuffer(g_rt.device.Get(), bytes + bytes / 4, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		g_rt.instanceCapacity[slot] = g_rt.instances[slot] ? bytes + bytes / 4 : 0;
	}
	void* mapped = nullptr;
	if (!g_rt.instances[slot] || FAILED(g_rt.instances[slot]->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return false;
	}
	memcpy(mapped, g_rtResolved.data(), bytes);
	if (!g_rtOmm) {
		auto* written = static_cast<D3D12_RAYTRACING_INSTANCE_DESC*>(mapped);
		for (size_t i = 0; i < g_rtResolved.size(); ++i) {
			written[i].Flags |= D3D12_RAYTRACING_INSTANCE_FLAG_DISABLE_OMMS;
		}
	}
	g_rt.instances[slot]->Unmap(0, nullptr);
	RecordRtWind(rl, slot);
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
	build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
	build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	build.Inputs.NumDescs = static_cast<UINT>(g_rtResolved.size());
	build.Inputs.InstanceDescs = g_rt.instances[slot]->GetGPUVirtualAddress();
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
	g_rt.device->GetRaytracingAccelerationStructurePrebuildInfo(&build.Inputs, &info);
	UINT64 completed = g_rt.toUnityRt->GetCompletedValue();
	for (size_t i = 0; i < g_rt.retired.size();) {
		if (g_rt.retired[i].second <= completed) {
			g_rt.retired.erase(g_rt.retired.begin() + i);
		} else {
			++i;
		}
	}
	// outgrown buffers live on until the traces recorded before this one are done
	auto grow = [&](ComPtr<ID3D12Resource>& buffer, UINT64& capacity, UINT64 size, D3D12_RESOURCE_STATES state) {
		if (buffer && capacity >= size) {
			return true;
		}
		if (buffer) {
			g_rt.retired.emplace_back(buffer, serial - 1);
		}
		capacity = size + size / 4;
		buffer = RtBuffer(g_rt.device.Get(), capacity, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, state);
		return buffer != nullptr;
	};
	if (!grow(g_rt.scratch, g_rt.scratchCapacity, info.ScratchDataSizeInBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS) ||
	    !grow(g_rt.tlas, g_rt.tlasCapacity, info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)) {
		return false;
	}
	build.DestAccelerationStructureData = g_rt.tlas->GetGPUVirtualAddress();
	build.ScratchAccelerationStructureData = g_rt.scratch->GetGPUVirtualAddress();
	rl->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
	D3D12_RESOURCE_BARRIER b = UavBarrier(g_rt.tlas.Get());
	rl->ResourceBarrier(1, &b);
	g_rt.tlasFrame = frame;
	return true;
}

// Render event 3 on the 1.619 device, for the kinds it handles (the sun); false: Unity's device traces it instead
bool TraceRt(const TraceEventData* data, ID3D12CommandQueue* queue) {
	if (!g_rtActive || !g_rtReady || data->kind != kTraceSun || !g_rt.sun || !g_copyRaw.pso) {
		return false;
	}
	UINT64 pixels = static_cast<UINT64>(data->width) * data->height;
	// packed: 16 bytes in (origin, pixel), 4 out (half visibility and distance)
	UINT64 inBytes = pixels * 16, outBytes = pixels * 4;
	// the hand over buffers of earlier traces may still be in use when they grow: wait for the GPU first
	if (g_rt.inSize < inBytes || g_rt.outSize < outBytes) {
		WaitRenderIdle();
		RtWaitIdle();  // the 1.619 queue may still read or write the old ones
		if (!RtEnsureHandover(inBytes, outBytes)) {
			return false;
		}
	}
	if (!g_rt.constants) {
		g_rt.constants = RtBuffer(g_rt.device.Get(), static_cast<UINT64>(kConstantSlot) * RtDevice::kSlots, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE,
		                          D3D12_RESOURCE_STATE_GENERIC_READ);
		if (!g_rt.constants) {
			return false;
		}
	}
	// a slot of the 1.619 device's ring, free once the trace kSlots ago finished
	UINT64 serial = g_rt.serial + 1;
	if (serial > RtDevice::kSlots && g_rt.toUnityRt->GetCompletedValue() < serial - RtDevice::kSlots) {
		return false;  // far behind: Unity's device traces this one
	}
	int slot = static_cast<int>(serial % RtDevice::kSlots);
	int pair = static_cast<int>(serial & 1);  // which of the two hand over buffers this trace uses

	// 1. the 1.619 list first (nothing submitted yet if it cannot be recorded): TLAS once per frame, the trace
	g_rt.allocators[slot]->Reset();
	auto* rl = g_rt.list.Get();
	rl->Reset(g_rt.allocators[slot].Get(), nullptr);
	g_rtCrumbs.Create(g_rt.device.Get());
	UINT crumb = g_rtCrumbs.Begin(rl, "rt sun " + std::to_string(serial));
	bool recorded = false;
	{
		std::lock_guard<std::mutex> lock(g_sceneMutex);
		UINT32 frame;
		frame = data->unityFrame;
		void* mapped = nullptr;
		if (g_rtReady && RtRecordTlas(rl, frame, slot, serial) && SUCCEEDED(g_rt.constants->Map(0, nullptr, &mapped)) && mapped != nullptr) {
			memcpy(static_cast<char*>(mapped) + static_cast<size_t>(slot) * kConstantSlot, data->constants, sizeof(data->constants));
			g_rt.constants->Unmap(0, nullptr);
			ID3D12DescriptorHeap* heaps[] = { g_rt.heap.Get() };
			rl->SetDescriptorHeaps(1, heaps);
			rl->SetComputeRootSignature(g_rt.root.Get());
			rl->SetPipelineState(g_rt.sun.Get());
			rl->SetComputeRootShaderResourceView(0, g_rt.tlas->GetGPUVirtualAddress());
			rl->SetComputeRootUnorderedAccessView(1, g_rt.outRt[pair]->GetGPUVirtualAddress());
			rl->SetComputeRootConstantBufferView(2, g_rt.constants->GetGPUVirtualAddress() + static_cast<UINT64>(slot) * kConstantSlot);
			rl->SetComputeRootUnorderedAccessView(3, g_rt.inRt[pair]->GetGPUVirtualAddress());
			rl->SetComputeRootShaderResourceView(4, g_rt.records->GetGPUVirtualAddress());
			rl->SetComputeRootShaderResourceView(5, g_rt.records->GetGPUVirtualAddress());  // no probe: the sun has no use for it
			rl->SetComputeRootShaderResourceView(6, g_rt.inRt[pair]->GetGPUVirtualAddress());
			rl->SetComputeRootUnorderedAccessView(7, g_rt.outRt[pair]->GetGPUVirtualAddress());
			rl->Dispatch((data->width + 7) / 8, (data->height + 7) / 8, 1);
			recorded = true;
			// the serial counts from here, under the lock: RtWaitIdle (ReleaseRtGeometry, MirrorScene) waits for it,
			// so the BLAS this list references are not freed between the recording and the submit below
			g_rt.serial = serial;
		}
	}
	g_rtCrumbs.End(rl, crumb);
	if (FAILED(rl->Close()) || !recorded) {
		if (recorded) {
			g_rt.toUnityRt->Signal(serial);  // nothing will run for this serial: let the waits pass
		}
		return false;
	}

	// 2. Unity's queue: Prepare's output into this trace's shared input buffer (the trace two ago, which used it, is
	// done: Unity's queue waited for it during the last trace), then "input ready"
	g_crumbTag = "rt pack " + std::to_string(serial);
	auto* list = g_render.Begin();
	if (list == nullptr) {
		g_rt.toUnityRt->Signal(serial);  // the trace is not submitted: let the waits for this serial pass
		return false;
	}
	RtCopyWords(list, data->surface, g_rt.inUnity[pair].Get(), static_cast<UINT>(inBytes / 4));
	g_render.Submit(queue);
	queue->Signal(g_rt.toRtUnity.Get(), serial);

	// 3. the 1.619 queue: wait for the input, trace, then "result ready"
	g_rt.queue->Wait(g_rt.toRtRt.Get(), serial);
	ID3D12CommandList* lists[] = { rl };
	g_rt.queue->ExecuteCommandLists(1, lists);
	g_rt.queue->Signal(g_rt.toUnityRt.Get(), serial);

	// 4. Unity's queue: the previous trace's result (long done, unless the GPU is far behind) into the mod's buffer;
	// none for the first trace, which leaves Prepare's defaults (lit)
	UINT64 previous = serial - 1;
	if (previous > 0 && g_rt.lastTraced == previous) {
		queue->Wait(g_rt.toUnityUnity.Get(), previous);
		g_crumbTag = "rt unpack " + std::to_string(previous);
		list = g_render.Begin();
		if (list != nullptr) {
			RtCopyWords(list, g_rt.outUnity[previous & 1].Get(), data->output, static_cast<UINT>(outBytes / 4));
			g_render.Submit(queue);
		}
	}
	g_rt.lastTraced = serial;
	++g_rt.traces;
	return true;
}

}  // namespace

// Makes the DXR 1.2 device (Agility SDK 1.619 from sdkPath, an absolute folder ending in a slash) for Unity's adapter
// and takes the current scene over. Returns 1, or < 0 (status in NFX_Native_RtStatus).
NFX_EXPORT int NFX_Native_StartRt(const char* sdkPath) {
	if (!g_device || g_dead) {
		g_rtStatus = "no Unity device";
		return -1;
	}
	std::lock_guard<std::mutex> lock(g_sceneMutex);
	if (!g_rt.device) {
		ComPtr<ID3D12SDKConfiguration1> config;
		ComPtr<ID3D12DeviceFactory> factory;
		ComPtr<IDXGIFactory4> dxgi;
		ComPtr<IDXGIAdapter1> adapter;
		if (FAILED(D3D12GetInterface(kSdkConfiguration, IID_PPV_ARGS(&config))) ||
		    FAILED(config->CreateDeviceFactory(619, sdkPath, IID_PPV_ARGS(&factory))) ||
		    FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&dxgi))) ||
		    FAILED(dxgi->EnumAdapterByLuid(g_device->GetAdapterLuid(), IID_PPV_ARGS(&adapter))) ||
		    FAILED(factory->CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&g_rt.device)))) {
			g_rtStatus = "the Agility SDK 1.619 device could not be made";
			g_rt.device.Reset();
			return -2;
		}
		D3D12_FEATURE_DATA_SHADER_MODEL sm = { D3D_SHADER_MODEL_6_9 };
		g_rt.device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &sm, sizeof(sm));
		D3D12_COMMAND_QUEUE_DESC qd = {};
		qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
		D3D12_DESCRIPTOR_HEAP_DESC hd = {};
		hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
		hd.NumDescriptors = kHeapSize;
		hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
		bool ok = sm.HighestShaderModel >= D3D_SHADER_MODEL_6_9 &&
		          SUCCEEDED(g_rt.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&g_rt.queue))) &&
		          SUCCEEDED(g_rt.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_rt.buildAllocator))) &&
		          SUCCEEDED(g_rt.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_rt.buildAllocator.Get(), nullptr, IID_PPV_ARGS(&g_rt.buildList))) &&
		          SUCCEEDED(g_rt.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_rt.buildFence))) &&
		          SUCCEEDED(g_rt.device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&g_rt.heap))) &&
		          SharedFence(g_device.Get(), g_rt.device.Get(), g_rt.toRtUnity, g_rt.toRtRt) &&
		          SharedFence(g_rt.device.Get(), g_device.Get(), g_rt.toUnityRt, g_rt.toUnityUnity);
		for (auto& a : g_rt.allocators) {
			ok = ok && SUCCEEDED(g_rt.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)));
		}
		ok = ok && SUCCEEDED(g_rt.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_rt.allocators[0].Get(), nullptr, IID_PPV_ARGS(&g_rt.list)));
		if (!ok) {
			g_rtStatus = "the DXR 1.2 device's queue, fences or heap could not be made";
			g_rt = RtDevice();
			return -3;
		}
		g_rt.buildList->Close();
		g_rt.list->Close();
		g_rt.buildEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
		g_rt.heapStep = g_rt.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
		g_rtStatus = "device made, no scene yet";
	}
	if (!g_geometries.empty() && !g_resolved.empty()) {
		int result = MirrorScene();
		if (result != 1) {
			return result;
		}
	}
	return 1;
}

// Whether the DXR 1.2 device uses its opacity micromaps (1) or tests every alpha tested hit in the shader (0)
NFX_EXPORT void NFX_Native_SetRtOmm(int enabled) {
	g_rtOmm = enabled != 0;
}

// Whether the sun traces go to the DXR 1.2 device (1) or Unity's (0)
NFX_EXPORT void NFX_Native_SetRtActive(int active) {
	g_rtActive = active != 0;
	if (!active) {
		g_rt.lastTraced = 0;  // switched back on later: no stale result from before
	}
}

NFX_EXPORT int NFX_Native_RtStatus(char* buffer, int size) {
	std::string s = g_rtStatus;
	if (g_rt.device) {
		char line[96];
		snprintf(line, sizeof(line), "; %zu swaying with the wind; %d sun traces on it", g_rtWind.size(), g_rt.traces);
		s += line;
	}
	return CopyOut(s, buffer, size);
}
