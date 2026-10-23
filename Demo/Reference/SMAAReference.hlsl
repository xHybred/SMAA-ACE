// Wrapper that exposes the original 2013 iryoku SMAA as plain D3D12 shader
// entry points. The only change to SMAA2013.hlsl itself is explicit sampler
// registers on LinearSampler (s0) and PointSampler (s1).

#define SMAA_HLSL_4
#define SMAA_INCLUDE_VS 1
#define SMAA_INCLUDE_PS 1
#define SMAA_THRESHOLD 0.1
#define SMAA_MAX_SEARCH_STEPS 32
#define SMAA_MAX_SEARCH_STEPS_DIAG 16
#define SMAA_CORNER_ROUNDING 25

cbuffer ReferenceParams : register(b0)
{
    float4 SMAA_RT_METRICS;
};

#include "SMAA2013.hlsl"

Texture2D<float4> colorTex : register(t0);
Texture2D<float4> edgesTex : register(t1);
Texture2D<float4> blendTex : register(t2);
Texture2D<float4> areaTex  : register(t3);
Texture2D<float4> searchTex: register(t4);

struct RefEdgeVSOut
{
    float4 position : SV_Position;
    float2 texcoord : TEXCOORD0;
    float4 offset0  : TEXCOORD1;
    float4 offset1  : TEXCOORD2;
    float4 offset2  : TEXCOORD3;
};

struct RefBlendVSOut
{
    float4 position : SV_Position;
    float2 texcoord : TEXCOORD0;
    float2 pixcoord : TEXCOORD1;
    float4 offset0  : TEXCOORD2;
    float4 offset1  : TEXCOORD3;
    float4 offset2  : TEXCOORD4;
};

struct RefResolveVSOut
{
    float4 position : SV_Position;
    float2 texcoord : TEXCOORD0;
    float4 offset   : TEXCOORD1;
};

void RefFullscreenVS(uint vertexId : SV_VertexID,
                     out float4 position : SV_Position,
                     out float2 texcoord : TEXCOORD0)
{
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    texcoord = uv;
    position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

RefEdgeVSOut RefEdgeVS(uint vertexId : SV_VertexID)
{
    RefEdgeVSOut o;
    float4 offsets[3];
    RefFullscreenVS(vertexId, o.position, o.texcoord);
    SMAAEdgeDetectionVS(o.texcoord, offsets);
    o.offset0 = offsets[0];
    o.offset1 = offsets[1];
    o.offset2 = offsets[2];
    return o;
}

float2 RefEdgePS(RefEdgeVSOut input) : SV_Target
{
    float4 offsets[3] = { input.offset0, input.offset1, input.offset2 };
    return SMAAColorEdgeDetectionPS(input.texcoord, offsets, colorTex);
}

RefBlendVSOut RefBlendVS(uint vertexId : SV_VertexID)
{
    RefBlendVSOut o;
    float4 offsets[3];
    RefFullscreenVS(vertexId, o.position, o.texcoord);
    SMAABlendingWeightCalculationVS(o.texcoord, o.pixcoord, offsets);
    o.offset0 = offsets[0];
    o.offset1 = offsets[1];
    o.offset2 = offsets[2];
    return o;
}

float4 RefBlendPS(RefBlendVSOut input) : SV_Target
{
    float4 offsets[3] = { input.offset0, input.offset1, input.offset2 };
    return SMAABlendingWeightCalculationPS(input.texcoord, input.pixcoord, offsets,
                                           edgesTex, areaTex, searchTex, 0.0);
}

RefResolveVSOut RefResolveVS(uint vertexId : SV_VertexID)
{
    RefResolveVSOut o;
    RefFullscreenVS(vertexId, o.position, o.texcoord);
    SMAANeighborhoodBlendingVS(o.texcoord, o.offset);
    return o;
}

float4 RefResolvePS(RefResolveVSOut input) : SV_Target
{
    float4 result = SMAANeighborhoodBlendingPS(input.texcoord, input.offset, colorTex, blendTex);
#if SMAA_OUTPUT_SRGB
    float3 linearColor = saturate(result.rgb);
    result.rgb = linearColor <= 0.0031308
        ? linearColor * 12.92
        : 1.055 * pow(linearColor, 1.0 / 2.4) - 0.055;
#endif
    return result;
}
