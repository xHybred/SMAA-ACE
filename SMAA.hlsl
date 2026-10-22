/*=============================================================================

    SMAA 1x - standalone HLSL implementation for D3D12, Vulkan, and SM5/DXBC

    The SMAA algorithm, area texture and search texture were created by
    Jorge Jimenez, Jose I. Echevarria, Tiago Sousa and Diego Gutierrez, and
    are distributed under the MIT license. See LICENSE-SMAA.txt for the
    original notice.

    Original SMAA project:
        https://github.com/iryoku/smaa

    The pixel-shader path in this file follows the original SMAA reference
    shader. The compute pipeline (edge mask, active-pixel compaction, blend
    dispatch) is an independent implementation written for this project.

    This file is a self-contained shader library with no host/API code.
    The application owns all resources, descriptor sets/root signatures,
    barriers, clears, stencil state and presentation.

    -------------------------------------------------------------------------
    PIPELINE OVERVIEW
    -------------------------------------------------------------------------

    Compute path (recommended):

        1. SMAAEdgeDetectionCS
           - 32x8 group, two pixels per thread, 32x16 pixels per group.
           - Writes EdgesTex (RG8) only where an edge is present.
           - Packs a 2-bit edge code per pixel into a per-tile mask and
             writes that mask to EdgeMask (R32I).
           - No clear is required for EdgeMask. The host must clear EdgesTex
             and BlendTex each frame: the blend searches sample neighboring
             non-edge texels, and the edge pass writes only edge pixels.

        2. SMAABlendWeightsCS
           - Reads the per-tile mask, compacts every active edge pixel and
             processes all of them. There is no sparse-tile skip.
           - Writes BlendTex (RGBA8).

        3. SMAAResolvePS
           - Reads BlendTex and the color input, and always writes a result.
             The no-blend path returns the center color instead of discarding.

    Optional low-contrast edge mode:

        SMAAEdgeDetectionLowContrastCS
           - Drop-in replacement for SMAAEdgeDetectionCS. Same dispatch and
             resources, but uses a luma-adaptive threshold plus CMAA-style
             local contrast suppression to catch low-contrast edges.
           - Enable with LowContrastParams and Options.z bits, and compile the
             entry point only when the mode is used. The canonical entry point
             does not reference this code, so DXC removes it entirely for the
             default path.
           - Costs roughly 40-55% more than the optimized path in the demo at
             3D resolutions, mostly in edge detection.

    Pixel fallback:

        1. SMAAEdgeDetectionPS with stencil REPLACE, ref 1.
           - Writes EdgesTex and discards pixels with no edge.
        2. SMAABlendWeightsPS with stencil EQUAL, ref 1.
           - Reads EdgesTex and writes BlendTex.
        3. SMAAResolvePS.
           - The application must clear EdgesTex and BlendTex before use.

    -------------------------------------------------------------------------
    COMPILE-TIME OPTIONS
    -------------------------------------------------------------------------

        SMAA_USE_SM6=1          Use SM6 wave compaction (default).
        SMAA_USE_SM6=0          Use the SM5 groupshared atomic fallback.
        SMAA_VULKAN=1           Emit Vulkan bindings for DXC SPIR-V.
        SMAA_VULKAN=0           Emit D3D12 registers (default).
        SMAA_ENABLE_COMPUTE=0   Exclude compute entry points.
        SMAA_ENABLE_PIXEL=0     Exclude pixel-shader entry points.

    D3D12 examples:
        dxc -T cs_6_0 -E SMAAEdgeDetectionCS  -DSMAA_USE_SM6=1 SMAA.hlsl
        dxc -T cs_6_0 -E SMAABlendWeightsCS   -DSMAA_USE_SM6=1 SMAA.hlsl
        dxc -T ps_6_0 -E SMAAResolvePS        SMAA.hlsl
        dxc -T vs_6_0 -E SMAAFullscreenVS     SMAA.hlsl

        SM5/DXBC fallback (FXC; DXC does not accept SM5 profiles):
        fxc /T vs_5_0 /E SMAAFullscreenVS     /DSMAA_USE_SM6=0 SMAA.hlsl
        fxc /T cs_5_0 /E SMAAEdgeDetectionCS  /DSMAA_USE_SM6=0 SMAA.hlsl
        fxc /T cs_5_0 /E SMAABlendWeightsCS   /DSMAA_USE_SM6=0 SMAA.hlsl
        fxc /T ps_5_0 /E SMAAResolvePS        /DSMAA_USE_SM6=0 SMAA.hlsl
        The define is required for every entry point, including the vertex
        shader, because SMAA_USE_SM6=1 is rejected below shader model 6.

    Vulkan examples:
        dxc -T cs_6_0 -E SMAAEdgeDetectionCS -spirv -DSMAA_VULKAN=1 SMAA.hlsl
        dxc -T cs_6_0 -E SMAABlendWeightsCS  -spirv -DSMAA_VULKAN=1 SMAA.hlsl
        dxc -T ps_6_0 -E SMAAResolvePS       -spirv -DSMAA_VULKAN=1 SMAA.hlsl

        Add -DSMAA_USE_SM6=0 for the groupshared-atomic fallback. For Vulkan,
        target a Vulkan 1.1+ environment so that subgroup operations are
        available when SMAA_USE_SM6=1.

    -------------------------------------------------------------------------
    RESOURCES
    -------------------------------------------------------------------------

    D3D12 registers (space 0):
        b0  SMAAParams
        t0  ColorTexGamma       non-sRGB SRV for edge detection
        t1  ColorTexLinear      sRGB SRV for resolve
        t2  EdgesTex            R8G8_UNORM SRV
        t3  BlendTex            R8G8B8A8_UNORM SRV
        t4  AreaLUT             R8G8_UNORM SRV
        t5  SearchLUT           R8_UNORM SRV
        t6  EdgeMaskTex         R32_SINT SRV
        u0  EdgesTexRW          R8G8_UNORM UAV
        u1  BlendTexRW          R8G8B8A8_UNORM UAV
        u2  EdgeMaskTexRW       R32_SINT UAV
        s0  ColorLinearSampler  linear, clamp
        s1  EdgesSampler        linear, clamp
        s2  AreaSampler         linear, clamp

    Vulkan set 0 bindings:
        0 params, 1 colorGamma, 2 colorLinear, 3 edges, 4 blend,
        5 area, 6 search, 7 edgeMask, 8 edgesRW, 9 blendRW,
        10 edgeMaskRW, 11 colorLinearSampler, 12 edgesSampler,
        13 areaSampler.

    Formats:
        Color input      RGBA8_UNORM_SRGB for resolve, RGBA8_UNORM for edges
                         (or R10G10B10A2_UNORM for 10-bit input; create both
                         views from the same resource)
        EdgesTex         R8G8_UNORM
        BlendTex         R8G8B8A8_UNORM
        EdgeMaskTex      R32_SINT (one 32-bit word per 16 pixels)
        AreaLUT          R8G8_UNORM, 160x560, no sRGB
        SearchLUT        R8_UNORM, 64x16, no sRGB

    Recommended parameters:
        ScreenParams = (width, height, 1 / width, 1 / height)
        SearchParams = (0.10, 32, 16, 25)
        Options      = (0, 0, 0, 0)

    Edge detection (Options.x):
        0  max-channel metric on the exact-center SMAA taps (default)
        1  luma-weighted metric on the same taps

    Low-contrast edge mode (Options.z):
        0     off; use the canonical detector
        bit 1 adaptive threshold, using LowContrastParams.xyz
        bit 4 CMAA circumferential local contrast
        bit 8 CMAA/SMAA LCA synergy, using LowContrastParams.w
        The tuned demo setting is 13 (1 | 4 | 8).

    Vulkan note: the compute path uses storage images. R8G8_UNORM storage
    support is optional on some older devices; if unavailable, use RGBA8 for
    EdgesTex and change the UAV type/image format in this file to match.

    LUTs:
        AreaTex.rg8 and SearchTex.r8 are supplied in Reference/.
        AreaLUT uses linear filtering. SearchLUT uses point/load access, so
        its sampler state is irrelevant; do not enable sRGB on either.

    Dispatch:
        dispatchX = (width  + 31) / 32
        dispatchY = (height + 15) / 16
        EdgeMask width  = dispatchX * 32
        EdgeMask height = dispatchY

    Barriers:
        After SMAAEdgeDetectionCS, transition EdgesTex and EdgeMask from UAV
        to sampled SRV state for SMAABlendWeightsCS. BlendTex stays a UAV for
        the blend-weight write. A UAV barrier is sufficient on D3D12 for the
        write/read dependencies; use appropriate image/buffer memory barriers
        on Vulkan. After SMAABlendWeightsCS, transition BlendTex to a sampled
        SRV for SMAAResolvePS.

    Pixel fallback:
        EdgesTex and BlendTex must be cleared. The edge pass uses stencil
        REPLACE with ref 1; the blend pass uses stencil EQUAL with ref 1.
        The resolve pass writes the final image. If the output is an sRGB
        target, bind an sRGB render-target view; this shader returns linear
        values from the sRGB color SRV. The fullscreen triangle assumes
        culling is disabled (or that the host uses a matching winding).

=============================================================================*/

/*=============================================================================
    Configuration
=============================================================================*/

#ifndef SMAA_VULKAN
#define SMAA_VULKAN 0
#endif

#ifndef SMAA_USE_SM6
#define SMAA_USE_SM6 1
#endif

#ifndef SMAA_ENABLE_COMPUTE
#define SMAA_ENABLE_COMPUTE 1
#endif

#ifndef SMAA_ENABLE_PIXEL
#define SMAA_ENABLE_PIXEL 1
#endif

// When the output target is UNORM rather than sRGB, set this to 1 so the
// resolve pass performs the linear-to-sRGB transfer itself.
#ifndef SMAA_OUTPUT_SRGB
#define SMAA_OUTPUT_SRGB 0
#endif

// Optional compile-time specialization. Leave both at -1 to read Options.x
// and Options.y at runtime, or set them to the values the host will use so the
// compiler can fold the branches.
#ifndef SMAA_EDGE_MODE
#define SMAA_EDGE_MODE -1
#endif

#ifndef SMAA_DEBUG_MODE
#define SMAA_DEBUG_MODE -1
#endif

#if SMAA_EDGE_MODE >= 0
#define SMAA_EDGE_OPTION SMAA_EDGE_MODE
#else
#define SMAA_EDGE_OPTION (Options.x)
#endif

#if SMAA_DEBUG_MODE >= 0
#define SMAA_DEBUG_OPTION SMAA_DEBUG_MODE
#else
#define SMAA_DEBUG_OPTION (Options.y)
#endif

// Low-contrast edge mode feature bits. These are intentionally opt-in so the
// canonical edge detector remains the default.
#define SMAA_LOW_CONTRAST_ADAPTIVE_THRESHOLD 1
#define SMAA_LOW_CONTRAST_CMAA_LCA           4
#define SMAA_LOW_CONTRAST_CMAA_SYNERGY       8

#define SMAA_AREATEX_MAX_DISTANCE       16
#define SMAA_AREATEX_MAX_DISTANCE_DIAG  20
#define SMAA_AREATEX_PIXEL_SIZE         (1.0 / float2(160.0, 560.0))

#if SMAA_USE_SM6 && defined(__SHADER_TARGET_MAJOR) && (__SHADER_TARGET_MAJOR < 6)
#error "SMAA_USE_SM6 requires shader model 6.0 or newer. Define SMAA_USE_SM6=0 for SM5."
#endif

// D3D12 uses registers; Vulkan uses explicit descriptor set bindings.
#if SMAA_VULKAN
#define SMAA_BINDING(b, s) [[vk::binding(b, s)]]
#define SMAA_IMAGE_FORMAT(format)  [[vk::image_format(format)]]
#define SMAA_REGISTER(type, reg)
#else
#define SMAA_BINDING(binding, set)
#define SMAA_IMAGE_FORMAT(format)
#define SMAA_REGISTER(type, reg) : register(type##reg)
#endif

/*=============================================================================
    Constant buffer
=============================================================================*/

SMAA_BINDING(0, 0)
cbuffer SMAAParams SMAA_REGISTER(b, 0)
{
    // xy = screen size in pixels, zw = 1 / screen size.
    float4 ScreenParams;

    // x = edge threshold, y = max search steps, z = max diagonal steps,
    // w = corner rounding percentage (0..100).
    float4 SearchParams;

    // x = edge detection mode (0 = max channel, 1 = luma weighted),
    // y = debug output (0 = none, 1 = edges, 2 = blend weights),
    // z = low-contrast edge feature flags (1 = adaptive threshold,
    //     4 = CMAA local contrast, 8 = CMAA/SMAA LCA synergy),
    // w reserved.
    int4 Options;

    // x = adaptive threshold in dark areas, y = in bright areas,
    // z = adaptive threshold floor, w = CMAA/SMAA LCA synergy.
    float4 LowContrastParams;
};

/*=============================================================================
    Resources
=============================================================================*/

SMAA_BINDING(1, 0)
Texture2D<float4> ColorTexGamma SMAA_REGISTER(t, 0);

SMAA_BINDING(2, 0)
Texture2D<float4> ColorTexLinear SMAA_REGISTER(t, 1);

SMAA_BINDING(3, 0)
Texture2D<float2> EdgesTex SMAA_REGISTER(t, 2);

SMAA_BINDING(4, 0)
Texture2D<float4> BlendTex SMAA_REGISTER(t, 3);

SMAA_BINDING(5, 0)
Texture2D<float4> AreaLUT SMAA_REGISTER(t, 4);

SMAA_BINDING(6, 0)
Texture2D<float> SearchLUT SMAA_REGISTER(t, 5);

SMAA_BINDING(7, 0)
Texture2D<int> EdgeMaskTex SMAA_REGISTER(t, 6);

SMAA_BINDING(8, 0)
SMAA_IMAGE_FORMAT("rg8")
RWTexture2D<float2> EdgesTexRW SMAA_REGISTER(u, 0);

SMAA_BINDING(9, 0)
SMAA_IMAGE_FORMAT("rgba8")
RWTexture2D<float4> BlendTexRW SMAA_REGISTER(u, 1);

SMAA_BINDING(10, 0)
SMAA_IMAGE_FORMAT("r32i")
RWTexture2D<int> EdgeMaskTexRW SMAA_REGISTER(u, 2);

SMAA_BINDING(11, 0)
SamplerState ColorLinearSampler SMAA_REGISTER(s, 0);

SMAA_BINDING(12, 0)
SamplerState EdgesSampler SMAA_REGISTER(s, 1);

SMAA_BINDING(13, 0)
SamplerState AreaSampler SMAA_REGISTER(s, 2);

/*=============================================================================
    SMAA constants
=============================================================================*/

#define SMAA_AREATEX_SUBTEX_SIZE        (1.0 / 7.0)
#define SMAA_SEARCHTEX_SIZE             float2(66.0, 33.0)
#define SMAA_SEARCHTEX_PACKED_SIZE      float2(64.0, 16.0)
#define SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR 2.0

#define SMAA_GROUP_SIZE_X 32
#define SMAA_GROUP_SIZE_Y 8
#define SMAA_BATCH_SIZE         2
#define SMAA_EDGE_MASK_WORDS    32
// Upper bound on compacted entries: one slot per pixel processed by the group.
#define SMAA_ACTIVE_SLOTS       (SMAA_GROUP_SIZE_X * SMAA_GROUP_SIZE_Y * SMAA_BATCH_SIZE)


/*=============================================================================
    Common helpers
=============================================================================*/

void SMAAMovc(bool2 cond, inout float2 variable, float2 value)
{
    variable = cond ? value : variable;
}

void SMAAMovc(bool4 cond, inout float4 variable, float4 value)
{
    variable = cond ? value : variable;
}

void SMAABlendingWeightCalculationOffsets(float2 texcoord, out float4 offset[3])
{
    float2 pixelSize = ScreenParams.zw;

    // Offsets used by the searches (see @PSEUDO_GATHER4).
    offset[0] = mad(pixelSize.xyxy, float4(-0.25, -0.125, 1.25, -0.125), texcoord.xyxy);
    offset[1] = mad(pixelSize.xyxy, float4(-0.125, -0.25, -0.125, 1.25), texcoord.xyxy);

    // End points for the searches.
    offset[2] = mad(pixelSize.xxyy,
                    float4(-2.0, 2.0, -2.0, 2.0) * SearchParams.y,
                    float4(offset[0].xz, offset[1].yw));
}

float SMAACornerRoundingNorm()
{
    return SearchParams.w / 100.0;
}

/*=============================================================================
    Edge detection
=============================================================================*/

float SMAAMetric(float3 a, float3 b)
{
    float3 t = abs(a - b);

    if (SMAA_EDGE_OPTION == 1)
    {
        return dot(t, float3(0.229, 0.587, 0.114) * 1.33);
    }

    return max(max(t.r, t.g), t.b);
}

// Low-contrast edge detection. Uses a full 3x3 neighborhood so it can apply a
// luma-adaptive threshold and CMAA-style local contrast suppression. The
// canonical path below is left untouched for the default mode.
float2 SMAAColorEdgeDetectionLowContrastCore(int2 pixel, Texture2D<float4> colorTex, out bool hasEdges)
{
    int2 size = int2(ScreenParams.xy);
    int2 p = clamp(pixel, int2(0, 0), size - 1);
    int2 lo = int2(0, 0);
    int2 hi = size - 1;

    // Load the neighborhood incrementally so the compiler does not keep nine
    // float3 values live at once.
    float3 cC = colorTex.Load(int3(p, 0)).rgb;
    float3 maxColor = cC;

    float3 cW = colorTex.Load(int3(clamp(p + int2(-1, 0), lo, hi), 0)).rgb;
    maxColor = max(maxColor, cW);
    float dLeft = SMAAMetric(cC, cW);

    float3 cN = colorTex.Load(int3(clamp(p + int2(0, -1), lo, hi), 0)).rgb;
    maxColor = max(maxColor, cN);
    float dTop = SMAAMetric(cC, cN);

    float3 cE = colorTex.Load(int3(clamp(p + int2(1, 0), lo, hi), 0)).rgb;
    maxColor = max(maxColor, cE);
    float dRight = SMAAMetric(cC, cE);

    float3 cS = colorTex.Load(int3(clamp(p + int2(0, 1), lo, hi), 0)).rgb;
    maxColor = max(maxColor, cS);
    float dBottom = SMAAMetric(cC, cS);

    // The diagonals are only needed by CMAA. Keeping them behind this branch
    // lets adaptive-threshold mode run with five taps instead of nine.
    float2 cmaa = float2(0.0, 0.0);
    if ((Options.z & SMAA_LOW_CONTRAST_CMAA_LCA) != 0)
    {
        float3 cNW = colorTex.Load(int3(clamp(p + int2(-1, -1), lo, hi), 0)).rgb;
        maxColor = max(maxColor, cNW);
        float3 cNE = colorTex.Load(int3(clamp(p + int2(1, -1), lo, hi), 0)).rgb;
        maxColor = max(maxColor, cNE);
        float3 cSW = colorTex.Load(int3(clamp(p + int2(-1, 1), lo, hi), 0)).rgb;
        maxColor = max(maxColor, cSW);
        float3 cSE = colorTex.Load(int3(clamp(p + int2(1, 1), lo, hi), 0)).rgb;
        maxColor = max(maxColor, cSE);

        float dLeftN  = SMAAMetric(cN, cNW);
        float dLeftNE = SMAAMetric(cNE, cN);
        float dTopS   = SMAAMetric(cS, cC);
        float dTopW   = SMAAMetric(cW, cNW);
        float dTopSW  = SMAAMetric(cSW, cW);
        float dLeftE  = SMAAMetric(cE, cC);

        float localLuma = saturate(max(maxColor.r * 0.297, max(maxColor.g, maxColor.b * 0.101)));
        float cmaaFactor = lerp(0.22, 0.15, localLuma);
        cmaa.x = max(max(dLeftN, dLeftNE), max(dLeft, dTopS)) * cmaaFactor;
        cmaa.y = max(max(dTopW, dTop), max(dTopSW, dLeftE)) * cmaaFactor;
    }

    float localLuma = saturate(max(maxColor.r * 0.297, max(maxColor.g, maxColor.b * 0.101)));

    float threshold = SearchParams.x;
    if ((Options.z & SMAA_LOW_CONTRAST_ADAPTIVE_THRESHOLD) != 0)
    {
        threshold = max(lerp(LowContrastParams.x, LowContrastParams.y, localLuma), LowContrastParams.z);
    }

    float2 centerDeltas = float2(dLeft, dTop) - cmaa;
    float2 edges = step(float2(threshold, threshold), centerDeltas);
    hasEdges = (edges.x + edges.y) > 0.0;

    if (!hasEdges)
    {
        return float2(0.0, 0.0);
    }

    // Neighboring deltas, not center-to-far deltas, are used for the local
    // contrast adaptation in this path.
    float3 cWW = colorTex.Load(int3(clamp(p + int2(-2, 0), lo, hi), 0)).rgb;
    float3 cNN = colorTex.Load(int3(clamp(p + int2(0, -2), lo, hi), 0)).rgb;
    float dLeftLeft = SMAAMetric(cW, cWW);
    float dTopTop = SMAAMetric(cN, cNN);

    float finalDelta = max(max(max(dLeft, dTop), max(dRight, dBottom)),
                           max(dLeftLeft, dTopTop));

    float2 lcaDeltas = float2(dLeft, dTop);
    if ((Options.z & SMAA_LOW_CONTRAST_CMAA_SYNERGY) != 0)
    {
        lcaDeltas += cmaa * LowContrastParams.w;
    }

    edges *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * lcaDeltas);
    return edges;
}

// Exact-center edge detection. Use a non-sRGB view of the color input.
// Returns 0 when there is no edge; hasEdges reports the early-out result so
// the pixel-shader wrapper can discard for stencil while the compute shader
// can store zeros without branching on a discard.
float2 SMAAColorEdgeDetectionCore(int2 pixel, Texture2D<float4> colorTex, out bool hasEdges)
{
    const float threshold = SearchParams.x;
    int2 size = int2(ScreenParams.xy);
    int2 p = clamp(pixel, int2(0, 0), size - 1);

    float4 delta;
    float3 c = colorTex.Load(int3(p, 0)).rgb;

    float3 cLeft = colorTex.Load(int3(clamp(p + int2(-1, 0), int2(0, 0), size - 1), 0)).rgb;
    delta.x = SMAAMetric(c, cLeft);

    float3 cTop = colorTex.Load(int3(clamp(p + int2(0, -1), int2(0, 0), size - 1), 0)).rgb;
    delta.y = SMAAMetric(c, cTop);

    float2 edges = step(float2(threshold, threshold), delta.xy);
    hasEdges = (edges.x + edges.y) > 0.0;

    if (!hasEdges)
    {
        return float2(0.0, 0.0);
    }

    float3 cRight = colorTex.Load(int3(clamp(p + int2(1, 0), int2(0, 0), size - 1), 0)).rgb;
    delta.z = SMAAMetric(c, cRight);

    float3 cBottom = colorTex.Load(int3(clamp(p + int2(0, 1), int2(0, 0), size - 1), 0)).rgb;
    delta.w = SMAAMetric(c, cBottom);

    float2 maxDelta = max(delta.xy, delta.zw);

    // The far deltas use the center sample, matching the reference shader.
    float3 cLeftLeft = colorTex.Load(int3(clamp(p + int2(-2, 0), int2(0, 0), size - 1), 0)).rgb;
    delta.z = SMAAMetric(c, cLeftLeft);

    float3 cTopTop = colorTex.Load(int3(clamp(p + int2(0, -2), int2(0, 0), size - 1), 0)).rgb;
    delta.w = SMAAMetric(c, cTopTop);

    maxDelta = max(maxDelta.xy, delta.zw);

    float finalDelta = max(maxDelta.x, maxDelta.y);
    edges.xy *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta.xy);

    return edges;
}

/*=============================================================================
    Edge and area decoding
=============================================================================*/

float2 SMAADecodeDiagBilinearAccess(float2 e)
{
    e.r = e.r * abs(5.0 * e.r - 5.0 * 0.75);
    return round(e);
}

float4 SMAADecodeDiagBilinearAccess(float4 e)
{
    e.rb = e.rb * abs(5.0 * e.rb - 5.0 * 0.75);
    return round(e);
}

// Integer-stepped diagonal search. The returned values are the same as the
// original coord.zw: the step count starts at -1 and the last edge dot starts
// at 1.0.
float2 SMAASearchDiag1(Texture2D<float2> edgesTex, float2 pixcoord, int2 dir, out float2 e)
{
    int steps = -1;
    float last = 1.0;
    int2 p = int2(pixcoord);
    int2 size = int2(ScreenParams.xy);
    int maxSteps = int(SearchParams.z);
    e = float2(0.0, 0.0);

    while (steps < maxSteps - 1 && last > 0.9)
    {
        p += dir;
        steps += 1;
        e = edgesTex.Load(int3(clamp(p, int2(0, 0), size - 1), 0));
        last = dot(e, 0.5);
    }

    return float2(float(steps), last);
}

// The second diagonal search deliberately uses bilinear taps at quarter-pixel
// offsets to decode the crossing edges. Do not convert this one to Load.
float2 SMAASearchDiag2(Texture2D<float2> edgesTex, float2 texcoord, float2 dir, out float2 e)
{
    float4 coord = float4(texcoord, -1.0, 1.0);
    coord.x += 0.25 * ScreenParams.z;

    float3 t = float3(ScreenParams.zw, 1.0);
    float maxSteps = SearchParams.z;

    while (coord.z < maxSteps - 1.0 && coord.w > 0.9)
    {
        coord.xyz = mad(t, float3(dir, 1.0), coord.xyz);
        e = edgesTex.SampleLevel(EdgesSampler, coord.xy, 0).rg;
        e = SMAADecodeDiagBilinearAccess(e);
        coord.w = dot(e, 0.5);
    }

    return coord.zw;
}

float2 SMAAAreaDiag(Texture2D<float4> areaTex, float2 dist, float2 e, float offset)
{
    float2 texcoord = mad(float2(SMAA_AREATEX_MAX_DISTANCE_DIAG, SMAA_AREATEX_MAX_DISTANCE_DIAG), e, dist);
    texcoord = mad(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);
    texcoord.x += 0.5;
    texcoord.y += SMAA_AREATEX_SUBTEX_SIZE * offset;

    return areaTex.SampleLevel(AreaSampler, texcoord, 0).rg;
}

float2 SMAACalculateDiagWeights(Texture2D<float2> edgesTex,
                                Texture2D<float4> areaTex,
                                float2 texcoord,
                                float2 pixcoord,
                                float2 e,
                                float4 subsampleIndices)
{
    float2 weights = float2(0.0, 0.0);
    float2 pixelSize = ScreenParams.zw;

    float4 d;
    float2 end;

    if (e.r > 0.0)
    {
        d.xz = SMAASearchDiag1(edgesTex, pixcoord, int2(-1, 1), end);
        d.x += float(end.y > 0.9);
    }
    else
    {
        d.xz = 0.0;
    }

    d.yw = SMAASearchDiag1(edgesTex, pixcoord, int2(1, -1), end);

    [branch]
    if (d.x + d.y > 2.0)
    {
        float4 coords = mad(float4(-d.x + 0.25, d.x, d.y, -d.y - 0.25),
                            pixelSize.xyxy,
                            texcoord.xyxy);

        float4 c;
        c.xy = edgesTex.SampleLevel(EdgesSampler, coords.xy + int2(-1, 0) * pixelSize, 0).rg;
        c.zw = edgesTex.SampleLevel(EdgesSampler, coords.zw + int2(1, 0) * pixelSize, 0).rg;
        c.yxwz = SMAADecodeDiagBilinearAccess(c.xyzw);

        float2 cc = mad(float2(2.0, 2.0), c.xz, c.yw);
        SMAAMovc(bool2(step(0.9, d.zw)), cc, float2(0.0, 0.0));

        weights += SMAAAreaDiag(areaTex, d.xy, cc, subsampleIndices.z);
    }

    d.xz = SMAASearchDiag2(edgesTex, texcoord, float2(-1.0, -1.0), end);

    int2 size = int2(ScreenParams.xy);
    int2 sidePixel = clamp(int2(pixcoord) + int2(1, 0), int2(0, 0), size - 1);
    if (edgesTex.Load(int3(sidePixel, 0)).r > 0.0)
    {
        d.yw = SMAASearchDiag2(edgesTex, texcoord, float2(1.0, 1.0), end);
        d.y += float(end.y > 0.9);
    }
    else
    {
        d.yw = 0.0;
    }

    [branch]
    if (d.x + d.y > 2.0)
    {
        float4 coords = mad(float4(-d.x, -d.x, d.y, d.y), pixelSize.xyxy, texcoord.xyxy);

        float4 c;
        c.x  = edgesTex.SampleLevel(EdgesSampler, coords.xy + int2(-1, 0) * pixelSize, 0).g;
        c.y  = edgesTex.SampleLevel(EdgesSampler, coords.xy + int2(0, -1) * pixelSize, 0).r;
        c.zw = edgesTex.SampleLevel(EdgesSampler, coords.zw + int2(1, 0) * pixelSize, 0).gr;

        float2 cc = mad(float2(2.0, 2.0), c.xz, c.yw);
        SMAAMovc(bool2(step(0.9, d.zw)), cc, float2(0.0, 0.0));

        weights += SMAAAreaDiag(areaTex, d.xy, cc, subsampleIndices.w).gr;
    }

    return weights;
}

float SMAASearchLength(Texture2D<float> searchTex, float2 e, float offset)
{
    float2 scale = SMAA_SEARCHTEX_SIZE * float2(0.5, -1.0);
    float2 bias = SMAA_SEARCHTEX_SIZE * float2(offset, 1.0);
    scale += float2(-1.0, 1.0);
    bias += float2(0.5, -0.5);
    scale /= SMAA_SEARCHTEX_PACKED_SIZE;
    bias /= SMAA_SEARCHTEX_PACKED_SIZE;
    return searchTex.SampleLevel(AreaSampler, mad(scale, e, bias), 0).r;
}

float SMAASearchXLeft(Texture2D<float2> edgesTex, Texture2D<float> searchTex, float2 texcoord, float end)
{
    float2 e = float2(0.0, 1.0);

    while (texcoord.x > end && e.g > 0.8281 && e.r == 0.0)
    {
        e = edgesTex.SampleLevel(EdgesSampler, texcoord, 0).rg;
        texcoord = mad(-float2(2.0, 0.0), ScreenParams.zw, texcoord);
    }

    float offset = mad(-(255.0 / 127.0), SMAASearchLength(searchTex, e, 0.0), 3.25);
    return mad(ScreenParams.z, offset, texcoord.x);
}

float SMAASearchXRight(Texture2D<float2> edgesTex, Texture2D<float> searchTex, float2 texcoord, float end)
{
    float2 e = float2(0.0, 1.0);

    while (texcoord.x < end && e.g > 0.8281 && e.r == 0.0)
    {
        e = edgesTex.SampleLevel(EdgesSampler, texcoord, 0).rg;
        texcoord = mad(float2(2.0, 0.0), ScreenParams.zw, texcoord);
    }

    float offset = mad(-(255.0 / 127.0), SMAASearchLength(searchTex, e, 0.5), 3.25);
    return mad(-ScreenParams.z, offset, texcoord.x);
}

float SMAASearchYUp(Texture2D<float2> edgesTex, Texture2D<float> searchTex, float2 texcoord, float end)
{
    float2 e = float2(1.0, 0.0);

    while (texcoord.y > end && e.r > 0.8281 && e.g == 0.0)
    {
        e = edgesTex.SampleLevel(EdgesSampler, texcoord, 0).rg;
        texcoord = mad(-float2(0.0, 2.0), ScreenParams.zw, texcoord);
    }

    float offset = mad(-(255.0 / 127.0), SMAASearchLength(searchTex, e.gr, 0.0), 3.25);
    return mad(ScreenParams.w, offset, texcoord.y);
}

float SMAASearchYDown(Texture2D<float2> edgesTex, Texture2D<float> searchTex, float2 texcoord, float end)
{
    float2 e = float2(1.0, 0.0);

    while (texcoord.y < end && e.r > 0.8281 && e.g == 0.0)
    {
        e = edgesTex.SampleLevel(EdgesSampler, texcoord, 0).rg;
        texcoord = mad(float2(0.0, 2.0), ScreenParams.zw, texcoord);
    }

    float offset = mad(-(255.0 / 127.0), SMAASearchLength(searchTex, e.gr, 0.5), 3.25);
    return mad(-ScreenParams.w, offset, texcoord.y);
}

float2 SMAAArea(Texture2D<float4> areaTex, float2 dist, float e1, float e2, float offset)
{
    // Rounding prevents precision errors of bilinear filtering.
    float2 texcoord = mad(float2(SMAA_AREATEX_MAX_DISTANCE, SMAA_AREATEX_MAX_DISTANCE),
                          round(4.0 * float2(e1, e2)),
                          dist);

    texcoord = mad(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);
    texcoord.y = mad(SMAA_AREATEX_SUBTEX_SIZE, offset, texcoord.y);

    return areaTex.SampleLevel(AreaSampler, texcoord, 0).rg;
}

/*=============================================================================
    Corner patterns
=============================================================================*/

void SMAADetectHorizontalCornerPattern(Texture2D<float2> edgesTex,
                                       inout float2 weights,
                                       float4 texcoord,
                                       float2 d)
{
    float2 leftRight = step(d.xy, d.yx);
    float2 rounding = (1.0 - SMAACornerRoundingNorm()) * leftRight;
    rounding /= leftRight.x + leftRight.y;

    if (rounding.x <= 0.0 && rounding.y <= 0.0)
    {
        return;
    }

    float2 factor = float2(1.0, 1.0);
    float2 pixelSize = ScreenParams.zw;

    factor.x -= rounding.x * edgesTex.SampleLevel(EdgesSampler, texcoord.xy + int2(0, 1) * pixelSize, 0).r;
    factor.x -= rounding.y * edgesTex.SampleLevel(EdgesSampler, texcoord.zw + int2(1, 1) * pixelSize, 0).r;
    factor.y -= rounding.x * edgesTex.SampleLevel(EdgesSampler, texcoord.xy + int2(0, -2) * pixelSize, 0).r;
    factor.y -= rounding.y * edgesTex.SampleLevel(EdgesSampler, texcoord.zw + int2(1, -2) * pixelSize, 0).r;

    weights *= saturate(factor);
}

void SMAADetectVerticalCornerPattern(Texture2D<float2> edgesTex,
                                     inout float2 weights,
                                     float4 texcoord,
                                     float2 d)
{
    float2 leftRight = step(d.xy, d.yx);
    float2 rounding = (1.0 - SMAACornerRoundingNorm()) * leftRight;
    rounding /= leftRight.x + leftRight.y;

    if (rounding.x <= 0.0 && rounding.y <= 0.0)
    {
        return;
    }

    float2 factor = float2(1.0, 1.0);
    float2 pixelSize = ScreenParams.zw;

    factor.x -= rounding.x * edgesTex.SampleLevel(EdgesSampler, texcoord.xy + int2(1, 0) * pixelSize, 0).g;
    factor.x -= rounding.y * edgesTex.SampleLevel(EdgesSampler, texcoord.zw + int2(1, 1) * pixelSize, 0).g;
    factor.y -= rounding.x * edgesTex.SampleLevel(EdgesSampler, texcoord.xy + int2(-2, 0) * pixelSize, 0).g;
    factor.y -= rounding.y * edgesTex.SampleLevel(EdgesSampler, texcoord.zw + int2(-2, 1) * pixelSize, 0).g;

    weights *= saturate(factor);
}

/*=============================================================================
    Blend weight calculation
=============================================================================*/

float4 SMAABlendingWeightCalculationPS(float2 texcoord,
                                       float2 pixcoord,
                                       float4 offset[3],
                                       float2 e,
                                       Texture2D<float2> edgesTex,
                                       Texture2D<float4> areaTex,
                                       Texture2D<float> searchTex,
                                       float4 subsampleIndices)
{
    float4 weights = float4(0.0, 0.0, 0.0, 0.0);
    float2 pixelSize = ScreenParams.zw;

    [branch]
    if (e.g > 0.0)
    {
        // Edge at north. Diagonals have both north and west edges, so searching
        // one boundary is enough.
        weights.rg = SMAACalculateDiagWeights(edgesTex, areaTex, texcoord, pixcoord, e, subsampleIndices);

        // Diagonals take priority; if a diagonal was found, skip the
        // horizontal/vertical processing.
        [branch]
        if (weights.r == -weights.g)
        {
            float2 d;

            float3 coords;
            coords.x = SMAASearchXLeft(edgesTex, searchTex, offset[0].xy, offset[2].x);
            coords.y = offset[1].y;
            d.x = coords.x;

            float e1 = edgesTex.SampleLevel(EdgesSampler, coords.xy, 0).r;

            coords.z = SMAASearchXRight(edgesTex, searchTex, offset[0].zw, offset[2].y);
            d.y = coords.z;

            d = abs(round(mad(ScreenParams.xx, d, -pixcoord.xx)));

            float2 sqrtD = sqrt(d);

            float e2 = edgesTex.SampleLevel(EdgesSampler, coords.zy + int2(1, 0) * pixelSize, 0).r;

            weights.rg = SMAAArea(areaTex, sqrtD, e1, e2, subsampleIndices.y);

            coords.y = texcoord.y;
            float2 horizontalWeights = weights.rg;
            SMAADetectHorizontalCornerPattern(edgesTex, horizontalWeights, coords.xyzy, d);
            weights.rg = horizontalWeights;
        }
        else
        {
            e.r = 0.0;
        }
    }

    [branch]
    if (e.r > 0.0)
    {
        // Edge at west.
        float2 d;

        float3 coords;
        coords.y = SMAASearchYUp(edgesTex, searchTex, offset[1].xy, offset[2].z);
        coords.x = offset[0].x;
        d.x = coords.y;

        float e1 = edgesTex.SampleLevel(EdgesSampler, coords.xy, 0).g;

        coords.z = SMAASearchYDown(edgesTex, searchTex, offset[1].zw, offset[2].w);
        d.y = coords.z;

        d = abs(round(mad(ScreenParams.yy, d, -pixcoord.yy)));

        float2 sqrtD = sqrt(d);

        float e2 = edgesTex.SampleLevel(EdgesSampler, coords.xz + int2(0, 1) * pixelSize, 0).g;

        weights.ba = SMAAArea(areaTex, sqrtD, e1, e2, subsampleIndices.x);

        coords.x = texcoord.x;
        float2 verticalWeights = weights.ba;
        SMAADetectVerticalCornerPattern(edgesTex, verticalWeights, coords.xyxz, d);
        weights.ba = verticalWeights;
    }

    return weights;
}

/*=============================================================================
    Resolve
=============================================================================*/

// Returns the center color instead of discarding when there is no blend, so
// this can write to an offscreen resolve target.
float4 SMAANeighborhoodBlendingPS(float2 texcoord,
                                  int2 pixel,
                                  Texture2D<float4> colorTex,
                                  Texture2D<float4> blendTex)
{
    int2 lastPixel = int2(ScreenParams.xy) - 1;
    float4 a;

    a.x = blendTex.Load(int3(min(pixel + int2(1, 0), lastPixel), 0)).a; // right
    a.y = blendTex.Load(int3(min(pixel + int2(0, 1), lastPixel), 0)).g; // +Y
    a.wz = blendTex.Load(int3(pixel, 0)).xz;                            // bottom / left

    if (dot(a, float4(1.0, 1.0, 1.0, 1.0)) < 1e-5)
    {
        return colorTex.Load(int3(pixel, 0));
    }

    bool h = max(a.x, a.z) > max(a.y, a.w);

    // The raw weights are also the pixel offsets, while the normalized
    // weights multiply the two resolved samples. This is the reference
    // two-sample blend, not a single bilinear lerp.
    float4 blendingOffset = float4(0.0, a.y, 0.0, a.w);
    float2 blendingWeight = a.yw;
    SMAAMovc(bool4(h, h, h, h), blendingOffset, float4(a.x, 0.0, a.z, 0.0));
    SMAAMovc(bool2(h, h), blendingWeight, a.xz);
    blendingWeight /= dot(blendingWeight, float2(1.0, 1.0));

    float4 blendingCoord = mad(blendingOffset,
                               float4(ScreenParams.z, ScreenParams.w, -ScreenParams.z, -ScreenParams.w),
                               texcoord.xyxy);

    float4 color = blendingWeight.x * colorTex.SampleLevel(ColorLinearSampler, blendingCoord.xy, 0);
    color += blendingWeight.y * colorTex.SampleLevel(ColorLinearSampler, blendingCoord.zw, 0);

    return color;
}

/*=============================================================================
    Pixel-shader path
=============================================================================*/

#if SMAA_ENABLE_PIXEL

float4 SMAAFullscreenVS(uint vertexID : SV_VertexID) : SV_Position
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    return float4(uv * 2.0 - 1.0, 0.0, 1.0);
}

float2 SMAAEdgeDetectionPS(float4 position : SV_Position) : SV_Target
{
    bool hasEdges;
    float2 edges = SMAAColorEdgeDetectionCore(int2(position.xy), ColorTexGamma, hasEdges);

    if (!hasEdges)
    {
        discard;
    }

    return edges;
}

float4 SMAABlendWeightsPS(float4 position : SV_Position) : SV_Target
{
    float2 pixcoord = position.xy;
    float2 texcoord = pixcoord * ScreenParams.zw;
    int2 pixel = int2(pixcoord);

    float2 e = EdgesTex.Load(int3(pixel, 0));

    float4 offset[3];
    SMAABlendingWeightCalculationOffsets(texcoord, offset);

    return SMAABlendingWeightCalculationPS(texcoord,
                                           pixcoord,
                                           offset,
                                           e,
                                           EdgesTex,
                                           AreaLUT,
                                           SearchLUT,
                                           float4(0.0, 0.0, 0.0, 0.0));
}

float4 SMAAResolvePS(float4 position : SV_Position) : SV_Target
{
    float2 texcoord = position.xy * ScreenParams.zw;
    int2 pixel = int2(position.xy);

    if (SMAA_DEBUG_OPTION == 1)
    {
        float2 edges = EdgesTex.SampleLevel(EdgesSampler, texcoord, 0);
        return float4(edges, 0.0, 1.0);
    }

    if (SMAA_DEBUG_OPTION == 2)
    {
        return BlendTex.Load(int3(pixel, 0));
    }

    float4 result = SMAANeighborhoodBlendingPS(texcoord, pixel, ColorTexLinear, BlendTex);
#if SMAA_OUTPUT_SRGB
    float3 linearColor = saturate(result.rgb);
    result.rgb = linearColor <= 0.0031308
        ? linearColor * 12.92
        : 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
#endif
    return result;
}

#endif // SMAA_ENABLE_PIXEL

/*=============================================================================
    Compute path
=============================================================================*/

#if SMAA_ENABLE_COMPUTE

groupshared int g_EdgeMask[SMAA_EDGE_MASK_WORDS];
groupshared uint g_ActiveIDs[SMAA_ACTIVE_SLOTS];
groupshared uint g_ActiveCount;

// Each group owns a 32x16 pixel tile (32x8 threads, two pixels per thread).
// The tile edge mask is one 32-bit word per 16 pixels: 512 pixels / 16 = 32
// words. Word w, bit pair (2*b, 2*b+1) holds the X/Y edge bits for pixel
// (w*16 + b) in tile-linear order. In-bounds pixels with no edge leave their
// pair at zero, so nothing has to clear the mask between frames.
[numthreads(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y, 1)]
void SMAAEdgeDetectionCS(uint3 groupID : SV_GroupID, uint threadID : SV_GroupIndex)
{
    const uint2 groupSize = uint2(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y);
    const uint2 workingArea = groupSize * uint2(1, SMAA_BATCH_SIZE);
    const uint2 screenSize = uint2(ScreenParams.xy);
    const uint groupPixels = groupSize.x * groupSize.y;

    if (threadID < SMAA_EDGE_MASK_WORDS)
    {
        g_EdgeMask[threadID] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint batch = 0; batch < SMAA_BATCH_SIZE; ++batch)
    {
        uint id = batch * groupPixels + threadID;
        uint2 pos = groupID.xy * workingArea + uint2(id % groupSize.x, id / groupSize.x);
        uint code = 0u;

        if (pos.x < screenSize.x && pos.y < screenSize.y)
        {
            bool hasEdges;
            float2 edges = SMAAColorEdgeDetectionCore(int2(pos), ColorTexGamma, hasEdges);

            if (hasEdges && any(edges))
            {
                EdgesTexRW[pos] = edges;
                code = uint(edges.x > 0.5) | (uint(edges.y > 0.5) << 1u);
            }
        }

        if (code != 0u)
        {
            uint word = id / 16u;
            uint shift = (id % 16u) * 2u;
            InterlockedOr(g_EdgeMask[word], int(code << shift));
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (threadID < SMAA_EDGE_MASK_WORDS)
    {
        int2 maskCoord = int2(int(groupID.x * SMAA_EDGE_MASK_WORDS + threadID), int(groupID.y));
        EdgeMaskTexRW[maskCoord] = g_EdgeMask[threadID];
    }
}

// Optional low-contrast edge detector. Kept as a separate entry point so the
// canonical shader compiles exactly as before when this path is not selected.
[numthreads(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y, 1)]
void SMAAEdgeDetectionLowContrastCS(uint3 groupID : SV_GroupID, uint threadID : SV_GroupIndex)
{
    const uint2 groupSize = uint2(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y);
    const uint2 workingArea = groupSize * uint2(1, SMAA_BATCH_SIZE);
    const uint2 screenSize = uint2(ScreenParams.xy);
    const uint groupPixels = groupSize.x * groupSize.y;

    if (threadID < SMAA_EDGE_MASK_WORDS)
    {
        g_EdgeMask[threadID] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint batch = 0; batch < SMAA_BATCH_SIZE; ++batch)
    {
        uint id = batch * groupPixels + threadID;
        uint2 pos = groupID.xy * workingArea + uint2(id % groupSize.x, id / groupSize.x);
        uint code = 0u;

        if (pos.x < screenSize.x && pos.y < screenSize.y)
        {
            bool hasEdges;
            float2 edges = SMAAColorEdgeDetectionLowContrastCore(int2(pos), ColorTexGamma, hasEdges);

            if (hasEdges && any(edges))
            {
                EdgesTexRW[pos] = edges;
                code = uint(edges.x > 0.5) | (uint(edges.y > 0.5) << 1u);
            }
        }

        if (code != 0u)
        {
            uint word = id / 16u;
            uint shift = (id % 16u) * 2u;
            InterlockedOr(g_EdgeMask[word], int(code << shift));
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (threadID < SMAA_EDGE_MASK_WORDS)
    {
        int2 maskCoord = int2(int(groupID.x * SMAA_EDGE_MASK_WORDS + threadID), int(groupID.y));
        EdgeMaskTexRW[maskCoord] = g_EdgeMask[threadID];
    }
}

[numthreads(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y, 1)]
void SMAABlendWeightsCS(uint3 groupID : SV_GroupID, uint threadID : SV_GroupIndex)
{
    const uint2 groupSize = uint2(SMAA_GROUP_SIZE_X, SMAA_GROUP_SIZE_Y);
    const uint2 workingArea = groupSize * uint2(1, SMAA_BATCH_SIZE);

    // Rebuild the tile mask in shared memory. The edge pass wrote it as one
    // word per 16 pixels; here every thread owns two consecutive pixel ids and
    // tests the 2-bit code at its own slot.
    if (threadID < SMAA_EDGE_MASK_WORDS)
    {
        int2 maskCoord = int2(int(groupID.x * SMAA_EDGE_MASK_WORDS + threadID), int(groupID.y));
        g_EdgeMask[threadID] = EdgeMaskTex.Load(int3(maskCoord, 0));
    }

    if (threadID == 0)
    {
        g_ActiveCount = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    uint id0 = threadID * SMAA_BATCH_SIZE + 0;
    uint id1 = threadID * SMAA_BATCH_SIZE + 1;

    uint word0 = id0 / 16u;
    uint word1 = id1 / 16u;
    uint shift0 = (id0 % 16u) * 2u;
    uint shift1 = (id1 % 16u) * 2u;

    uint code0 = (uint(g_EdgeMask[word0]) >> shift0) & 3u;
    uint code1 = (uint(g_EdgeMask[word1]) >> shift1) & 3u;

    bool active0 = code0 != 0u;
    bool active1 = code1 != 0u;

    // Compact the active pixels of this tile into a shared list. Each wave
    // reserves a contiguous block with one atomic, then scatters its set lanes
    // into that block with a prefix count. The SM5 path falls back to one
    // atomic per pixel but produces the same list semantics.
#if SMAA_USE_SM6
    uint count0 = WaveActiveCountBits(active0);
    uint count1 = WaveActiveCountBits(active1);
    uint base0 = 0;
    uint base1 = 0;
    if (WaveGetLaneIndex() == 0)
    {
        InterlockedAdd(g_ActiveCount, count0, base0);
        InterlockedAdd(g_ActiveCount, count1, base1);
    }
    base0 = WaveReadLaneFirst(base0);
    base1 = WaveReadLaneFirst(base1);
    if (active0)
    {
        g_ActiveIDs[base0 + WavePrefixCountBits(active0)] = id0;
    }
    if (active1)
    {
        g_ActiveIDs[base1 + WavePrefixCountBits(active1)] = id1;
    }
#else
    if (active0)
    {
        uint slot0;
        InterlockedAdd(g_ActiveCount, 1u, slot0);
        g_ActiveIDs[slot0] = id0;
    }
    if (active1)
    {
        uint slot1;
        InterlockedAdd(g_ActiveCount, 1u, slot1);
        g_ActiveIDs[slot1] = id1;
    }
#endif

    GroupMemoryBarrierWithGroupSync();

    // Every thread drains the compacted list with a stride equal to the thread
    // count. Idle pixels never enter the list, so only real edges run the
    // weight calculation.
    uint activeCount = g_ActiveCount;
    uint activeIndex = threadID;
    while (activeIndex < activeCount)
    {
        uint id = g_ActiveIDs[activeIndex];
        uint2 pos = groupID.xy * workingArea + uint2(id % groupSize.x, id / groupSize.x);

        uint word = id / 16u;
        uint shift = (id % 16u) * 2u;
        uint code = (uint(g_EdgeMask[word]) >> shift) & 3u;

        float2 e = float2(float(code & 1u), float((code >> 1u) & 1u));
        float2 pixcoord = float2(pos) + 0.5;
        float2 texcoord = pixcoord * ScreenParams.zw;
        float4 offset[3];
        SMAABlendingWeightCalculationOffsets(texcoord, offset);

        float4 weights = SMAABlendingWeightCalculationPS(texcoord,
                                                         pixcoord,
                                                         offset,
                                                         e,
                                                         EdgesTex,
                                                         AreaLUT,
                                                         SearchLUT,
                                                         float4(0.0, 0.0, 0.0, 0.0));

        BlendTexRW[pos] = weights;
        activeIndex += groupSize.x * groupSize.y;
    }
}

#endif // SMAA_ENABLE_COMPUTE
