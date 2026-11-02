#ifndef NFX_IMPOSTOR_INCLUDED
#define NFX_IMPOSTOR_INCLUDED

// Octahedral impostors (ShrubRenderer's far plants): a quad turned to the camera that shows the picture of the plant
// baked from the nearest of N x N directions over the upper half of a sphere (tools/kino_sdk/bake_shrub_lods.py,
// hemi-octahedral layout). The vertex code turns the quad, the fragment code finds the same frame again and reads its
// tile; the tile's normals are in the frame's own axes (x right, y up, z towards the viewer), which the quad's
// tangent frame is set to, so the Lit normal map path lights it.
//   _NFX_Impostor  x: frames per side (N), y: the tile's size (m), z: the centre's height above the origin (m),
//                  w: 1 = this is an impostor
// The quad's vertices: x across and y - z up from the centre, in metres (the size of the tile).

float3 NFX_HemiOctDecode(float2 e) {
	float3 d = float3((e.x + e.y) * 0.5, 0.0, (e.x - e.y) * 0.5);
	d.y = 1.0 - abs(d.x) - abs(d.z);
	return normalize(d);
}

float2 NFX_HemiOctEncode(float3 d) {
	d.y = max(d.y, 0.0);
	d /= abs(d.x) + abs(d.y) + abs(d.z) + 1e-6;
	return float2(d.x + d.z, d.x - d.z);
}

// the frame for a view from toViewOS (object space, towards the viewer), and its direction
void NFX_ImpostorFrame(float3 toViewOS, out float2 frame, out float3 dirOS) {
	float n = max(_NFX_Impostor.x, 1.0);
	float2 e = NFX_HemiOctEncode(normalize(toViewOS));
	frame = clamp(floor((e * 0.5 + 0.5) * n), 0.0, n - 1.0);
	dirOS = NFX_HemiOctDecode((frame + 0.5) / n * 2.0 - 1.0);
}

// the bake's camera axes for a frame (bake_shrub_lods.py frame_axes: the same formula)
void NFX_ImpostorAxes(float3 dir, out float3 right, out float3 up) {
	float3 reference = abs(dir.y) > 0.999 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
	right = normalize(cross(dir, reference));
	up = cross(right, dir);
}

// the quad turned to the frame the camera sees from
float3 NFX_ImpostorVertexRWS(float3 positionOS) {
	float3 centreOS = float3(0.0, _NFX_Impostor.z, 0.0);
	float3 toViewOS = TransformWorldToObjectDir(-TransformObjectToWorld(centreOS));
	float2 frame;
	float3 dir, right, up;
	NFX_ImpostorFrame(toViewOS, frame, dir);
	NFX_ImpostorAxes(dir, right, up);
	float2 corner = float2(positionOS.x, positionOS.y - _NFX_Impostor.z);
	return TransformObjectToWorld(centreOS + right * corner.x + up * corner.y);
}

// the frame's tile under this pixel, and the quad's tangent frame for its normals
void NFX_ImpostorFragment(inout FragInputs input) {
	float3 centreOS = float3(0.0, _NFX_Impostor.z, 0.0);
	float3 toViewOS = TransformWorldToObjectDir(-TransformObjectToWorld(centreOS));
	float2 frame;
	float3 dir, right, up;
	NFX_ImpostorFrame(toViewOS, frame, dir);
	NFX_ImpostorAxes(dir, right, up);
	float3 rel = TransformWorldToObject(input.positionRWS) - centreOS;
	float n = max(_NFX_Impostor.x, 1.0);
	// half a texel in from the tile's edge (a 2048 atlas): no bleeding from the next frame
	float edge = 0.5 * n / 2048.0;
	float2 local = clamp(float2(dot(rel, right), dot(rel, up)) / max(_NFX_Impostor.y, 1e-3) + 0.5, edge, 1.0 - edge);
	input.texCoord0 = float4((frame + local) / n, 0.0, 0.0);
	input.tangentToWorld[0] = TransformObjectToWorldDir(right);
	input.tangentToWorld[1] = TransformObjectToWorldDir(up);
	input.tangentToWorld[2] = TransformObjectToWorldDir(dir);
	input.isFrontFace = true;
}

#endif
