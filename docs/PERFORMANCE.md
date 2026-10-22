# Performance and quality

## Benchmark method

- **Scene:** the demo's primary workload is a procedural raymarched 3D corridor. A 2D high-frequency stress pattern provides a separate, deliberately pessimistic worst-case reference.
- **Deterministic time:** scene animation is frame-based (`frame / 60`), not wall-clock based, so a given frame number always renders the same image. This makes screenshots and benchmark runs reproducible.
- **GPU timestamps:** D3D12 timestamp queries bracket the SMAA work. Timestamp 0 is recorded after the scene has been drawn, so scene rendering is excluded. Timestamps 1, 2, and 3 bracket edge detection, blend weights, and resolve. The reported total spans timestamp 0 to 3.
- **Sampling:** 300 frames per run, 3 repeats per configuration. Tables report the median of the three run medians and the range across repeats.
- **Resolution:** 1920x1080, 2560x1440, and 3840x2160 at true output resolution, with no render scaling or upscaling.
- **GPU:** NVIDIA RTX 4070 SUPER.
- **Post-processing:** ReShade and other external post-processing must be disabled for benchmarks. No ReShade proxy DLL, injection registry entry, `reshade-shaders` directory, or loaded ReShade module is present for these runs; the only `dxgi`/`d3d12` modules loaded by the demo are the Microsoft system DLLs. See [Benchmark prerequisites](#benchmark-prerequisites).
- **Screenshots:** the in-app `--screenshot` capture is recorded from the render target into a readback buffer and written **before** `Present`, so it contains exactly the SMAA output and no desktop compositor or overlay contribution.
- **Debug layer:** the D3D12 debug layer is enabled by default. `--no-debug-layer` disables it for benchmarking. GPU timestamp results are reported both ways; the debug layer does not materially change GPU pass cost but does add host-side validation overhead.
- **Settings:** each mode runs with the same scene and SMAA parameters: threshold 0.10, 32 search steps, 16 diagonal steps, and 25% corner rounding. Default edge mode is 0 (max-channel). The low-contrast edge mode is off unless a table says otherwise.
- **Compile-time specialization:** the demo compiles one edge shader per edge mode and one resolve shader per debug view, defining `SMAA_EDGE_MODE` and `SMAA_DEBUG_MODE`. When `--low-contrast-edge` is present it also compiles the separate low-contrast edge entry point; otherwise that shader is never compiled and the stock entry point does not contain its code. This folds the `Options.x` and `Options.y` branches for the standard demo path. There is no runtime toggle between specialized and generic shaders. Standalone users that leave the macros undefined retain the runtime `Options` behavior.

## Benchmark prerequisites

ReShade or any other injector/overlay changes the presented image and can invalidate both quality captures and timing. Before benchmarking:

- Remove or rename local `dxgi.dll`, `d3d12.dll`, `d3d11.dll`, `opengl32.dll`, `ReShade64.dll`, `ReShade32.dll`, or `reshade-shaders` proxies next to `Demo.exe`.
- Disable system-wide/global injection.
- Confirm the running process loads only the Microsoft `dxgi.dll`/`d3d12.dll` from `C:\Windows\System32`.
- No ReShade installation or global injection was detected for the results in this document.

## 3D corridor scene results

These are the primary demo results. Median of 300-frame runs, 3 repeats; brackets show the range across repeats. "Debug on" is the default D3D12 debug layer; "debug off" uses `--no-debug-layer`.

| Resolution | Optimized (debug on) | Optimized (debug off) | Original 2013 (debug on) | Original 2013 (debug off) |
| --- | ---: | ---: | ---: | ---: |
| 1920x1080 | 0.057 [0.055-0.057] ms | 0.055 [0.055-0.057] ms | 0.115 [0.115-0.115] ms | 0.115 [0.099-0.115] ms |
| 2560x1440 | 0.084 [0.084-0.087] ms | 0.084 [0.084-0.087] ms | 0.186 [0.163-0.187] ms | 0.163 [0.163-0.164] ms |
| 3840x2160 | 0.170 [0.164-0.170] ms | 0.169 [0.164-0.170] ms | 0.407 [0.353-0.409] ms | 0.352 [0.352-0.352] ms |

The aggregate `total` can flip between two clock states on the heavier original path. The 4K original total across the three debug-off runs had a median of 0.352 ms; the pass sum (edge + blend + resolve) is 0.350 ms. The optimized path is stable within about 0.006 ms.

### 4K breakdown

Debug layer off, medians:

| Pass | Optimized | Original 2013 |
| --- | ---: | ---: |
| Edge detection | 0.058 ms | 0.059 ms |
| Blend weights | 0.051 ms | 0.222 ms |
| Resolve | 0.060 ms | 0.069 ms |
| Total | 0.169 ms | 0.352 ms |

At 4K the optimized path is about 52% lower in total, and its blend-weight pass is about 4.4x faster. Edge detection and resolve are close between the two paths; the gain comes from the compacted blend pass, which skips search and weight calculation for pixels without edges.

### Per-pass notes

Debug layer off, medians:

| Resolution | Edge | Blend | Resolve | Total |
| --- | ---: | ---: | ---: | ---: |
| 1920x1080 optimized | 0.018 ms | 0.020 ms | 0.016 ms | 0.055 ms |
| 2560x1440 optimized | 0.029 ms | 0.029 ms | 0.026 ms | 0.084 ms |
| 3840x2160 optimized | 0.058 ms | 0.051 ms | 0.060 ms | 0.169 ms |

The 3D corridor is a sparse-edge scene relative to the stress pattern, so the blend pass has comparatively little edge work to do. Absolute totals are therefore low and should not be read as a ceiling for edge-dense content.

### Low-contrast edge mode (opt-in)

The low-contrast mode is a separate edge-detection entry point selected by `--low-contrast-edge`. It is disabled by default, uses a 3x3 neighborhood, and does not change the blend or resolve passes. Debug layer off, medians of 300-frame runs, 3 repeats.

| Scene | Optimized stock | Low-contrast mode | Difference |
| --- | ---: | ---: | ---: |
| 1920x1080 3D | 0.055 ms | 0.078 ms | +42% |
| 2560x1440 3D | 0.084 ms | 0.123 ms | +46% |
| 3840x2160 3D | 0.169 ms | 0.248 ms | +47% |
| 3840x2160 2D stress | 0.545 ms | 0.620-0.641 ms | +14-18% |

At 4K 3D the low-contrast edge pass is 0.129 ms versus 0.058 ms for stock. The extra cost is nine color loads per pixel plus additional active blend pixels from the lower threshold. It is still faster than the original 2013 path at every measured resolution.

## 2D high-frequency stress test

The 2D high-frequency pattern is a separate worst-case benchmark, not the primary demo scene. Its numbers are shown here for comparison and should not be mixed with the 3D results. Debug layer off, medians of 300-frame runs, 3 repeats.

| Resolution | Optimized compute | Original 2013 |
| --- | ---: | ---: |
| 1920x1080 | 0.132 [0.132-0.132] ms | 0.194 [0.194-0.194] ms |
| 2560x1440 | 0.241 [0.230-0.241] ms | 0.361 [0.347-0.361] ms |
| 3840x2160 | 0.545 [0.545-0.545] ms | 0.858 [0.858-0.859] ms |

### 4K stress breakdown (optimized)

| Pass | Time |
| --- | ---: |
| Edge detection | 0.103 ms |
| Blend weights | 0.321 ms |
| Resolve | 0.120 ms |
| Total | 0.545 ms |

On the stress pattern the optimized path is faster at every resolution; the 4K total is about 36% lower than the original pixel-shader path because the compacted blend pass avoids running weight calculation for non-edge pixels. Edge detection itself is slightly slower than the original pixel path on this pattern, but the blend-pass savings dominate.

## Quality

Quality was measured by comparing mode 0 (optimized compute) against mode 1 (original 2013 SMAA) on deterministic frame-30 captures of the same scene. Captures are written before `Present`.

### 3D corridor scene

| Metric | Result |
| --- | ---: |
| Maximum per-channel difference | 1/255 |
| Mean absolute error | 0.00000015 |
| Pixels differing by more than 1/255 | 0.0% |
| Pixels differing by more than 10/255 | 0.0% |
| Pixels changed at all | 0.0087% |
| Flat-region MAE | 0 |
| Flat-region maximum difference | 0 |
| Flat-region mask | 7,068,479 pixels |

On the primary 3D scene the two paths agree to within 1/255 per channel, and flat regions are bit-identical.

### 2D stress pattern

| Metric | Result |
| --- | ---: |
| Maximum per-channel difference | 2/255 |
| Mean absolute error | 0.00001701 |
| Pixels differing by more than 1/255 | 0.0017% |
| Pixels differing by more than 10/255 | 0% |
| Pixels changed at all | 0.7453% |
| Flat-region MAE | 0 |

The slightly larger maximum on the stress pattern is consistent with floating-point and sampling-order differences between the compute and pixel-shader paths on dense, high-frequency edges; flat regions still match exactly.

### Low-contrast edge mode versus SSAA

Measured on deterministic 4K 3D captures against a 4x SSAA reference:

| Metric | Optimized stock | Low-contrast mode |
| --- | ---: | ---: |
| Mean absolute error | 0.00033556 | 0.00032792 |
| Pixels above 1/255 | 3.0351% | 3.0544% |
| Pixels above 10/255 | 0.2220% | 0.1965% |
| Flat-region MAE | 0.00002020 | 0.00002020 |
| Flat-region maximum | 6/255 | 6/255 |

The mode lowers average error and the visible large-error tail while leaving flat regions unchanged. It has no blur, smoothing, or sharpening pass. The tradeoff is slightly more pixels changed just above 1/255 and about 3% less high-frequency energy relative to SSAA, which reads as a small softening of edges. Results at 1080p and 1440p show the same direction but a smaller margin.

### Baseline capture reproducibility

A clean re-test reproduced the deterministic 4K captures exactly: mode 0 and mode 1 matched the stored baseline captures with MAE 0, max 0, and zero changed pixels. Fresh 2x SSAA references at 720p and 4K were also bit-identical to the cached references. Captures from older scene revisions were not used as baselines.

## Caveats

- **GPU clock variance:** consumer GPUs vary core and memory clocks between runs. Treat small differences as run-to-run variance, not a distribution guarantee. The original 2013 blend pass at 4K is the most sensitive: its median switches between roughly 0.223 ms and 0.276 ms depending on clock state.
- **4K range:** the 0.545 ms result applies only to the *2D stress test* optimized total in this baseline; the 4K original 2013 3D total ranged from 0.352 to 0.409 ms across debug states.
- **Scene dependence:** edge density and screen coverage drive the blend pass. The sparse-edge 3D corridor finds much less edge work than the 2D stress pattern, so edge-dense content can produce substantially higher absolute numbers.
- **Debug layer:** validation is enabled by default and adds CPU overhead. `--no-debug-layer` disables it; the clean GPU timings above use that flag. The debug-layer-on and -off GPU totals are within run-to-run variance.
- **Scope:** these numbers cover SMAA only. Scene rendering, presentation, and any host-side work are excluded by the timestamp range.
- **Vulkan:** no Vulkan benchmark was run. The Vulkan notes in [INTEGRATION.md](INTEGRATION.md) are integration guidance, not validated measurements.
