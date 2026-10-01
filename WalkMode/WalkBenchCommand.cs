using System;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace WalkMode
{
    /// <summary>
    /// Toggles frame recording.  Run once, navigate (Ctrl+Alt+RMB and/or
    /// WalkMode), run again to print a comparison and write a CSV.
    /// </summary>
    public class WalkBenchCommand : Command
    {
        public override string EnglishName => "WalkBench";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (!FrameRecorder.IsRecording)
            {
                FrameRecorder.Start();
                RhinoApp.WriteLine("WalkBench: recording. Navigate natively and/or run WalkMode, then run WalkBench again.");
                return Result.Success;
            }

            FrameRecorder.Stop().Report("interactive");
            return Result.Success;
        }
    }

    /// <summary>
    /// Switches WalkMode's mouse-look source between cursor warping and raw
    /// device input, so both can be recorded in one WalkBench session.
    /// </summary>
    public class WalkInputModeCommand : Command
    {
        public override string EnglishName => "WalkInputMode";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var input = new Rhino.Input.Custom.OptionToggle(WalkGetter.UseRawInput,    "Cursor",     "Raw");
            var look  = new Rhino.Input.Custom.OptionToggle(WalkGetter.UseMouseNav,    "Projection", "MouseNav");

            var go = new Rhino.Input.Custom.GetOption();
            go.SetCommandPrompt("WalkMode input settings (Enter when done)");
            go.AcceptNothing(true);
            go.AddOptionToggle("Input", ref input);
            go.AddOptionToggle("Camera", ref look);
            while (go.Get() == Rhino.Input.GetResult.Option) { }

            WalkGetter.UseRawInput    = input.CurrentValue;
            WalkGetter.UseMouseNav    = look.CurrentValue;
            RhinoApp.WriteLine(
                $"WalkMode input: {(input.CurrentValue ? "Raw" : "Cursor")}, camera: {(look.CurrentValue ? "MouseNav" : "Projection")}");
            return Result.Success;
        }
    }

    /// <summary>
    /// Scripted test with no hand input: how fast can the active view redraw
    /// while rotating, via Rhino's own SpeedTest and via WalkMode's camera
    /// path?  This is the ceiling either approach could reach.
    /// </summary>
    public class WalkBenchSyntheticCommand : Command
    {
        public override string EnglishName => "WalkBenchSynthetic";

        const int Frames = 240;


        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null || !view.ActiveViewport.IsPerspectiveProjection)
            {
                RhinoApp.WriteLine("WalkBenchSynthetic needs an active perspective view.");
                return Result.Failure;
            }

            if (FrameRecorder.IsRecording)
            {
                RhinoApp.WriteLine("WalkBench recording is running; stop it first.");
                return Result.Cancel;
            }

            var vp    = view.ActiveViewport;
            var saved = new ViewportInfo(vp);
            var savedTarget = vp.CameraTarget;
            string prevTag = FrameRecorder.Tag;

            FrameRecorder.Start();
            try
            {
                // 1. Rhino's built-in rotation speed test (same as TestMaxSpeed),
                //    with and without rebuilding the draw list every frame.
                FrameRecorder.Tag = "native-speedtest";
                view.SpeedTest(Frames, true, 0, 2.0 * Math.PI / Frames);
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                FrameRecorder.Tag = "native-speedtest-unfrozen";
                view.SpeedTest(Frames, false, 0, 2.0 * Math.PI / Frames);
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                var dir = vp.CameraDirection;
                dir.Unitize();
                double yaw        = Math.Atan2(dir.X, dir.Y);
                double pitch      = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.Z)));
                double upm        = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
                double targetDist = Math.Max(vp.CameraLocation.DistanceTo(vp.CameraTarget), 10.0 * upm);
                var    pos        = vp.CameraLocation;
                var    rec        = FrameRecorder.Active;

                // 2. WalkMode's camera path (forced near/far): one full turn in place.
                FrameRecorder.Tag = "walk-synthetic";
                for (int i = 1; i <= Frames; i++)
                {
                    double t0 = rec.NowMs;
                    WalkGetter.ApplyCamera(vp, pos, yaw + 2.0 * Math.PI * i / Frames, pitch,
                                           targetDist, 0.1 * upm);
                    double t1 = rec.NowMs;
                    WalkGetter.RedrawNow(view);
                    FrameRecorder.Sample("walk.apply ms",  t1 - t0);
                    FrameRecorder.Sample("walk.redraw ms", rec.NowMs - t1);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                // 3. Same in-place turn through other camera APIs.  If one of
                //    these draws near static-redraw cost, that's the way to
                //    move the camera without the per-change penalty.
                var b      = vp.Bounds;
                var centre = new System.Drawing.Point(b.Width / 2, b.Height / 2);

                // 3a. What Ctrl+Alt+RMB drag calls.
                FrameRecorder.Tag = "walk-mouserotate";
                for (int i = 1; i <= Frames; i++)
                {
                    vp.MouseRotateCamera(centre, new System.Drawing.Point(centre.X + 8, centre.Y));
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                // 3b. MouseRotateCamera, then force near/far like walk's
                //     mm/cm clipping fix.  Does touching the frustum bring the
                //     penalty back?
                FrameRecorder.Tag = "look-mrc+nearfix";
                for (int i = 1; i <= Frames; i++)
                {
                    vp.MouseRotateCamera(centre, new System.Drawing.Point(centre.X + 8, centre.Y));
                    var vi = new ViewportInfo(vp);
                    vi.SetFrustumNearFar(0.1 * upm, Math.Max(vi.FrustumFar, 0.1 * upm * 1e6));
                    vp.SetViewProjection(vi, true);
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                // 4. Moving the camera position — which calls avoid the penalty?
                var fwd  = new Rhino.Geometry.Vector3d(Math.Sin(yaw), Math.Cos(yaw), 0);
                double step = 0.03 * upm;   // ~1.8 m/s at 60 fps

                FrameRecorder.Tag = "move-setlocation";
                for (int i = 1; i <= Frames; i++)
                {
                    vp.SetCameraLocation(pos + fwd * (step * i), true);
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                FrameRecorder.Tag = "move-inoutdolly";
                for (int i = 1; i <= Frames; i++)
                {
                    vp.MouseInOutDolly(centre, new System.Drawing.Point(centre.X, centre.Y - 4));
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                FrameRecorder.Tag = "move-lateraldolly";
                for (int i = 1; i <= Frames; i++)
                {
                    vp.MouseLateralDolly(centre, new System.Drawing.Point(centre.X + 4, centre.Y));
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                // 4. Our redraw path, but the speed test's motion (orbit the
                //    target).  ≈ native-speedtest → our redraw path is fine and
                //    the cost is what the in-place turn looks at.
                FrameRecorder.Tag = "walk-orbit";
                var target = vp.CameraTarget;
                for (int i = 1; i <= Frames; i++)
                {
                    var xf  = Rhino.Geometry.Transform.Rotation(2.0 * Math.PI * i / Frames,
                                  Rhino.Geometry.Vector3d.ZAxis, target);
                    var loc = pos;
                    loc.Transform(xf);
                    vp.SetCameraLocations(target, loc);
                    WalkGetter.RedrawNow(view);
                }
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);

                // 5. No camera change at all: the fixed cost of one redraw.
                FrameRecorder.Tag = "static-redraw";
                for (int i = 1; i <= 60; i++)
                    WalkGetter.RedrawNow(view);
            }
            finally
            {
                FrameRecorder.Tag = prevTag;
                var rec = FrameRecorder.Stop();
                vp.SetViewProjection(saved, false);
                vp.SetCameraTarget(savedTarget, false);
                view.Redraw();
                rec.Report("synthetic");
            }

            return Result.Success;
        }
    }
}
