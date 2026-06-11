// Unity adapter for GCodeParser
// Drop both GCodeParser.cs and this file into your Unity project.
// Use GCodeParserUnity.Sample() instead of GCodeParser.GCode.Sample()
// to get results in Unity's coordinate system directly.

using UnityEngine;

public static class GCodeParserUnity
{
    /// <summary>
    /// Sample the print at normalized t [0,1], returning nozzle position
    /// and layer height already scaled and swizzled for Unity world space.
    /// GCode XY maps to Unity XY. GCode Z (height) maps to Unity Z.
    /// </summary>
    /// <param name="gCode">Parsed GCode object.</param>
    /// <param name="t">Normalized print progress [0,1].</param>
    /// <param name="scaleFactor">GCode mm to Unity units. Typically 0.001 for mm→m.</param>
    public static UnitySample Sample(this GCodeParser.GCode gCode, float t, float scaleFactor)
    {
        GCodeParser.PrintSample raw = gCode.Sample(t);
        return new UnitySample(
            new Vector3(raw.NozzlePosition.X * scaleFactor, raw.NozzlePosition.Y * scaleFactor, 0f),
            raw.LayerHeight * scaleFactor,
            raw.LayerIndex,
            raw.IsExtruding
        );
    }

    /// <summary>
    /// Subscribe to unknown GCode instructions and forward them to Unity's log.
    /// Call once at startup or when parsing.
    /// </summary>
    public static void EnableUnityLogging()
    {
        GCodeParser.UnknownInstruction -= OnUnknownInstruction;
        GCodeParser.UnknownInstruction += OnUnknownInstruction;
    }

    private static void OnUnknownInstruction(string code, string fullLine)
        => Debug.LogWarning($"[GCodeParser] Unknown instruction: <b>{code}</b> — {fullLine}");

    public struct UnitySample
    {
        /// <summary>Nozzle XY position in Unity world units. Z is always 0 — use LayerHeight for vertical.</summary>
        public Vector3 NozzlePosition;
        /// <summary>Current layer height in Unity world units.</summary>
        public float LayerHeight;
        /// <summary>Zero-based layer index.</summary>
        public int LayerIndex;
        /// <summary>True if the nozzle is currently extruding material.</summary>
        public bool IsExtruding;

        public UnitySample(Vector3 nozzlePosition, float layerHeight, int layerIndex, bool isExtruding)
        {
            NozzlePosition = nozzlePosition;
            LayerHeight = layerHeight;
            LayerIndex = layerIndex;
            IsExtruding = isExtruding;
        }
    }
}
