using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WalkMode
{
    internal sealed class WalkGetter : IDisposable
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────
        [DllImport("user32.dll")]  static extern bool   SetCursorPos(int x, int y);
        [DllImport("user32.dll", EntryPoint = "ShowCursor")]
        static extern int ShowCursorWin32(bool bShow);
        [DllImport("user32.dll")]  static extern bool   ClientToScreen(IntPtr hWnd, ref System.Drawing.Point lpPoint);
        [DllImport("user32.dll")]  static extern bool   GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]  static extern bool   ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        static extern bool ClipCursorNone(IntPtr lpRect);
        [DllImport("user32.dll")]  static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]  static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]  static extern bool   UpdateWindow(IntPtr hWnd);

        // Raw input
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint   Flags;
            public IntPtr Target;
        }

        const uint RID_INPUT             = 0x10000003;
        const uint RIM_TYPEMOUSE         = 0;
        const uint RIDEV_REMOVE          = 0x00000001;
        const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
        // RAWINPUTHEADER: DWORD dwType, DWORD dwSize, HANDLE hDevice, WPARAM wParam
        static readonly int RawHeaderSize = 8 + 2 * IntPtr.Size;
        [DllImport("winmm.dll")]   static extern int    timeBeginPeriod(int uPeriod);
        [DllImport("winmm.dll")]   static extern int    timeEndPeriod(int uPeriod);

        // Message pump P/Invokes
        [DllImport("user32.dll")]
        static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        static extern uint MsgWaitForMultipleObjectsEx(
            uint nCount,
            IntPtr pHandles,
            uint dwMilliseconds,
            uint dwWakeMask,
            uint dwFlags);

        const uint PM_REMOVE = 0x0001;
        const uint INFINITE = 0xFFFFFFFF;
        const uint QS_ALLINPUT = 0x04FF;
        const uint MWMO_INPUTAVAILABLE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint   message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint   time;
            public POINT  pt;
        }

        // Window message codes
        const uint WM_INPUT        = 0x00FF;
        const uint WM_KEYDOWN      = 0x0100;
        const uint WM_KEYUP        = 0x0101;
        const uint WM_SYSKEYDOWN   = 0x0104;
        const uint WM_SYSKEYUP     = 0x0105;
        const uint WM_NCMOUSEFIRST = 0x00A0;
        const uint WM_NCMOUSELAST  = 0x00AD;
        const uint WM_MOUSEFIRST   = 0x0200;
        const uint WM_RBUTTONDOWN  = 0x0204;
        const uint WM_RBUTTONUP    = 0x0205;
        const uint WM_MOUSEWHEEL   = 0x020A;
        const uint WM_MOUSELAST    = 0x020E;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        // ── Context ───────────────────────────────────────────────────────────
        readonly RhinoDoc      _doc;
        readonly RhinoView     _view;
        readonly RhinoViewport _vp;

        // ── Camera state ──────────────────────────────────────────────────────
        double  _yaw, _pitch;
        Point3d _camPos;

        // ── Cancel restore ────────────────────────────────────────────────────
        readonly ViewportInfo _savedViewport;
        readonly Point3d _savedCameraTarget;

        // ── Unit scale (doc units per metre) ─────────────────────────────────
        double _unitsPerMeter;

        // ── Speed (doc units / s) ─────────────────────────────────────────────
        double _baseSpeed;

        // ── Cursor / screen centre ────────────────────────────────────────────
        System.Drawing.Point     _screenCenter;
        RECT                     _screenClip;
        System.Drawing.Rectangle _lastBounds;
        bool                     _hadFocus;
        readonly uint            _pid;

        // ── Frame timing ──────────────────────────────────────────────────────
        const int FrameIntervalMs = 4;
        // Yield to the camera even when a high-polling-rate device keeps the
        // queue busy. Messages left over are processed on the next pass.
        const double MessagePumpBudgetMs = 2;
        const double LookSensitivity    = 0.0025;   // rad per cursor pixel
        // Raw counts skip Windows pointer speed; the robot bench measured raw
        // turning ~1.7x faster than cursor mode on the same input, so scale
        // down to keep the feel tuned in cursor mode.
        const double RawLookSensitivity = 0.0015;   // rad per raw count
        readonly Stopwatch _sw = Stopwatch.StartNew();
        long _lastTickMs;

        // ── Input state — written by the message filter, read by physics tick ─
        bool _kW, _kA, _kS, _kD, _kQ, _kE, _kShift, _kAlt;
        bool _tabEdge, _vEdge, _spaceEdge, _cEdge, _xEdge;
        bool _kTabDown, _kVDown, _kSpaceDown, _kCDown, _kXDown;
        int  _lensStep;   // +1 / -1 per bracket press, applied in the tick
        bool _rButtonDown;

        // ── Exit flags (replace GetPoint's Get() return) ─────────────────────
        bool _exitRequested;
        bool _cancelled;

        // ── Gravity ───────────────────────────────────────────────────────────
        bool   _gravityEnabled;
        double _verticalVelocity;
        bool   _grounded;
        double _lastFloorZ;
        bool   _lastFloorValid;

        // ── Teleport ──────────────────────────────────────────────────────────
        bool     _teleporting;
        Point3d  _teleportOrigin, _teleportTarget;
        DateTime _teleportStart;
        double   _teleportDuration;

        // ── Human-scale walking (all in doc units) ────────────────────────────
        double _eyeStanding, _eyeSeated;   // eye level above the floor
        double _eyeCur;                    // eased toward the active one
        bool   _seated;
        double _stepHeight;
        double _bodyRadius;
        double _walkSpeed;                 // used while walking; _baseSpeed while flying
        bool   _collision;
        bool   _stepSettling;              // easing onto a stair tread
        double _lensMm;                    // current 35 mm lens length

        double EyeTarget => _seated ? _eyeSeated : _eyeStanding;

        // ── Scene geometry ────────────────────────────────────────────────────
        Mesh[]        _meshGeometry;
        BoundingBox[] _meshBoxes;
        bool     _geometryDirty;
        DateTime _lastGeometryRebuild;

        // ── Render state ────────────────────────────────────────────────────
        bool _redrawPending;

        // ── Near clip plane (doc units) ───────────────────────────────────────
        double _nearClip;

        // ── Doc event delegates ─────────────────────────────────────────────
        EventHandler<RhinoObjectEventArgs>                 _objHandler;
        EventHandler<RhinoReplaceObjectEventArgs>          _replaceHandler;
        EventHandler<RhinoModifyObjectAttributesEventArgs> _attrHandler;
        EventHandler<LayerTableEventArgs>                  _layerHandler;

        // ── Display conduit ───────────────────────────────────────────────────
        WalkConduit _conduit;

        // ── Loading state ─────────────────────────────────────────────────────
        bool _loading;

        // ── Disposal guard ────────────────────────────────────────────────────
        bool _cleaned;

        // ── Sticky speed ──────────────────────────────────────────────────────
        static double s_lastSpeed = 0;

        // ── Mouse input method (toggled by the WalkInputMode command) ─────────
        // false: read the cursor each frame and warp it back to centre.
        // true:  accumulate WM_INPUT deltas straight from the device.
        internal static bool UseRawInput = true;

        /// <summary>True while the walk message pump is running (read by WalkBenchRobot).</summary>
        internal static volatile bool IsRunning;

        /// <summary>Move/look via Rhino mouse-nav calls (toggled by WalkInputMode for A/B).</summary>
        internal static bool UseMouseNav = true;
        int  _rawDx, _rawDy;
        bool _rawRegistered;
        IntPtr _rawInputBuffer;
        const int RawInputBufferSize = 64;

        // ── Display conduit for crosshair + status HUD ────────────────────────
        sealed class WalkConduit : DisplayConduit
        {
            readonly WalkGetter _owner;
            readonly Guid       _vpId;

            public WalkConduit(WalkGetter owner)
            {
                _owner = owner;
                _vpId  = owner._vp.Id;
            }

            protected override void DrawForeground(DrawEventArgs e)
            {
                if (e.Viewport.Id != _vpId) return;

                if (_owner._loading)
                {
                    int cx = _owner._vp.Bounds.Width  / 2;
                    int cy = _owner._vp.Bounds.Height / 2;
                    e.Display.Draw2dText("Loading geometry...",
                        System.Drawing.Color.White, new Point2d(cx - 60, cy + 30), false, 15);
                    return;
                }

                var rec = FrameRecorder.Active;
                double t0 = rec?.NowMs ?? 0;
                DrawCrosshair(e);
                DrawStatus(e);
                if (rec != null) FrameRecorder.Sample("walk.hud ms", rec.NowMs - t0);
            }

            void DrawCrosshair(DrawEventArgs e)
            {
                int cx = _owner._vp.Bounds.Width  / 2;
                int cy = _owner._vp.Bounds.Height / 2;

                const int   arm = 12;
                const int   gap = 4;
                const float thk = 1.5f;
                var white = System.Drawing.Color.White;

                e.Display.Draw2dLine(
                    new System.Drawing.PointF(cx,       cy - gap),
                    new System.Drawing.PointF(cx,       cy - gap - arm), white, thk);
                e.Display.Draw2dLine(
                    new System.Drawing.PointF(cx,       cy + gap),
                    new System.Drawing.PointF(cx,       cy + gap + arm), white, thk);
                e.Display.Draw2dLine(
                    new System.Drawing.PointF(cx - gap, cy),
                    new System.Drawing.PointF(cx - gap - arm, cy),       white, thk);
                e.Display.Draw2dLine(
                    new System.Drawing.PointF(cx + gap, cy),
                    new System.Drawing.PointF(cx + gap + arm, cy),       white, thk);
                e.Display.Draw2dLine(
                    new System.Drawing.PointF(cx - 1, cy),
                    new System.Drawing.PointF(cx + 1, cy), white, 2.0f);
            }

            void DrawStatus(DrawEventArgs e)
            {
                var    o       = _owner;
                double u       = o._unitsPerMeter;
                double speedMs = o.CurrentBaseSpeed / u;
                string text;
                if (o._gravityEnabled)
                {
                    string pose = o._seated ? "seated" : "standing";
                    string coll = o._collision ? "Walls ON" : "Walls OFF";
                    text = $"WALK   Eye {o.EyeTarget / u:F2} m ({pose})   {coll}   Speed {speedMs:F1} m/s   Lens {o._lensMm:F0} mm";
                    if (!o._lastFloorValid && !o._grounded) text += "   (no floor below)";
                }
                else
                {
                    text = $"FLY   Speed {speedMs:F1} m/s   Lens {o._lensMm:F0} mm";
                }
                int y = o._vp.Bounds.Height - 24;
                // Dark outline keeps the HUD readable over bright walls.
                var shadow = System.Drawing.Color.FromArgb(160, 0, 0, 0);
                e.Display.Draw2dText(text, shadow, new Point2d(11, y + 1), false, 13);
                e.Display.Draw2dText(text, System.Drawing.Color.White, new Point2d(10, y), false, 13);
                const string keys = "WASD move · Shift run · Alt slow · C seated · X walls · [ ] lens · Tab walk/fly · V jump · Space teleport";
                e.Display.Draw2dText(keys, shadow, new Point2d(11, y - 19), false, 11);
                e.Display.Draw2dText(keys, System.Drawing.Color.Gainsboro, new Point2d(10, y - 20), false, 11);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        public WalkGetter(RhinoDoc doc, RhinoView view)
        {
            _doc  = doc;
            _view = view;
            _vp   = view.ActiveViewport;

            _savedViewport = new ViewportInfo(_vp);
            _savedCameraTarget = _vp.CameraTarget;

            _unitsPerMeter = RhinoMath.UnitScale(UnitSystem.Meters, _doc.ModelUnitSystem);

            if (s_lastSpeed > 0)
            {
                _baseSpeed = s_lastSpeed;
            }
            else
            {
                double camDist = _vp.CameraLocation.DistanceTo(_vp.CameraTarget);
                _baseSpeed = Math.Max( 1.5 * _unitsPerMeter,
                             Math.Min(camDist / 50.0,
                                      50.0 * _unitsPerMeter));
            }

            var dir = _vp.CameraDirection;
            dir.Unitize();
            _yaw   = Math.Atan2(dir.X, dir.Y);
            _pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.Z)));
            _camPos = _vp.CameraLocation;

            double u = _unitsPerMeter;
            _eyeStanding    = WalkSettings.EyeHeight       * u;
            _eyeSeated      = WalkSettings.SeatedEyeHeight * u;
            _eyeCur         = _eyeStanding;
            _stepHeight     = WalkSettings.StepHeight      * u;
            _bodyRadius     = WalkSettings.BodyRadius      * u;
            _walkSpeed      = WalkSettings.WalkSpeed       * u;
            _collision      = WalkSettings.Collision;
            _gravityEnabled = WalkSettings.StartWalking;
            _lensMm         = _vp.Camera35mmLensLength;

            _lastBounds = _vp.Bounds;
            // Closer than the body radius, so walls you stand against never clip.
            _nearClip = Math.Min(0.1 * u, 0.5 * _bodyRadius);
            ComputeScreenCenter();

            using (var proc = Process.GetCurrentProcess())
                _pid = (uint)proc.Id;

            _conduit = new WalkConduit(this);
            _conduit.Enabled = true;
            _redrawPending = true;
            // Only gravity uses the collision meshes. Fly mode and teleport
            // (which uses the depth buffer) can start without building them.
            _geometryDirty = true;

            _objHandler     = (s, e) => _geometryDirty = true;
            _replaceHandler = (s, e) => _geometryDirty = true;
            _attrHandler    = (s, e) => _geometryDirty = true;
            _layerHandler   = (s, e) => _geometryDirty = true;
            RhinoDoc.AddRhinoObject         += _objHandler;
            RhinoDoc.DeleteRhinoObject      += _objHandler;
            RhinoDoc.ReplaceRhinoObject     += _replaceHandler;
            RhinoDoc.ModifyObjectAttributes += _attrHandler;
            RhinoDoc.LayerTableEvent        += _layerHandler;

            _lastTickMs = _sw.ElapsedMilliseconds;

            timeBeginPeriod(1);
        }

        // ── Geometry cache ────────────────────────────────────────────────────
        void BuildGeometryCache()
        {
            var meshes = new List<Mesh>();
            // VisibleFilter also drops objects on layers that are turned off.
            var settings = new ObjectEnumeratorSettings
            {
                VisibleFilter     = true,
                HiddenObjects     = false,
                LockedObjects     = true,
                NormalObjects     = true,
                ReferenceObjects  = true,
                IncludeLights     = false,
                IncludeGrips      = false,
                ObjectTypeFilter  = ObjectType.Mesh | ObjectType.Brep | ObjectType.Surface
                                  | ObjectType.Extrusion | ObjectType.SubD | ObjectType.InstanceReference,
            };
            CollectGeometry(_doc.Objects.GetObjectList(settings), Transform.Identity, meshes);
            _meshGeometry  = meshes.ToArray();
            _meshBoxes     = new BoundingBox[_meshGeometry.Length];
            for (int i = 0; i < _meshGeometry.Length; i++)
                _meshBoxes[i] = _meshGeometry[i].GetBoundingBox(false);
            _geometryDirty = false;
        }

        void CollectGeometry(IEnumerable<RhinoObject> objects, Transform xform,
                             List<Mesh> meshes)
        {
            foreach (var obj in objects)
            {
                if (!obj.Visible) continue;

                if (obj is InstanceObject inst)
                {
                    var childXform = xform * inst.InstanceXform;
                    CollectGeometry(inst.InstanceDefinition.GetObjects(), childXform, meshes);
                    continue;
                }

                var geom = obj.Geometry;
                Mesh[] source = null;

                if (geom is Mesh m)
                {
                    source = new[] { m };
                }
                else if (geom is Brep || geom is Surface || geom is Extrusion || geom is SubD)
                {
                    // Extrusions are what Rhino's Box/slab commands make: without
                    // them gravity fell straight through most architectural floors.
                    source = obj.GetMeshes(MeshType.Render);
                    if (source == null || source.Length == 0)
                        source = FallbackMesh(geom);
                }

                if (source == null) continue;
                foreach (var rm in source)
                {
                    if (rm == null) continue;
                    if (xform.IsIdentity)
                    {
                        meshes.Add(rm);
                    }
                    else
                    {
                        var dup = (Mesh)rm.Duplicate();
                        dup.Transform(xform);
                        meshes.Add(dup);
                    }
                }
            }
        }

        static Mesh[] FallbackMesh(GeometryBase geom)
        {
            var mp = MeshingParameters.FastRenderMesh;
            switch (geom)
            {
                case Brep b:      return Mesh.CreateFromBrep(b, mp);
                case Extrusion x: var xb = x.ToBrep(); return xb == null ? null : Mesh.CreateFromBrep(xb, mp);
                case SubD s:      var sm = Mesh.CreateFromSubD(s, 2); return sm == null ? null : new[] { sm };
                case Surface f:   var fm = Mesh.CreateFromSurface(f, mp); return fm == null ? null : new[] { fm };
            }
            return null;
        }

        /// <summary>Slab test: does the ray hit the box within maxDist?</summary>
        static bool RayHitsBox(in BoundingBox b, Point3d o, Vector3d d, double maxDist)
        {
            double tMin = 0, tMax = maxDist;
            for (int axis = 0; axis < 3; axis++)
            {
                double oa = o[axis], da = d[axis], lo = b.Min[axis], hi = b.Max[axis];
                if (Math.Abs(da) < 1e-12)
                {
                    if (oa < lo || oa > hi) return false;
                    continue;
                }
                double t1 = (lo - oa) / da, t2 = (hi - oa) / da;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }
            return true;
        }

        static Vector3d FaceNormal(Mesh mesh, int faceIndex)
        {
            if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) return Vector3d.Unset;
            var f = mesh.Faces[faceIndex];
            var v = mesh.Vertices;
            Point3d a = v[f.A], b = v[f.B], c = v[f.C];
            var n = f.IsQuad
                ? Vector3d.CrossProduct(c - a, (Point3d)v[f.D] - b)
                : Vector3d.CrossProduct(b - a, c - a);
            return n.Unitize() ? n : Vector3d.Unset;
        }

        void EnsureGeometry()
        {
            if (_meshGeometry == null || _geometryDirty)
            {
                var now = DateTime.UtcNow;
                if (_meshGeometry == null || (now - _lastGeometryRebuild).TotalSeconds >= 0.5)
                {
                    _loading = true;
                    try
                    {
                        RedrawNow(_view);
                        BuildGeometryCache();
                        _lastGeometryRebuild = now;
                    }
                    finally
                    {
                        _loading = false;
                        _redrawPending = true;
                    }
                }
            }
        }

        Point3d? ShootRay(Ray3d ray) => ShootRay(ray, double.MaxValue, out _, out _);

        /// <summary>
        /// Nearest hit along a ray within maxDist (doc units). Meshes whose
        /// bounding box the ray misses are skipped before the exact test, so
        /// the several probes per frame stay cheap in large models.
        /// </summary>
        Point3d? ShootRay(Ray3d ray, double maxDist, out double hitDist, out Vector3d normal)
        {
            EnsureGeometry();
            hitDist = double.MaxValue;
            normal  = Vector3d.Unset;

            var dir = ray.Direction;
            if (!dir.Unitize() || _meshGeometry == null) return null;
            var unitRay = new Ray3d(ray.Position, dir);

            Mesh bestMesh = null;
            int  bestFace = -1;
            for (int i = 0; i < _meshGeometry.Length; i++)
            {
                double limit = Math.Min(maxDist, hitDist);
                if (!RayHitsBox(_meshBoxes[i], unitRay.Position, dir, limit)) continue;
                double t = Intersection.MeshRay(_meshGeometry[i], unitRay, out int[] faces);
                if (t >= 0 && t < limit)
                {
                    hitDist  = t;
                    bestMesh = _meshGeometry[i];
                    bestFace = faces != null && faces.Length > 0 ? faces[0] : -1;
                }
            }

            if (bestMesh == null) return null;
            normal = FaceNormal(bestMesh, bestFace);
            return unitRay.Position + dir * hitDist;
        }

        void ComputeScreenCenter()
        {
            var bounds = _vp.Bounds;
            var pt = new System.Drawing.Point(
                bounds.X + bounds.Width  / 2,
                bounds.Y + bounds.Height / 2);
            var tl = new System.Drawing.Point(bounds.Left,  bounds.Top);
            var br = new System.Drawing.Point(bounds.Right, bounds.Bottom);
            if (_view.Handle != IntPtr.Zero)
            {
                ClientToScreen(_view.Handle, ref pt);
                ClientToScreen(_view.Handle, ref tl);
                ClientToScreen(_view.Handle, ref br);
            }
            _screenCenter = pt;
            _screenClip   = UseRawInput
                // Raw deltas don't need the cursor to move: pin it in place.
                ? new RECT { left = pt.X, top = pt.Y, right = pt.X + 1, bottom = pt.Y + 1 }
                : new RECT { left = tl.X, top = tl.Y, right = br.X, bottom = br.Y };
        }

        bool RegisterRawMouse(bool register)
        {
            var dev = new[]
            {
                new RAWINPUTDEVICE
                {
                    UsagePage = 0x01,   // generic desktop
                    Usage     = 0x02,   // mouse
                    Flags     = register ? 0 : RIDEV_REMOVE,
                    Target    = register ? _view.Handle : IntPtr.Zero,
                }
            };
            return RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        void ReadRawInput(IntPtr hRawInput)
        {
            // Reuse storage: gaming mice can send thousands of packets/sec.
            // RAWINPUTHEADER + RAWMOUSE fits in 64 bytes on both bitnesses.
            if (_rawInputBuffer == IntPtr.Zero)
                _rawInputBuffer = Marshal.AllocHGlobal(RawInputBufferSize);
            uint size = RawInputBufferSize;
            uint got = GetRawInputData(hRawInput, RID_INPUT, _rawInputBuffer, ref size, (uint)RawHeaderSize);
            if (got == uint.MaxValue || got < RawHeaderSize + 20) return;
            if ((uint)Marshal.ReadInt32(_rawInputBuffer, 0) != RIM_TYPEMOUSE) return;

            // RAWMOUSE: USHORT usFlags, (pad), ULONG buttons, ULONG rawButtons, LONG lLastX, LONG lLastY
            ushort flags = (ushort)Marshal.ReadInt16(_rawInputBuffer, RawHeaderSize);
            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0) return;   // tablets / remote desktop
            _rawDx += Marshal.ReadInt32(_rawInputBuffer, RawHeaderSize + 12);
            _rawDy += Marshal.ReadInt32(_rawInputBuffer, RawHeaderSize + 16);
        }

        bool RhinoHasFocus()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == _pid;
        }

        void ReleaseHeldKeys()
        {
            _kW = _kA = _kS = _kD = _kQ = _kE = _kShift = _kAlt = false;
            _kTabDown = _kVDown = _kSpaceDown = _kCDown = _kXDown = _rButtonDown = false;
        }

        /// <summary>Walking keeps its own human pace; flying keeps the scene-scaled one.</summary>
        double CurrentBaseSpeed => _gravityEnabled ? _walkSpeed : _baseSpeed;

        void OnWheelDelta(int delta)
        {
            double u = _unitsPerMeter;
            if (_gravityEnabled)
            {
                _walkSpeed *= delta > 0 ? 1.1 : 1 / 1.1;
                _walkSpeed = Math.Max(0.3 * u, Math.Min(_walkSpeed, 6.0 * u));
                WalkSettings.WalkSpeed = _walkSpeed / u;
            }
            else
            {
                if (delta > 0) _baseSpeed *= 1.2;
                else           _baseSpeed /= 1.2;
                _baseSpeed = Math.Max(0.1 * u, Math.Min(_baseSpeed, 100.0 * u));
                s_lastSpeed = _baseSpeed;
            }
            _redrawPending = true;
        }

        // ── Input message filter ──────────────────────────────────────────────
        // We own the message pump, so walk input is taken straight off the
        // queue and never dispatched.  Returns true if the message was consumed.
        bool FilterInput(ref MSG msg)
        {
            uint m = msg.message;

            if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN || m == WM_KEYUP || m == WM_SYSKEYUP)
            {
                bool down = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
                switch ((Keys)(int)msg.wParam)
                {
                    case Keys.W:        _kW     = down; return true;
                    case Keys.A:        _kA     = down; return true;
                    case Keys.S:        _kS     = down; return true;
                    case Keys.D:        _kD     = down; return true;
                    case Keys.Q:        _kQ     = down; return true;
                    case Keys.E:        _kE     = down; return true;
                    case Keys.ShiftKey: _kShift = down; return true;
                    case Keys.Menu:     _kAlt   = down; return true;
                    case Keys.Tab:
                        if (down && !_kTabDown) _tabEdge = true;
                        _kTabDown = down;
                        return true;
                    case Keys.V:
                        if (down && !_kVDown) _vEdge = true;
                        _kVDown = down;
                        return true;
                    case Keys.Space:
                        if (down && !_kSpaceDown) _spaceEdge = true;
                        _kSpaceDown = down;
                        return true;
                    case Keys.C:
                        if (down && !_kCDown) _cEdge = true;
                        _kCDown = down;
                        return true;
                    case Keys.X:
                        if (down && !_kXDown) _xEdge = true;
                        _kXDown = down;
                        return true;
                    case Keys.OemOpenBrackets:
                        if (down) _lensStep--;
                        return true;
                    case Keys.OemCloseBrackets:
                        if (down) _lensStep++;
                        return true;
                    case Keys.Escape:
                        if (down) { _cancelled = true; _exitRequested = true; }
                        return true;
                    case Keys.Return:
                        if (down) _exitRequested = true;
                        return true;
                }
                return false;
            }

            if (m >= WM_MOUSEFIRST && m <= WM_MOUSELAST)
            {
                if (m == WM_RBUTTONDOWN)
                {
                    _rButtonDown = true;
                }
                else if (m == WM_RBUTTONUP)
                {
                    // Accept on release so Rhino never sees a stray button-up.
                    if (_rButtonDown) _exitRequested = true;
                }
                else if (m == WM_MOUSEWHEEL)
                {
                    OnWheelDelta((short)((long)msg.wParam >> 16));
                }
                // Mouse moves are read directly from the cursor each frame.
                return true;
            }

            return m >= WM_NCMOUSEFIRST && m <= WM_NCMOUSELAST;
        }

        // ── Physics timer tick (250 Hz) ──────────────────────────────────────
        void TickFrame(long nowMs)
        {
            // Force the Win32 cursor-display counter to exactly -1 (hidden).
            {
                int c = ShowCursorWin32(false);
                while (c > -1) c = ShowCursorWin32(false);
                while (c < -1) c = ShowCursorWin32(true);
            }

            double dt    = Math.Min((nowMs - _lastTickMs) / 1000.0, 0.1);
            _lastTickMs  = nowMs;

            // Refresh screen centre if the viewport was resized or moved
            var currentBounds = _vp.Bounds;
            if (currentBounds != _lastBounds)
            {
                _lastBounds = currentBounds;
                ComputeScreenCenter();
                if (_hadFocus) ClipCursor(ref _screenClip);
            }

            // ── Mouse look: read all motion since the last frame at once ─────
            bool hasFocus = RhinoHasFocus();
            int mdx = 0, mdy = 0;
            if (hasFocus && UseRawInput)
            {
                if (_hadFocus) { mdx = _rawDx; mdy = _rawDy; }
                else           ClipCursor(ref _screenClip);
            }
            else if (hasFocus)
            {
                if (_hadFocus && GetCursorPos(out var cur))
                {
                    mdx = cur.x - _screenCenter.X;
                    mdy = cur.y - _screenCenter.Y;
                }
                else
                {
                    ClipCursor(ref _screenClip);
                }
                if (mdx != 0 || mdy != 0 || !_hadFocus)
                    SetCursorPos(_screenCenter.X, _screenCenter.Y);
            }
            else if (_hadFocus)
            {
                // Let the user work in other apps; don't carry held keys over.
                ClipCursorNone(IntPtr.Zero);
                ReleaseHeldKeys();
            }
            _hadFocus = hasFocus;
            _rawDx = _rawDy = 0;

            if (mdx != 0 || mdy != 0)
            {
                double sens = UseRawInput ? RawLookSensitivity : LookSensitivity;
                _yaw   += mdx * sens;
                _pitch -= mdy * sens;
                const double maxPitch = 89.0 * Math.PI / 180.0;
                if (_pitch >  maxPitch) _pitch =  maxPitch;
                if (_pitch < -maxPitch) _pitch = -maxPitch;
                _redrawPending = true;
            }

            bool moved = ProcessKeyboardMovement(dt);
            if (_gravityEnabled && !_teleporting) moved |= ProcessGravity(dt);
            if (_teleporting)                     { ProcessTeleport(); moved = true; }
            ProcessToggles();

            if (moved) _redrawPending = true;

            if (_redrawPending)
            {
                _redrawPending = false;
                var rec = FrameRecorder.Active;
                double t0 = rec?.NowMs ?? 0;
                if (!NavigateViaMouse())
                {
                    FrameRecorder.Sample("walk.projection fallback", 1);
                    ApplyCamera();
                }
                double t1 = rec?.NowMs ?? 0;
                RedrawNow(_view);
                if (rec != null)
                {
                    FrameRecorder.Sample("walk.apply ms",  t1 - t0);
                    FrameRecorder.Sample("walk.redraw ms", rec.NowMs - t1);
                    FrameRecorder.Sample("walk.mouse px",  Math.Sqrt(mdx * mdx + mdy * mdy));
                }
            }
        }

        // ── Movement / physics ────────────────────────────────────────────────
        bool NeedsContinuousTick()
        {
            if (_teleporting) return true;
            if (_gravityEnabled && (!_grounded || _stepSettling)) return true;
            if (_gravityEnabled && Math.Abs(_eyeCur - EyeTarget) > 1e-6 * _unitsPerMeter) return true;
            if (_kW || _kA || _kS || _kD) return true;
            if (!_gravityEnabled && (_kQ || _kE)) return true;
            return false;
        }

        bool ProcessKeyboardMovement(double dt)
        {
            double speed = CurrentBaseSpeed;
            if (_gravityEnabled)
            {
                // A person jogs at ~2-3x walking pace, not 5x.
                if (_kShift) speed *= 2.2;
                if (_kAlt)   speed *= 0.5;
            }
            else
            {
                if (_kShift) speed *= 5.0;
                if (_kAlt)   speed *= 0.2;
            }

            var fwd   = new Vector3d( Math.Sin(_yaw),  Math.Cos(_yaw), 0);
            var right = new Vector3d( Math.Cos(_yaw), -Math.Sin(_yaw), 0);

            var move = Vector3d.Zero;
            if (_kW) move += fwd;
            if (_kS) move -= fwd;
            if (_kD) move += right;
            if (_kA) move -= right;

            if (!_gravityEnabled)
            {
                if (_kQ) move -= Vector3d.ZAxis;
                if (_kE) move += Vector3d.ZAxis;
            }

            if (move.SquareLength > 0)
            {
                move.Unitize();
                var delta = move * (speed * dt);
                if (_gravityEnabled && _collision) delta = CollideAndSlide(delta);
                if (delta.SquareLength == 0) return false;
                _camPos = _camPos + delta;
                return true;
            }
            return false;
        }

        double FeetZ => _camPos.Z - _eyeCur;

        // The tread you stand on, not the eased feet: while easing up a stair
        // the feet lag behind, which made the knee probe hit the next riser
        // and the floor probe start inside the step.
        double SupportZ => _grounded && _lastFloorValid ? _lastFloorZ : FeetZ;

        // A probe starting exactly on the floor can miss it (StepHeight 0).
        double ProbeLift => Math.Max(_stepHeight, 0.02 * _unitsPerMeter);

        /// <summary>
        /// Body as a vertical stack of probes from just above step height to
        /// the eye. Movement stops at body radius from the nearest wall and
        /// the rest of the step slides along it, so you can walk along walls
        /// and through doorways instead of sticking or passing through.
        /// </summary>
        Vector3d CollideAndSlide(Vector3d delta)
        {
            var result = Vector3d.Zero;
            var pos    = _camPos;
            for (int iter = 0; iter < 3 && delta.SquareLength > 1e-18; iter++)
            {
                double len = delta.Length;
                var dir  = delta / len;
                var side = new Vector3d(-dir.Y, dir.X, 0);

                // Just above step height: anything you can't step onto blocks you.
                double low  = SupportZ + _stepHeight + 0.01 * _unitsPerMeter;
                double high = pos.Z;
                // Knee, table, chest and eye level.
                double[] heights = low < high
                    ? new[] { low, low + (high - low) / 3, low + 2 * (high - low) / 3, high }
                    : new[] { high };

                double   best  = double.MaxValue;
                Vector3d bestN = Vector3d.Unset;
                foreach (double h in heights)
                    foreach (double o in SideOffsets)
                    {
                        var origin = new Point3d(pos.X, pos.Y, h) + side * (o * _bodyRadius);
                        // Side probes start off-centre: the body edge is closer to them.
                        double edge  = _bodyRadius * Math.Sqrt(1 - o * o);
                        if (ShootRay(new Ray3d(origin, dir), len + edge, out double t, out Vector3d n) == null) continue;
                        double gap = t - edge;
                        if (gap < best) { best = gap; bestN = n; }
                    }

                if (best >= len) { result += delta; break; }

                double allowed = Math.Max(0, best - 0.002 * _unitsPerMeter);
                result += dir * allowed;
                pos    += dir * allowed;

                // Slide: drop the component pushing into the wall (horizontal normal).
                var n2 = bestN.IsValid ? new Vector3d(bestN.X, bestN.Y, 0) : -dir;
                if (!n2.Unitize()) n2 = -dir;
                if (n2 * dir > 0) n2 = -n2;
                var rest = dir * (len - allowed);
                rest -= n2 * (rest * n2);
                // Don't let a corner bounce us backwards.
                if (rest * dir <= 0) break;
                delta = rest;
            }
            return result;
        }

        static readonly double[] SideOffsets = { 0.0, 0.7, -0.7 };

        bool ProcessGravity(double dt)
        {
            double u        = _unitsPerMeter;
            double g        = -9.8  * u;
            double terminal = -50.0 * u;
            double prevZ    = _camPos.Z;

            // Sitting down / standing up eases over ~0.3 s instead of snapping.
            double eyeGoal = EyeTarget;
            if (Math.Abs(_eyeCur - eyeGoal) > 1e-6 * u)
            {
                double feetBefore = FeetZ;
                _eyeCur += (eyeGoal - _eyeCur) * (1 - Math.Exp(-dt * 12.0));
                if (Math.Abs(_eyeCur - eyeGoal) < 0.001 * u) _eyeCur = eyeGoal;
                _camPos.Z = feetBefore + _eyeCur;
            }

            double feet  = FeetZ;
            double baseZ = SupportZ;

            // Probe from step height, not from the eye: a table or railing
            // under your head is no longer mistaken for the floor.
            var floor = ShootRay(new Ray3d(new Point3d(_camPos.X, _camPos.Y, baseZ + ProbeLift), -Vector3d.ZAxis));
            _lastFloorValid = floor.HasValue;
            if (floor.HasValue) _lastFloorZ = floor.Value.Z;

            if (_grounded)
            {
                if (_lastFloorValid && _lastFloorZ >= baseZ - _stepHeight)
                {
                    // Up or down a stair tread: ease the body onto it.
                    double diff = _lastFloorZ - feet;
                    if (Math.Abs(diff) < 0.001 * u)
                    {
                        feet = _lastFloorZ;
                        _stepSettling = false;
                    }
                    else
                    {
                        feet += diff * (1 - Math.Exp(-dt * 18.0));
                        _stepSettling = true;
                    }
                    _verticalVelocity = 0;
                }
                else
                {
                    _grounded     = false;   // walked off a ledge
                    _stepSettling = false;
                }
            }

            if (!_grounded)
            {
                _verticalVelocity += g * dt;
                if (_verticalVelocity < terminal) _verticalVelocity = terminal;
                feet += _verticalVelocity * dt;
                if (_lastFloorValid && feet <= _lastFloorZ)
                {
                    feet              = _lastFloorZ;
                    _verticalVelocity = 0;
                    _grounded         = true;
                }
                else if (!_lastFloorValid && _verticalVelocity < -20.0 * u)
                {
                    // Nothing below to land on: stop falling forever and fly.
                    _gravityEnabled   = false;
                    _verticalVelocity = 0;
                    RhinoApp.WriteLine("WalkMode: no floor below, switched to fly mode (Tab to walk).");
                }
            }

            _camPos.Z = feet + _eyeCur;
            return Math.Abs(_camPos.Z - prevZ) > 1e-9;
        }

        void ProcessTeleport()
        {
            double elapsed = (DateTime.UtcNow - _teleportStart).TotalSeconds;
            double t       = Math.Min(elapsed / _teleportDuration, 1.0);
            double smooth  = t * t * (3.0 - 2.0 * t);

            _camPos = _teleportOrigin + (_teleportTarget - _teleportOrigin) * smooth;

            if (t >= 1.0)
            {
                _teleporting      = false;
                _verticalVelocity = 0;
                _grounded         = false;   // the next gravity tick settles onto the floor
            }
        }

        /// <summary>Floor under (x, y), searching down from z.</summary>
        bool FindFloor(double x, double y, double z, out double floorZ)
        {
            var hit = ShootRay(new Ray3d(new Point3d(x, y, z), -Vector3d.ZAxis));
            floorZ = hit?.Z ?? 0;
            return hit.HasValue;
        }

        void StartTeleport(Point3d target)
        {
            double dist = _camPos.DistanceTo(target);
            if (dist < 0.01 * _unitsPerMeter) return;
            _teleportOrigin   = _camPos;
            _teleportTarget   = target;
            _teleportStart    = DateTime.UtcNow;
            _teleportDuration = Math.Max(0.06, Math.Min(0.15, dist / (20.0 * _unitsPerMeter)));
            _teleporting      = true;
        }

        void BeginTeleport()
        {
            Point3d hit;
            using (var zBuf = new ZBufferCapture(_vp))
            {
                int cx = _vp.Bounds.Width  / 2;
                int cy = _vp.Bounds.Height / 2;
                hit = zBuf.WorldPointAt(cx, cy);
            }

            if (!hit.IsValid) return;
            if (_camPos.DistanceTo(hit) < 0.1 * _unitsPerMeter) return;

            // Stop a body radius short of what you aimed at, so aiming at a
            // wall puts you in front of it rather than inside it.
            var horiz = new Vector3d(hit.X - _camPos.X, hit.Y - _camPos.Y, 0);
            var spot  = hit;
            if (horiz.Unitize()) spot -= horiz * (_bodyRadius * 1.2);

            // Aiming up at a ceiling means the floor under it, not the slab above.
            double searchZ = hit.Z > _camPos.Z ? hit.Z - 0.05 * _unitsPerMeter : hit.Z + ProbeLift;
            double z = hit.Z + _eyeCur;
            if (_gravityEnabled
                && FindFloor(spot.X, spot.Y, searchZ, out double floorZ)
                && floorZ > hit.Z - 3.0 * _unitsPerMeter)
                z = floorZ + _eyeCur;

            StartTeleport(new Point3d(spot.X, spot.Y, z));
        }

        /// <summary>
        /// At start, put the eye at standing height over the floor below the
        /// camera. If there is no floor (empty sky), fly instead.
        /// </summary>
        void SnapToFloorAtStart()
        {
            if (!_gravityEnabled) return;
            if (FindFloor(_camPos.X, _camPos.Y, _camPos.Z, out double floorZ))
            {
                _camPos.Z = floorZ + _eyeCur;
                _grounded = true;
                _lastFloorValid = true;
                _lastFloorZ = floorZ;
            }
            else
            {
                _gravityEnabled = false;
                RhinoApp.WriteLine("WalkMode: no floor below the camera, starting in fly mode (Tab to walk).");
            }
        }

        static readonly double[] LensPresets = { 12, 14, 16, 18, 20, 24, 28, 35, 40, 50, 70, 85 };

        void ApplyLens(double mm)
        {
            if (mm > 0 && Math.Abs(mm - _vp.Camera35mmLensLength) > 0.01)
            {
                _vp.Camera35mmLensLength = mm;
                // Pixel scales of the mouse-nav calls depend on the field of view.
                if (UseMouseNav) CalibrateMouseNav();
                else             ApplyCamera();
            }
            _lensMm = _vp.Camera35mmLensLength;
        }

        void StepLens(int steps)
        {
            double cur = _vp.Camera35mmLensLength;
            // Past the last preset in that direction: keep the lens rather
            // than jump the other way (e.g. ] at 100 mm went to 85 mm).
            int idx = -1;
            if (steps > 0)
            {
                for (int i = 0; i < LensPresets.Length; i++)
                    if (LensPresets[i] > cur + 0.5) { idx = i; break; }
                if (idx < 0) return;
                idx = Math.Min(LensPresets.Length - 1, idx + steps - 1);
            }
            else
            {
                for (int i = LensPresets.Length - 1; i >= 0; i--)
                    if (LensPresets[i] < cur - 0.5) { idx = i; break; }
                if (idx < 0) return;
                idx = Math.Max(0, idx + steps + 1);
            }
            ApplyLens(LensPresets[idx]);
            WalkSettings.Lens = _lensMm;
        }

        void ProcessToggles()
        {
            if (_tabEdge)
            {
                _tabEdge = false;
                _gravityEnabled   = !_gravityEnabled;
                _verticalVelocity = 0;
                _grounded         = false;
                _stepSettling     = false;
                if (_gravityEnabled) _eyeCur = EyeTarget;
                _redrawPending = true;
            }

            if (_vEdge)
            {
                _vEdge = false;
                if (_gravityEnabled && _grounded)
                {
                    // ~0.45 m hop: a person's jump, not a 1.3 m leap.
                    _verticalVelocity = 3.0 * _unitsPerMeter;
                    _grounded     = false;
                    _stepSettling = false;
                }
            }

            if (_cEdge)
            {
                _cEdge = false;
                _seated = !_seated;
                _redrawPending = true;
            }

            if (_xEdge)
            {
                _xEdge = false;
                _collision = !_collision;
                _redrawPending = true;
            }

            if (_lensStep != 0)
            {
                int s = _lensStep;
                _lensStep = 0;
                StepLens(s);
                _redrawPending = true;
            }

            if (_spaceEdge)
            {
                _spaceEdge = false;
                if (!_teleporting) BeginTeleport();
            }
        }

        /// <summary>
        /// RhinoView.Redraw() only invalidates; the frame is drawn later when
        /// WM_PAINT comes through the pump.  Force it to draw now so the camera
        /// we just set is what appears, with no queueing in between.
        /// </summary>
        internal static void RedrawNow(RhinoView view)
        {
            var rec = FrameRecorder.Active;
            double t0 = rec?.NowMs ?? 0;
            view.Redraw();
            if (view.Handle != IntPtr.Zero) UpdateWindow(view.Handle);
            if (rec != null && rec.LastStartMs >= t0)
            {
                // Time spent around the pipeline pass: before it starts, and
                // after it ends (buffer swap / vsync wait).
                FrameRecorder.Sample("redraw.before-pipeline ms", rec.LastStartMs - t0);
                FrameRecorder.Sample("redraw.after-pipeline ms",  rec.NowMs - rec.LastEndMs);
            }
        }

        void ApplyCamera() => ApplyCamera(_mouseNavReady ? _navTargetDist : AcceptTargetDist());

        // Target distance for a view the user keeps (and for the fallback path).
        double AcceptTargetDist() => Math.Max(_baseSpeed * 10, 10.0 * _unitsPerMeter);

        void ApplyCamera(double targetDist)
        {
            ApplyCamera(_vp, _camPos, _yaw, _pitch, targetDist, _nearClip);
            _appliedYaw   = _yaw;
            _appliedPitch = _pitch;
            _appliedPos   = _camPos;
        }

        internal static void ApplyCamera(RhinoViewport vp, Point3d pos, double yaw, double pitch,
                                         double targetDist, double nearClip)
        {
            // Build the whole projection and apply it in one call.  Going via
            // SetCameraLocations re-runs Rhino's auto near/far (which clips
            // badly in mm/cm files) and then needs a second update to undo it.
            var dir = new Vector3d(
                Math.Cos(pitch) * Math.Sin(yaw),
                Math.Cos(pitch) * Math.Cos(yaw),
                Math.Sin(pitch));
            var right = new Vector3d(Math.Cos(yaw), -Math.Sin(yaw), 0);
            var up    = Vector3d.CrossProduct(right, dir);

            var vi = new ViewportInfo(vp);
            vi.SetCameraLocation(pos);
            vi.SetCameraDirection(dir);
            vi.SetCameraUp(up);
            vi.TargetPoint = pos + dir * targetDist;
            double far = Math.Max(vi.FrustumFar, nearClip * 1e6);
            vi.SetFrustumNearFar(nearClip, far);
            vp.SetViewProjection(vi, true);
            // SetViewProjection aligns Rhino's existing target but does not
            // copy ViewportInfo.TargetPoint. Set it explicitly: otherwise a
            // distant scene target makes a single dolly pixel metres wide.
            vp.SetCameraTarget(pos + dir * targetDist, false);
        }

        // ── Camera via Rhino's mouse-navigation calls ─────────────────────────
        // Every "set" camera API (SetViewProjection, SetCameraLocations,
        // SetCameraLocation, SetCameraDirection, Rotate) makes Rhino redo
        // ~20 ms of view setup on the next frame in heavy scenes.  The calls
        // behind Rhino's own mouse navigation — MouseRotateCamera,
        // MouseLateralDolly, MouseInOutDolly — don't (bench: ~14 ms vs ~36 ms
        // per frame).  So each frame is expressed as pixel "drags":
        //   rotate:  MouseRotateCamera   (yaw about world Z, pitch about camera X)
        //   move:    MouseLateralDolly   (camera X / Y)
        //            MouseInOutDolly     (camera direction)
        // Pixel scales are measured at start.  Each frame steers from what the
        // viewport actually shows, so whole-pixel rounding never accumulates.
        const double MaxLookPitch = 89.0 * Math.PI / 180.0;
        const int    ProbePx      = 20;
        const double WantedMmPerPx = 1.0;   // movement resolution to aim for

        bool    _mouseNavReady;
        double  _navTargetDist;
        double  _radPerPxX, _radPerPxY;          // MouseRotateCamera, signed
        double  _latPerPxX, _latPerPxY;          // MouseLateralDolly, units along CameraX/CameraY, signed
        double  _dollyPerPx;                     // MouseInOutDolly, units along CameraDirection, signed
        double  _appliedYaw, _appliedPitch;      // what the viewport shows now
        Point3d _appliedPos;

        static double WrapAngle(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));

        void ReadCameraAngles(out double yaw, out double pitch)
        {
            var dir = _vp.CameraDirection;
            dir.Unitize();
            yaw   = Math.Atan2(dir.X, dir.Y);
            pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dir.Z)));
        }

        System.Drawing.Point ViewportCentre() =>
            new System.Drawing.Point(_vp.Bounds.Width / 2, _vp.Bounds.Height / 2);

        static System.Drawing.Point Offset(System.Drawing.Point p, int dx, int dy) =>
            new System.Drawing.Point(p.X + dx, p.Y + dy);

        /// <summary>
        /// Measure the pixel scales of the three navigation calls (they depend
        /// on Rhino's settings and the target distance), pick a target distance
        /// that gives ~1 mm per pixel of movement, then restore the camera.
        /// </summary>
        void CalibrateMouseNav()
        {
            _mouseNavReady = false;
            var c = ViewportCentre();
            try
            {
                _navTargetDist = AcceptTargetDist();
                for (int pass = 0; pass < 2; pass++)
                {
                    ApplyCamera(_navTargetDist);
                    var start = new ViewportInfo(_vp);
                    var startTarget = _vp.CameraTarget;
                    var X   = _vp.CameraX;         X.Unitize();
                    var Y   = _vp.CameraY;         Y.Unitize();
                    var dir = _vp.CameraDirection; dir.Unitize();
                    var p0  = _vp.CameraLocation;

                    _vp.MouseLateralDolly(c, Offset(c, ProbePx, 0));
                    _latPerPxX = (_vp.CameraLocation - p0) * X / ProbePx;
                    _vp.SetViewProjection(start, true);
                    _vp.SetCameraTarget(startTarget, false);

                    _vp.MouseLateralDolly(c, Offset(c, 0, ProbePx));
                    _latPerPxY = (_vp.CameraLocation - p0) * Y / ProbePx;
                    _vp.SetViewProjection(start, true);
                    _vp.SetCameraTarget(startTarget, false);

                    _vp.MouseInOutDolly(c, Offset(c, 0, ProbePx));
                    _dollyPerPx = (_vp.CameraLocation - p0) * dir / ProbePx;
                    _vp.SetViewProjection(start, true);
                    _vp.SetCameraTarget(startTarget, false);

                    // Scales are proportional to target distance: shrink it so
                    // the coarsest axis moves ~WantedMmPerPx per pixel.
                    double coarsest = Math.Max(Math.Abs(_dollyPerPx),
                                      Math.Max(Math.Abs(_latPerPxX), Math.Abs(_latPerPxY)));
                    double wanted = WantedMmPerPx * 0.001 * _unitsPerMeter;
                    if (pass == 0 && coarsest > 1e-12)
                        _navTargetDist = Math.Max(0.5 * _unitsPerMeter,
                                         Math.Min(_navTargetDist, _navTargetDist * wanted / coarsest));
                }

                // Rotation last, from the final target distance.
                {
                    var start = new ViewportInfo(_vp);
                    var startTarget = _vp.CameraTarget;
                    ReadCameraAngles(out double y0, out double p0);
                    _vp.MouseRotateCamera(c, Offset(c, ProbePx, 0));
                    ReadCameraAngles(out double y1, out _);
                    _vp.SetViewProjection(start, true);
                    _vp.SetCameraTarget(startTarget, false);

                    _vp.MouseRotateCamera(c, Offset(c, 0, ProbePx));
                    ReadCameraAngles(out _, out double p1);
                    _vp.SetViewProjection(start, true);
                    _vp.SetCameraTarget(startTarget, false);

                    _radPerPxX = WrapAngle(y1 - y0) / ProbePx;
                    _radPerPxY = (p1 - p0) / ProbePx;
                }

                const double eps = 1e-9;
                _mouseNavReady = Math.Abs(_radPerPxX) > eps && Math.Abs(_radPerPxY) > eps
                              && Math.Abs(_latPerPxX) > eps && Math.Abs(_latPerPxY) > eps
                              && Math.Abs(_dollyPerPx) > eps;
            }
            catch
            {
                _mouseNavReady = false;
            }

            // Leave the viewport exactly at our state, with the chosen target distance.
            ApplyCamera();
        }

        static int ToPixels(double amount, double perPx)
        {
            double px = amount / perPx;
            return Math.Abs(px) > 1e6 ? int.MaxValue : (int)Math.Round(px);
        }

        /// <summary>
        /// Bring the viewport to (_camPos, _yaw, _pitch) using only mouse-nav
        /// calls.  Returns false if the caller should fall back to ApplyCamera.
        /// </summary>
        bool NavigateViaMouse()
        {
            if (!UseMouseNav || !_mouseNavReady) return false;
            var c = ViewportCentre();

            // 1. Look.
            int rx = ToPixels(WrapAngle(_yaw - _appliedYaw), _radPerPxX);
            int ry = ToPixels(_pitch - _appliedPitch,        _radPerPxY);
            if (rx == int.MaxValue || ry == int.MaxValue) return false;
            if (rx != 0 || ry != 0)
            {
                if (!_vp.MouseRotateCamera(c, Offset(c, rx, ry))) return false;
                ReadCameraAngles(out _appliedYaw, out _appliedPitch);
                if (Math.Abs(_appliedPitch) > MaxLookPitch + 0.01)
                    return false;   // went over the top; let ApplyCamera put it right
            }

            // 2. Move, in the camera frame as it is after the rotation.
            var delta = _camPos - _appliedPos;
            if (delta.SquareLength > 0)
            {
                var X   = _vp.CameraX;         X.Unitize();
                var Y   = _vp.CameraY;         Y.Unitize();
                var dir = _vp.CameraDirection; dir.Unitize();

                int lx = ToPixels(delta * X,   _latPerPxX);
                int ly = ToPixels(delta * Y,   _latPerPxY);
                int dz = ToPixels(delta * dir, _dollyPerPx);
                if (lx == int.MaxValue || ly == int.MaxValue || dz == int.MaxValue)
                    return false;   // huge jump: one slow frame is fine

                if ((lx != 0 || ly != 0) && !_vp.MouseLateralDolly(c, Offset(c, lx, ly))) return false;
                if (dz != 0              && !_vp.MouseInOutDolly(c, Offset(c, 0, dz)))    return false;
                _appliedPos = _vp.CameraLocation;
            }
            return true;
        }

        // ── Entry point ───────────────────────────────────────────────────────
        internal Result RunWalk()
        {
            if (!_vp.IsPerspectiveProjection)
            {
                RhinoApp.WriteLine("WalkMode requires a perspective viewport.");
                return Result.Cancel;
            }

            if (UseRawInput)
            {
                _rawRegistered = RegisterRawMouse(true);
                if (!_rawRegistered)
                {
                    RhinoApp.WriteLine("WalkMode: raw mouse input unavailable, using cursor input.");
                    UseRawInput = false;
                }
                ComputeScreenCenter();   // clip rect depends on the input mode
            }

            if (WalkSettings.Lens > 0) _vp.Camera35mmLensLength = WalkSettings.Lens;
            _lensMm = _vp.Camera35mmLensLength;
            SnapToFloorAtStart();

            // Measures the mouse-nav pixel scales and leaves the viewport at our
            // camera state with the target distance and near clip set.
            if (UseMouseNav) CalibrateMouseNav();
            else             ApplyCamera();

            SetCursorPos(_screenCenter.X, _screenCenter.Y);
            _hadFocus = RhinoHasFocus();
            if (_hadFocus) ClipCursor(ref _screenClip);

            string prevTag = FrameRecorder.Tag;
            FrameRecorder.Tag = (UseRawInput ? "walk-raw" : "walk-cursor")
                              + (UseMouseNav && _mouseNavReady ? "-nav" : "");

            RhinoApp.SetCommandPrompt(
                "Walk  WASD·move  Mouse·look  C·seated  X·walls  [ ]·lens  Tab·walk/fly  V·jump  Space·teleport  Scroll·speed  Esc·exit");

            try
            {
                // Manual message pump.  Walk input is filtered off the queue
                // and everything else dispatched normally.  WM_MOUSEMOVE is
                // coalesced by Windows, so a burst of motion costs one frame
                // and the cursor offset read in TickFrame covers all of it.
                _lastTickMs = _sw.ElapsedMilliseconds;
                long nextTickMs = _lastTickMs;
                IsRunning = true;
                while (!_exitRequested)
                {
                    bool gotInput = false;
                    double pumpStartMs = _sw.Elapsed.TotalMilliseconds;
                    int messageCount = 0;
                    while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        bool consumed = false;
                        if (msg.message == WM_INPUT)
                        {
                            // Read, then still dispatch: DefWindowProc must free it.
                            if (UseRawInput) { ReadRawInput(msg.lParam); gotInput = true; }
                        }
                        else if (FilterInput(ref msg)) { gotInput = true; consumed = true; }
                        if (!consumed)
                        {
                            TranslateMessage(ref msg);
                            DispatchMessage(ref msg);
                        }
                        messageCount++;
                        if (_exitRequested || _sw.Elapsed.TotalMilliseconds - pumpStartMs >= MessagePumpBudgetMs)
                            break;
                    }
                    if (_exitRequested) break;

                    if (FrameRecorder.IsRecording && messageCount > 0)
                    {
                        FrameRecorder.Sample("walk.pump ms", _sw.Elapsed.TotalMilliseconds - pumpStartMs);
                        FrameRecorder.Sample("walk.pump messages", messageCount);
                    }

                    long nowMs = _sw.ElapsedMilliseconds;
                    bool needsTick = NeedsContinuousTick();
                    bool shouldTick = gotInput || _redrawPending || (needsTick && nowMs >= nextTickMs);

                    if (shouldTick)
                    {
                        TickFrame(nowMs);
                        nextTickMs = nowMs + FrameIntervalMs;
                        continue;
                    }

                    uint waitMs = needsTick
                        ? (uint)Math.Max(1L, nextTickMs - nowMs)
                        : INFINITE;

                    MsgWaitForMultipleObjectsEx(
                        0,
                        IntPtr.Zero,
                        waitMs,
                        QS_ALLINPUT,
                        MWMO_INPUTAVAILABLE);
                }
            }
            finally
            {
                IsRunning = false;
                FrameRecorder.Tag = prevTag;
                Cleanup();
            }

            if (_cancelled)
            {
                RestoreCamera();
                return Result.Cancel;
            }

            // Walking used a short target distance for fine dolly steps; give
            // the kept view a sensible one (and our near clip) again.
            ApplyCamera(AcceptTargetDist());
            _view.Redraw();
            return Result.Success;
        }

        void RestoreCamera()
        {
            _vp.SetViewProjection(_savedViewport, false);
            _vp.SetCameraTarget(_savedCameraTarget, false);
        }

        // ── Cleanup ───────────────────────────────────────────────────────────
        void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            s_lastSpeed = _baseSpeed;

            // Disable display conduit
            if (_conduit != null) { _conduit.Enabled = false; _conduit = null; }

            timeEndPeriod(1);

            ClipCursorNone(IntPtr.Zero);
            if (_rawRegistered) { RegisterRawMouse(false); _rawRegistered = false; }
            if (_rawInputBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_rawInputBuffer);
                _rawInputBuffer = IntPtr.Zero;
            }

            RhinoDoc.AddRhinoObject         -= _objHandler;
            RhinoDoc.DeleteRhinoObject      -= _objHandler;
            RhinoDoc.ReplaceRhinoObject     -= _replaceHandler;
            RhinoDoc.ModifyObjectAttributes -= _attrHandler;
            RhinoDoc.LayerTableEvent        -= _layerHandler;

            // Force cursor visible
            {
                int c = ShowCursorWin32(true);
                while (c < 0) c = ShowCursorWin32(true);
                while (c > 0) c = ShowCursorWin32(false);
            }

            _meshGeometry = null;
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
