Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);

struct PassthroughVSOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

PassthroughVSOut PassthroughVS(uint vertexID : SV_VertexID)
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    PassthroughVSOut o;
    o.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}

float4 PassthroughPS(PassthroughVSOut input) : SV_Target
{
    return Source.SampleLevel(SourceSampler, input.uv, 0.0);
}
