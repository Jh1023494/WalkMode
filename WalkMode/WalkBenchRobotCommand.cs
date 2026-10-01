using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace WalkMode
{
    /// <summary>
    /// Drives the view with scripted, constant-rate mouse input (SendInput) so
    /// native navigation and WalkMode can be compared on identical input:
    /// start/stop lag and how evenly the camera turns.
    ///   Native: holds Ctrl+Alt+RMB and sweeps the mouse.
    ///   Walk:   runs WalkMode, sweeps the mouse, then presses Esc.
    ///   WalkMove: same, holding W the whole sweep (movement + look).
    ///   Forward/Backward/Left/Right: hold W/S/A/D without mouse movement.
    /// Keep hands off the mouse and keyboard while it runs (~3 s).
    /// </summary>
    public class WalkBenchRobotCommand : Command
    {
        public override string EnglishName => "WalkBenchRobot";

        const int    SweepMs     = 3000;
        const double PxPerSecond = 400;
        const int    StepMs      = 2;

        // ── SendInput ─────────────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref System.Drawing.Point p);
        [DllImport("winmm.dll")]  static extern int  timeBeginPeriod(int ms);
        [DllImport("winmm.dll")]  static extern int  timeEndPeriod(int ms);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_W = 0x57, VK_S = 0x53, VK_A = 0x41, VK_D = 0x44;

        static void Send(INPUT i) => SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());

        static void Mouse(uint flags, int dx = 0, int dy = 0) =>
            Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } });

        static void Key(ushort vk, bool up) =>
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } });

        /// <summary>Constant-rate horizontal sweep, paced by wall clock so sleep jitter doesn't change the rate.</summary>
        static void Sweep(FrameRecorder rec)
        {
            var sw = Stopwatch.StartNew();
            int sent = 0;
            rec.MarkRobotStart();
            while (sw.ElapsedMilliseconds < SweepMs)
            {
                int target = (int)(sw.Elapsed.TotalSeconds * PxPerSecond);
                if (target > sent) { Mouse(MOUSEEVENTF_MOVE, target - sent, 0); sent = target; }
                Thread.Sleep(StepMs);
            }
            rec.MarkRobotEnd();
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null || !view.ActiveViewport.IsPerspectiveProjection)
            {
                RhinoApp.WriteLine("WalkBenchRobot needs an active perspective view.");
                return Result.Failure;
            }
            if (FrameRecorder.IsRecording)
            {
                RhinoApp.WriteLine("WalkBench recording is running; stop it first.");
                return Result.Cancel;
            }

            var go = new GetOption();
            go.SetCommandPrompt("Scripted input test");
            int optNative = go.AddOption("Native");
            int optWalk   = go.AddOption("Walk");
            int optMove   = go.AddOption("WalkMove");
            int optForward = go.AddOption("Forward");
            int optBackward = go.AddOption("Backward");
            int optLeft = go.AddOption("Left");
            int optRight = go.AddOption("Right");
            if (go.Get() != GetResult.Option) return Result.Cancel;
            int selected = go.OptionIndex();
            bool walk = selected != optNative;
            bool moveOnly = selected == optForward || selected == optBackward
                         || selected == optLeft || selected == optRight;
            ushort moveKey = selected == optBackward ? VK_S
                           : selected == optLeft ? VK_A : selected == optRight ? VK_D : VK_W;
            bool holdMove = moveOnly || selected == optMove;
            string label = selected == optForward ? "robot-forward" : selected == optBackward ? "robot-backward"
                         : selected == optLeft ? "robot-left" : selected == optRight ? "robot-right" : "robot-walk";

            // Put the cursor in the middle of the view so native navigation grabs it.
            var b  = view.ActiveViewport.Bounds;
            var pt = new System.Drawing.Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            ClientToScreen(view.Handle, ref pt);
            SetCursorPos(pt.X, pt.Y);

            FrameRecorder.Start();
            var rec = FrameRecorder.Active;

            if (!walk)
            {
                FrameRecorder.Tag = "robot-native";
                var t = new Thread(() =>
                {
                    timeBeginPeriod(1);
                    try
                    {
                        Thread.Sleep(300);
                        Key(VK_CONTROL, false);
                        Key(VK_MENU, false);
                        Mouse(MOUSEEVENTF_RIGHTDOWN);
                        Thread.Sleep(100);
                        Sweep(rec);
                        Mouse(MOUSEEVENTF_RIGHTUP);
                        Key(VK_MENU, true);
                        Key(VK_CONTROL, true);
                        Thread.Sleep(500);   // let the tail frames land
                    }
                    finally
                    {
                        timeEndPeriod(1);
                        RhinoApp.InvokeOnUiThread(new Action(() =>
                        {
                            FrameRecorder.Tag = "native";
                            FrameRecorder.Stop()?.Report("robot-native");
                        }));
                    }
                }) { IsBackground = true };
                t.Start();
                RhinoApp.WriteLine("WalkBenchRobot: sweeping natively, hands off for ~4 s...");
                return Result.Success;
            }

            // Walk: input thread waits for the walk pump, sweeps, then Esc (restores the view).
            var input = new Thread(() =>
            {
                timeBeginPeriod(1);
                try
                {
                    var wait = Stopwatch.StartNew();
                    while (!WalkGetter.IsRunning && wait.ElapsedMilliseconds < 60000) Thread.Sleep(5);
                    if (!WalkGetter.IsRunning) return;
                    Thread.Sleep(300);
                    if (holdMove) Key(moveKey, false);
                    try
                    {
                        if (moveOnly)
                        {
                            rec.MarkRobotStart();
                            Thread.Sleep(SweepMs);
                            rec.MarkRobotEnd();
                        }
                        else Sweep(rec);
                    }
                    finally { if (holdMove) Key(moveKey, true); }
                    Thread.Sleep(500);
                    Key(VK_ESCAPE, false);
                    Key(VK_ESCAPE, true);
                }
                finally { timeEndPeriod(1); }
            }) { IsBackground = true };
            input.Start();

            try
            {
                using (var walker = new WalkGetter(doc, view))
                    walker.RunWalk();
            }
            finally
            {
                FrameRecorder.Stop()?.Report(label);
            }
            return Result.Success;
        }
    }
}
