// DXR 1.2 probe, shared by Dxr12Probe.exe (Dxr12Probe.cpp) and NFXNative (NFX_Native_Dxr12Probe, in the game's
// process): an Agility SDK 1.619 device factory, an independent device with Shader Model 6.9 / ray tracing tier 1.2,
// an opacity micromap traced inline (ProbeOmm.hlsl) and a ray generation shader with shader execution reordering
// (ProbeSer.hlsl). Include after d3d12.h / dxgi1_6.h from the Agility SDK 1.619 headers, with ProbeOmm.h and
// ProbeSer.h on the include path; define ProbeLog(fmt, ...) before including.
#pragma once
#include <wrl/client.h>
#include <cstring>
#include <string>
#include <vector>

#include "ProbeOmm.h"  // g_ProbeOmm: cs_6_9
#include "ProbeSer.h"  // g_ProbeSer: lib_6_9

namespace dxr12 {
using Microsoft::WRL::ComPtr;

#define CHECK(expr, what)                                                                     \
	do {                                                                                      \
		HRESULT hr_ = (expr);                                                                 \
		if (FAILED(hr_)) {                                                                    \
			ProbeLog("FAIL %s (hr 0x%08lX)\n", what, static_cast<unsigned long>(hr_));   \
			return false;                                                                     \
		}                                                                                     \
	} while (0)

struct Gpu {
	ComPtr<ID3D12Device5> device;
	ComPtr<ID3D12CommandQueue> queue;
	ComPtr<ID3D12CommandAllocator> allocator;
	ComPtr<ID3D12GraphicsCommandList4> list;
	ComPtr<ID3D12Fence> fence;
	UINT64 fenceValue = 0;
};

static ComPtr<ID3D12Resource> Buffer(ID3D12Device* device, UINT64 size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags,
                                     D3D12_RESOURCE_STATES state) {
	D3D12_HEAP_PROPERTIES hp = {};
	hp.Type = heap;
	D3D12_RESOURCE_DESC rd = {};
	rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
	rd.Width = size < 256 ? 256 : size;
	rd.Height = rd.DepthOrArraySize = rd.MipLevels = 1;
	rd.SampleDesc.Count = 1;
	rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
	rd.Flags = flags;
	ComPtr<ID3D12Resource> r;
	if (FAILED(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, state, nullptr, IID_PPV_ARGS(&r)))) {
		return nullptr;
	}
	return r;
}

static ComPtr<ID3D12Resource> Upload(ID3D12Device* device, const void* data, UINT64 size) {
	auto r = Buffer(device, size, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
	void* mapped = nullptr;
	if (r && SUCCEEDED(r->Map(0, nullptr, &mapped))) {
		std::memcpy(mapped, data, static_cast<size_t>(size));
		r->Unmap(0, nullptr);
	}
	return r;
}

static ComPtr<ID3D12Resource> Uav(ID3D12Device* device, UINT64 size, D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_UNORDERED_ACCESS) {
	return Buffer(device, size, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, state);
}

static void UavBarrier(ID3D12GraphicsCommandList* list) {
	D3D12_RESOURCE_BARRIER b = {};
	b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
	list->ResourceBarrier(1, &b);
}

static bool Run(Gpu& g) {
	CHECK(g.list->Close(), "close command list");
	ID3D12CommandList* lists[] = { g.list.Get() };
	g.queue->ExecuteCommandLists(1, lists);
	CHECK(g.queue->Signal(g.fence.Get(), ++g.fenceValue), "signal");
	HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	g.fence->SetEventOnCompletion(g.fenceValue, e);
	if (WaitForSingleObject(e, 10000) != WAIT_OBJECT_0) {
		CloseHandle(e);
		ProbeLog("FAIL GPU did not finish (device removed reason 0x%08lX)\n", static_cast<unsigned long>(g.device->GetDeviceRemovedReason()));
		return false;
	}
	CloseHandle(e);
	CHECK(g.allocator->Reset(), "reset allocator");
	CHECK(g.list->Reset(g.allocator.Get(), nullptr), "reset list");
	return true;
}

static bool ReadBack(Gpu& g, ID3D12Resource* source, void* out, UINT64 size) {
	auto readback = Buffer(g.device.Get(), size, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
	D3D12_RESOURCE_BARRIER b = {};
	b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
	b.Transition.pResource = source;
	b.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
	b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
	g.list->ResourceBarrier(1, &b);
	g.list->CopyBufferRegion(readback.Get(), 0, source, 0, size);
	std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
	g.list->ResourceBarrier(1, &b);
	if (!Run(g)) {
		return false;
	}
	void* mapped = nullptr;
	CHECK(readback->Map(0, nullptr, &mapped), "map readback");
	std::memcpy(out, mapped, static_cast<size_t>(size));
	readback->Unmap(0, nullptr);
	return true;
}

// Builds an acceleration structure (BLAS, TLAS or micromap array) into a new buffer
static ComPtr<ID3D12Resource> Build(Gpu& g, const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS& inputs,
                                    std::vector<ComPtr<ID3D12Resource>>& keep, const char* what) {
	D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
	g.device->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
	if (info.ResultDataMaxSizeInBytes == 0) {
		ProbeLog("FAIL %s: prebuild info says size 0 (not supported)\n", what);
		return nullptr;
	}
	auto result = Uav(g.device.Get(), info.ResultDataMaxSizeInBytes, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
	auto scratch = Uav(g.device.Get(), info.ScratchDataSizeInBytes > 0 ? info.ScratchDataSizeInBytes : 256);
	keep.push_back(scratch);
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC desc = {};
	desc.Inputs = inputs;
	desc.DestAccelerationStructureData = result->GetGPUVirtualAddress();
	desc.ScratchAccelerationStructureData = scratch->GetGPUVirtualAddress();
	g.list->BuildRaytracingAccelerationStructure(&desc, 0, nullptr);
	UavBarrier(g.list.Get());
	ProbeLog("  %s: %llu bytes\n", what, static_cast<unsigned long long>(info.ResultDataMaxSizeInBytes));
	return result;
}

// Two triangles side by side, both non-opaque: the first with a micromap that says transparent, the second with the
// special index "fully opaque". Returns the TLAS.
static ComPtr<ID3D12Resource> BuildScene(Gpu& g, std::vector<ComPtr<ID3D12Resource>>& keep) {
	const float vertices[] = {
		-2.0f, -1.0f, 0.0f,  -0.5f, -1.0f, 0.0f,  -1.25f, 1.0f, 0.0f,
		 0.5f, -1.0f, 0.0f,   2.0f, -1.0f, 0.0f,   1.25f, 1.0f, 0.0f,
	};
	auto vb = Upload(g.device.Get(), vertices, sizeof(vertices));
	keep.push_back(vb);

	// the micromap array: one micromap, subdivision level 0 (one micro triangle), 2 states, its one bit 0 = transparent
	const UINT ommData[64] = { 0u };
	auto ommInput = Upload(g.device.Get(), ommData, sizeof(ommData));
	D3D12_RAYTRACING_OPACITY_MICROMAP_DESC ommDesc = {};
	ommDesc.ByteOffset = 0;
	ommDesc.SubdivisionLevel = 0;
	ommDesc.Format = D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_2_STATE;
	auto ommDescs = Upload(g.device.Get(), &ommDesc, sizeof(ommDesc));
	keep.push_back(ommInput);
	keep.push_back(ommDescs);
	D3D12_RAYTRACING_OPACITY_MICROMAP_HISTOGRAM_ENTRY histogram = { 1, 0, D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_2_STATE };
	D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_DESC arrayDesc = {};
	arrayDesc.NumOmmHistogramEntries = 1;
	arrayDesc.pOmmHistogram = &histogram;
	arrayDesc.InputBuffer = ommInput->GetGPUVirtualAddress();
	arrayDesc.PerOmmDescs.StartAddress = ommDescs->GetGPUVirtualAddress();
	arrayDesc.PerOmmDescs.StrideInBytes = sizeof(D3D12_RAYTRACING_OPACITY_MICROMAP_DESC);
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS ommInputs = {};
	ommInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_OPACITY_MICROMAP_ARRAY;
	ommInputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
	ommInputs.NumDescs = 1;
	ommInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	ommInputs.pOpacityMicromapArrayDesc = &arrayDesc;
	auto ommArray = Build(g, ommInputs, keep, "opacity micromap array");
	if (!ommArray) {
		return nullptr;
	}
	keep.push_back(ommArray);

	// per triangle: micromap 0, then the special index -2 (fully opaque)
	const INT ommIndices[2] = { 0, static_cast<INT>(D3D12_RAYTRACING_OPACITY_MICROMAP_SPECIAL_INDEX_FULLY_OPAQUE) };
	auto ommIndexBuffer = Upload(g.device.Get(), ommIndices, sizeof(ommIndices));
	keep.push_back(ommIndexBuffer);

	D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles = {};
	triangles.VertexBuffer.StartAddress = vb->GetGPUVirtualAddress();
	triangles.VertexBuffer.StrideInBytes = 12;
	triangles.VertexCount = 6;
	triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
	D3D12_RAYTRACING_GEOMETRY_OMM_LINKAGE_DESC linkage = {};
	linkage.OpacityMicromapIndexBuffer.StartAddress = ommIndexBuffer->GetGPUVirtualAddress();
	linkage.OpacityMicromapIndexBuffer.StrideInBytes = 4;
	linkage.OpacityMicromapIndexFormat = DXGI_FORMAT_R32_UINT;
	linkage.OpacityMicromapBaseLocation = 0;
	linkage.OpacityMicromapArray = ommArray->GetGPUVirtualAddress();
	D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
	geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_OMM_TRIANGLES;
	geometry.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_NONE; // non-opaque: without the micromap both would be candidates
	geometry.OmmTriangles.pTriangles = &triangles;
	geometry.OmmTriangles.pOmmLinkage = &linkage;
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS blasInputs = {};
	blasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
	blasInputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
	blasInputs.NumDescs = 1;
	blasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	blasInputs.pGeometryDescs = &geometry;
	auto blas = Build(g, blasInputs, keep, "BLAS with micromaps");
	if (!blas) {
		return nullptr;
	}
	keep.push_back(blas);

	D3D12_RAYTRACING_INSTANCE_DESC instance = {};
	instance.Transform[0][0] = instance.Transform[1][1] = instance.Transform[2][2] = 1.0f;
	instance.InstanceMask = 0xFF;
	instance.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE;
	instance.AccelerationStructure = blas->GetGPUVirtualAddress();
	auto instances = Upload(g.device.Get(), &instance, sizeof(instance));
	keep.push_back(instances);
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tlasInputs = {};
	tlasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	tlasInputs.NumDescs = 1;
	tlasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	tlasInputs.InstanceDescs = instances->GetGPUVirtualAddress();
	return Build(g, tlasInputs, keep, "TLAS");
}

static bool RootSignature(ID3D12Device* device, ComPtr<ID3D12RootSignature>& root) {
	D3D12_ROOT_PARAMETER params[2] = {};
	params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
	params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
	D3D12_ROOT_SIGNATURE_DESC desc = {};
	desc.NumParameters = 2;
	desc.pParameters = params;
	ComPtr<ID3DBlob> blob, error;
	CHECK(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error), "serialize root signature");
	CHECK(device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root)), "root signature");
	return true;
}

// output: 16 bytes, unordered access state (null: a new buffer). The results stay in it for another device to read.
static bool TestOmm(Gpu& g, ID3D12Resource* tlas, ID3D12RootSignature* root, ID3D12Resource* sharedOutput = nullptr) {
	D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
	pso.pRootSignature = root;
	pso.CS = { g_ProbeOmm, sizeof(g_ProbeOmm) };
	ComPtr<ID3D12PipelineState> pipeline;
	CHECK(g.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&pipeline)), "SM 6.9 compute pipeline (opacity micromaps)");
	ComPtr<ID3D12Resource> output = sharedOutput ? sharedOutput : Uav(g.device.Get(), 16);
	g.list->SetComputeRootSignature(root);
	g.list->SetPipelineState(pipeline.Get());
	g.list->SetComputeRootShaderResourceView(0, tlas->GetGPUVirtualAddress());
	g.list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	g.list->Dispatch(1, 1, 1);
	UavBarrier(g.list.Get());
	struct { float t; UINT candidates; } r[2] = {};
	if (!ReadBack(g, output.Get(), r, sizeof(r))) {
		return false;
	}
	ProbeLog("  micromap transparent triangle: t %.3f, %u candidates (expect -1, 0)\n", r[0].t, r[0].candidates);
	ProbeLog("  special index opaque triangle: t %.3f, %u candidates (expect 1, 0)\n", r[1].t, r[1].candidates);
	return r[0].t < 0.0f && r[0].candidates == 0 && r[1].t > 0.99f && r[1].t < 1.01f && r[1].candidates == 0;
}

static bool TestSer(Gpu& g, ID3D12Resource* tlas, ID3D12RootSignature* root) {
	D3D12_EXPORT_DESC exports[3] = { { L"RayGen", nullptr, D3D12_EXPORT_FLAG_NONE }, { L"Hit", nullptr, D3D12_EXPORT_FLAG_NONE },
	                                 { L"Miss", nullptr, D3D12_EXPORT_FLAG_NONE } };
	D3D12_DXIL_LIBRARY_DESC library = { { g_ProbeSer, sizeof(g_ProbeSer) }, 3, exports };
	D3D12_HIT_GROUP_DESC hitGroup = { L"HitGroup", D3D12_HIT_GROUP_TYPE_TRIANGLES, nullptr, L"Hit", nullptr };
	D3D12_RAYTRACING_SHADER_CONFIG shaderConfig = { 4, 8 };
	D3D12_GLOBAL_ROOT_SIGNATURE globalRoot = { root };
	D3D12_RAYTRACING_PIPELINE_CONFIG pipelineConfig = { 1 };
	D3D12_STATE_SUBOBJECT subobjects[] = {
		{ D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, &library },
		{ D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, &hitGroup },
		{ D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, &shaderConfig },
		{ D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, &globalRoot },
		{ D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, &pipelineConfig },
	};
	D3D12_STATE_OBJECT_DESC desc = { D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, _countof(subobjects), subobjects };
	ComPtr<ID3D12StateObject> pipeline;
	CHECK(g.device->CreateStateObject(&desc, IID_PPV_ARGS(&pipeline)), "SM 6.9 ray tracing pipeline (SER)");
	ComPtr<ID3D12StateObjectProperties> props;
	CHECK(pipeline.As(&props), "state object properties");

	// shader tables: one record each, 64 byte aligned
	std::vector<unsigned char> table(192, 0);
	std::memcpy(&table[0], props->GetShaderIdentifier(L"RayGen"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
	std::memcpy(&table[64], props->GetShaderIdentifier(L"Miss"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
	std::memcpy(&table[128], props->GetShaderIdentifier(L"HitGroup"), D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
	auto tableBuffer = Upload(g.device.Get(), table.data(), table.size());
	auto output = Uav(g.device.Get(), 16);
	D3D12_DISPATCH_RAYS_DESC rays = {};
	D3D12_GPU_VIRTUAL_ADDRESS base = tableBuffer->GetGPUVirtualAddress();
	rays.RayGenerationShaderRecord = { base, 32 };
	rays.MissShaderTable = { base + 64, 32, 32 };
	rays.HitGroupTable = { base + 128, 32, 32 };
	rays.Width = 3;
	rays.Height = rays.Depth = 1;
	g.list->SetComputeRootSignature(root);
	g.list->SetPipelineState1(pipeline.Get());
	g.list->SetComputeRootShaderResourceView(0, tlas->GetGPUVirtualAddress());
	g.list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	g.list->DispatchRays(&rays);
	UavBarrier(g.list.Get());
	float t[3] = {};
	if (!ReadBack(g, output.Get(), t, sizeof(t))) {
		return false;
	}
	ProbeLog("  SER rays: %.3f %.3f %.3f (expect 1, 1, -2)\n", t[0], t[1], t[2]);
	return t[0] > 0.99f && t[0] < 1.01f && t[1] > 0.99f && t[1] < 1.01f && t[2] == -2.0f;
}


// Everything before the two tests, on a device made by the 1.619 factory for this adapter
static bool ProbeAll(ID3D12DeviceFactory* factory, IDXGIAdapter1* adapter, Gpu& g, std::vector<ComPtr<ID3D12Resource>>& keep,
                     ComPtr<ID3D12Resource>& tlas, ComPtr<ID3D12RootSignature>& root) {
	HRESULT hr = factory->CreateDevice(adapter, D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&g.device));
	if (FAILED(hr)) {
		ProbeLog("FAIL CreateDevice on the 1.619 runtime hr 0x%08lX\n", static_cast<unsigned long>(hr));
		return false;
	}
	ProbeLog("OK   independent device on the 1.619 runtime\n");
	D3D12_FEATURE_DATA_SHADER_MODEL sm = { D3D_SHADER_MODEL_6_9 };
	g.device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &sm, sizeof(sm));
	D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5 = {};
	g.device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &o5, sizeof(o5));
	D3D12_FEATURE_DATA_D3D12_OPTIONS22 o22 = {};
	bool haveO22 = SUCCEEDED(g.device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS22, &o22, sizeof(o22)));
	ProbeLog("     shader model %d.%d, ray tracing tier %d.%d, SER actually reorders: %s\n", sm.HighestShaderModel >> 4, sm.HighestShaderModel & 15,
	         o5.RaytracingTier / 10, o5.RaytracingTier % 10, haveO22 ? (o22.ShaderExecutionReorderingActuallyReorders ? "yes" : "no") : "unknown");
	if (sm.HighestShaderModel < D3D_SHADER_MODEL_6_9 || o5.RaytracingTier < D3D12_RAYTRACING_TIER_1_2) {
		ProbeLog("FAIL shader model 6.9 and ray tracing tier 1.2\n");
		return false;
	}
	D3D12_COMMAND_QUEUE_DESC qd = {};
	qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
	if (FAILED(g.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&g.queue))) ||
	    FAILED(g.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g.allocator))) ||
	    FAILED(g.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g.allocator.Get(), nullptr, IID_PPV_ARGS(&g.list))) ||
	    FAILED(g.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g.fence)))) {
		ProbeLog("FAIL command objects\n");
		return false;
	}
	tlas = BuildScene(g, keep);
	if (!tlas || !Run(g)) {
		ProbeLog("FAIL opacity micromap scene\n");
		return false;
	}
	ProbeLog("OK   opacity micromap array, BLAS and TLAS built\n");
	return RootSignature(g.device.Get(), root);
}

} // namespace dxr12
