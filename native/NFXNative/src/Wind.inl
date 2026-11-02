// NFXNative: foliage the wind sways (NatureFX/Foliage instances, flag bit 2 of NFX_Native_SetInstances), included in
// NFXNative.cpp before the scene API. NatureFX/Foliage moves its vertices as it draws; the traced shadows of a still
// copy would not move with them, and the leaves would shadow themselves where they sway out of it. So each such
// instance gets its own vertex buffer and BLAS (built with ALLOW_UPDATE from the still geometry): once a frame, on the
// render thread before the TLAS, WindDisplace.hlsl moves its vertices with the same wind and the BLAS is updated.
namespace {

struct WindBlas {
	int geometry = -1;
	float transform[12] = {};
	ComPtr<ID3D12Resource> positions;  // swayed, object space float3 (unordered access)
	ComPtr<ID3D12Resource> blas;
	UINT64 updateScratch = 0;
	D3D12_RAYTRACING_GEOMETRY_DESC desc = {};  // its geometry, with the swayed positions
	bool used = false;
};
std::vector<WindBlas> g_windBlas;
std::vector<int> g_instanceWind;   // per entry of g_instances: index into g_windBlas, -1 still
std::vector<bool> g_instanceSways; // per entry of g_instances: flag bit 2
ComputePipeline g_windDisplace;
ComPtr<ID3D12Resource> g_windScratch;
UINT64 g_windScratchCapacity = 0;
ComPtr<ID3D12Resource> g_windFrames;  // kWindFrameBytes per render slot
constexpr UINT kWindFrameBytes = 256;
float g_windFrame[48] = {};            // NFX_Native_SetWind
std::atomic<bool> g_windActive{ false };
constexpr size_t kWindBudget = 64;  // wind BLAS swayed and updated a frame (RecordWind)
size_t g_windCursor = 0;
std::mutex g_windMutex;

// The per frame updates' scratch, for the largest wind BLAS: only here, while the render thread's work is waited for
// (NFX_Native_BuildScene did before this)
bool EnsureWindScratch() {
	UINT64 size = 0;
	for (const auto& w : g_windBlas) {
		size = w.updateScratch > size ? w.updateScratch : size;
	}
	return size == 0 || EnsureBuffer(g_windScratch, g_windScratchCapacity, size, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
	                                 D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
}

// The BLAS of the swaying instances, after NFX_Native_BuildScene built the still ones. Reuses those of the instances
// that kept their geometry and place. g_sceneMutex held. Fills windBlasOf (per instance, -1 none).
bool BuildWindBlas() {
	g_instanceWind.assign(g_instances.size(), -1);
	for (auto& w : g_windBlas) {
		w.used = false;
	}
	bool any = false;
	for (size_t i = 0; i < g_instances.size(); ++i) {
		any = any || (i < g_instanceSways.size() && g_instanceSways[i]);
	}
	if (!any) {
		g_windBlas.clear();
		return true;
	}
	if (!g_windDisplace.Create(g_WindDisplace, sizeof(g_WindDisplace), 28, 1)) {
		return false;
	}
	std::vector<size_t> toBuild;
	for (size_t i = 0; i < g_instances.size(); ++i) {
		if (i >= g_instanceSways.size() || !g_instanceSways[i]) {
			continue;
		}
		int gi = g_instanceGeometry[i];
		const auto& g = g_geometries[gi];
		if (g.released || !g.blas || !g.ownPositions || !g.ownIndices) {
			continue;
		}
		int found = -1;
		for (size_t k = 0; k < g_windBlas.size(); ++k) {
			if (!g_windBlas[k].used && g_windBlas[k].geometry == gi && memcmp(g_windBlas[k].transform, g_instances[i].Transform, sizeof(float) * 12) == 0) {
				found = static_cast<int>(k);
				break;
			}
		}
		if (found < 0) {
			WindBlas w;
			w.geometry = gi;
			memcpy(w.transform, g_instances[i].Transform, sizeof(w.transform));
			g_windBlas.push_back(w);
			found = static_cast<int>(g_windBlas.size() - 1);
			toBuild.push_back(static_cast<size_t>(found));
		}
		g_windBlas[found].used = true;
		g_instanceWind[i] = found;
	}
	// drop those no instance uses any more (the render thread's work was waited for by NFX_Native_BuildScene)
	std::vector<WindBlas> kept;
	std::vector<int> remap(g_windBlas.size(), -1);
	for (size_t k = 0; k < g_windBlas.size(); ++k) {
		if (g_windBlas[k].used) {
			remap[k] = static_cast<int>(kept.size());
			kept.push_back(g_windBlas[k]);
		}
	}
	for (auto& v : g_instanceWind) {
		v = v >= 0 ? remap[v] : -1;
	}
	for (auto& k : toBuild) {
		k = static_cast<size_t>(remap[k]);
	}
	g_windBlas = kept;
	if (toBuild.empty()) {
		return EnsureWindScratch();
	}

	// build the new ones from the still geometry, updatable
	UINT64 scratchSize = 0;
	std::vector<D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO> info(toBuild.size());
	for (size_t n = 0; n < toBuild.size(); ++n) {
		auto& w = g_windBlas[toBuild[n]];
		const auto& g = g_geometries[w.geometry];
		w.desc = g.desc;
		w.desc.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
		w.desc.Triangles.VertexBuffer.StartAddress = g.ownPositions.Address();
		w.desc.Triangles.VertexBuffer.StrideInBytes = 12;
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
		inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
		inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		inputs.NumDescs = 1;
		inputs.pGeometryDescs = &w.desc;
		g_device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info[n]);
		scratchSize = info[n].ScratchDataSizeInBytes > scratchSize ? info[n].ScratchDataSizeInBytes : scratchSize;
		w.updateScratch = info[n].UpdateScratchDataSizeInBytes;
		w.positions = CreateBuffer(static_cast<UINT64>(g.desc.Triangles.VertexCount) * 12, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                           D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
		w.blas = CreateBuffer(info[n].ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
		                      D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
		if (!w.positions || !w.blas) {
			return false;
		}
	}
	ComPtr<ID3D12Resource> scratch = CreateBuffer(scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
	if (!scratch) {
		return false;
	}
	g_gpu.Begin();
	for (size_t n = 0; n < toBuild.size(); ++n) {
		auto& w = g_windBlas[toBuild[n]];
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
		build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		build.Inputs.NumDescs = 1;
		build.Inputs.pGeometryDescs = &w.desc;
		build.DestAccelerationStructureData = w.blas->GetGPUVirtualAddress();
		build.ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress();
		g_gpu.list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
		g_gpu.list->ResourceBarrier(1, &b);
		// from now on the updates read the swayed copy
		w.desc.Triangles.VertexBuffer.StartAddress = w.positions->GetGPUVirtualAddress();
	}
	return g_gpu.SubmitAndWait() && EnsureWindScratch();
}

// WindDisplace.hlsl's constants for one instance: its vertex count, object to world (3x4, row major) and the inverse
// of its 3x3 part
void WindConstants(const float (*m)[4], UINT vertices, float constants[28]) {
	float a = m[0][0], b = m[0][1], c = m[0][2], d = m[1][0], e = m[1][1], f = m[1][2], gg = m[2][0], h = m[2][1], ii = m[2][2];
	float det = a * (e * ii - f * h) - b * (d * ii - f * gg) + c * (d * h - e * gg);
	float inv = fabsf(det) > 1e-12f ? 1.0f / det : 0.0f;
	memset(constants, 0, sizeof(float) * 28);
	memcpy(&constants[0], &vertices, 4);
	memcpy(&constants[4], m, sizeof(float) * 12);
	constants[16] = (e * ii - f * h) * inv; constants[17] = (c * h - b * ii) * inv; constants[18] = (b * f - c * e) * inv;
	constants[20] = (f * gg - d * ii) * inv; constants[21] = (a * ii - c * gg) * inv; constants[22] = (c * d - a * f) * inv;
	constants[24] = (d * h - e * gg) * inv; constants[25] = (b * gg - a * h) * inv; constants[26] = (a * e - b * d) * inv;
}

// Render thread, once a frame before the TLAS: sway every wind instance and update its BLAS. resolved: the instances
// as the TLAS takes them (their transforms now). g_sceneMutex held.
void RecordWind(ID3D12GraphicsCommandList4* list, int slot, const std::vector<D3D12_RAYTRACING_INSTANCE_DESC>& resolved) {
	if (g_windBlas.empty() || !g_windActive || !g_windDisplace.pso) {
		return;
	}
	if (!g_windFrames) {
		g_windFrames = CreateBuffer(static_cast<UINT64>(kWindFrameBytes) * 16, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
		if (!g_windFrames) {
			return;
		}
	}
	// the update scratch is sized by BuildWindBlas while nothing is in flight: replacing it here freed the one the
	// frames still queued used (GPU hang, device removed)
	if (!g_windScratch) {
		return;
	}
	for (const auto& w : g_windBlas) {
		if (w.updateScratch > g_windScratchCapacity) {
			return;
		}
	}
	void* mapped = nullptr;
	if (FAILED(g_windFrames->Map(0, nullptr, &mapped)) || mapped == nullptr) {
		return;
	}
	{
		std::lock_guard<std::mutex> lock(g_windMutex);
		memcpy(static_cast<char*>(mapped) + static_cast<size_t>(slot % 16) * kWindFrameBytes, g_windFrame, sizeof(g_windFrame));
	}
	g_windFrames->Unmap(0, nullptr);
	D3D12_GPU_VIRTUAL_ADDRESS frame = g_windFrames->GetGPUVirtualAddress() + static_cast<UINT64>(slot % 16) * kWindFrameBytes;

	// Only kWindBudget of the wind BLAS a frame, in turn: each update waits for the one before (they share the
	// scratch), and a forest of 500 swaying trees updated all at once took most of the frame. A tree's traced
	// shadow then follows its sway every few frames, which the shadow filter's history hides.
	std::vector<int> firstInstance(g_windBlas.size(), -1);
	for (size_t i = 0; i < g_instanceWind.size() && i < resolved.size(); ++i) {
		int k = g_instanceWind[i];
		if (k >= 0 && firstInstance[k] < 0) {
			firstInstance[k] = static_cast<int>(i);
		}
	}
	const size_t count = g_windBlas.size();
	const size_t budget = count < kWindBudget ? count : kWindBudget;
	std::vector<bool> chosen(count, false);
	for (size_t n = 0; n < budget; ++n) {
		chosen[(g_windCursor + n) % count] = true;
	}
	g_windCursor = (g_windCursor + budget) % count;

	// 1. sway the vertices of this frame's wind BLAS (its first instance's place)
	list->SetComputeRootSignature(g_windDisplace.root.Get());
	list->SetPipelineState(g_windDisplace.pso.Get());
	list->SetComputeRootShaderResourceView(3, frame);
	for (size_t k = 0; k < count; ++k) {
		if (!chosen[k] || firstInstance[k] < 0) {
			continue;
		}
		size_t i = static_cast<size_t>(firstInstance[k]);
		const auto& w = g_windBlas[k];
		const auto& g = g_geometries[w.geometry];
		if (!g.ownPositions) {
			continue;  // released since the scene was built (built again soon)
		}
		UINT vertices = g.desc.Triangles.VertexCount;
		float constants[28];
		WindConstants(resolved[i].Transform, vertices, constants);
		list->SetComputeRootShaderResourceView(0, g.ownPositions.Address());
		list->SetComputeRootUnorderedAccessView(1, w.positions->GetGPUVirtualAddress());
		list->SetComputeRoot32BitConstants(2, 28, constants, 0);
		list->Dispatch((vertices + 63) / 64, 1, 1);
	}
	// 2. the swayed vertices are read by the updates: unordered access -> shader resource, and back after
	std::vector<D3D12_RESOURCE_BARRIER> toRead, toWrite;
	for (size_t k = 0; k < count; ++k) {
		if (!chosen[k]) {
			continue;
		}
		const auto& w = g_windBlas[k];
		toRead.push_back(Transition(w.positions.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
		toWrite.push_back(Transition(w.positions.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
	}
	list->ResourceBarrier(static_cast<UINT>(toRead.size()), toRead.data());
	for (size_t k = 0; k < count; ++k) {
		if (!chosen[k] || firstInstance[k] < 0) {
			continue;
		}
		auto& w = g_windBlas[k];
		// a geometry released since the scene was built: its index buffer is gone, the update would read freed memory
		const auto& g = g_geometries[w.geometry];
		if (g.released || !g.ownIndices || !g.ownPositions) {
			continue;
		}
		D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
		build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
		build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD |
		                     D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE;
		build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
		build.Inputs.NumDescs = 1;
		build.Inputs.pGeometryDescs = &w.desc;
		build.DestAccelerationStructureData = w.blas->GetGPUVirtualAddress();
		build.SourceAccelerationStructureData = w.blas->GetGPUVirtualAddress();
		build.ScratchAccelerationStructureData = g_windScratch->GetGPUVirtualAddress();
		list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
		D3D12_RESOURCE_BARRIER b = UavBarrier(nullptr);
		list->ResourceBarrier(1, &b);
	}
	list->ResourceBarrier(static_cast<UINT>(toWrite.size()), toWrite.data());
}

}  // namespace

// The wind as NatureFX/Foliage draws it, once a frame (48 floats: see WindDisplace.hlsl); active 0: the ray traced
// foliage stays where its last frame left it
NFX_EXPORT void NFX_Native_SetWind(const float* frame, int active) {
	std::lock_guard<std::mutex> lock(g_windMutex);
	memcpy(g_windFrame, frame, sizeof(g_windFrame));
	g_windActive = active != 0;
}
