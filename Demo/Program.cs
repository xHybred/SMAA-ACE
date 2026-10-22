using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;
using Vortice.Mathematics;

internal static class Program
{
    private const int DefaultWidth = 1920;
    private const int DefaultHeight = 1080;
    private const string ClassName = "SMAADemoWindow";

    private static int _width = DefaultWidth;
    private static int _height = DefaultHeight;
    private static int _initialMode = 0;
    private static SceneMode _scene = SceneMode.Corridor3D;
    private static int _frameLimit = int.MaxValue;
    private static string? _autoScreenshotPath;
    private static int _captureFrame = 30;
    private static float? _thresholdOverride;
    private static int _initialDebug = 0;
    private static int _edgeMode = 0;
    private static int _lowContrastFlags = 0;
    private static bool _lowContrastEdge;
    private static float _thresholdLow = 0.030f;
    private static float _thresholdHigh = 0.095f;
    private static float _thresholdFloor = 0.035f;
    private static float _cmaaSynergy = -0.45f;
    private static bool _debugLayer = true;
    private static IntPtr _hwnd;
    private static bool _running = true;
    private static D3D12Renderer? _renderer;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Demo.log");
    private static WndProcDelegate? _wndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const int VK_SPACE = 0x20;
    private const int VK_1 = 0x31;
    private const int VK_2 = 0x32;
    private const int VK_3 = 0x33;
    private const int VK_4 = 0x34;
    private const int VK_F1 = 0x70;
    private const int VK_F2 = 0x71;
    private const int VK_F3 = 0x72;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private const int VK_F6 = 0x75;
    private const int VK_F12 = 0x7B;
    private const uint PM_REMOVE = 0x0001;
    private const int SW_SHOW = 5;
    private const int IDC_ARROW = 32512;
    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, IntPtr lpString);

    internal static bool SetWindowText(IntPtr hWnd, string text)
    {
        IntPtr native = Marshal.StringToHGlobalUni(text);
        try
        {
            return SetWindowTextW(hWnd, native);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
        Console.WriteLine(message);
    }

    private static byte[] Compile(string path, string entry, DxcShaderStage stage)
    {
        string source = File.ReadAllText(path);
        var options = new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_0 };
        var result = DxcCompiler.Compile(stage, source, entry, options, path, Array.Empty<DxcDefine>(), null);
        if (result.GetStatus() != Result.Ok)
        {
            string errors = result.GetErrors();
            result.Dispose();
            throw new Exception($"{Path.GetFileName(path)} {entry}:\n{errors}");
        }
        byte[] data = result.GetResult().AsBytes();
        result.Dispose();
        return data;
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMaxTrackSize.X = Math.Max(info.ptMaxTrackSize.X, _width + 32);
            info.ptMaxTrackSize.Y = Math.Max(info.ptMaxTrackSize.Y, _height + 64);
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_KEYDOWN:
                int key = wParam.ToInt32();
                if (key == VK_F1) SetMode(0);              // F1 optimized compute
                else if (key == VK_F2) SetMode(1);         // F2 original SMAA
                else if (key == VK_F3) SetMode(2);         // F3 off
                else if (key == VK_1) SetMode(0);          // numeric fallback
                else if (key == VK_2) SetMode(1);
                else if (key == VK_3) SetMode(2);
                else if (key == VK_F4) ToggleScene();      // F4 2D/3D
                else if (key == VK_F5) ToggleDebug(1);     // F5 edges
                else if (key == VK_F6) ToggleDebug(2);     // F6 blend weights
                else if (key == VK_F12) _renderer?.RequestScreenshot();
                return IntPtr.Zero;
            case WM_KEYUP:
                if (wParam.ToInt32() == VK_SPACE) TogglePause(); // Space pause on release
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                BeginPaint(hWnd, out PAINTSTRUCT ps);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                _running = false;
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void SetMode(int mode)
    {
        if (_renderer == null) return;
        _renderer.Mode = mode;
        _renderer.ResetDebug();
        Log($"Mode -> {_renderer.ModeName}");
        _renderer.UpdateTitle();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private static void ToggleDebug(int debug)
    {
        if (_renderer == null) return;
        _renderer.DebugMode = _renderer.DebugMode == debug ? 0 : debug;
        Log($"Debug -> {_renderer.DebugMode}");
        _renderer.UpdateTitle();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private static void ToggleScene()
    {
        if (_renderer == null) return;
        _scene = _scene == SceneMode.Corridor3D ? SceneMode.TestPattern2D : SceneMode.Corridor3D;
        _renderer.SetScene(_scene);
    }

    // SMAA's standard threshold. A 2x supersampled 3D reference showed 0.10
    // is closer to ground truth than 0.075/0.05, so keep the canonical value
    // and expose --threshold for experimentation instead of changing it here.
    private const float DefaultThreshold = 0.10f;

    private static void TogglePause()
    {
        _renderer?.TogglePause();
    }

    private static void Main(string[] args)
    {
        string? sceneWarning = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--width" && i + 1 < args.Length) int.TryParse(args[++i], out _width);
            else if (args[i] == "--height" && i + 1 < args.Length) int.TryParse(args[++i], out _height);
            else if (args[i] == "--mode" && i + 1 < args.Length) int.TryParse(args[++i], out _initialMode);
            else if (args[i] == "--aa" && i + 1 < args.Length)
            {
                string aa = args[++i].Trim().ToLowerInvariant();
                if (aa == "optimized" || aa == "opt" || aa == "smaa") _initialMode = 0;
                else if (aa == "original" || aa == "2013" || aa == "reference") _initialMode = 1;
                else if (aa == "off" || aa == "none") _initialMode = 2;
                else if (int.TryParse(aa, out int aaMode)) _initialMode = aaMode;
            }
            else if (args[i] == "--frames" && i + 1 < args.Length) int.TryParse(args[++i], out _frameLimit);
            else if (args[i] == "--screenshot" && i + 1 < args.Length) _autoScreenshotPath = args[++i];
            else if (args[i] == "--capture-frame" && i + 1 < args.Length) int.TryParse(args[++i], out _captureFrame);
            else if (args[i] == "--threshold" && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float threshold))
                    _thresholdOverride = Math.Clamp(threshold, 0.005f, 0.5f);
            }
            else if (args[i] == "--debug" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int debug))
                    _initialDebug = Math.Clamp(debug, 0, 2);
            }
            else if (args[i] == "--no-debug-layer") _debugLayer = false;
            else if (args[i] == "--edge-mode" && i + 1 < args.Length)
            {
                string value = args[++i].Trim().ToLowerInvariant();
                if (value == "max" || value == "max-channel") _edgeMode = 0;
                else if (value == "luma" || value == "luma-weighted") _edgeMode = 1;
                else if (int.TryParse(value, out int edgeMode) && (edgeMode == 0 || edgeMode == 1))
                    _edgeMode = edgeMode;
            }
            else if (args[i] == "--low-contrast-edge")
            {
                _lowContrastEdge = true;
            }
            else if (args[i] == "--adaptive-threshold") _lowContrastFlags |= 1;
            else if (args[i] == "--cmaa-lca") _lowContrastFlags |= 4;
            else if (args[i] == "--cmaa-synergy") _lowContrastFlags |= 8;
            else if (args[i] == "--low-contrast-flags" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int flags)) _lowContrastFlags = flags & 13;
            }
            else if (args[i] == "--threshold-low" && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    _thresholdLow = Math.Clamp(value, 0.0f, 0.5f);
            }
            else if (args[i] == "--threshold-high" && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    _thresholdHigh = Math.Clamp(value, 0.0f, 0.5f);
            }
            else if (args[i] == "--threshold-floor" && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    _thresholdFloor = Math.Clamp(value, 0.0f, 0.5f);
            }
            else if (args[i] == "--cmaa-synergy-value" && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    _cmaaSynergy = Math.Clamp(value, -1.0f, 1.0f);
            }
            else if (args[i] == "--scene" && i + 1 < args.Length)
            {
                string scene = args[++i].Trim().ToLowerInvariant();
                if (scene == "2d") _scene = SceneMode.TestPattern2D;
                else if (scene == "3d") _scene = SceneMode.Corridor3D;
                else sceneWarning = $"Unknown --scene value '{scene}', using 3d";
            }
        }
        if (_lowContrastEdge && _lowContrastFlags == 0) _lowContrastFlags = 13;
        _lowContrastEdge |= _lowContrastFlags != 0;
        _width = Math.Clamp(_width, 320, 7680);
        _height = Math.Clamp(_height, 200, 4320);

        File.WriteAllText(LogPath, string.Empty);
        Log($"SMAA demo starting at {_width}x{_height}");
        if (sceneWarning != null) Log(sceneWarning);

        _wndProc = WindowProc;
        IntPtr hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            lpszClassName = ClassName
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new Exception("RegisterClassEx failed");

        uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        var rc = new RECT { Left = 0, Top = 0, Right = _width, Bottom = _height };
        AdjustWindowRect(ref rc, style, false);
        int outerW = rc.Right - rc.Left;
        int outerH = rc.Bottom - rc.Top;

        _hwnd = CreateWindowEx(0, ClassName, "SMAA Demo", style, 80, 60, outerW, outerH,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Exception("CreateWindowEx failed");

        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
        UpdateWindow(_hwnd);

        GetClientRect(_hwnd, out RECT client);
        _width = Math.Max(16, client.Right - client.Left);
        _height = Math.Max(16, client.Bottom - client.Top);
        Log($"Client size {_width}x{_height}");

        try
        {
            _renderer = new D3D12Renderer(_hwnd, _width, _height, _scene, _debugLayer, _lowContrastEdge);
            _renderer.Mode = Math.Clamp(_initialMode, 0, 2);
            _renderer.Threshold = _thresholdOverride ?? DefaultThreshold;
            _renderer.EdgeMode = Math.Clamp(_edgeMode, 0, 1);
            _renderer.DebugMode = _initialDebug;
            _renderer.LowContrastFlags = _lowContrastFlags;
            _renderer.ThresholdLow = _thresholdLow;
            _renderer.ThresholdHigh = _thresholdHigh;
            _renderer.ThresholdFloor = _thresholdFloor;
            _renderer.CmaaSynergy = _cmaaSynergy;
            Log($"Edge mode -> {_renderer.EdgeModeName} ({_renderer.EdgeMode})");
            Log($"Low contrast edge mode -> {(_renderer.LowContrastFlags != 0 ? "on" : "off")} (flags {_renderer.LowContrastFlags})");
            _renderer.SetScene(_scene);
            _renderer.UpdateTitle();
            if (!string.IsNullOrEmpty(_autoScreenshotPath))
                _renderer.ScreenshotPath = _autoScreenshotPath;
            int captureAt = Math.Max(1, Math.Min(_captureFrame, _frameLimit - 1));
            int rendered = 0;
            var msg = new MSG();
            while (_running)
            {
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == 0x0012) _running = false;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                if (!_running) break;
                if (_autoScreenshotPath != null && rendered == captureAt - 1)
                    _renderer.RequestScreenshot();
                _renderer.Render();
                rendered++;
                if (rendered >= _frameLimit) break;
            }
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            File.AppendAllText(LogPath, ex.ToString());
        }
        finally
        {
            _renderer?.Dispose();
            _renderer = null;
        }
    }
}

internal enum SceneMode
{
    Corridor3D,
    TestPattern2D
}

internal sealed class D3D12Renderer : IDisposable
{
    private const int OptimizedSrvBase = 0;
    private const int RefEdgeSrvBase = 7;
    private const int RefResolveSrvBase = 12;
    private const int OptimizedUavBase = 17;
    private const int OffSrvBase = 20;

    private readonly IntPtr _hwnd;
    private readonly int _width;
    private readonly int _height;
    private readonly ID3D12Device2 _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _cmd;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _waitHandle = new(false);
    private ulong _fenceValue;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly ID3D12Resource[] _backBuffers = new ID3D12Resource[2];
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly uint _rtvSize;
    private readonly uint _srvSize;
    private readonly ID3D12RootSignature _optRoot;
    private readonly ID3D12RootSignature _refRoot;
    private readonly bool _lowContrastEdge;
    private readonly ID3D12PipelineState[] _psoEdgeCS;
    private readonly ID3D12PipelineState _psoBlendCS;
    private readonly ID3D12PipelineState[] _psoResolve;
    private readonly ID3D12PipelineState _psoRefEdge;
    private readonly ID3D12PipelineState _psoRefBlend;
    private readonly ID3D12PipelineState _psoRefResolve;
    private readonly ID3D12PipelineState _psoOff;
    private readonly ID3D12PipelineState _psoScene;
    private readonly ID3D12Resource _colorTex;
    private readonly ID3D12Resource _edgesTex;
    private readonly ID3D12Resource _blendTex;
    private readonly ID3D12Resource _edgeMaskTex;
    private readonly ID3D12Resource _areaOpt;
    private readonly ID3D12Resource _searchOpt;
    private readonly ID3D12Resource _areaRef;
    private readonly ID3D12Resource _searchRef;
    private readonly ID3D12Resource _optCb;
    private readonly ID3D12Resource _refCb;
    private readonly ID3D12Resource _sceneCb;
    private readonly ID3D12QueryHeap _queryHeap;
    private readonly ID3D12Resource _timestampReadback;
    private readonly ID3D12Resource _screenshotReadback;
    private readonly int _screenshotRowPitch;
    private readonly byte[] _pattern;
    private string _screenshotPath = Path.Combine(AppContext.BaseDirectory, "screenshot.bmp");
    private readonly ulong _timestampFreq;
    private readonly List<ID3D12Resource> _uploads = new();
    private readonly List<double> _edgeMs = new();
    private readonly List<double> _blendMs = new();
    private readonly List<double> _resolveMs = new();
    private readonly List<double> _totalMs = new();
    private readonly Stopwatch _cpuClock = Stopwatch.StartNew();
    private readonly Stopwatch _sceneClock = Stopwatch.StartNew();
    private double _lastCpuMs;
    private int _frame;
    private int _sceneFrame;
    private bool _screenshot;
    private bool _paused;
    private SceneMode _scene = SceneMode.Corridor3D;
    private bool _disposed;

    public int Mode { get; set; }
    public int DebugMode { get; set; }
    public float Threshold { get; set; } = 0.10f;
    public int EdgeMode { get; set; }
    public int LowContrastFlags { get; set; }
    public float ThresholdLow { get; set; } = 0.030f;
    public float ThresholdHigh { get; set; } = 0.095f;
    public float ThresholdFloor { get; set; } = 0.035f;
    public float CmaaSynergy { get; set; } = -0.45f;

    public string SceneName => _scene == SceneMode.TestPattern2D ? "2DDemo" : "3DDemo";

    public string ModeName => Mode switch
{
    0 => "Optimized compute",
    1 => "Original 2013 SMAA",
    2 => "Off",
    _ => "Off"
};

    public string EdgeModeName => EdgeMode switch
    {
        1 => "luma",
        _ => "max"
    };

    public string ScreenshotPath
    {
        get => _screenshotPath;
        set => _screenshotPath = value;
    }

    private static void Log(string message) => Program.Log(message);

    private bool _infoDumped;

    private void CheckDevice(string stage)
    {
        SharpGen.Runtime.Result reason = _device.DeviceRemovedReason;
        if (reason.Failure)
        {
            Log($"Device removed at {stage}: {reason}");
            if (!_infoDumped)
            {
                _infoDumped = true;
                DumpInfoQueue();
            }
        }
    }

    private void DumpInfoQueue()
    {
        try
        {
            using var info = _device.QueryInterface<Vortice.Direct3D12.Debug.ID3D12InfoQueue>();
            ulong count = info.NumStoredMessages;
            Log($"D3D12 info queue messages: {count}");
            for (ulong i = 0; i < count; i++)
            {
                var message = info.GetMessage(i);
                Log($"D3D12 [{message.Severity}] {message.Description}");
            }
        }
        catch (Exception ex)
        {
            Log("InfoQueue unavailable: " + ex.Message);
        }
    }

    private static void SetWindowText(IntPtr hwnd, string text) => Program.SetWindowText(hwnd, text);

    public D3D12Renderer(IntPtr hwnd, int width, int height, SceneMode scene = SceneMode.Corridor3D,
        bool enableDebugLayer = true, bool lowContrastEdge = false)
    {
        _hwnd = hwnd;
        _width = width;
        _height = height;
        _scene = scene;
        _lowContrastEdge = lowContrastEdge;

        if (!enableDebugLayer)
        {
            Log("D3D12 debug layer disabled by --no-debug-layer");
        }
        else
        {
            try
            {
                var debugResult = D3D12.D3D12GetDebugInterface(out Vortice.Direct3D12.Debug.ID3D12Debug? debug);
                if (debugResult.Success && debug != null)
                {
                    using (debug)
                    {
                        debug.EnableDebugLayer();
                        Log("D3D12 debug layer enabled");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("D3D12 debug layer unavailable: " + ex.Message);
            }
        }

        using IDXGIFactory4 factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        factory.EnumAdapters1(0, out IDXGIAdapter1? adapter).CheckError();
        using IDXGIAdapter1 adapterLifetime = adapter!;
        D3D12.D3D12CreateDevice(adapter!, FeatureLevel.Level_11_0, out ID3D12Device2? device).CheckError();
        _device = device!;
        CheckDevice("device creation");

        _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct, CommandQueuePriority.Normal, CommandQueueFlags.None, 0));
        _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _cmd = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, null);
        _cmd.Close();
        _fence = _device.CreateFence(0, FenceFlags.None);
        _queue.GetTimestampFrequency(out _timestampFreq);

        var swapDesc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };
        using (IDXGISwapChain1 sc1 = factory.CreateSwapChainForHwnd(_queue, _hwnd, swapDesc))
            _swapChain = sc1.QueryInterface<IDXGISwapChain3>();

        for (int i = 0; i < 2; i++)
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);

        var rtvHeapDesc = new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 7, DescriptorHeapFlags.None, 0);
        _rtvHeap = _device.CreateDescriptorHeap(rtvHeapDesc);
        _rtvSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        var srvHeapDesc = new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 32, DescriptorHeapFlags.ShaderVisible, 0);
        _srvHeap = _device.CreateDescriptorHeap(srvHeapDesc);
        _srvSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        for (int i = 0; i < 2; i++)
            CreateRtv(_backBuffers[i], Format.R8G8B8A8_UNorm, RtvCpu(i * 2));
        CheckDevice("backbuffer RTVs");

        _colorTex = CreateTexture(Format.R8G8B8A8_Typeless, _width, _height, ResourceFlags.AllowRenderTarget, ResourceStates.CopyDest, "TestPattern");
        _edgesTex = CreateTexture(Format.R8G8_UNorm, _width, _height, ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowRenderTarget, ResourceStates.AllShaderResource, "Edges");
        _blendTex = CreateTexture(Format.R8G8B8A8_UNorm, _width, _height, ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowRenderTarget, ResourceStates.AllShaderResource, "Blend");
        _edgeMaskTex = CreateTexture(Format.R32_SInt, MaskWidth, MaskHeight, ResourceFlags.AllowUnorderedAccess, ResourceStates.AllShaderResource, "EdgeMask");

        _areaOpt = CreateTexture(Format.R8G8_UNorm, 160, 560, ResourceFlags.None, ResourceStates.CopyDest, "AreaOpt");
        _searchOpt = CreateTexture(Format.R8_UNorm, 64, 16, ResourceFlags.None, ResourceStates.CopyDest, "SearchOpt");
        _areaRef = CreateTexture(Format.R8G8_UNorm, 160, 560, ResourceFlags.None, ResourceStates.CopyDest, "AreaRef");
        _searchRef = CreateTexture(Format.R8_UNorm, 64, 16, ResourceFlags.None, ResourceStates.CopyDest, "SearchRef");

        CreateRtv(_edgesTex, Format.R8G8_UNorm, RtvCpu(4));
        CreateRtv(_blendTex, Format.R8G8B8A8_UNorm, RtvCpu(5));
        CreateRtv(_colorTex, Format.R8G8B8A8_UNorm, RtvCpu(6));
        CheckDevice("texture RTVs");

        CreateSrv(_colorTex, Format.R8G8B8A8_UNorm, SrvCpu(OptimizedSrvBase + 0), "ColorGamma");
        CheckDevice("opt srv 0 color gamma");
        CreateSrv(_colorTex, Format.R8G8B8A8_UNorm_SRgb, SrvCpu(OptimizedSrvBase + 1), "ColorLinear");
        CheckDevice("opt srv 1 color linear");
        CreateSrv(_edgesTex, Format.R8G8_UNorm, SrvCpu(OptimizedSrvBase + 2), "EdgesSrvOpt");
        CheckDevice("opt srv 2 edges");
        CreateSrv(_blendTex, Format.R8G8B8A8_UNorm, SrvCpu(OptimizedSrvBase + 3), "BlendSrvOpt");
        CheckDevice("opt srv 3 blend");
        CreateSrv(_areaOpt, Format.R8G8_UNorm, SrvCpu(OptimizedSrvBase + 4), "AreaOptSrv");
        CheckDevice("opt srv 4 area");
        CreateSrv(_searchOpt, Format.R8_UNorm, SrvCpu(OptimizedSrvBase + 5), "SearchOptSrv");
        CheckDevice("opt srv 5 search");
        CreateSrv(_edgeMaskTex, Format.R32_SInt, SrvCpu(OptimizedSrvBase + 6), "MaskSrv");
        CheckDevice("opt srv 6 mask");

        CreateSrv(_colorTex, Format.R8G8B8A8_UNorm, SrvCpu(RefEdgeSrvBase + 0), "RefColorGamma");
        CreateSrv(_edgesTex, Format.R8G8_UNorm, SrvCpu(RefEdgeSrvBase + 1), "RefEdges1");
        CreateSrv(_blendTex, Format.R8G8B8A8_UNorm, SrvCpu(RefEdgeSrvBase + 2), "RefBlend1");
        CreateSrv(_areaRef, Format.R8G8_UNorm, SrvCpu(RefEdgeSrvBase + 3), "RefArea1");
        CreateSrv(_searchRef, Format.R8_UNorm, SrvCpu(RefEdgeSrvBase + 4), "RefSearch1");

        CreateSrv(_colorTex, Format.R8G8B8A8_UNorm_SRgb, SrvCpu(RefResolveSrvBase + 0), "RefColorLinear");
        CreateSrv(_edgesTex, Format.R8G8_UNorm, SrvCpu(RefResolveSrvBase + 1), "RefEdges2");
        CreateSrv(_blendTex, Format.R8G8B8A8_UNorm, SrvCpu(RefResolveSrvBase + 2), "RefBlend2");
        CreateSrv(_areaRef, Format.R8G8_UNorm, SrvCpu(RefResolveSrvBase + 3), "RefArea2");
        CreateSrv(_searchRef, Format.R8_UNorm, SrvCpu(RefResolveSrvBase + 4), "RefSearch2");

        CreateUav(_edgesTex, Format.R8G8_UNorm, SrvCpu(OptimizedUavBase + 0), "EdgesUav");
        CreateUav(_blendTex, Format.R8G8B8A8_UNorm, SrvCpu(OptimizedUavBase + 1), "BlendUav");
        CreateUav(_edgeMaskTex, Format.R32_SInt, SrvCpu(OptimizedUavBase + 2), "MaskUav");

        CreateSrv(_colorTex, Format.R8G8B8A8_UNorm, SrvCpu(OffSrvBase + 0), "OffColor");
        CreateSrv(_edgesTex, Format.R8G8_UNorm, SrvCpu(OffSrvBase + 1), "OffEdges");
        CreateSrv(_blendTex, Format.R8G8B8A8_UNorm, SrvCpu(OffSrvBase + 2), "OffBlend");
        CreateSrv(_areaRef, Format.R8G8_UNorm, SrvCpu(OffSrvBase + 3), "OffArea");
        CreateSrv(_searchRef, Format.R8_UNorm, SrvCpu(OffSrvBase + 4), "OffSearch");
        CheckDevice("resource views");

        string root = AppContext.BaseDirectory;
        byte[] optEdgeCSMax = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAEdgeDetectionCS", DxcShaderStage.Compute,
            new DxcDefine { Name = "SMAA_EDGE_MODE", Value = "0" });
        byte[] optEdgeCSLuma = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAEdgeDetectionCS", DxcShaderStage.Compute,
            new DxcDefine { Name = "SMAA_EDGE_MODE", Value = "1" });
        byte[]? optEdgeCSLowContrastMax = null;
        byte[]? optEdgeCSLowContrastLuma = null;
        if (_lowContrastEdge)
        {
            optEdgeCSLowContrastMax = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAEdgeDetectionLowContrastCS", DxcShaderStage.Compute,
                new DxcDefine { Name = "SMAA_EDGE_MODE", Value = "0" });
            optEdgeCSLowContrastLuma = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAEdgeDetectionLowContrastCS", DxcShaderStage.Compute,
                new DxcDefine { Name = "SMAA_EDGE_MODE", Value = "1" });
        }
        byte[] optBlendCS = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAABlendWeightsCS", DxcShaderStage.Compute);
        byte[] optVS = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAFullscreenVS", DxcShaderStage.Vertex);
        byte[] optResolveNormal = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAResolvePS", DxcShaderStage.Pixel,
            new DxcDefine { Name = "SMAA_DEBUG_MODE", Value = "0" });
        byte[] optResolveEdges = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAResolvePS", DxcShaderStage.Pixel,
            new DxcDefine { Name = "SMAA_DEBUG_MODE", Value = "1" });
        byte[] optResolveBlend = ProgramCompile(Path.Combine(root, "SMAA.hlsl"), "SMAAResolvePS", DxcShaderStage.Pixel,
            new DxcDefine { Name = "SMAA_DEBUG_MODE", Value = "2" });
        byte[] refEdgeVS, refEdgePS, refBlendVS, refBlendPS, refResolveVS, refResolvePS;
        string oldCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.Combine(root, "Reference");
            refEdgeVS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefEdgeVS", DxcShaderStage.Vertex);
            refEdgePS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefEdgePS", DxcShaderStage.Pixel);
            refBlendVS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefBlendVS", DxcShaderStage.Vertex);
            refBlendPS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefBlendPS", DxcShaderStage.Pixel);
            refResolveVS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefResolveVS", DxcShaderStage.Vertex);
            refResolvePS = ProgramCompile(Path.Combine(root, "Reference", "SMAAReference.hlsl"), "RefResolvePS", DxcShaderStage.Pixel);
        }
        finally
        {
            Environment.CurrentDirectory = oldCwd;
        }
        byte[] passVS = ProgramCompile(Path.Combine(AppContext.BaseDirectory, "DemoShaders.hlsl"), "PassthroughVS", DxcShaderStage.Vertex);
        byte[] passPS = ProgramCompile(Path.Combine(AppContext.BaseDirectory, "DemoShaders.hlsl"), "PassthroughPS", DxcShaderStage.Pixel);
        byte[] sceneVS = ProgramCompile(Path.Combine(AppContext.BaseDirectory, "DemoScene.hlsl"), "SceneFullscreenVS", DxcShaderStage.Vertex);
        byte[] scenePS = ProgramCompile(Path.Combine(AppContext.BaseDirectory, "DemoScene.hlsl"), "ScenePS", DxcShaderStage.Pixel);
        CheckDevice("shader compilation");

        var optParams = new RootParameter[]
        {
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new[] { new DescriptorRange(DescriptorRangeType.ShaderResourceView, 7, 0, 0, 0) }), ShaderVisibility.All),
            new(new RootDescriptorTable(new[] { new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 3, 0, 0, 0) }), ShaderVisibility.All)
        };
        var optSamplers = new[]
        {
            new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.All, 0, 0),
            new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.All, 1, 0),
            new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.All, 2, 0)
        };
        var optRootDesc = new RootSignatureDescription(RootSignatureFlags.None, optParams, optSamplers);
        Log($"DeviceRemovedReason before optimized root signature: {_device.DeviceRemovedReason}");
        try
        {
            _optRoot = _device.CreateRootSignature(0, optRootDesc, RootSignatureVersion.Version10);
        }
        catch (Exception ex)
        {
            Log($"Optimized root signature failed. DeviceRemovedReason: {_device.DeviceRemovedReason}. {ex.Message}");
            throw;
        }

        var refParams = new RootParameter[]
        {
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new[] { new DescriptorRange(DescriptorRangeType.ShaderResourceView, 5, 0, 0, 0) }), ShaderVisibility.All)
        };
        var refSamplers = new[]
        {
            new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.All, 0, 0),
            new StaticSamplerDescription(SamplerDescription.PointClamp, ShaderVisibility.All, 1, 0)
        };
        var refRootDesc = new RootSignatureDescription(RootSignatureFlags.None, refParams, refSamplers);
        _refRoot = _device.CreateRootSignature(0, refRootDesc, RootSignatureVersion.Version10);

        var edgePsos = new List<ID3D12PipelineState>
        {
            _device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _optRoot,
                ComputeShader = new ReadOnlyMemory<byte>(optEdgeCSMax),
                NodeMask = 0
            }),
            _device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _optRoot,
                ComputeShader = new ReadOnlyMemory<byte>(optEdgeCSLuma),
                NodeMask = 0
            })
        };
        if (_lowContrastEdge)
        {
            edgePsos.Add(_device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _optRoot,
                ComputeShader = new ReadOnlyMemory<byte>(optEdgeCSLowContrastMax!),
                NodeMask = 0
            }));
            edgePsos.Add(_device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = _optRoot,
                ComputeShader = new ReadOnlyMemory<byte>(optEdgeCSLowContrastLuma!),
                NodeMask = 0
            }));
        }
        _psoEdgeCS = edgePsos.ToArray();
        _psoBlendCS = _device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = _optRoot,
            ComputeShader = new ReadOnlyMemory<byte>(optBlendCS),
            NodeMask = 0
        });
        _psoResolve = new[]
        {
            CreateGraphicsPso(_optRoot, optVS, optResolveNormal, Format.R8G8B8A8_UNorm),
            CreateGraphicsPso(_optRoot, optVS, optResolveEdges, Format.R8G8B8A8_UNorm),
            CreateGraphicsPso(_optRoot, optVS, optResolveBlend, Format.R8G8B8A8_UNorm)
        };
        _psoRefEdge = CreateGraphicsPso(_refRoot, refEdgeVS, refEdgePS, Format.R8G8_UNorm);
        _psoRefBlend = CreateGraphicsPso(_refRoot, refBlendVS, refBlendPS, Format.R8G8B8A8_UNorm);
        _psoRefResolve = CreateGraphicsPso(_refRoot, refResolveVS, refResolvePS, Format.R8G8B8A8_UNorm);
        _psoOff = CreateGraphicsPso(_refRoot, passVS, passPS, Format.R8G8B8A8_UNorm);
        _psoScene = CreateGraphicsPso(_optRoot, sceneVS, scenePS, Format.R8G8B8A8_UNorm);

        _optCb = CreateUploadBuffer(256, "OptCb");
        _refCb = CreateUploadBuffer(256, "RefCb");
        _sceneCb = CreateUploadBuffer(256, "SceneCb");
        var refMetrics = new Vector4(1.0f / _width, 1.0f / _height, _width, _height);
        WriteStruct(_refCb, refMetrics);

        _queryHeap = _device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, 8, 0));
        _timestampReadback = _device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer(4 * 8), ResourceStates.CopyDest);
        _timestampReadback.Name = "Timestamps";

        _screenshotRowPitch = (_width * 4 + 255) & ~255;
        _screenshotReadback = _device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)_screenshotRowPitch * (ulong)_height), ResourceStates.CopyDest);
        _screenshotReadback.Name = "ScreenshotReadback";

        _pattern = TestPattern.Create(_width, _height);
        UploadAndWait(_pattern, _width, _height, 4, Format.R8G8B8A8_UNorm, _colorTex,
            ResourceStates.AllShaderResource);
        byte[] areaRef = File.ReadAllBytes(Path.Combine(root, "Reference", "AreaTex.rg8"));
        byte[] searchRef = File.ReadAllBytes(Path.Combine(root, "Reference", "SearchTex.r8"));
        UploadAndWait(areaRef, 160, 560, 2, Format.R8G8_UNorm, _areaOpt, ResourceStates.AllShaderResource);
        UploadAndWait(searchRef, 64, 16, 1, Format.R8_UNorm, _searchOpt, ResourceStates.AllShaderResource);
        UploadAndWait(areaRef, 160, 560, 2, Format.R8G8_UNorm, _areaRef, ResourceStates.AllShaderResource);
        UploadAndWait(searchRef, 64, 16, 1, Format.R8_UNorm, _searchRef, ResourceStates.AllShaderResource);

        Log($"Device ready: {_width}x{_height}, timestamps {_timestampFreq} Hz");
        Log($"Scene -> {SceneName}");
        SetWindowTitle();
    }

    private int MaskWidth => ((_width + 31) / 32) * 32;
    private int MaskHeight => (_height + 15) / 16;

    private static byte[] ProgramCompile(string path, string entry, DxcShaderStage stage, params DxcDefine[] extraDefines)
    {
        // Kept as a separate method so compile errors include the source path.
        string source = File.ReadAllText(path);
        var options = new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_0 };
        var defines = new List<DxcDefine> { new DxcDefine { Name = "SMAA_OUTPUT_SRGB", Value = "1" } };
        defines.AddRange(extraDefines);
        var result = DxcCompiler.Compile(stage, source, entry, options, path, defines.ToArray(), null);
        if (result.GetStatus() != Result.Ok)
        {
            string errors = result.GetErrors();
            result.Dispose();
            throw new Exception($"{path} {entry}:\n{errors}");
        }
        byte[] data = result.GetResult().AsBytes();
        result.Dispose();
        return data;
    }

    private ID3D12PipelineState CreateGraphicsPso(ID3D12RootSignature root, byte[] vs, byte[] ps, params Format[] formats)
    {
        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = new ReadOnlyMemory<byte>(vs),
            PixelShader = new ReadOnlyMemory<byte>(ps),
            BlendState = BlendDescription.Opaque,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = formats,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
            NodeMask = 0
        };
        return _device.CreateGraphicsPipelineState(desc);
    }

    private ID3D12Resource CreateTexture(Format format, int width, int height, ResourceFlags flags,
        ResourceStates initial, string name)
    {
        var desc = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1, 1, 0,
            flags, TextureLayout.Unknown);
        var res = _device.CreateCommittedResource(HeapType.Default, HeapFlags.None, desc, initial);
        res.Name = name;
        return res;
    }

    private ID3D12Resource CreateUploadBuffer(ulong size, string name)
    {
        var res = _device.CreateCommittedResource(HeapType.Upload, HeapFlags.None,
            ResourceDescription.Buffer(size), ResourceStates.GenericRead);
        res.Name = name;
        return res;
    }

    private CpuDescriptorHandle SrvCpu(int index) => _srvHeap.GetCPUDescriptorHandleForHeapStart() + (int)(index * _srvSize);
    private GpuDescriptorHandle SrvGpu(int index) => _srvHeap.GetGPUDescriptorHandleForHeapStart() + (int)(index * _srvSize);
    private CpuDescriptorHandle RtvCpu(int index) => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)(index * _rtvSize);

    private void CreateSrv(ID3D12Resource resource, Format format, CpuDescriptorHandle handle, string name)
    {
        var desc = new ShaderResourceViewDescription
        {
            Format = format,
            Shader4ComponentMapping = 0x1688,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                PlaneSlice = 0,
                ResourceMinLODClamp = 0
            }
        };
        _device.CreateShaderResourceView(resource, desc, handle);
    }

    private void CreateRtv(ID3D12Resource resource, Format format, CpuDescriptorHandle handle)
    {
        var desc = new RenderTargetViewDescription
        {
            Format = format,
            ViewDimension = RenderTargetViewDimension.Texture2D,
            Texture2D = new Texture2DRenderTargetView { MipSlice = 0, PlaneSlice = 0 }
        };
        _device.CreateRenderTargetView(resource, desc, handle);
    }

    private void CreateUav(ID3D12Resource resource, Format format, CpuDescriptorHandle handle, string name)
    {
        var desc = new UnorderedAccessViewDescription
        {
            Format = format,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0, PlaneSlice = 0 }
        };
        _device.CreateUnorderedAccessView(resource, null, desc, handle);
    }

    private unsafe void WriteStruct<T>(ID3D12Resource resource, T value) where T : struct
    {
        void* ptr;
        resource.Map(0, null, &ptr);
        Marshal.StructureToPtr(value, (IntPtr)ptr, false);
        resource.Unmap(0, null);
    }

    private void UploadAndWait(byte[] data, int width, int height, int bytesPerPixel, Format format,
        ID3D12Resource destination, ResourceStates finalState,
        ResourceStates initialState = ResourceStates.CopyDest)
    {
        int rowPitch = (width * bytesPerPixel + 255) & ~255;
        ulong size = (ulong)rowPitch * (ulong)height;
        ID3D12Resource upload = CreateUploadBuffer(size, "Upload");
        unsafe
        {
            void* ptr;
            upload.Map(0, null, &ptr);
            for (int y = 0; y < height; y++)
                Marshal.Copy(data, y * width * bytesPerPixel, IntPtr.Add((IntPtr)ptr, y * rowPitch), width * bytesPerPixel);
            upload.Unmap(0, null);
        }

        _allocator.Reset();
        _cmd.Reset(_allocator, null);
        if (initialState != ResourceStates.CopyDest)
            _cmd.ResourceBarrierTransition(destination, initialState, ResourceStates.CopyDest);
        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint
            {
                Format = format,
                Width = (uint)width,
                Height = (uint)height,
                Depth = 1,
                RowPitch = (uint)rowPitch
            }
        };
        _cmd.CopyTextureRegion(new TextureCopyLocation(destination, 0), 0, 0, 0,
            new TextureCopyLocation(upload, footprint), null);
        _cmd.ResourceBarrierTransition(destination, ResourceStates.CopyDest, finalState);
        _cmd.Close();
        _queue.ExecuteCommandLists(new ID3D12CommandList[] { _cmd });
        WaitForGpu();
        upload.Dispose();
    }

    private void WaitForGpu()
    {
        _fenceValue++;
        _queue.Signal(_fence, _fenceValue);
        _fence.SetEventOnCompletion(_fenceValue, _waitHandle);
        _waitHandle.WaitOne();
    }

    public void ResetDebug()
    {
        DebugMode = 0;
    }

    public void SetScene(SceneMode scene)
    {
        if (_scene == scene)
        {
            UpdateTitle();
            return;
        }
        _scene = scene;
        if (scene == SceneMode.TestPattern2D)
        {
            // The 3D scene overwrites _colorTex every frame, so restore the cached pattern.
            UploadAndWait(_pattern, _width, _height, 4, Format.R8G8B8A8_UNorm, _colorTex,
                ResourceStates.AllShaderResource, ResourceStates.AllShaderResource);
        }
        Log($"Scene -> {SceneName}");
        UpdateTitle();
    }

    public void TogglePause()
    {
        _paused = !_paused;
        Log($"Paused -> {_paused}");
        UpdateTitle();
    }

    public void UpdateTitle()
    {
        SetWindowTitle();
    }

    public void RequestScreenshot()
    {
        _screenshot = true;
        Log("Screenshot requested");
    }

    private void RecordScreenshot(ID3D12Resource backBuffer)
    {
        _cmd.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.CopySource);
        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint
            {
                Format = Format.R8G8B8A8_UNorm,
                Width = (uint)_width,
                Height = (uint)_height,
                Depth = 1,
                RowPitch = (uint)_screenshotRowPitch
            }
        };
        _cmd.CopyTextureRegion(new TextureCopyLocation(_screenshotReadback, footprint), 0, 0, 0,
            new TextureCopyLocation(backBuffer, 0), null);
        _cmd.ResourceBarrierTransition(backBuffer, ResourceStates.CopySource, ResourceStates.Present);
    }

    private unsafe void WriteScreenshot()
    {
        void* ptr;
        _screenshotReadback.Map(0, null, &ptr);
        try
        {
            int rowSize = _width * 4;
            int imageSize = rowSize * _height;
            byte[] bmp = new byte[54 + imageSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmp.AsSpan(2), 54 + imageSize);
            BitConverter.TryWriteBytes(bmp.AsSpan(10), 54);
            BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
            BitConverter.TryWriteBytes(bmp.AsSpan(18), _width);
            BitConverter.TryWriteBytes(bmp.AsSpan(22), _height);
            BitConverter.TryWriteBytes(bmp.AsSpan(26), (short)1);
            BitConverter.TryWriteBytes(bmp.AsSpan(28), (short)32);
            BitConverter.TryWriteBytes(bmp.AsSpan(34), imageSize);
            BitConverter.TryWriteBytes(bmp.AsSpan(38), 2835);
            BitConverter.TryWriteBytes(bmp.AsSpan(42), 2835);

            byte* source = (byte*)ptr;
            for (int y = 0; y < _height; y++)
            {
                int sourceY = _height - 1 - y;
                byte* sourceRow = source + sourceY * _screenshotRowPitch;
                int dest = 54 + y * rowSize;
                for (int x = 0; x < _width; x++)
                {
                    int s = x * 4;
                    bmp[dest + x * 4 + 0] = sourceRow[s + 2];
                    bmp[dest + x * 4 + 1] = sourceRow[s + 1];
                    bmp[dest + x * 4 + 2] = sourceRow[s + 0];
                    bmp[dest + x * 4 + 3] = 255;
                }
            }

            string? directory = Path.GetDirectoryName(_screenshotPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(_screenshotPath, bmp);
            Log($"Screenshot saved: {_screenshotPath}");
            _screenshot = false;
        }
        finally
        {
            _screenshotReadback.Unmap(0, null);
        }
    }

    public void Render()
    {
        _frame++;
        if (!_paused) _sceneFrame++;
        var frameWatch = Stopwatch.StartNew();
        _allocator.Reset();
        _cmd.Reset(_allocator, null);
        _cmd.SetDescriptorHeaps(_srvHeap);

        int backIndex = (int)_swapChain.CurrentBackBufferIndex;
        ID3D12Resource backBuffer = _backBuffers[backIndex];

        // In 3D mode, draw the raymarched corridor into _colorTex before timing/mode passes.
        // In 2D mode _colorTex already holds the uploaded test pattern.
        if (_scene == SceneMode.Corridor3D)
            RenderScene();

        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 0);

        if (Mode == 0) RenderOptimized(backIndex);
        else if (Mode == 1) RenderReference(backIndex);
        else RenderOff(backIndex);

        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 3);
        _cmd.ResolveQueryData(_queryHeap, QueryType.Timestamp, 0, 4, _timestampReadback, 0);
        if (_screenshot)
            RecordScreenshot(backBuffer);
        try
        {
            _cmd.Close();
        }
        catch
        {
            DumpInfoQueue();
            throw;
        }
        _queue.ExecuteCommandLists(new ID3D12CommandList[] { _cmd });
        WaitForGpu();
        if (_screenshot)
            WriteScreenshot();
        ReadTimings();

        _swapChain.Present(0, PresentFlags.None);
        _lastCpuMs = frameWatch.Elapsed.TotalMilliseconds;

        if (_frame % 30 == 0)
        {
            SetWindowTitle();
            Log($"{ModeName} | edge {Median(_edgeMs):F3} ms | blend {Median(_blendMs):F3} ms | resolve {Median(_resolveMs):F3} ms | total {Median(_totalMs):F3} ms | cpu {_lastCpuMs:F3} ms");
        }
    }

    private void RenderScene()
    {
        UpdateSceneConstants();

        _cmd.ResourceBarrierTransition(_colorTex, ResourceStates.AllShaderResource, ResourceStates.RenderTarget);
        _cmd.OMSetRenderTargets(RtvCpu(6));
        _cmd.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _cmd.RSSetScissorRect(_width, _height);
        _cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmd.SetGraphicsRootSignature(_optRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _sceneCb.GPUVirtualAddress);
        _cmd.SetPipelineState(_psoScene);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.AllShaderResource);
    }

    private void UpdateSceneConstants()
    {
        float time = _sceneFrame / 60.0f;
        float camZ = 5.2f - (time * 0.55f) % 11.0f;
        float camX = MathF.Sin(time * 0.70f) * 0.55f;
        float camY = 1.35f + MathF.Sin(time * 1.10f) * 0.10f;

        var position = new Vector3(camX, camY, camZ);
        var target = new Vector3(
            MathF.Sin(time * 0.35f) * 0.35f,
            1.15f + MathF.Sin(time * 0.60f) * 0.12f,
            camZ - 9.0f);

        Vector3 forward = Vector3.Normalize(target - position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward);
        float tanHalfFov = MathF.Tan(MathF.PI / 6.0f);

        var p = new SceneParams
        {
            TimeRes = new Vector4(time, _sceneFrame, _width, _height),
            CamPos = new Vector4(position, 0.0f),
            CamRight = new Vector4(right, 0.0f),
            CamUp = new Vector4(up, 0.0f),
            CamForward = new Vector4(forward, 0.0f),
            CamParams = new Vector4(tanHalfFov, (float)_width / _height, 0.0f, 0.0f)
        };
        WriteStruct(_sceneCb, p);
    }

    private void RenderOptimized(int backIndex)
    {
    _cmd.ResourceBarrierTransition(_edgesTex, ResourceStates.AllShaderResource, ResourceStates.RenderTarget);
    _cmd.ResourceBarrierTransition(_blendTex, ResourceStates.AllShaderResource, ResourceStates.RenderTarget);
    _cmd.ClearRenderTargetView(RtvCpu(4), new Color4(0.0f, 0.0f, 0.0f, 0.0f));
    _cmd.ClearRenderTargetView(RtvCpu(5), new Color4(0.0f, 0.0f, 0.0f, 0.0f));
    _cmd.ResourceBarrierTransition(_edgesTex, ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
    _cmd.ResourceBarrierTransition(_blendTex, ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
    _cmd.ResourceBarrierTransition(_edgeMaskTex, ResourceStates.AllShaderResource, ResourceStates.UnorderedAccess);

        UpdateOptConstants();
        _cmd.SetComputeRootSignature(_optRoot);
        _cmd.SetComputeRootConstantBufferView(0, _optCb.GPUVirtualAddress);
        _cmd.SetComputeRootDescriptorTable(1, SrvGpu(OptimizedSrvBase));
        _cmd.SetComputeRootDescriptorTable(2, SrvGpu(OptimizedUavBase));

        int edgePso = Math.Clamp(EdgeMode, 0, 1);
        if (_lowContrastEdge) edgePso += 2;
        _cmd.SetPipelineState(_psoEdgeCS[edgePso]);
        _cmd.Dispatch((uint)((_width + 31) / 32), (uint)((_height + 15) / 16), 1);
        _cmd.ResourceBarrierUnorderedAccessView(_edgesTex);
        _cmd.ResourceBarrierUnorderedAccessView(_blendTex);
        _cmd.ResourceBarrierUnorderedAccessView(_edgeMaskTex);
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 1);

        _cmd.ResourceBarrierTransition(_edgesTex, ResourceStates.UnorderedAccess, ResourceStates.AllShaderResource);
        _cmd.ResourceBarrierTransition(_edgeMaskTex, ResourceStates.UnorderedAccess, ResourceStates.AllShaderResource);

        _cmd.SetPipelineState(_psoBlendCS);
        _cmd.Dispatch((uint)((_width + 31) / 32), (uint)((_height + 15) / 16), 1);
        _cmd.ResourceBarrierUnorderedAccessView(_blendTex);
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 2);
        _cmd.ResourceBarrierTransition(_blendTex, ResourceStates.UnorderedAccess, ResourceStates.AllShaderResource);

        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.Present, ResourceStates.RenderTarget);
        _cmd.OMSetRenderTargets(RtvCpu(backIndex * 2));
        _cmd.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _cmd.RSSetScissorRect(_width, _height);
        _cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmd.SetGraphicsRootSignature(_optRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _optCb.GPUVirtualAddress);
        _cmd.SetGraphicsRootDescriptorTable(1, SrvGpu(OptimizedSrvBase));
        _cmd.SetPipelineState(_psoResolve[Math.Clamp(DebugMode, 0, 2)]);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.RenderTarget, ResourceStates.Present);
    }

    private void RenderReference(int backIndex)
    {
        UpdateRefConstants();
        _cmd.ResourceBarrierTransition(_edgesTex, ResourceStates.AllShaderResource, ResourceStates.RenderTarget);
        _cmd.ResourceBarrierTransition(_blendTex, ResourceStates.AllShaderResource, ResourceStates.RenderTarget);
        _cmd.ClearRenderTargetView(RtvCpu(4), new Color4(0, 0, 0, 0));
        _cmd.ClearRenderTargetView(RtvCpu(5), new Color4(0, 0, 0, 0));

        _cmd.OMSetRenderTargets(RtvCpu(4));
        _cmd.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _cmd.RSSetScissorRect(_width, _height);
        _cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmd.SetGraphicsRootSignature(_refRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _refCb.GPUVirtualAddress);
        _cmd.SetGraphicsRootDescriptorTable(1, SrvGpu(RefEdgeSrvBase));
        _cmd.SetPipelineState(_psoRefEdge);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 1);
        _cmd.ResourceBarrierTransition(_edgesTex, ResourceStates.RenderTarget, ResourceStates.AllShaderResource);

        _cmd.OMSetRenderTargets(RtvCpu(5));
        _cmd.SetGraphicsRootSignature(_refRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _refCb.GPUVirtualAddress);
        _cmd.SetGraphicsRootDescriptorTable(1, SrvGpu(RefEdgeSrvBase));
        _cmd.SetPipelineState(_psoRefBlend);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 2);
        _cmd.ResourceBarrierTransition(_blendTex, ResourceStates.RenderTarget, ResourceStates.AllShaderResource);

        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.Present, ResourceStates.RenderTarget);
        _cmd.OMSetRenderTargets(RtvCpu(backIndex * 2));
        _cmd.SetGraphicsRootSignature(_refRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _refCb.GPUVirtualAddress);
        _cmd.SetGraphicsRootDescriptorTable(1, SrvGpu(RefResolveSrvBase));
        _cmd.SetPipelineState(_psoRefResolve);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.RenderTarget, ResourceStates.Present);
    }

    private void RenderOff(int backIndex)
    {
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 1);
        _cmd.EndQuery(_queryHeap, QueryType.Timestamp, 2);
        UpdateRefConstants();

        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.Present, ResourceStates.RenderTarget);
        _cmd.OMSetRenderTargets(RtvCpu(backIndex * 2));
        _cmd.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        _cmd.RSSetScissorRect(_width, _height);
        _cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmd.SetGraphicsRootSignature(_refRoot);
        _cmd.SetGraphicsRootConstantBufferView(0, _refCb.GPUVirtualAddress);
        _cmd.SetGraphicsRootDescriptorTable(1, SrvGpu(OffSrvBase));
        _cmd.SetPipelineState(_psoOff);
        _cmd.DrawInstanced(3, 1, 0, 0);
        _cmd.ResourceBarrierTransition(_backBuffers[backIndex], ResourceStates.RenderTarget, ResourceStates.Present);
    }

    private void UpdateOptConstants()
    {
        var p = new SmaaParams
        {
            ScreenParams = new Vector4(_width, _height, 1.0f / _width, 1.0f / _height),
            SearchParams = new Vector4(Threshold, 32.0f, 16.0f, 25.0f),
            OptionsX = EdgeMode,
            OptionsY = DebugMode,
            OptionsZ = LowContrastFlags,
            OptionsW = 0,
            LowContrastParams = new Vector4(ThresholdLow, ThresholdHigh, ThresholdFloor, CmaaSynergy)
        };
        WriteStruct(_optCb, p);
    }

    private void UpdateRefConstants()
    {
        WriteStruct(_refCb, new Vector4(1.0f / _width, 1.0f / _height, _width, _height));
    }

    private unsafe void ReadTimings()
    {
        void* ptr;
        _timestampReadback.Map(0, null, &ptr);
        ulong t0 = (ulong)Marshal.ReadInt64((IntPtr)ptr, 0);
        ulong t1 = (ulong)Marshal.ReadInt64((IntPtr)ptr, 8);
        ulong t2 = (ulong)Marshal.ReadInt64((IntPtr)ptr, 16);
        ulong t3 = (ulong)Marshal.ReadInt64((IntPtr)ptr, 24);
        _timestampReadback.Unmap(0, null);
        if (_timestampFreq == 0) return;
        double scale = 1000.0 / _timestampFreq;
        AddSample(_edgeMs, (t1 - t0) * scale);
        AddSample(_blendMs, (t2 - t1) * scale);
        AddSample(_resolveMs, (t3 - t2) * scale);
        AddSample(_totalMs, (t3 - t0) * scale);
    }

    private static void AddSample(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > 240) list.RemoveAt(0);
    }

    private static double Median(List<double> list)
    {
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private void SetWindowTitle()
    {
        string debug = DebugMode == 0 ? "debug off" : $"debug {DebugMode}";
        string pause = _paused ? " | PAUSED" : string.Empty;
        string edge = Mode == 0 ? $" | edge {EdgeModeName}" : string.Empty;
        string text = $"SMAA {SceneName} | {ModeName}{edge} | {debug} | {_width}x{_height} | total {Median(_totalMs):F3} ms{pause}";
        Program.SetWindowText(_hwnd, text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { WaitForGpu(); } catch { }
        _queryHeap.Dispose();
        _timestampReadback.Dispose();
        _screenshotReadback.Dispose();
        _optCb.Dispose();
        _refCb.Dispose();
        _sceneCb.Dispose();
        _searchRef.Dispose();
        _areaRef.Dispose();
        _searchOpt.Dispose();
        _areaOpt.Dispose();
        _edgeMaskTex.Dispose();
        _blendTex.Dispose();
        _edgesTex.Dispose();
        _colorTex.Dispose();
        _psoOff.Dispose();
        _psoScene.Dispose();
        _psoRefResolve.Dispose();
        _psoRefBlend.Dispose();
        _psoRefEdge.Dispose();
        foreach (var pso in _psoResolve) pso.Dispose();
        _psoBlendCS.Dispose();
        foreach (var pso in _psoEdgeCS) pso.Dispose();
        _refRoot.Dispose();
        _optRoot.Dispose();
        _srvHeap.Dispose();
        _rtvHeap.Dispose();
        foreach (var bb in _backBuffers) bb.Dispose();
        _swapChain.Dispose();
        _fence.Dispose();
        _cmd.Dispose();
        _allocator.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _waitHandle.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmaaParams
{
    public Vector4 ScreenParams;
    public Vector4 SearchParams;
    public int OptionsX;
    public int OptionsY;
    public int OptionsZ;
    public int OptionsW;
    public Vector4 LowContrastParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SceneParams
{
    public Vector4 TimeRes;
    public Vector4 CamPos;
    public Vector4 CamRight;
    public Vector4 CamUp;
    public Vector4 CamForward;
    public Vector4 CamParams;
}

internal static class TestPattern
{
    public static byte[] Create(int width, int height)
    {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            float fy = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float fx = (x + 0.5f) / width;
                float r = 0.04f + 0.10f * fx;
                float g = 0.05f + 0.10f * fy;
                float b = 0.08f + 0.12f * (1.0f - fx);

                float dx = fx - 0.5f;
                float dy = fy - 0.5f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < 0.20f) { r = 0.10f; g = 0.35f; b = 0.75f; }
                if (dist < 0.14f) { r = 0.85f; g = 0.20f; b = 0.15f; }
                if (dist < 0.08f) { r = 0.95f; g = 0.90f; b = 0.20f; }

                int cell = ((x / 24) + (y / 24)) & 1;
                if (cell != 0) { r += 0.05f; g += 0.05f; b += 0.05f; }

                if (((x + y) % 12) < 2) { r = 0.95f; g = 0.95f; b = 0.95f; }
                if (((x - y + width) % 12) < 2) { r = 0.05f; g = 0.95f; b = 0.10f; }

                if (x > width * 0.10f && x < width * 0.16f && y > height * 0.70f && y < height * 0.90f) { r = 0.95f; g = 0.40f; b = 0.05f; }
                if (x > width * 0.18f && x < width * 0.24f && y > height * 0.70f && y < height * 0.90f) { r = 0.10f; g = 0.90f; b = 0.95f; }

                int i = (y * width + x) * 4;
                data[i + 0] = ToByte(r);
                data[i + 1] = ToByte(g);
                data[i + 2] = ToByte(b);
                data[i + 3] = 255;
            }
        }
        return data;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255.0f), 0, 255);
}
