# Navigation performance and movement checks

## 1.0.3: forward/backward stutter

`RhinoViewport.SetViewProjection(info, true)` aligns the existing Rhino target with the camera direction; it does not copy `ViewportInfo.TargetPoint`. WalkMode's calibration previously assumed the requested short target distance had been applied. With a distant scene target, a single `MouseInOutDolly` pixel could move the camera metres, so small per-frame movements rounded to zero and accumulated into occasional large jumps.

The fix explicitly calls `SetCameraTarget(target, false)` when applying a projection, restores the target after calibration probes, and restores the original target when cancelling. Per-frame movement still uses Rhino's fast mouse-navigation APIs.

An isolated live probe measured the unchanged target at approximately 531 document units despite requesting 0.5 units. Explicitly setting the target changed a one-pixel dolly from approximately 3.26 units to 0.00307 units.

### Validation

Live tests in Rhino 8 used an 875 × 782 Shaded viewport and three-second scripted key holds. Each test restored the starting view. Camera location and target restoration errors were both zero.

| Direction | Median frame interval | 95th percentile | Stationary translation intervals during movement |
| --- | ---: | ---: | ---: |
| Forward (W) | 16.68 ms | 17.03 ms | 0 / 180 |
| Backward (S) | 16.69 ms | 17.27 ms | 0 / 175 |
| Left (A) | 16.68 ms | 17.02 ms | 0 / 179 |
| Right (D) | 16.67 ms | 17.06 ms | 0 / 179 |

An earlier pre-fix movement-plus-look recording had 170 stationary translation intervals out of 180, despite approximately 60 fps. Frame rate alone missed the movement defect because the camera could rotate while its position remained unchanged. The earlier and corrected tests used different input paths, so their frame times are not a controlled before/after performance comparison. The target probe and elimination of held translation frames validate the movement correction.

These are short live tests, not a guarantee of performance in every scene. No sensitivity change was made.

## Benchmark commands

- `WalkBench`: start/stop recording while navigating manually.
- `WalkBenchRobot Native`: scripted Ctrl+Alt+right-drag reference.
- `WalkBenchRobot Walk`: scripted mouse-look.
- `WalkBenchRobot WalkMove`: forward movement plus mouse-look.
- `WalkBenchRobot Forward`, `Backward`, `Left`, `Right`: isolated movement tests.
- `WalkBenchSynthetic`: compare camera APIs and redraw costs.
- `WalkInputMode`: compare raw/cursor input and mouse-navigation/projection camera paths.

Leave mouse and keyboard untouched during robot runs. Run tests from the same viewport, camera, and display mode. Native robot runs change the view; use `UndoView` before the next comparison. Walk robot runs cancel and restore their starting view. Reports include translation statistics as well as frame timing. Raw frame CSVs are written under the temporary `WalkMode` directory.

## 1.0.2 changes

Raw mouse input, calibrated mouse-navigation camera calls, a bounded message pump, reusable raw-input storage, deferred collision geometry loading, and diagnostic commands. Version 1.0.3 corrects the target-distance assumption in that release.
