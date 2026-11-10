using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!File.Exists(options.DemoPath))
        {
            Console.Error.WriteLine($"Demo executable not found: {options.DemoPath}");
            Console.Error.WriteLine("Build it first: dotnet build Demo/Demo.csproj -c Release");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDir);
        var rows = new List<Row>();
        var references = new Dictionary<string, ReferenceData>(StringComparer.Ordinal);

        int candidatesPerScene = options.Adaptive ? 1 : options.Thresholds.Count;
        int totalRuns = options.Resolutions.Count * options.Scenes.Count * (2 + candidatesPerScene);
        int run = 0;
        foreach (var (width, height) in options.Resolutions)
        {
            string resolution = $"{width}x{height}";
            foreach (string scene in options.Scenes)
            {
                string groupDir = Path.Combine(options.OutputDir, resolution, scene);
                Directory.CreateDirectory(groupDir);
                Console.WriteLine();
                Console.WriteLine($"== {resolution} / {scene} ==");

                int gtWidth = width * options.Scale;
                int gtHeight = height * options.Scale;
                string gtPath = Path.Combine(groupDir, $"gt_{options.Scale}x.bmp");
                RunDemo(options, gtWidth, gtHeight, scene, "off", null, gtPath, ++run, totalRuns);
                BitmapImage gt = BitmapImage.Load(gtPath);
                if (gt.Width != gtWidth || gt.Height != gtHeight)
                {
                    throw new InvalidOperationException(
                        $"Ground truth is {gt.Width}x{gt.Height}, expected {gtWidth}x{gtHeight}. " +
                        "DPI scaling may be changing the client size.");
                }

                var reference = ReferenceData.Create(gt, width, height, options.Scale, options.Scale);
                string referencePath = Path.Combine(groupDir, "gt_downsampled.bmp");
                BitmapImage.SaveBmp24(referencePath, reference.Image);
                references[Key(resolution, scene)] = reference;
                if (!options.KeepFrames) File.Delete(gtPath);

                string offPath = Path.Combine(groupDir, "off.bmp");
                RunDemo(options, width, height, scene, "off", null, offPath, ++run, totalRuns);
                AddRow(rows, resolution, scene, "off", null, BitmapImage.Load(offPath), reference);
                if (!options.KeepFrames) File.Delete(offPath);

                if (options.Adaptive)
                {
                    string framePath = Path.Combine(groupDir, "adaptive.bmp");
                    RunDemo(options, width, height, scene, "optimized", null, framePath, ++run, totalRuns);
                    AddRow(rows, resolution, scene, "adaptive", null, BitmapImage.Load(framePath), reference);
                    if (!options.KeepFrames) File.Delete(framePath);
                }
                else
                {
                    foreach (double threshold in options.Thresholds)
                    {
                        string framePath = Path.Combine(groupDir,
                            $"smaa_{threshold.ToString("0.000", CultureInfo.InvariantCulture)}.bmp");
                        RunDemo(options, width, height, scene, "optimized", threshold, framePath, ++run, totalRuns);
                        AddRow(rows, resolution, scene, "smaa", threshold, BitmapImage.Load(framePath), reference);
                        if (!options.KeepFrames) File.Delete(framePath);
                    }
                }

                WriteCsv(options.OutputDir, rows);
                WriteSummary(options.OutputDir, options, rows, references);
            }
        }

        WriteCsv(options.OutputDir, rows);
        WriteSummary(options.OutputDir, options, rows, references);
        Console.WriteLine();
        Console.WriteLine($"Wrote {Path.Combine(options.OutputDir, "results.csv")}");
        Console.WriteLine($"Wrote {Path.Combine(options.OutputDir, "summary.md")}");
        return 0;
    }

    private static string Key(string resolution, string scene) => $"{resolution}|{scene}";

    private static void AddRow(List<Row> rows, string resolution, string scene, string kind,
        double? threshold, BitmapImage actual, ReferenceData reference)
    {
        Metrics metrics = Metrics.Compare(reference, actual);
        rows.Add(new Row(resolution, scene, kind, threshold, metrics, reference.MeanLuma));
        Console.WriteLine($"  {kind,-4} {threshold?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-",-5} " +
                          $"MAE {metrics.Mae:F8}  >10/255 {metrics.PctGt10:F4}%  " +
                          $"edge {metrics.EdgeMae:F8}  max {metrics.MaxDiff}/255");
    }

    private static void RunDemo(Options options, int width, int height, string scene,
        string aa, double? threshold, string screenshot, int index, int total)
    {
        if (File.Exists(screenshot)) File.Delete(screenshot);
        Console.WriteLine($"  [{index}/{total}] render {width}x{height} {scene} {aa}" +
                          (threshold.HasValue ? $" t={threshold.Value:0.000}" : string.Empty));

        var start = new ProcessStartInfo
        {
            FileName = options.DemoPath,
            WorkingDirectory = Path.GetDirectoryName(options.DemoPath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        void Arg(string value) => start.ArgumentList.Add(value);
        Arg("--width"); Arg(width.ToString(CultureInfo.InvariantCulture));
        Arg("--height"); Arg(height.ToString(CultureInfo.InvariantCulture));
        Arg("--scene"); Arg("3d");
        Arg("--scene-preset"); Arg(scene);
        Arg("--aa"); Arg(aa);
        Arg("--frames"); Arg(options.Frames.ToString(CultureInfo.InvariantCulture));
        Arg("--capture-frame"); Arg(options.CaptureFrame.ToString(CultureInfo.InvariantCulture));
        Arg("--screenshot"); Arg(screenshot);
        Arg("--no-debug-layer");
        if (options.Adaptive)
        {
            Arg("--adaptive-edge");
            Arg("--adaptive-dark"); Arg(options.AdaptiveDark.ToString("0.000", CultureInfo.InvariantCulture));
            Arg("--adaptive-bright"); Arg(options.AdaptiveBright.ToString("0.000", CultureInfo.InvariantCulture));
            Arg("--adaptive-luma-low"); Arg(options.AdaptiveLumaLow.ToString("0.000", CultureInfo.InvariantCulture));
            Arg("--adaptive-luma-high"); Arg(options.AdaptiveLumaHigh.ToString("0.000", CultureInfo.InvariantCulture));
        }
        if (threshold.HasValue)
        {
            Arg("--threshold"); Arg(threshold.Value.ToString("0.000", CultureInfo.InvariantCulture));
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start demo.");
        if (!process.WaitForExit(options.TimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Demo did not exit within {options.TimeoutMs} ms: {screenshot}");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Demo exited with code {process.ExitCode}: {screenshot}");

        string logPath = Path.Combine(start.WorkingDirectory, "Demo.log");
        if (File.Exists(logPath))
        {
            string log = File.ReadAllText(logPath);
            if (log.Contains("FATAL:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Demo reported a fatal error for {screenshot}:\n{Tail(log, 2000)}");
        }

        if (!File.Exists(screenshot))
            throw new InvalidOperationException($"Demo did not create {screenshot}");
    }

    private static string Tail(string text, int length)
        => text.Length <= length ? text : text[^length..];

    private static void WriteCsv(string outputDir, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("resolution,scene,kind,threshold,mae,rmse,max_diff_255,pct_gt_1,pct_gt_2,pct_gt_4,pct_gt_10,changed_pct,edge_mae,flat_mae,grad_ratio,edge_pixels,mean_luma");
        foreach (Row row in rows.OrderBy(r => r.Resolution).ThenBy(r => r.Scene)
                     .ThenBy(r => r.Kind).ThenBy(r => r.Threshold ?? -1))
        {
            Metrics m = row.Metrics;
            sb.Append(row.Resolution).Append(',')
              .Append(row.Scene).Append(',')
              .Append(row.Kind).Append(',')
              .Append(row.Threshold?.ToString("0.000", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
              .Append(m.Mae.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.Rmse.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.MaxDiff).Append(',')
              .Append(m.PctGt1.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.PctGt2.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.PctGt4.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.PctGt10.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.ChangedPct.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.EdgeMae.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.FlatMae.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.GradientRatio.ToString("G9", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.EdgePixels).Append(',')
              .Append(row.MeanLuma.ToString("F5", CultureInfo.InvariantCulture))
              .AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDir, "results.csv"), sb.ToString());
    }

    private static void WriteSummary(string outputDir, Options options, List<Row> rows,
        Dictionary<string, ReferenceData> references)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SMAA threshold quality sweep");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  ");
        sb.AppendLine($"Demo: `{options.DemoPath}`  ");
        sb.AppendLine($"Ground truth: {options.Scale}x{options.Scale} SSAA ({options.Scale * options.Scale} samples/pixel), " +
                      $"frame {options.CaptureFrame}, 3D corridor presets.  ");
        sb.AppendLine("All metrics compare display-referred 8-bit output against the downsampled SSAA reference.");
        sb.AppendLine();

        foreach (var group in rows.Where(r => r.Kind == "smaa")
                     .GroupBy(r => (r.Resolution, r.Scene))
                     .OrderBy(g => g.Key.Resolution).ThenBy(g => g.Key.Scene))
        {
            string resolution = group.Key.Resolution;
            string scene = group.Key.Scene;
            ReferenceData reference = references[Key(resolution, scene)];
            Row? off = rows.FirstOrDefault(r => r.Resolution == resolution && r.Scene == scene && r.Kind == "off");
            var candidates = group.OrderBy(r => r.Threshold).ToList();
            Row bestMae = candidates.OrderBy(r => r.Metrics.Mae).First();
            Row bestEdge = candidates.OrderBy(r => r.Metrics.EdgeMae).First();

            sb.AppendLine($"## {resolution} / {scene}");
            sb.AppendLine();
            sb.AppendLine($"Mean reference luma: {reference.MeanLuma:F3}. " +
                          $"Edge pixels: {reference.EdgeCount * 100.0 / (reference.EdgeCount + reference.FlatCount):F2}%.");
            sb.AppendLine();
            sb.AppendLine("| Threshold | MAE | RMSE | Max | >1/255 | >10/255 | Edge MAE | Flat MAE | Grad ratio |");
            sb.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            if (off != null)
            {
                Metrics m = off.Metrics;
                sb.AppendLine($"| off | {m.Mae:F8} | {m.Rmse:F8} | {m.MaxDiff}/255 | " +
                              $"{m.PctGt1:F4}% | {m.PctGt10:F4}% | {m.EdgeMae:F8} | {m.FlatMae:F8} | " +
                              $"{m.GradientRatio:F3} |");
            }
            foreach (Row row in candidates)
            {
                Metrics m = row.Metrics;
                string mark = ReferenceEquals(row, bestMae) && ReferenceEquals(row, bestEdge) ? " *+"
                    : ReferenceEquals(row, bestMae) ? " *"
                    : ReferenceEquals(row, bestEdge) ? " +"
                    : string.Empty;
                sb.AppendLine($"| {row.Threshold:0.000}{mark} | {m.Mae:F8} | {m.Rmse:F8} | {m.MaxDiff}/255 | " +
                              $"{m.PctGt1:F4}% | {m.PctGt10:F4}% | {m.EdgeMae:F8} | {m.FlatMae:F8} | " +
                              $"{m.GradientRatio:F3} |");
            }
            sb.AppendLine();
            sb.AppendLine($"Best global MAE: **{bestMae.Threshold:0.000}** ({bestMae.Metrics.Mae:F8}). " +
                          $"Best edge MAE: **{bestEdge.Threshold:0.000}** ({bestEdge.Metrics.EdgeMae:F8}).");
            sb.AppendLine();
        }

        var adaptiveRows = rows.Where(r => r.Kind == "adaptive").ToList();
        if (adaptiveRows.Count > 0)
        {
            sb.AppendLine("## Luma-adaptive canonical edge pass");
            sb.AppendLine();
            sb.AppendLine($"Parameters: dark {options.AdaptiveDark:F3}, bright {options.AdaptiveBright:F3}, " +
                          $"luma {options.AdaptiveLumaLow:F3}-{options.AdaptiveLumaHigh:F3}.");
            sb.AppendLine();
            sb.AppendLine("| Resolution | Scene | Mean luma | MAE | Edge MAE | >10/255 | Grad ratio |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (Row row in adaptiveRows.OrderBy(r => r.Resolution).ThenBy(r => r.Scene))
            {
                Metrics m = row.Metrics;
                sb.AppendLine($"| {row.Resolution} | {row.Scene} | {row.MeanLuma:F3} | {m.Mae:F8} | " +
                              $"{m.EdgeMae:F8} | {m.PctGt10:F4}% | {m.GradientRatio:F3} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Cross-scene averages");
        sb.AppendLine();
        foreach (var resolutionGroup in rows.Where(r => r.Kind == "smaa")
                     .GroupBy(r => r.Resolution).OrderBy(g => g.Key))
        {
            string resolution = resolutionGroup.Key;
            var sceneNames = resolutionGroup.Select(r => r.Scene).Distinct().OrderBy(s => s).ToList();
            var thresholdGroups = resolutionGroup.GroupBy(r => r.Threshold!.Value)
                .OrderBy(g => g.Key).ToList();
            var baseline = thresholdGroups.FirstOrDefault(g => Math.Abs(g.Key - 0.10) < 1e-9);
            var sceneBestMae = sceneNames.ToDictionary(
                scene => scene,
                scene => resolutionGroup.Where(r => r.Scene == scene).Min(r => r.Metrics.Mae));
            var sceneBestEdge = sceneNames.ToDictionary(
                scene => scene,
                scene => resolutionGroup.Where(r => r.Scene == scene).Min(r => r.Metrics.EdgeMae));

            sb.AppendLine($"### {resolution}");
            sb.AppendLine();
            sb.AppendLine("| Threshold | Avg MAE | Avg norm MAE (best=100) | Worst MAE delta vs 0.10 | Avg edge MAE | Avg norm edge | Worst edge delta vs 0.10 | Avg >10/255 | Avg grad ratio |");
            sb.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var thresholdGroup in thresholdGroups)
            {
                var byScene = thresholdGroup.ToDictionary(r => r.Scene);
                if (byScene.Count != sceneNames.Count) continue;
                double avgMae = byScene.Values.Average(r => r.Metrics.Mae);
                double avgEdge = byScene.Values.Average(r => r.Metrics.EdgeMae);
                double avgTail = byScene.Values.Average(r => r.Metrics.PctGt10);
                double avgGrad = byScene.Values.Average(r => r.Metrics.GradientRatio);
                double avgNormMae = sceneNames.Average(scene =>
                    byScene[scene].Metrics.Mae / sceneBestMae[scene] * 100.0);
                double avgNormEdge = sceneNames.Average(scene =>
                    byScene[scene].Metrics.EdgeMae / sceneBestEdge[scene] * 100.0);
                double worstMaeDelta = double.NaN;
                double worstEdgeDelta = double.NaN;
                if (baseline != null)
                {
                    var baselineByScene = baseline.ToDictionary(r => r.Scene);
                    worstMaeDelta = sceneNames.Max(scene =>
                    {
                        double b = baselineByScene[scene].Metrics.Mae;
                        double v = byScene[scene].Metrics.Mae;
                        return b > 0 ? (v - b) / b * 100.0 : 0;
                    });
                    worstEdgeDelta = sceneNames.Max(scene =>
                    {
                        double b = baselineByScene[scene].Metrics.EdgeMae;
                        double v = byScene[scene].Metrics.EdgeMae;
                        return b > 0 ? (v - b) / b * 100.0 : 0;
                    });
                }
                sb.AppendLine($"| {thresholdGroup.Key:0.000} | {avgMae:F8} | {avgNormMae:F2} | " +
                              $"{(double.IsNaN(worstMaeDelta) ? "-" : $"{worstMaeDelta:+0.0;-0.0;0.0}%")} | " +
                              $"{avgEdge:F8} | {avgNormEdge:F2} | " +
                              $"{(double.IsNaN(worstEdgeDelta) ? "-" : $"{worstEdgeDelta:+0.0;-0.0;0.0}%")} | " +
                              $"{avgTail:F4}% | {avgGrad:F3} |");
            }
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(outputDir, "summary.md"), sb.ToString());
    }

    private sealed record Row(
        string Resolution,
        string Scene,
        string Kind,
        double? Threshold,
        Metrics Metrics,
        double MeanLuma);

    private sealed record Metrics(
        double Mae,
        double Rmse,
        int MaxDiff,
        double PctGt1,
        double PctGt2,
        double PctGt4,
        double PctGt10,
        double ChangedPct,
        double EdgeMae,
        double FlatMae,
        double GradientRatio,
        long EdgePixels)
    {
        public static Metrics Compare(ReferenceData referenceData, BitmapImage actual)
        {
            BitmapImage reference = referenceData.Image;
            if (reference.Width != actual.Width || reference.Height != actual.Height)
                throw new InvalidOperationException(
                    $"Image size mismatch: reference {reference.Width}x{reference.Height}, " +
                    $"actual {actual.Width}x{actual.Height}");

            long n = (long)reference.Width * reference.Height;
            double sumAbs = 0, sumSq = 0, edgeAbs = 0, flatAbs = 0;
            long gt1 = 0, gt2 = 0, gt4 = 0, gt10 = 0, changed = 0;
            int maxDiff = 0;
            bool[] edgeMask = referenceData.EdgeMask;
            long edgeCount = referenceData.EdgeCount;
            long flatCount = referenceData.FlatCount;

            byte[] rr = reference.R, rg = reference.G, rb = reference.B;
            byte[] ar = actual.R, ag = actual.G, ab = actual.B;
            for (int i = 0; i < n; i++)
            {
                int dr = Math.Abs(rr[i] - ar[i]);
                int dg = Math.Abs(rg[i] - ag[i]);
                int db = Math.Abs(rb[i] - ab[i]);
                int m = Math.Max(dr, Math.Max(dg, db));

                sumAbs += dr + dg + db;
                sumSq += (double)dr * dr + (double)dg * dg + (double)db * db;
                if (m > maxDiff) maxDiff = m;
                if (m > 0) changed++;
                if (m > 1) gt1++;
                if (m > 2) gt2++;
                if (m > 4) gt4++;
                if (m > 10) gt10++;
                if (edgeMask[i]) edgeAbs += dr + dg + db;
                else flatAbs += dr + dg + db;
            }

            double mae = sumAbs / (n * 3.0 * 255.0);
            double rmse = Math.Sqrt(sumSq / (n * 3.0)) / 255.0;
            double edgeMae = edgeCount > 0 ? edgeAbs / (edgeCount * 3.0 * 255.0) : 0;
            double flatMae = flatCount > 0 ? flatAbs / (flatCount * 3.0 * 255.0) : 0;
            double gradientRatio = referenceData.GradientMean > 0
                ? GradientMean(actual) / referenceData.GradientMean
                : 1.0;
            return new Metrics(mae, rmse, maxDiff, gt1 * 100.0 / n, gt2 * 100.0 / n,
                gt4 * 100.0 / n, gt10 * 100.0 / n, changed * 100.0 / n,
                edgeMae, flatMae, gradientRatio, edgeCount);
        }

        public static double GradientMean(BitmapImage image)
        {
            int width = image.Width, height = image.Height;
            var luma = new float[width * height];
            byte[] r = image.R, g = image.G, b = image.B;
            for (int i = 0; i < luma.Length; i++)
                luma[i] = (0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i]) / 255.0f;

            double sum = 0;
            for (int y = 0; y < height; y++)
            {
                int ym = Math.Max(0, y - 1) * width;
                int yp = Math.Min(height - 1, y + 1) * width;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int xm = Math.Max(0, x - 1);
                    int xp = Math.Min(width - 1, x + 1);
                    float gx = luma[row + xp] - luma[row + xm];
                    float gy = luma[yp + x] - luma[ym + x];
                    sum += Math.Sqrt(gx * gx + gy * gy);
                }
            }
            return sum / luma.Length;
        }
    }

    private sealed class ReferenceData
    {
        public required BitmapImage Image { get; init; }
        public required bool[] EdgeMask { get; init; }
        public required long EdgeCount { get; init; }
        public required long FlatCount { get; init; }
        public required double MeanLuma { get; init; }
        public required double GradientMean { get; init; }

        public static ReferenceData Create(BitmapImage source, int width, int height, int scaleX, int scaleY)
        {
            if (source.Width != width * scaleX || source.Height != height * scaleY)
                throw new InvalidOperationException(
                    $"Reference size {source.Width}x{source.Height} is not {scaleX}x{scaleY} of {width}x{height}.");

            BitmapImage image = source.Downsample(width, height, scaleX, scaleY);
            long n = (long)width * height;
            var luma = new float[n];
            double lumaSum = 0;
            for (int i = 0; i < n; i++)
            {
                float value = (0.2126f * image.R[i] + 0.7152f * image.G[i] + 0.0722f * image.B[i]) / 255.0f;
                luma[i] = value;
                lumaSum += value;
            }

            var mask = new bool[n];
            long edgeCount = 0;
            const float edgeRange = 8.0f / 255.0f;
            for (int y = 0; y < height; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(width - 1, x + 1);
                    float min = 1.0f, max = 0.0f;
                    for (int sy = y0; sy <= y1; sy++)
                    {
                        int row = sy * width;
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            float v = luma[row + sx];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    }
                    if (max - min > edgeRange)
                    {
                        mask[y * width + x] = true;
                        edgeCount++;
                    }
                }
            }

            return new ReferenceData
            {
                Image = image,
                EdgeMask = mask,
                EdgeCount = edgeCount,
                FlatCount = n - edgeCount,
                MeanLuma = lumaSum / n,
                GradientMean = Metrics.GradientMean(image)
            };
        }
    }

    private sealed class BitmapImage
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] R { get; init; }
        public required byte[] G { get; init; }
        public required byte[] B { get; init; }

        public static BitmapImage Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            byte[] header = reader.ReadBytes(54);
            if (header.Length != 54 || header[0] != (byte)'B' || header[1] != (byte)'M')
                throw new InvalidDataException($"Not a BMP file: {path}");

            int dataOffset = BitConverter.ToInt32(header, 10);
            int width = BitConverter.ToInt32(header, 18);
            int signedHeight = BitConverter.ToInt32(header, 22);
            short bpp = BitConverter.ToInt16(header, 28);
            int compression = BitConverter.ToInt32(header, 30);
            int height = Math.Abs(signedHeight);
            bool topDown = signedHeight < 0;
            if (width <= 0 || height <= 0) throw new InvalidDataException($"Invalid BMP size: {path}");
            if (bpp != 24 && bpp != 32) throw new InvalidDataException($"Unsupported BMP depth {bpp}: {path}");
            if (compression != 0) throw new InvalidDataException($"Compressed BMP not supported: {path}");

            int rowSize = (width * bpp + 31) / 32 * 4;
            stream.Seek(dataOffset, SeekOrigin.Begin);
            byte[] raw = reader.ReadBytes(rowSize * height);
            if (raw.Length != rowSize * height) throw new InvalidDataException($"Truncated BMP: {path}");

            int n = width * height;
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            int bytesPerPixel = bpp / 8;
            for (int y = 0; y < height; y++)
            {
                int sourceY = topDown ? y : height - 1 - y;
                int source = sourceY * rowSize;
                int destination = y * width;
                for (int x = 0; x < width; x++)
                {
                    int s = source + x * bytesPerPixel;
                    b[destination + x] = raw[s];
                    g[destination + x] = raw[s + 1];
                    r[destination + x] = raw[s + 2];
                }
            }

            return new BitmapImage { Width = width, Height = height, R = r, G = g, B = b };
        }

        public BitmapImage Downsample(int width, int height, int scaleX, int scaleY)
        {
            if (Width != width * scaleX || Height != height * scaleY)
                throw new InvalidOperationException("Downsample dimensions do not match the integer scale.");

            int n = width * height;
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            int samples = scaleX * scaleY;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sr = 0, sg = 0, sb = 0;
                    for (int sy = 0; sy < scaleY; sy++)
                    {
                        int source = (y * scaleY + sy) * Width + x * scaleX;
                        for (int sx = 0; sx < scaleX; sx++)
                        {
                            sr += R[source + sx];
                            sg += G[source + sx];
                            sb += B[source + sx];
                        }
                    }
                    int destination = y * width + x;
                    r[destination] = (byte)((sr + samples / 2) / samples);
                    g[destination] = (byte)((sg + samples / 2) / samples);
                    b[destination] = (byte)((sb + samples / 2) / samples);
                }
            }

            return new BitmapImage { Width = width, Height = height, R = r, G = g, B = b };
        }

        public static void SaveBmp24(string path, BitmapImage image)
        {
            int rowSize = (image.Width * 3 + 3) / 4 * 4;
            int imageSize = rowSize * image.Height;
            var data = new byte[54 + imageSize];
            data[0] = (byte)'B';
            data[1] = (byte)'M';
            BitConverter.TryWriteBytes(data.AsSpan(2), 54 + imageSize);
            BitConverter.TryWriteBytes(data.AsSpan(10), 54);
            BitConverter.TryWriteBytes(data.AsSpan(14), 40);
            BitConverter.TryWriteBytes(data.AsSpan(18), image.Width);
            BitConverter.TryWriteBytes(data.AsSpan(22), image.Height);
            BitConverter.TryWriteBytes(data.AsSpan(26), (short)1);
            BitConverter.TryWriteBytes(data.AsSpan(28), (short)24);
            BitConverter.TryWriteBytes(data.AsSpan(34), imageSize);
            BitConverter.TryWriteBytes(data.AsSpan(38), 2835);
            BitConverter.TryWriteBytes(data.AsSpan(42), 2835);

            for (int y = 0; y < image.Height; y++)
            {
                int sourceRow = (image.Height - 1 - y) * image.Width;
                int destination = 54 + y * rowSize;
                for (int x = 0; x < image.Width; x++)
                {
                    int source = sourceRow + x;
                    data[destination + x * 3 + 0] = image.B[source];
                    data[destination + x * 3 + 1] = image.G[source];
                    data[destination + x * 3 + 2] = image.R[source];
                }
            }

            File.WriteAllBytes(path, data);
        }
    }

    private sealed class Options
    {
        public required string DemoPath { get; init; }
        public required string OutputDir { get; init; }
        public required List<(int Width, int Height)> Resolutions { get; init; }
        public required List<string> Scenes { get; init; }
        public required List<double> Thresholds { get; init; }
        public required int Scale { get; init; }
        public required int Frames { get; init; }
        public required int CaptureFrame { get; init; }
        public required bool KeepFrames { get; init; }
        public required int TimeoutMs { get; init; }
        public required bool Adaptive { get; init; }
        public required double AdaptiveDark { get; init; }
        public required double AdaptiveBright { get; init; }
        public required double AdaptiveLumaLow { get; init; }
        public required double AdaptiveLumaHigh { get; init; }

        public static Options Parse(string[] args)
        {
            string demoPath = Path.GetFullPath(Path.Combine("Demo", "bin", "Release", "net8.0-windows", "Demo.exe"));
            string outputDir = Path.GetFullPath(Path.Combine("Demo", "QualityResults"));
            var resolutions = new List<(int, int)> { (1920, 1080) };
            var scenes = new List<string> { "day", "night", "bright", "lowcontrast", "night-lowcontrast" };
            var thresholds = new List<double> { 0.03, 0.04, 0.05, 0.07, 0.09, 0.10, 0.11 };
            int scale = 2;
            int frames = 3;
            int captureFrame = 2;
            bool keepFrames = false;
            int timeoutMs = 300_000;
            bool adaptive = false;
            double adaptiveDark = 0.045;
            double adaptiveBright = 0.090;
            double adaptiveLumaLow = 0.150;
            double adaptiveLumaHigh = 0.380;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next()
                {
                    if (++i >= args.Length) throw new ArgumentException($"Missing value for {arg}");
                    return args[i];
                }

                switch (arg)
                {
                    case "--demo":
                        demoPath = Path.GetFullPath(Next());
                        break;
                    case "--out":
                        outputDir = Path.GetFullPath(Next());
                        break;
                    case "--res":
                        resolutions = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(value => ParseResolution(value.Trim())).ToList();
                        break;
                    case "--scenes":
                        scenes = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(value => value.Trim().ToLowerInvariant()).ToList();
                        break;
                    case "--thresholds":
                        thresholds = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(value => double.Parse(value.Trim(), CultureInfo.InvariantCulture)).ToList();
                        break;
                    case "--scale":
                        scale = int.Parse(Next(), CultureInfo.InvariantCulture);
                        if (scale < 1 || scale > 4) throw new ArgumentException("--scale must be 1..4");
                        break;
                    case "--frames":
                        frames = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--capture-frame":
                        captureFrame = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--keep-frames":
                        keepFrames = true;
                        break;
                    case "--timeout-ms":
                        timeoutMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--adaptive":
                        adaptive = true;
                        break;
                    case "--adaptive-dark":
                        adaptiveDark = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--adaptive-bright":
                        adaptiveBright = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--adaptive-luma-low":
                        adaptiveLumaLow = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--adaptive-luma-high":
                        adaptiveLumaHigh = double.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            if (resolutions.Count == 0) throw new ArgumentException("At least one resolution is required.");
            if (scenes.Count == 0) throw new ArgumentException("At least one scene preset is required.");
            if (thresholds.Count == 0) throw new ArgumentException("At least one threshold is required.");
            if (frames < captureFrame + 1) throw new ArgumentException("--frames must be greater than --capture-frame.");
            if (demoPath.Contains(Path.DirectorySeparatorChar) && !Path.IsPathRooted(demoPath))
                demoPath = Path.GetFullPath(demoPath);

            return new Options
            {
                DemoPath = demoPath,
                OutputDir = outputDir,
                Resolutions = resolutions,
                Scenes = scenes,
                Thresholds = thresholds.OrderBy(t => t).ToList(),
                Scale = scale,
                Frames = frames,
                CaptureFrame = captureFrame,
                KeepFrames = keepFrames,
                TimeoutMs = timeoutMs,
                Adaptive = adaptive,
                AdaptiveDark = adaptiveDark,
                AdaptiveBright = adaptiveBright,
                AdaptiveLumaLow = adaptiveLumaLow,
                AdaptiveLumaHigh = adaptiveLumaHigh
            };
        }

        private static (int, int) ParseResolution(string value)
        {
            string[] parts = value.ToLowerInvariant().Split('x');
            if (parts.Length != 2) throw new ArgumentException($"Invalid resolution '{value}', expected WxH.");
            return (int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }
}
