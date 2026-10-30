// DXR 1.2 probe: checks NFXNative's micromap baking (BakeOmm.hlsl) against the mask it was baked from. A unit quad
// (two triangles, uv = xy) with a 40 x 40 byte mask holding a disc is baked at level 4, built into a BLAS with the
// micromaps and traced with a 64 x 64 grid of rays (ProbeOmmCheck.hlsl). Wherever the micromap decided (opaque or
// transparent without asking), the mask must agree there; a wrong micro-triangle order or bit layout disagrees
// about half the time. Include after Dxr12Probe.h, with BakeOmm.h and ProbeOmmCheck.h on the include path.
#pragma once
#include "BakeOmm.h"        // g_BakeOmm: native/NFXNative/shaders/BakeOmm.hlsl
#include "ProbeOmmCheck.h"  // g_ProbeOmmCheck: cs_6_9

namespace dxr12 {

constexpr UINT kMaskSize = 40;  // finer than the level 4 micro-triangles: some see both inside and outside

static bool MaskSolid(const unsigned char* mask, float u, float v) {
	u -= static_cast<float>(static_cast<int>(u));
	v -= static_cast<float>(static_cast<int>(v));
	UINT x = static_cast<UINT>(u * kMaskSize), y = static_cast<UINT>(v * kMaskSize);
	x = x < kMaskSize ? x : kMaskSize - 1;
	y = y < kMaskSize ? y : kMaskSize - 1;
	return mask[y * kMaskSize + x] / 255.0f >= 0.5f;
}

static bool TestOmmBake(Gpu& g) {
	// the quad and its disc mask
	const float positions[] = { 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f, 0.0f };
	const float uvs[] = { 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f };
	const UINT indices[] = { 0, 1, 2, 1, 3, 2 };
	unsigned char mask[kMaskSize * kMaskSize];
	for (UINT y = 0; y < kMaskSize; ++y) {
		for (UINT x = 0; x < kMaskSize; ++x) {
			float dx = (x + 0.5f) / kMaskSize - 0.45f, dy = (y + 0.5f) / kMaskSize - 0.55f;
			mask[y * kMaskSize + x] = dx * dx + dy * dy < 0.33f * 0.33f ? 255 : 0;
		}
	}
	std::vector<ComPtr<ID3D12Resource>> keep;
	auto vb = Upload(g.device.Get(), positions, sizeof(positions));
	auto uvBuffer = Upload(g.device.Get(), uvs, sizeof(uvs));
	auto ib = Upload(g.device.Get(), indices, sizeof(indices));
	auto maskBuffer = Upload(g.device.Get(), mask, sizeof(mask));
	constexpr UINT kLevel = 4, kTriangles = 2;
	constexpr UINT kBytesPerTriangle = (1u << (2 * kLevel)) / 4;  // 2 bits per micro-triangle
	auto data = Uav(g.device.Get(), kBytesPerTriangle * kTriangles);  // committed: zeroed

	// bake
	D3D12_ROOT_PARAMETER params[5] = {};
	for (int i = 0; i < 3; ++i) {
		params[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
		params[i].Descriptor.ShaderRegister = i;
	}
	params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
	params[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
	params[4].Constants.Num32BitValues = 12;
	D3D12_ROOT_SIGNATURE_DESC rootDesc = { 5, params, 0, nullptr, D3D12_ROOT_SIGNATURE_FLAG_NONE };
	ComPtr<ID3DBlob> blob, error;
	ComPtr<ID3D12RootSignature> bakeRoot;
	CHECK(D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error), "serialize bake root signature");
	CHECK(g.device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&bakeRoot)), "bake root signature");
	D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
	pso.pRootSignature = bakeRoot.Get();
	pso.CS = { g_BakeOmm, sizeof(g_BakeOmm) };
	ComPtr<ID3D12PipelineState> bake;
	CHECK(g.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&bake)), "bake pipeline");
	UINT threads = kTriangles << (2 * kLevel);
	UINT groups = (threads + 63) / 64;
	float st[4] = { 1.0f, 1.0f, 0.0f, 0.0f };
	float cutoff = 0.5f, alphaScale = 1.0f;
	UINT constants[12] = { kTriangles, kLevel, kMaskSize, kMaskSize };
	std::memcpy(&constants[4], st, 16);
	std::memcpy(&constants[8], &cutoff, 4);
	std::memcpy(&constants[9], &alphaScale, 4);
	constants[10] = groups;
	g.list->SetComputeRootSignature(bakeRoot.Get());
	g.list->SetPipelineState(bake.Get());
	g.list->SetComputeRootShaderResourceView(0, ib->GetGPUVirtualAddress());
	g.list->SetComputeRootShaderResourceView(1, uvBuffer->GetGPUVirtualAddress());
	g.list->SetComputeRootShaderResourceView(2, maskBuffer->GetGPUVirtualAddress());
	g.list->SetComputeRootUnorderedAccessView(3, data->GetGPUVirtualAddress());
	g.list->SetComputeRoot32BitConstants(4, 12, constants, 0);
	g.list->Dispatch(groups, 1, 1);
	D3D12_RESOURCE_BARRIER toRead = {};
	toRead.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
	toRead.Transition.pResource = data.Get();
	toRead.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
	toRead.Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
	g.list->ResourceBarrier(1, &toRead);

	// micromap array, BLAS with it, TLAS
	D3D12_RAYTRACING_OPACITY_MICROMAP_DESC descs[kTriangles] = {};
	for (UINT t = 0; t < kTriangles; ++t) {
		descs[t].ByteOffset = t * kBytesPerTriangle;
		descs[t].SubdivisionLevel = kLevel;
		descs[t].Format = D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_4_STATE;
	}
	auto descBuffer = Upload(g.device.Get(), descs, sizeof(descs));
	D3D12_RAYTRACING_OPACITY_MICROMAP_HISTOGRAM_ENTRY histogram = { kTriangles, kLevel, D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT_OC1_4_STATE };
	D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_DESC arrayDesc = {};
	arrayDesc.NumOmmHistogramEntries = 1;
	arrayDesc.pOmmHistogram = &histogram;
	arrayDesc.InputBuffer = data->GetGPUVirtualAddress();
	arrayDesc.PerOmmDescs = { descBuffer->GetGPUVirtualAddress(), sizeof(D3D12_RAYTRACING_OPACITY_MICROMAP_DESC) };
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS ommInputs = {};
	ommInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_OPACITY_MICROMAP_ARRAY;
	ommInputs.NumDescs = 1;
	ommInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	ommInputs.pOpacityMicromapArrayDesc = &arrayDesc;
	auto ommArray = Build(g, ommInputs, keep, "baked micromap array");
	if (!ommArray) {
		return false;
	}
	const UINT ommIndices[kTriangles] = { 0, 1 };
	auto ommIndexBuffer = Upload(g.device.Get(), ommIndices, sizeof(ommIndices));
	D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles = {};
	triangles.VertexBuffer = { vb->GetGPUVirtualAddress(), 12 };
	triangles.VertexCount = 4;
	triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
	triangles.IndexBuffer = ib->GetGPUVirtualAddress();
	triangles.IndexCount = 6;
	triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
	D3D12_RAYTRACING_GEOMETRY_OMM_LINKAGE_DESC linkage = {};
	linkage.OpacityMicromapIndexBuffer = { ommIndexBuffer->GetGPUVirtualAddress(), 4 };
	linkage.OpacityMicromapIndexFormat = DXGI_FORMAT_R32_UINT;
	linkage.OpacityMicromapArray = ommArray->GetGPUVirtualAddress();
	D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
	geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_OMM_TRIANGLES;
	geometry.OmmTriangles = { &triangles, &linkage };
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS blasInputs = {};
	blasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
	blasInputs.NumDescs = 1;
	blasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	blasInputs.pGeometryDescs = &geometry;
	auto blas = Build(g, blasInputs, keep, "BLAS with baked micromaps");
	if (!blas) {
		return false;
	}
	D3D12_RAYTRACING_INSTANCE_DESC instance = {};
	instance.Transform[0][0] = instance.Transform[1][1] = instance.Transform[2][2] = 1.0f;
	instance.InstanceMask = 0xFF;
	instance.Flags = D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE;
	instance.AccelerationStructure = blas->GetGPUVirtualAddress();
	auto instances = Upload(g.device.Get(), &instance, sizeof(instance));
	D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tlasInputs = {};
	tlasInputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
	tlasInputs.NumDescs = 1;
	tlasInputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
	tlasInputs.InstanceDescs = instances->GetGPUVirtualAddress();
	auto tlas = Build(g, tlasInputs, keep, "TLAS");
	if (!tlas) {
		return false;
	}

	// trace the grid
	ComPtr<ID3D12RootSignature> traceRoot;
	if (!RootSignature(g.device.Get(), traceRoot)) {
		return false;
	}
	pso.pRootSignature = traceRoot.Get();
	pso.CS = { g_ProbeOmmCheck, sizeof(g_ProbeOmmCheck) };
	ComPtr<ID3D12PipelineState> check;
	CHECK(g.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(&check)), "micromap check pipeline");
	constexpr UINT kGrid = 64;
	auto output = Uav(g.device.Get(), kGrid * kGrid * 4);
	g.list->SetComputeRootSignature(traceRoot.Get());
	g.list->SetPipelineState(check.Get());
	g.list->SetComputeRootShaderResourceView(0, tlas->GetGPUVirtualAddress());
	g.list->SetComputeRootUnorderedAccessView(1, output->GetGPUVirtualAddress());
	g.list->Dispatch(kGrid / 8, kGrid / 8, 1);
	UavBarrier(g.list.Get());
	std::vector<UINT> states(kGrid * kGrid);
	if (!ReadBack(g, output.Get(), states.data(), states.size() * 4)) {
		return false;
	}
	UINT opaque = 0, transparent = 0, unknown = 0, wrong = 0;
	for (UINT y = 0; y < kGrid; ++y) {
		for (UINT x = 0; x < kGrid; ++x) {
			UINT s = states[y * kGrid + x];
			bool solid = MaskSolid(mask, (x + 0.5f) / kGrid, (y + 0.5f) / kGrid);
			if (s == 1) {
				++opaque;
				wrong += solid ? 0 : 1;
			} else if (s == 0) {
				++transparent;
				wrong += solid ? 1 : 0;
			} else {
				++unknown;
			}
		}
	}
	UINT decided = opaque + transparent;
	ProbeLog("  baked micromaps: %u opaque, %u transparent, %u unknown of %u rays; %u disagree with the mask (expect 0)\n",
	         opaque, transparent, unknown, kGrid * kGrid, wrong);
	return decided > kGrid * kGrid / 2 && wrong == 0;
}

} // namespace dxr12
