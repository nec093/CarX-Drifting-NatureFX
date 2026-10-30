// DXR 1.2 probe as a program (the checks are in Dxr12Probe.h; NFXNative runs them inside the game too).
//
//   Dxr12Probe.exe <folder with the 1.619 D3D12Core.dll> [adapter index]
#include <initguid.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <cstdio>
#include <cstdlib>

#define ProbeLog(...) std::printf(__VA_ARGS__)
#include "Dxr12Probe.h"
#include "OmmBakeTest.h"

using Microsoft::WRL::ComPtr;
using namespace dxr12;

int main(int argc, char** argv) {
	std::string sdkPath = argc > 1 ? argv[1] : ".\\D3D12\\";
	if (!sdkPath.empty() && sdkPath.back() != '\\' && sdkPath.back() != '/') {
		sdkPath += '\\';
	}
	int adapterIndex = argc > 2 ? std::atoi(argv[2]) : 0;
	std::printf("DXR 1.2 probe, Agility SDK 1.619 from %s\n", sdkPath.c_str());

	ComPtr<ID3D12SDKConfiguration1> config;
	if (FAILED(D3D12GetInterface(CLSID_D3D12SDKConfiguration, IID_PPV_ARGS(&config)))) {
		std::printf("FAIL D3D12GetInterface(SDK configuration 1): this Windows' D3D12 has no device factories\n");
		return 1;
	}
	ComPtr<ID3D12DeviceFactory> factory;
	HRESULT hr = config->CreateDeviceFactory(619, sdkPath.c_str(), IID_PPV_ARGS(&factory));
	if (FAILED(hr)) {
		std::printf("FAIL CreateDeviceFactory(619) hr 0x%08lX\n", static_cast<unsigned long>(hr));
		return 1;
	}
	std::printf("OK   device factory for SDK 619\n");

	ComPtr<IDXGIFactory6> dxgi;
	ComPtr<IDXGIAdapter1> adapter;
	if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&dxgi))) ||
	    FAILED(dxgi->EnumAdapterByGpuPreference(adapterIndex, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)))) {
		std::printf("FAIL no adapter %d\n", adapterIndex);
		return 1;
	}
	DXGI_ADAPTER_DESC1 ad = {};
	adapter->GetDesc1(&ad);
	std::printf("     adapter: %ls\n", ad.Description);

	Gpu g;
	std::vector<ComPtr<ID3D12Resource>> keep;
	ComPtr<ID3D12Resource> tlas;
	ComPtr<ID3D12RootSignature> root;
	if (!ProbeAll(factory.Get(), adapter.Get(), g, keep, tlas, root)) {
		return 1;
	}
	bool omm = TestOmm(g, tlas.Get(), root.Get());
	std::printf("%s opacity micromaps (inline ray query)\n", omm ? "OK  " : "FAIL");
	bool ser = TestSer(g, tlas.Get(), root.Get());
	std::printf("%s shader execution reordering (ray generation shader)\n", ser ? "OK  " : "FAIL");
	bool bake = TestOmmBake(g);
	std::printf("%s micromaps baked from an alpha mask (NFXNative's BakeOmm.hlsl)\n", bake ? "OK  " : "FAIL");
	bool all = omm && ser && bake;
	std::printf(all ? "DXR 1.2: all checks passed\n" : "DXR 1.2: some checks failed\n");
	return all ? 0 : 1;
}
