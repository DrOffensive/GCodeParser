# GCodeParser

A C# GCode parser built for real-time scrubbing and smooth animation. Instead of replaying instructions frame-by-frame, it converts an entire print into a normalized timeline — pass any `t` value in `[0, 1]` and get the exact nozzle position, layer height, and extrusion state at that point.

Originally built for a Unity game, but the core has no engine dependencies and works anywhere C# runs.

---

## Features

- **Scrubbing** — sample the print at any normalized `t` with sub-instruction precision
- **Travel weighting** — travel (G0) and extrusion (G1) moves are weighted separately so the timeline reflects actual print time, not instruction count
- **Layer-based storage** — instructions are grouped per layer rather than in one flat list, keeping memory bounded for large prints
- **Engine-agnostic core** — no Unity, Godot, or MonoGame dependencies in `GCodeParser.cs`
- **Unity adapter** included — coordinate swizzle, scale factor, and Unity logging wired up out of the box

---

## Supported GCode

| Code | Handling |
|------|----------|
| `G0` | Rapid travel move — weighted by `travelFactor` |
| `G1` | Extrusion move — full timeline weight |
| `G28` | Home — zero-width, instantaneous in timeline |
| `G21`, `G90`, `G92` | Silently ignored (unit/mode setup) |
| `M*` | Silently ignored (machine commands) |
| Unknown | Fires `GCodeParser.UnknownInstruction` event |

Tested against Prusa Slicer output. Should work with any FFF/FDM slicer producing standard RepRap GCode.

---

## Usage

### Core (any C# project)

```csharp
string raw = File.ReadAllText("myprint.gcode");

// travelFactor: how much faster G0 moves are vs G1.
// 3.0 = travel is 3x faster, so it occupies 1/3 the timeline for equal distance.
GCodeParser.GCode gcode = GCodeParser.Parse(raw, travelFactor: 3f);

// Sample at the halfway point
GCodeParser.PrintSample sample = gcode.Sample(0.5f);

Console.WriteLine($"Nozzle: {sample.NozzlePosition}");
Console.WriteLine($"Height: {sample.LayerHeight} mm");
Console.WriteLine($"Layer:  {sample.LayerIndex}");
Console.WriteLine($"Extruding: {sample.IsExtruding}");
```

### Unity

Drop both `GCodeParser.cs` and `Unity/GCodeParserUnity.cs` into your project.

```csharp
GCodeParser.GCode gcode = GCodeParser.Parse(rawGCode, travelFactor: 3f);

// Optional: forward unknown instructions to Unity's console
GCodeParserUnity.EnableUnityLogging();

// Drive progress from game time
float _progress = 0f;
float _printDurationSeconds = 120f; // however you compute this

void Update()
{
    _progress += Time.deltaTime / _printDurationSeconds;
    _progress = Mathf.Clamp01(_progress);

    // scaleFactor converts GCode mm to Unity world units (0.001 = mm to metres)
    GCodeParserUnity.UnitySample sample = gcode.Sample(_progress, scaleFactor: 0.001f);

    printHead.localPosition = new Vector3(
        sample.NozzlePosition.x,
        sample.NozzlePosition.y,
        0f
    );
    frame.localPosition = new Vector3(0f, 0f, sample.LayerHeight);
}
```

## How the timeline works

Each instruction gets a weighted length:

```
extrusion move  →  weightedLength = distance
travel move     →  weightedLength = distance / travelFactor
home (G28)      →  weightedLength = 0  (instantaneous)
```

The total weighted length of the print becomes the denominator. Each instruction's `t` range is `[cumulativeWeight / total, (cumulativeWeight + ownWeight) / total]`. Sampling at any `t` binary searches layers then instructions to find the owning range, then lerps within it for smooth sub-instruction interpolation.

---

## Files

| File | Description |
|------|-------------|
| `GCodeParser.cs` | Core parser — no engine dependencies |
| `Unity/GCodeParserUnity.cs` | Unity extension methods and coordinate adapter |

---

## License

MIT
