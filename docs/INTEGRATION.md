# Integration guide

`SMAA.hlsl` is a standalone shader library. The application owns every resource, view, root signature or descriptor set, barrier, clear, and presentation step.

- Optimized path: `SMAAEdgeDetectionCS` -> `SMAABlendWeightsCS` -> `SMAAResolvePS`.
- Comparison path: the original 2013 SMAA pixel shaders exposed by `Reference/SMAAReference.hlsl`.
- The optimized compute path requires Shader Model 6.0 by default because it uses wave intrinsics.

## D3D12 resource contract

| Resource | Format | Size | Usage |
| --- | --- | --- | --- |
| `ColorTexGamma` | `R8G8B8A8_UNORM` view (non-sRGB) | screen size | Edge detection input |
| `ColorTexLinear` | `R8G8B8A8_UNORM_SRGB` view | screen size | Resolve color input |
| `EdgesTex` | `R8G8_UNORM` | screen size | Edge flags (X/Y) |
| `BlendTex` | `R8G8B8A8_UNORM` | screen size | Blend weights and coverage |
| `EdgeMaskTex` | `R32_SINT` | `ceil(width/32)*32` x `ceil(height/16)` | 2-bit-per-pixel tile mask |
| `AreaLUT` | `R8G8_UNORM` | 160x560 | SMAA area lookup table |
| `SearchLUT` | `R8_UNORM` | 64x16 | SMAA search lookup table |

Create the color resource as a typeless format (for example `R8G8B8A8_TYPELESS`) and make both color views from it. A 10-bit source can use `R10G10B10A2_UNORM` for the gamma view and an sRGB view for resolve. Do not enable sRGB on either LUT. Area uses linear filtering; search uses point/load access, so its sampler state does not affect the result.

`BlendTex` must be cleared each frame or otherwise fully initialized: the blend pass writes only active pixels, while resolve samples the whole texture. `EdgesTex` must also be cleared each frame: it receives only edge pixels, but the blend pass's horizontal and vertical searches sample neighboring texels along each line, and non-edge neighbors must read zero. `EdgeMaskTex` is fully rewritten by the edge pass and does not need a clear.

The supplied LUT binaries are:

- `Reference/AreaTex.rg8`: 160x560, two bytes per pixel (R = area X, G = area Y).
- `Reference/SearchTex.r8`: 64x16, one byte per pixel.

## D3D12 bindings

Constant buffer, space 0:

```hlsl
cbuffer SMAAParams : register(b0)
{
    float4 ScreenParams;      // (width, height, 1/width, 1/height)
    float4 SearchParams;      // (0.10, 32, 16, 25)
    int4   Options;           // x: edge mode (0 max channel, 1 luma),
                              // y: debug (0/1/2), z: low-contrast flags, w: reserved
    float4 LowContrastParams; // (threshold low, threshold high, threshold floor, CMAA synergy)
};
```

| Register | Resource | View |
| --- | --- | --- |
| `b0` | `SMAAParams` | Constant buffer |
| `t0` | `ColorTexGamma` | SRV, non-sRGB |
| `t1` | `ColorTexLinear` | SRV, sRGB |
| `t2` | `EdgesTex` | SRV, `R8G8_UNORM` |
| `t3` | `BlendTex` | SRV, `R8G8B8A8_UNORM` |
| `t4` | `AreaLUT` | SRV, `R8G8_UNORM` |
| `t5` | `SearchLUT` | SRV, `R8_UNORM` |
| `t6` | `EdgeMaskTex` | SRV, `R32_SINT` |
| `u0` | `EdgesTexRW` | UAV, `R8G8_UNORM` |
| `u1` | `BlendTexRW` | UAV, `R8G8B8A8_UNORM` |
| `u2` | `EdgeMaskTexRW` | UAV, `R32_SINT` |
| `s0` | `ColorLinearSampler` | Linear, clamp |
| `s1` | `EdgesSampler` | Linear, clamp |
| `s2` | `AreaSampler` | Linear, clamp |

The demo uses a root signature with `b0`, a seven-SRV table starting at `t0`, and a three-UAV table starting at `u0`. The three samplers are static samplers.

## Dispatch and tile layout

Each group has 256 threads (`32x8`) and covers a `32x16` pixel tile, with two pixels per thread:

```text
dispatchX = ceil(width  / 32)
dispatchY = ceil(height / 16)
EdgeMaskWidth  = dispatchX * 32
EdgeMaskHeight = dispatchY
```

The tile mask is 32 32-bit words, one word per 16 pixels. Word `w` holds 16 pixels `w*16 .. w*16+15` in tile-linear order, two bits per pixel: bit 0 is the X edge, bit 1 is the Y edge. Word `w` for group `(gx, gy)` is stored at `(gx * 32 + w, gy)`. In-bounds pixels without an edge leave their two bits at zero, so the mask does not need a texture-level clear. Out-of-bounds threads write nothing.

The edge pass writes `EdgesTex` only when an edge is present. The blend pass rebuilds the mask in groupshared memory, compacts active pixel IDs into a shared list, and drains that list with a thread-count stride. Pixels without edges never run the search or weight calculation.

## Barrier sequence

A minimal D3D12 sequence:

1. Transition `EdgesTex`, `BlendTex`, and `EdgeMaskTex` to `UNORDERED_ACCESS`. Clear `EdgesTex` and `BlendTex` every frame: the blend searches sample neighboring non-edge texels, and the edge pass only writes pixels that have edges.
2. Bind the optimized root signature, constants, SRVs, and UAVs. Dispatch `SMAAEdgeDetectionCS`.
3. UAV barrier on `EdgesTex` and `EdgeMaskTex`, then transition both to `ALL_SHADER_RESOURCE` (or `NON_PIXEL_SHADER_RESOURCE`). Keep `BlendTex` as a UAV. Dispatch `SMAABlendWeightsCS`.
4. UAV barrier on `BlendTex`, then transition it to a sampled state.
5. Draw the fullscreen triangle with `SMAAResolvePS`, reading `ColorTexLinear` and `BlendTex`. Transition the output target to `PRESENT` afterward.

On D3D12 a UAV barrier is sufficient for UAV write/read hazards within the same queue. State transitions are still required when the resource moves between UAV and SRV use.

## Pixel-shader fallback

If compute is unavailable or undesirable, the library also exposes the original pixel path:

- `SMAAEdgeDetectionPS` writes `EdgesTex`.
- `SMAABlendingWeightCalculationPS` reads `EdgesTex` and writes `BlendTex`.
- `SMAANeighborhoodBlendingPS` combines color and blend weights.

For the fallback, clear `EdgesTex` and `BlendTex` before use. To skip non-edge pixels, use a stencil buffer: draw the edge pass with stencil `REPLACE`, reference 1, then draw the blend pass with stencil `EQUAL`, reference 1. The resolve pass always writes an output pixel. The fullscreen triangle assumes culling is disabled or the host uses a matching winding. The demo's comparison path uses the pixel shaders without stencil, which is also valid.

`SMAAResolvePS` returns linear values from an sRGB color SRV. If the render target is a UNORM view, compile with `SMAA_OUTPUT_SRGB=1` so the shader performs the linear-to-sRGB transfer (the demo does this). If the target is an sRGB render-target view, leave it at 0.

## Modes in the demo

| Mode | Path |
| --- | --- |
| 0 | Optimized compute edge, blend, and resolve (default) |
| 1 | Original 2013 SMAA pixel shaders through `SMAAReference.hlsl` |
| 2 | SMAA off; scene is copied to the back buffer |

When compiled without specialization, edge detection is selected by `Options.x` at runtime:

| Edge mode | Detector |
| --- | --- |
| 0 | Max-channel metric on the exact-center taps (default) |
| 1 | Luma-weighted metric on the same taps |

### Low-contrast edge mode

`SMAAEdgeDetectionLowContrastCS` is a separate optional edge-detection entry point. It uses a 3x3 color neighborhood with a luma-adaptive threshold and CMAA-style contrast suppression to find low-contrast edges that the stock detector misses. It writes the same `EdgesTex` and `EdgeMaskTex` in the same format, with the same group size and dispatch dimensions, so the blend and resolve passes are unchanged.

Enable it by dispatching `SMAAEdgeDetectionLowContrastCS` instead of `SMAAEdgeDetectionCS` and setting the `Options.z` bits:

| Bit | Feature |
| ---: | --- |
| 1 | Luma-adaptive threshold |
| 4 | CMAA local contrast suppression |
| 8 | CMAA/SMAA LCA synergy |

Set `LowContrastParams` to `(low, high, floor, synergy)`. The demo uses `(0.030, 0.095, 0.035, -0.45)` and flag value 13. The mode is off by default; `Options.z = 0` means the stock path.

The mode is roughly 40-55% slower than optimized stock in 3D because it reads nine color texels per pixel and finds more active edges. It uses no blur, smoothing, or sharpening pass. The stock entry point does not call any low-contrast code, so DXC removes it from the stock PSO. Hosts that only use stock SMAA do not need to compile the low-contrast entry point at all; the demo compiles it only when `--low-contrast-edge` is present.

### Compile-time option specialization

For fixed settings, define `SMAA_EDGE_MODE` and/or `SMAA_DEBUG_MODE` when compiling to remove the corresponding `Options` branches:

| Macro | Values | Effect |
| --- | --- | --- |
| `SMAA_EDGE_MODE` | 0, 1 | Folds edge detection to max-channel or luma |
| `SMAA_DEBUG_MODE` | 0, 1, 2 | Folds resolve to normal, edge view, or blend view |

```text
dxc -T cs_6_0 -E SMAAEdgeDetectionCS -DSMAA_EDGE_MODE=0 SMAA.hlsl
dxc -T cs_6_0 -E SMAAEdgeDetectionLowContrastCS -DSMAA_EDGE_MODE=0 SMAA.hlsl
dxc -T ps_6_0 -E SMAAResolvePS -DSMAA_DEBUG_MODE=0 -DSMAA_OUTPUT_SRGB=1 SMAA.hlsl
```

Leave either macro undefined to read `Options.x`/`Options.y` at runtime. The demo defines both macros, always compiles the two stock edge compute shaders and three resolve pixel shaders, and compiles the two low-contrast edge variants only when the mode is enabled. It selects the PSOs from the active edge mode, low-contrast flag, and debug view. `SMAA_EDGE_MODE` affects both edge entry points; `SMAA_DEBUG_MODE` affects `SMAAResolvePS`.

## Demo controls

| Key | Action |
| --- | --- |
| F1 or 1 | Optimized compute path (mode 0) |
| F2 or 2 | Original 2013 SMAA path (mode 1) |
| F3 or 3 | SMAA off (mode 2) |
| F4 | Toggle the 2D test pattern and the 3D corridor scene |
| F5 | Toggle edge visualization |
| F6 | Toggle blend-weight visualization |
| F12 | Save a BMP screenshot |
| Space | Pause or resume the 3D scene animation; SMAA keeps running |

The scene can also be selected at startup with `--scene 3d|2d` (default `3d`). In 2D mode the cached test-pattern bytes are re-uploaded to the color texture, because the 3D scene overwrites that texture every frame.

## Vulkan notes

The shader supports DXC SPIR-V output:

```text
dxc -T cs_6_0 -E SMAAEdgeDetectionCS -spirv -DSMAA_VULKAN=1 SMAA.hlsl
dxc -T cs_6_0 -E SMAABlendWeightsCS  -spirv -DSMAA_VULKAN=1 SMAA.hlsl
dxc -T ps_6_0 -E SMAAResolvePS       -spirv -DSMAA_VULKAN=1 SMAA.hlsl
dxc -T vs_6_0 -E SMAAFullscreenVS    -spirv -DSMAA_VULKAN=1 SMAA.hlsl
```

`SMAA_VULKAN=1` switches the D3D register declarations to explicit `[[vk::binding(n, 0)]]` bindings and adds `[[vk::image_format(...)]]` to the storage images.

| Binding (set 0) | Resource |
| --- | --- |
| 0 | `SMAAParams` constant buffer |
| 1 | `ColorTexGamma` sampled image |
| 2 | `ColorTexLinear` sampled image |
| 3 | `EdgesTex` sampled image |
| 4 | `BlendTex` sampled image |
| 5 | `AreaLUT` sampled image |
| 6 | `SearchLUT` sampled image |
| 7 | `EdgeMaskTex` sampled image |
| 8 | `EdgesTexRW` storage image, `rg8` |
| 9 | `BlendTexRW` storage image, `rgba8` |
| 10 | `EdgeMaskTexRW` storage image, `r32i` |
| 11 | `ColorLinearSampler` |
| 12 | `EdgesSampler` |
| 13 | `AreaSampler` |

Use `VK_IMAGE_LAYOUT_GENERAL` for the storage images and `VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL` for sampled resources, with image memory barriers between passes. Match the 48-byte constant buffer layout in the host (three 16-byte vectors). The edge mask is an R32 signed integer storage image with the same word/tile layout as D3D12.

The default `SMAA_USE_SM6=1` path uses wave operations, which DXC lowers to subgroup operations. Target Vulkan 1.1+ and check that the device exposes the required subgroup support; if it does not, compile with `SMAA_USE_SM6=0` to use the groupshared-atomic fallback. `R8G8_UNORM` storage-image support is optional on some older devices; in that case use an RGBA8 edge texture and change the declared format and UAV type to match.

The Vulkan path is provided by the shader but is **not validated by this repository**. The demo and all measurements in this project use D3D12.
