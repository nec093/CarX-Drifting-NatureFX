#ifndef NFX_RAYTRACING_INCLUDED
#define NFX_RAYTRACING_INCLUDED

// Shared by the ray tracing composite shaders. The rays themselves are traced by the own engine (NFXNative,
// native/NFXNative/shaders).

// Perceptual roughness of a clear coat: the reflection follows the varnish, not the base layer HDRP's normal
// buffer describes
#define NFX_COAT_ROUGHNESS 0.04

#endif
