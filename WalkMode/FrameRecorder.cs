using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace WalkMode
{
    /// <summary>
    /// Records every viewport frame (start/end time, camera, dynamic-display
    /// state) so native navigation and walk mode can be compared on the same
    /// measurements.  Frames are grouped by <see cref="Tag"/>.
    /// </summary>
    internal sealed class FrameRecorder : DisplayConduit
    {
        internal struct Frame
        {
            public string  Tag;
            public string  Viewport;
            public Guid    ViewportId;
            public string  Mode;
            public int     Width, Height;
            public double  Near, Far;
            public double  StartMs, EndMs;
            public double  PreDrawMs, PostDrawMs, ForegroundMs;   // pipeline stage stamps
            public bool    Dynamic;
            public Point3d CamPos;
            public Vector3d CamDir;
        }

        static FrameRecorder s_instance;
        internal static FrameRecorder Active => s_instance;
        internal static bool IsRecording => s_instance != null;

        /// <summary>Label applied to frames drawn from now on.</summary>
        internal static string Tag = "native";

        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly List<Frame> _frames = new List<Frame>();
        readonly Dictionary<string, List<double>> _samples = new Dictionary<string, List<double>>();
        readonly Dictionary<Guid, Frame> _open = new Dictionary<Guid, Frame>();

        internal double NowMs => _sw.Elapsed.TotalMilliseconds;

        // Scripted-input window (set by WalkBenchRobot from its input thread).
        double _robotStartMs = -1, _robotEndMs = -1;
        internal void MarkRobotStart() => Interlocked.Exchange(ref _robotStartMs, NowMs);
        internal void MarkRobotEnd()   => Interlocked.Exchange(ref _robotEndMs,   NowMs);

        internal static void Start()
        {
            if (s_instance != null) return;
            s_instance = new FrameRecorder { Enabled = true };
        }

        internal static FrameRecorder Stop()
        {
            var r = s_instance;
            if (r == null) return null;
            r.Enabled = false;
            s_instance = null;
            return r;
        }

        /// <summary>Record a named timing (ms) or value from outside the pipeline.</summary>
        internal static void Sample(string name, double value)
        {
            var r = s_instance;
            if (r == null) return;
            name = $"{Tag}: {name}";
            if (!r._samples.TryGetValue(name, out var list))
                r._samples[name] = list = new List<double>();
            list.Add(value);
        }

        // ── Pipeline hooks ────────────────────────────────────────────────────
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            var vp = e.Viewport;
            var dir = vp.CameraDirection;
            dir.Unitize();
            vp.GetFrustum(out _, out _, out _, out _, out double near, out double far);
            _open[vp.Id] = new Frame
            {
                Tag        = Tag,
                Viewport   = vp.Name,
                ViewportId = vp.Id,
                Mode       = vp.DisplayMode?.EnglishName ?? "?",
                Width      = vp.Size.Width,
                Height     = vp.Size.Height,
                Near       = near,
                Far        = far,
                StartMs    = NowMs,
                CamPos     = vp.CameraLocation,
                CamDir     = dir,
            };
        }

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (_open.TryGetValue(e.Viewport.Id, out var f)) { f.PreDrawMs = NowMs; _open[e.Viewport.Id] = f; }
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (_open.TryGetValue(e.Viewport.Id, out var f)) { f.PostDrawMs = NowMs; _open[e.Viewport.Id] = f; }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (_open.TryGetValue(e.Viewport.Id, out var f)) { f.ForegroundMs = NowMs; _open[e.Viewport.Id] = f; }
        }

        /// <summary>Pipeline start/end of the most recently finished frame.</summary>
        internal double LastStartMs { get; private set; } = -1;
        internal double LastEndMs   { get; private set; } = -1;

        protected override void DrawOverlay(DrawEventArgs e)
        {
            if (!_open.TryGetValue(e.Viewport.Id, out var f)) return;
            _open.Remove(e.Viewport.Id);
            f.EndMs   = NowMs;
            LastStartMs = f.StartMs;
            LastEndMs   = f.EndMs;
            f.Dynamic = e.Display.IsDynamicDisplay;
            _frames.Add(f);
        }

        // ── Reporting ─────────────────────────────────────────────────────────
        const double GapMs = 250.0;   // longer than this = idle, not a slow frame

        internal void Report(string label)
        {
            if (_frames.Count == 0)
            {
                RhinoApp.WriteLine("WalkBench: no frames recorded.");
                return;
            }

            foreach (var tag in _frames.Select(f => f.Tag).Distinct())
                ReportTag(tag, _frames.Where(f => f.Tag == tag).ToList());

            foreach (var kv in _samples.OrderBy(k => k.Key))
            {
                var v = kv.Value;
                RhinoApp.WriteLine(
                    $"  {kv.Key,-18} n={v.Count,5}  p50={Pct(v, 50),7:F2}  p95={Pct(v, 95),7:F2}  max={v.Max(),7:F2}");
            }

            string path = WriteCsv(label);
            RhinoApp.WriteLine($"WalkBench: per-frame data written to {path}");
        }

        void ReportTag(string tag, List<Frame> frames)
        {
            var intervals = new List<double>();
            var draws     = new List<double>();
            var stepRates = new List<double>();   // deg/s per frame, motion frames only
            var translationSteps = new List<double>();
            var moving    = new List<(Frame f, double rate)>();
            int dynamic = 0, held = 0;

            foreach (var group in frames.GroupBy(f => f.ViewportId))
            {
                var seq = group.OrderBy(f => f.StartMs).ToList();
                for (int i = 0; i < seq.Count; i++)
                {
                    draws.Add(seq[i].EndMs - seq[i].StartMs);
                    if (seq[i].Dynamic) dynamic++;
                    if (i == 0) continue;

                    double dt = seq[i].StartMs - seq[i - 1].StartMs;
                    if (dt > GapMs) continue;
                    intervals.Add(dt);

                    double ang  = Vector3d.VectorAngle(seq[i - 1].CamDir, seq[i].CamDir) * 180.0 / Math.PI;
                    double dist = seq[i - 1].CamPos.DistanceTo(seq[i].CamPos);
                    translationSteps.Add(dist);
                    if (double.IsNaN(ang)) ang = 0;
                    bool moved = ang > 1e-4 || dist > 1e-9;
                    if (moved)
                    {
                        double rate = ang / (dt / 1000.0);
                        stepRates.Add(rate);
                        moving.Add((seq[i], rate));
                    }
                    else held++;
                }
            }

            var first = frames[0];
            RhinoApp.WriteLine($"── {tag} ──────────────────────────────");
            RhinoApp.WriteLine($"  view              {first.Viewport}  {first.Width}x{first.Height}  mode '{first.Mode}'");
            RhinoApp.WriteLine($"  frames            {frames.Count}   dynamic-display {100.0 * dynamic / frames.Count:F0}%");
            var nears = frames.Select(f => f.Near).ToList();
            var ratio = frames.Select(f => f.Near > 0 ? f.Far / f.Near : 0).ToList();
            RhinoApp.WriteLine($"  near clip         p50={Pct(nears, 50):G4}  max={nears.Max():G4}   far/near p50={Pct(ratio, 50):G3}");
            if (intervals.Count > 0)
            {
                double med = Pct(intervals, 50);
                int hitches = intervals.Count(d => d > 1.5 * med);
                RhinoApp.WriteLine(
                    $"  frame interval ms p50={med:F2}  p95={Pct(intervals, 95):F2}  p99={Pct(intervals, 99):F2}  max={intervals.Max():F2}   (~{1000.0 / med:F0} fps)");
                RhinoApp.WriteLine(
                    $"  hitches (>1.5x p50) {hitches} ({100.0 * hitches / intervals.Count:F1}%)   frames with no camera change {held}");
            }
            RhinoApp.WriteLine(
                $"  draw time ms      p50={Pct(draws, 50):F2}  p95={Pct(draws, 95):F2}  max={draws.Max():F2}");
            if (translationSteps.Any(d => d > 1e-9))
            {
                int stationary = translationSteps.Count(d => d <= 1e-9);
                RhinoApp.WriteLine(
                    $"  translation       stationary {stationary}/{translationSteps.Count} frames; step p50={Pct(translationSteps, 50):G4} p95={Pct(translationSteps, 95):G4} document units");
            }

            // Where inside the pipeline the time goes (p50 of each stage).
            var staged = frames.Where(f => f.PreDrawMs > 0 && f.PostDrawMs > 0 && f.ForegroundMs > 0).ToList();
            if (staged.Count > 0)
            {
                double setup = Pct(staged.Select(f => f.PreDrawMs    - f.StartMs).ToList(),      50);
                double objs  = Pct(staged.Select(f => f.PostDrawMs   - f.PreDrawMs).ToList(),    50);
                double post  = Pct(staged.Select(f => f.ForegroundMs - f.PostDrawMs).ToList(),   50);
                double fg    = Pct(staged.Select(f => f.EndMs        - f.ForegroundMs).ToList(), 50);
                RhinoApp.WriteLine(
                    $"  stages p50 ms     setup {setup:F2} | objects {objs:F2} | post-objects {post:F2} | foreground {fg:F2}");
            }

            // Motion jitter: how much the turn rate jumps between consecutive
            // frames, relative to the typical turn rate.  Hand motion changes
            // smoothly, so large values mean the image steps unevenly.
            // Frames turning at under 10% of the typical rate are near-still
            // (panning, settling) and would blow up the ratio, so skip them.
            double typical = Pct(stepRates, 50);
            var turning = stepRates.Where(r => r > 0.1 * typical).ToList();
            if (turning.Count > 2 && typical > 1e-6)
            {
                var diffs = new List<double>();
                for (int i = 1; i < turning.Count; i++)
                    diffs.Add(Math.Abs(turning[i] - turning[i - 1]));
                RhinoApp.WriteLine(
                    $"  turn rate p50 {typical:F1} deg/s   motion jitter p50={100 * Pct(diffs, 50) / typical:F0}%  p95={100 * Pct(diffs, 95) / typical:F0}%");
            }

            ReportRobot(moving);
        }

        void ReportRobot(List<(Frame f, double rate)> moving)
        {
            double start = Volatile.Read(ref _robotStartMs);
            double end   = Volatile.Read(ref _robotEndMs);
            if (start < 0 || end < 0 || moving.Count == 0) return;

            // "Shown" ≈ end of the frame's pipeline pass.
            var after = moving.Where(m => m.f.StartMs >= start).ToList();
            if (after.Count == 0) return;
            double startLag = after[0].f.EndMs - start;
            double stopLag  = after[after.Count - 1].f.EndMs - end;

            // Input is constant-rate, so ideally so is the turn rate.  Skip the
            // first 300 ms (acceleration curves, first-frame effects).
            var steady = after.Where(m => m.f.StartMs >= start + 300 && m.f.StartMs <= end)
                              .Select(m => m.rate).ToList();
            string cv = "n/a";
            if (steady.Count > 2)
            {
                double mean = steady.Average();
                double sd   = Math.Sqrt(steady.Sum(r => (r - mean) * (r - mean)) / steady.Count);
                cv = $"{100 * sd / mean:F1}%  (mean {mean:F1} deg/s, n={steady.Count})";
            }
            RhinoApp.WriteLine($"  robot: start lag {startLag:F1} ms   stop lag {stopLag:F1} ms   turn-rate variation {cv}");
        }

        static double Pct(List<double> values, double p)
        {
            if (values.Count == 0) return 0;
            var s = values.OrderBy(v => v).ToList();
            double idx = (p / 100.0) * (s.Count - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return s[lo] + (s[hi] - s[lo]) * (idx - lo);
        }

        string WriteCsv(string label)
        {
            var dir = Path.Combine(Path.GetTempPath(), "WalkMode");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"bench-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("tag,viewport,start_ms,end_ms,draw_ms,dynamic,near,far,cam_x,cam_y,cam_z,dir_x,dir_y,dir_z\n");
            foreach (var f in _frames.OrderBy(f => f.StartMs))
            {
                sb.AppendLine(string.Join(",",
                    f.Tag, f.Viewport,
                    f.StartMs.ToString("F3", ci), f.EndMs.ToString("F3", ci),
                    (f.EndMs - f.StartMs).ToString("F3", ci), f.Dynamic ? "1" : "0",
                    f.Near.ToString("R", ci), f.Far.ToString("R", ci),
                    f.CamPos.X.ToString("R", ci), f.CamPos.Y.ToString("R", ci), f.CamPos.Z.ToString("R", ci),
                    f.CamDir.X.ToString("R", ci), f.CamDir.Y.ToString("R", ci), f.CamDir.Z.ToString("R", ci)));
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
