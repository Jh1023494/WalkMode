using System;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace WalkMode
{
    /// <summary>
    /// Human-scale walk settings, stored in metres in the plug-in settings
    /// so they survive Rhino restarts.
    /// </summary>
    internal static class WalkSettings
    {
        public const double DefaultEyeHeight       = 1.60;  // standing eye level (height ~1.70 m)
        public const double DefaultSeatedEyeHeight = 1.20;  // seated / wheelchair eye level
        public const double DefaultStepHeight      = 0.30;  // tallest ledge you walk up without jumping
        public const double DefaultBodyRadius      = 0.25;  // shoulder half-width for wall collision
        public const double DefaultWalkSpeed       = 1.40;  // average walking pace, m/s
        public const double DefaultLens            = 0;     // 0 = keep the viewport's lens

        static PersistentSettings S => WalkModePlugin.Instance?.Settings;

        static double Get(string key, double fallback)
        {
            var s = S;
            return s == null ? fallback : s.GetDouble(key, fallback);
        }

        static void Set(string key, double value) => S?.SetDouble(key, value);

        static bool GetBool(string key, bool fallback)
        {
            var s = S;
            return s == null ? fallback : s.GetBool(key, fallback);
        }

        static void SetBool(string key, bool value) => S?.SetBool(key, value);

        public static double EyeHeight
        {
            get => Get("EyeHeight", DefaultEyeHeight);
            set => Set("EyeHeight", value);
        }

        public static double SeatedEyeHeight
        {
            get => Get("SeatedEyeHeight", DefaultSeatedEyeHeight);
            set => Set("SeatedEyeHeight", value);
        }

        public static double StepHeight
        {
            get => Get("StepHeight", DefaultStepHeight);
            set => Set("StepHeight", value);
        }

        public static double BodyRadius
        {
            get => Get("BodyRadius", DefaultBodyRadius);
            set => Set("BodyRadius", value);
        }

        public static double WalkSpeed
        {
            get => Get("WalkSpeed", DefaultWalkSpeed);
            set => Set("WalkSpeed", value);
        }

        /// <summary>35 mm lens length applied while walking; 0 keeps the viewport's own.</summary>
        public static double Lens
        {
            get => Get("Lens", DefaultLens);
            set => Set("Lens", value);
        }

        public static bool StartWalking
        {
            get => GetBool("StartWalking", true);
            set => SetBool("StartWalking", value);
        }

        public static bool Collision
        {
            get => GetBool("Collision", true);
            set => SetBool("Collision", value);
        }

        public static void Reset()
        {
            EyeHeight       = DefaultEyeHeight;
            SeatedEyeHeight = DefaultSeatedEyeHeight;
            StepHeight      = DefaultStepHeight;
            BodyRadius      = DefaultBodyRadius;
            WalkSpeed       = DefaultWalkSpeed;
            Lens            = DefaultLens;
            StartWalking    = true;
            Collision       = true;
        }
    }

    public class WalkModeSettingsCommand : Command
    {
        public override string EnglishName => "WalkModeSettings";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            while (true)
            {
                var go = new GetOption();
                go.SetCommandPrompt("WalkMode settings, metres (Enter when done)");
                go.AcceptNothing(true);

                var eye    = new OptionDouble(WalkSettings.EyeHeight,       0.5, 2.5);
                var seated = new OptionDouble(WalkSettings.SeatedEyeHeight, 0.5, 2.0);
                var step   = new OptionDouble(WalkSettings.StepHeight,      0.0, 1.0);
                var radius = new OptionDouble(WalkSettings.BodyRadius,      0.05, 1.0);
                var speed  = new OptionDouble(WalkSettings.WalkSpeed,       0.1, 10.0);
                var lens   = new OptionDouble(WalkSettings.Lens,            0.0, 300.0);
                var start  = new OptionToggle(WalkSettings.StartWalking, "Fly", "Walk");
                var coll   = new OptionToggle(WalkSettings.Collision,    "Off", "On");

                go.AddOptionDouble("EyeHeight",       ref eye);
                go.AddOptionDouble("SeatedEyeHeight", ref seated);
                go.AddOptionDouble("StepHeight",      ref step);
                go.AddOptionDouble("BodyRadius",      ref radius);
                go.AddOptionDouble("WalkSpeed",       ref speed);
                go.AddOptionDouble("LensMm",          ref lens);
                go.AddOptionToggle("StartIn",         ref start);
                go.AddOptionToggle("Collision",       ref coll);
                int reset = go.AddOption("ResetDefaults");

                var res = go.Get();

                WalkSettings.EyeHeight       = eye.CurrentValue;
                WalkSettings.SeatedEyeHeight = seated.CurrentValue;
                WalkSettings.StepHeight      = step.CurrentValue;
                WalkSettings.BodyRadius      = radius.CurrentValue;
                WalkSettings.WalkSpeed       = speed.CurrentValue;
                WalkSettings.Lens            = lens.CurrentValue;
                WalkSettings.StartWalking    = start.CurrentValue;
                WalkSettings.Collision       = coll.CurrentValue;

                if (res == GetResult.Option)
                {
                    if (go.OptionIndex() == reset) WalkSettings.Reset();
                    continue;
                }
                if (res == GetResult.Nothing) return Result.Success;
                return go.CommandResult();
            }
        }
    }
}
