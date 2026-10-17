# WalkMode (human-scale fork)

A fast first-person walkthrough camera plugin for Rhino 8. Uses a lightweight Win32 message pump instead of Rhino's GetPoint framework, giving smooth mouse-look and movement that matches native Rhino performance.

## Install

Search for **WalkMode** in Rhino's Package Manager (`_PackageManager`), or install via the Yak CLI:

```
"C:\Program Files\Rhino 8\System\Yak.exe" install WalkMode
```

## Usage

Run the `WalkMode` command in a perspective viewport.

### Controls

| Input | Action |
|---|---|
| **Mouse** | Look around |
| **W A S D** | Move forward / left / back / right |
| **Q / E** | Move down / up (fly mode only) |
| **Shift** | Run (2.2x walking, 5x flying) |
| **Alt** | Slow (0.5x walking, 0.2x flying) |
| **Scroll wheel** | Adjust speed (walking and flying keep separate speeds) |
| **Tab** | Switch walk / fly |
| **C** | Standing / seated eye height |
| **X** | Wall collision on / off |
| **[ / ]** | Wider / narrower lens (12-85 mm presets) |
| **V** | Jump (walking, on the ground) |
| **Space** | Teleport to crosshair target |
| **Enter** or **Right-click** | Accept current view |
| **Escape** | Cancel and restore original view |

### Human scale

Walking is set up to read a building the way a person would:

- **Eye height** 1.60 m standing, 1.20 m seated (wheelchair / sitting). The eye eases between them.
- **Floor** is found from step height down, so tables and railings under your head are not mistaken for floor.
- **Stairs and ramps**: you walk up and down anything up to 0.30 m per step; taller ledges block you or drop you.
- **Walls** stop you at a 0.25 m body radius and you slide along them.
- **Speed** 1.4 m/s walking pace.
- **Floors** made with Box / extrusions and SubD count; objects on hidden layers do not.

Change these with the `WalkModeSettings` command (values in metres; `LensMm` 0 keeps the viewport lens). Settings persist between sessions.

### Features

- **Walk mode** with eye height, step climbing, wall collision, jumping, and terminal velocity
- **Teleport** to any visible surface via depth buffer; stops in front of walls and lands on the floor
- **Adaptive near-clip** for mm/cm scenes — no walk-through clipping
- **Speed persistence** across command invocations
- **HUD** with crosshair, mode, eye height, speed, and lens readout

## Building from source

Requires the .NET SDK and Rhino 8 installed.

```
dotnet build
```

Outputs both `net48` and `net7.0-windows` builds to `WalkMode/bin/Debug/`, along with a `.yak` package.

## License

[MIT](LICENSE)
