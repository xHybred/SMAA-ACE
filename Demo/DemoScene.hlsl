// Procedural raymarched 3D room/corridor used as the live color source for the SMAA demo.
// Everything is analytic: no textures or external assets.

struct SceneVSOut
{
    float4 position : SV_Position;
};

cbuffer SceneCB : register(b0)
{
    float4 gTimeRes;    // x: seconds, y: frame, z: width, w: height
    float4 gCamPos;     // xyz: camera position
    float4 gCamRight;   // xyz: camera right
    float4 gCamUp;      // xyz: camera up
    float4 gCamForward; // xyz: camera forward
    float4 gCamParams;  // x: tan(half fov), y: aspect ratio
};

static const int MAX_STEPS = 110;
static const float MAX_DIST = 55.0;

static const float3 LIGHT_POS[3] =
{
    float3(0.0, 2.65, 3.0),
    float3(0.0, 2.65, -3.2),
    float3(0.0, 2.65, -9.4)
};

static const float3 LIGHT_COL[3] =
{
    float3(1.00, 0.52, 0.22),
    float3(0.28, 0.72, 1.00),
    float3(0.95, 0.28, 0.55)
};

SceneVSOut SceneFullscreenVS(uint vertexID : SV_VertexID)
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    SceneVSOut o;
    o.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float sdBox(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float sdCylinderZ(float2 p, float r)
{
    return length(p) - r;
}

float sdBoxRotX(float3 p, float3 b, float a)
{
    float c = cos(a);
    float s = sin(a);
    float3 q = float3(p.x, c * p.y + s * p.z, -s * p.y + c * p.z);
    return sdBox(q, b);
}

float sdBoxRotY(float3 p, float3 b, float a)
{
    float c = cos(a);
    float s = sin(a);
    float3 q = float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
    return sdBox(q, b);
}

// Returns (distance, material id).
// Materials: 0 room shell, 1 pillar, 2 wall panel, 3 ceiling pipe,
//            4 cyan strip, 5 orange strip, 6 diagonal brace, 7 handrail, 8 crate.
float2 mapScene(float3 p)
{
    float3 rc = p - float3(0.0, 0.9, -4.0);
    float room = -max(abs(rc.x) - 4.0, max(abs(rc.y) - 2.1, abs(rc.z) - 12.0));
    float2 res = float2(room, 0.0);

    // Pillars repeating every 4 units along the corridor.
    float pz = p.z - 4.0 * round(p.z * 0.25);
    float pillar = sdBox(float3(p.x - 3.30, p.y - 0.9, pz), float3(0.28, 2.1, 0.28));
    pillar = min(pillar, sdBox(float3(p.x + 3.30, p.y - 0.9, pz), float3(0.28, 2.1, 0.28)));
    if (pillar < res.x) res = float2(pillar, 1.0);

    // Recessed wall panels, offset half a cell from the pillars.
    float wz = p.z - 4.0 * round((p.z - 2.0) * 0.25);
    float panel = sdBox(float3(p.x - 3.88, p.y - 1.55, wz), float3(0.05, 0.55, 0.95));
    panel = min(panel, sdBox(float3(p.x + 3.88, p.y - 1.55, wz), float3(0.05, 0.55, 0.95)));
    panel = min(panel, sdBox(float3(p.x - 3.88, p.y - 0.32, wz), float3(0.05, 0.16, 0.95)));
    panel = min(panel, sdBox(float3(p.x + 3.88, p.y - 0.32, wz), float3(0.05, 0.16, 0.95)));
    if (panel < res.x) res = float2(panel, 2.0);

    // Thin ceiling pipes running the length of the corridor.
    float pipe = sdCylinderZ(float2(p.x - 2.15, p.y - 2.62), 0.055);
    pipe = min(pipe, sdCylinderZ(float2(p.x + 2.15, p.y - 2.62), 0.055));
    if (pipe < res.x) res = float2(pipe, 3.0);

    // Thin handrails.
    float rail = sdCylinderZ(float2(p.x - 3.55, p.y + 0.45), 0.045);
    rail = min(rail, sdCylinderZ(float2(p.x + 3.55, p.y + 0.45), 0.045));
    if (rail < res.x) res = float2(rail, 7.0);

    // Emissive ceiling strips.
    float ceilStrip = sdBox(p - float3(0.95, 2.94, -4.0), float3(0.05, 0.02, 9.5));
    ceilStrip = min(ceilStrip, sdBox(p - float3(-0.95, 2.94, -4.0), float3(0.05, 0.02, 9.5)));
    if (ceilStrip < res.x) res = float2(ceilStrip, 4.0);

    // Emissive floor strips.
    float floorStrip = sdBox(p - float3(1.45, -1.18, -4.0), float3(0.035, 0.018, 9.5));
    floorStrip = min(floorStrip, sdBox(p - float3(-1.45, -1.18, -4.0), float3(0.035, 0.018, 9.5)));
    if (floorStrip < res.x) res = float2(floorStrip, 5.0);

    // Diagonal structural braces against the walls.
    float brace = sdBoxRotX(float3(p.x - 3.80, p.y - 0.9, pz), float3(0.045, 1.15, 0.045), 0.7);
    brace = min(brace, sdBoxRotX(float3(p.x + 3.80, p.y - 0.9, pz), float3(0.045, 1.15, 0.045), -0.7));
    if (brace < res.x) res = float2(brace, 6.0);

    // A few crates with deliberately non-axis-aligned edges.
    float crate = sdBoxRotY(p - float3(2.55, -0.72, 1.0), float3(0.46, 0.46, 0.46), 0.45);
    crate = min(crate, sdBoxRotY(p - float3(-2.45, -0.78, -3.6), float3(0.42, 0.42, 0.42), -0.30));
    crate = min(crate, sdBoxRotY(p - float3(1.55, -0.86, -7.4), float3(0.32, 0.32, 0.52), 0.20));
    if (crate < res.x) res = float2(crate, 8.0);

    return res;
}

float3 materialColor(float m, float3 p, float3 n)
{
    if (m < 0.5)
    {
        if (n.y > 0.55)
        {
            // Tiled floor with dark grout lines.
            float3 c = float3(0.20, 0.22, 0.26);
            float2 g = abs(frac(p.xz) - 0.5);
            float groutLine = min(g.x, g.y);
            c *= lerp(0.30, 1.0, smoothstep(0.012, 0.035, groutLine));
            return c;
        }
        if (n.y < -0.55)
            return float3(0.24, 0.26, 0.30);

        // Wall with a diagonal painted accent stripe.
        float3 c = float3(0.34, 0.36, 0.42);
        float stripe = abs(frac((p.z + p.y * 1.5) * 0.55) - 0.5);
        c = lerp(c, float3(0.80, 0.40, 0.12), (1.0 - smoothstep(0.004, 0.020, stripe)) * 0.85);
        if (p.y < -0.80) c = float3(0.10, 0.11, 0.13);
        if (p.y > 2.72) c = float3(0.27, 0.28, 0.32);
        return c;
    }
    if (m < 1.5) return float3(0.42, 0.44, 0.48); // pillar
    if (m < 2.5) return float3(0.55, 0.40, 0.24); // panel
    if (m < 3.5) return float3(0.14, 0.15, 0.18); // pipe
    if (m < 4.5) return float3(0.07, 0.10, 0.12); // cyan strip base
    if (m < 5.5) return float3(0.12, 0.07, 0.04); // orange strip base
    if (m < 6.5) return float3(0.30, 0.29, 0.27); // brace
    if (m < 7.5) return float3(0.62, 0.46, 0.20); // rail

    // Crate with plank seams.
    float3 c = float3(0.44, 0.27, 0.14);
    float plank = abs(frac(p.y * 6.0) - 0.5);
    c *= lerp(0.55, 1.0, smoothstep(0.02, 0.06, plank));
    return c;
}

float3 shadeScene(float3 p, float3 n, float m, float3 rd, float t)
{
    float3 albedo = materialColor(m, p, n);
    float3 color = albedo * (0.13 + 0.10 * saturate(n.y));
    float3 v = -rd;

    [unroll]
    for (int i = 0; i < 3; i++)
    {
        float3 lv = LIGHT_POS[i] - p;
        float d2 = dot(lv, lv);
        float3 l = lv * rsqrt(d2);
        float ndl = saturate(dot(n, l));
        float atten = 3.0 / (1.0 + 0.22 * d2);
        color += albedo * LIGHT_COL[i] * (ndl * atten * 0.55);

        float3 h = normalize(l + v);
        float spec = pow(saturate(dot(n, h)), 48.0);
        color += LIGHT_COL[i] * (spec * atten * 0.30);
    }

    if (m > 3.5 && m < 4.5)
        color += float3(0.25, 1.30, 1.60) * 1.25;
    else if (m > 4.5 && m < 5.5)
        color += float3(1.60, 0.55, 0.18) * 1.25;

    float fog = 1.0 - exp(-t * t * 0.0045);
    color = lerp(color, float3(0.020, 0.028, 0.045), fog);
    return color;
}

float3 calcNormal(float3 p, float t)
{
    float e = max(0.0008, t * 0.0009);
    float3 n = float3(
        mapScene(p + float3(e, 0.0, 0.0)).x - mapScene(p - float3(e, 0.0, 0.0)).x,
        mapScene(p + float3(0.0, e, 0.0)).x - mapScene(p - float3(0.0, e, 0.0)).x,
        mapScene(p + float3(0.0, 0.0, e)).x - mapScene(p - float3(0.0, 0.0, e)).x);
    return normalize(n);
}

float2 marchScene(float3 ro, float3 rd)
{
    float t = 0.0;
    [loop]
    for (int i = 0; i < MAX_STEPS; i++)
    {
        float3 p = ro + rd * t;
        float2 h = mapScene(p);
        if (h.x < max(0.0012, t * 0.0011))
            return float2(t, h.y);
        t += h.x;
        if (t > MAX_DIST)
            break;
    }
    return float2(-1.0, -1.0);
}

float4 ScenePS(SceneVSOut input) : SV_Target
{
    float2 uv = input.position.xy / gTimeRes.zw;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);

    float tanHalf = gCamParams.x;
    float aspect = gCamParams.y;
    float3 rd = normalize(gCamForward.xyz
        + gCamRight.xyz * (ndc.x * aspect * tanHalf)
        + gCamUp.xyz * (ndc.y * tanHalf));

    float3 ro = gCamPos.xyz;
    float2 hit = marchScene(ro, rd);

    float3 color;
    if (hit.x < 0.0)
    {
        color = float3(0.020, 0.028, 0.045);
    }
    else
    {
        float3 p = ro + rd * hit.x;
        float3 n = calcNormal(p, hit.x);
        color = shadeScene(p, n, hit.y, rd, hit.x);
    }

    // Filmic-ish tonemap, then gamma-space output for the UNORM color target.
    color = 1.0 - exp(-color * 1.35);
    color = pow(saturate(color), 1.0 / 2.2);
    return float4(color, 1.0);
}
