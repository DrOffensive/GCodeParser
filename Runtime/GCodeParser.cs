// MIT License
// Copyright (c) 2026 Marc
// https://github.com/marc/gcode-parser

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// A GCode parser designed for real-time scrubbing and animation.
/// Parses FFF/FDM GCode into a normalized timeline where t=0 is the start
/// of the print and t=1 is the end. Travel and extrusion moves are weighted
/// separately so the timeline reflects actual print time distribution.
///
/// Usage:
///   GCodeParser.GCode gcode = GCodeParser.Parse(rawString, travelFactor: 3f);
///   GCodeParser.PrintSample sample = gcode.Sample(0.5f); // midpoint of print
/// </summary>
public static class GCodeParser
{
    private static readonly Regex MoveRegex = new Regex(
        @"(?<key>[A-Z])(?<value>-?\d*\.?\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a GCode string into a scrrubbable GCode object.
    /// </summary>
    /// <param name="gCode">Raw GCode string.</param>
    /// <param name="travelFactor">
    /// How much faster travel (G0) moves are relative to extrusion (G1) moves.
    /// e.g. 3.0 means travel moves are 3x faster, so they occupy 1/3 of the
    /// normalized timeline for the same distance. Default is 3.0.
    /// </param>
    public static GCode Parse(string gCode, float travelFactor = 3f)
    {
        if (string.IsNullOrEmpty(gCode))
            return new GCode(new List<GCodeLayer>());

        travelFactor = Math.Max(travelFactor, 0.0001f);

        var layers = new List<GCodeLayer>();
        var instructions = new List<GCodeInstruction>();
        var pos = new Vec3();
        float e = 0f;
        float travelLength = 0f;
        float rapidTravelLength = 0f;
        float currentLayerHeight = 0f;

        foreach (string line in gCode.Split('\n'))
        {
            ParseInstruction(
                line, travelFactor,
                ref pos, ref e, ref currentLayerHeight,
                ref travelLength, ref rapidTravelLength,
                ref layers, ref instructions);
        }

        if (instructions.Count > 0)
        {
            var last = new GCodeLayer(currentLayerHeight, layers.Count, instructions,
                new Vec2(pos.X, pos.Y), travelLength, rapidTravelLength);
            last = AssignLocalT(last);
            layers.Add(last);
        }

        // Second pass: assign global tStart/tEnd to each layer
        float totalWeightedLength = 0f;
        foreach (var layer in layers)
            totalWeightedLength += layer.WeightedLength;

        float cursor = 0f;
        for (int i = 0; i < layers.Count; i++)
        {
            float span = totalWeightedLength > 0f
                ? layers[i].WeightedLength / totalWeightedLength
                : 0f;
            layers[i] = layers[i].WithGlobalT(cursor, cursor + span);
            cursor += span;
        }

        return new GCode(layers);
    }

    private static void ParseInstruction(
        string gCode,
        float travelFactor,
        ref Vec3 position,
        ref float extrusion,
        ref float currentLayerHeight,
        ref float travelLength,
        ref float rapidTravelLength,
        ref List<GCodeLayer> layers,
        ref List<GCodeInstruction> currentLayer)
    {
        gCode = gCode.Trim();
        if (gCode.Length < 2 || gCode.StartsWith(";"))
            return;

        string[] parts = gCode.Split(' ');
        string code = parts[0];

        // Skip unsupported or irrelevant codes
        if (code.StartsWith("M")) return;
        if (code == "G92" || code == "G21" || code == "G90") return;

        if (code == "G0" || code == "G1")
        {
            ParseMove(gCode, out float? x, out float? y, out float? z, out float? eVal);
            bool rapid = code == "G0";
            bool extruding = false;

            if (z.HasValue)
                position.Z = z.Value;

            if (eVal.HasValue)
            {
                float delta = eVal.Value - extrusion;
                extrusion = eVal.Value;
                extruding = delta > 0f;

                if (extruding && position.Z > currentLayerHeight + 0.001f)
                {
                    if (currentLayer.Count > 0)
                    {
                        var completed = new GCodeLayer(
                            currentLayerHeight, layers.Count, currentLayer,
                            new Vec2(position.X, position.Y), travelLength, rapidTravelLength);
                        completed = AssignLocalT(completed);
                        layers.Add(completed);
                    }
                    travelLength = 0f;
                    rapidTravelLength = 0f;
                    currentLayer = new List<GCodeInstruction>();
                    currentLayerHeight = position.Z;
                }
            }

            if (x.HasValue || y.HasValue)
            {
                Vec3 startPosition = position;
                position.X = x ?? position.X;
                position.Y = y ?? position.Y;

                float distance = Vec3.Distance(startPosition, position);
                float weightedDistance = rapid ? distance / travelFactor : distance;

                if (rapid) rapidTravelLength += distance;
                else travelLength += distance;

                currentLayer.Add(new GCodeInstruction(
                    GCodeInstruction.InstructionType.MoveHorizontal,
                    startPosition, position,
                    rapid, extruding,
                    distance, weightedDistance));
            }
            return;
        }

        if (code == "G28")
        {
            ParseMove(gCode, out float? hX, out float? hY, out float? hZ, out _);
            bool all = !hX.HasValue && !hY.HasValue && !hZ.HasValue;
            Vec3 homeTarget = new Vec3(
                (all || hX.HasValue) ? 0f : position.X,
                (all || hY.HasValue) ? 0f : position.Y,
                (all || hZ.HasValue) ? 0f : position.Z);

            // Home is zero-width — executes instantaneously in the timeline
            currentLayer.Add(new GCodeInstruction(
                GCodeInstruction.InstructionType.HomePosition,
                position, homeTarget,
                false, false, 0f, 0f));
            return;
        }

        // Unknown instruction — consumers can handle via events or logging
        UnknownInstruction?.Invoke(code, gCode);
    }

    /// <summary>Fired when an unrecognized GCode instruction is encountered.</summary>
    public static event Action<string, string> UnknownInstruction;

    private static GCodeLayer AssignLocalT(GCodeLayer layer)
    {
        float totalWeighted = layer.WeightedLength;
        var instructions = new List<GCodeInstruction>(layer.Instructions);

        float cursor = 0f;
        for (int i = 0; i < instructions.Count; i++)
        {
            float span = totalWeighted > 0f
                ? instructions[i].WeightedLength / totalWeighted
                : 0f;
            instructions[i] = instructions[i].WithLocalT(cursor, cursor + span);
            cursor += span;
        }

        return new GCodeLayer(layer.LayerHeight, layer.LayerIndex, instructions,
            layer.EndPosition, layer.TravelLength, layer.RapidTravelLength);
    }

    private static void ParseMove(string gCode, out float? x, out float? y, out float? z, out float? e)
    {
        x = y = z = e = null;
        foreach (Match match in MoveRegex.Matches(gCode))
        {
            string key = match.Groups["key"].Value;
            float value = float.Parse(match.Groups["value"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            switch (key)
            {
                case "X": x = value; break;
                case "Y": y = value; break;
                case "Z": z = value; break;
                case "E": e = value; break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Data types
    // -------------------------------------------------------------------------

    /// <summary>
    /// A parsed and normalized GCode file. Sample it at any t in [0,1]
    /// to get the exact nozzle state at that point in the print.
    /// </summary>
    [Serializable]
    public class GCode
    {
        [SerializeField] private List<GCodeLayer> _layers;
        public IReadOnlyList<GCodeLayer> Layers { get => _layers; }

        public GCode(List<GCodeLayer> layers) => _layers = layers;

        /// <summary>
        /// Sample the print at normalized t [0,1].
        /// Returns nozzle position, layer height, and extrusion state in GCode mm space.
        /// </summary>
        public PrintSample Sample(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            if (_layers == null || Layers.Count == 0)
                return default;

            int lo = 0, hi = _layers.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Layers[mid].TEnd < t) lo = mid + 1;
                else hi = mid;
            }

            GCodeLayer layer = _layers[lo];
            float localT = layer.TEnd > layer.TStart
                ? InverseLerp(layer.TStart, layer.TEnd, t)
                : 0f;

            return layer.Sample(localT);
        }

        /// <summary>
        /// Estimate total print duration in seconds given speed parameters.
        /// extrusionSpeed and travelSpeed are in GCode mm/s, scaleFactor converts
        /// GCode mm to your world units.
        /// </summary>
        public float GetPrintDuration(float extrusionSpeed, float travelSpeed, float scaleFactor)
        {
            if (extrusionSpeed <= 0f) extrusionSpeed = 0.0001f;
            if (travelSpeed <= 0f) travelSpeed = 0.0001f;
            if (Layers == null || Layers.Count == 0) return 0f;

            float duration = 0f;
            float currentHeight = 0f;
            foreach (var layer in Layers)
            {
                duration += layer.GetLayerDuration(extrusionSpeed, travelSpeed, scaleFactor);
                float deltaHeight = layer.LayerHeight - currentHeight;
                currentHeight = layer.LayerHeight;
                duration += (deltaHeight * scaleFactor) / travelSpeed;
            }
            return duration;
        }

        private static float InverseLerp(float a, float b, float t)
            => b > a ? (t - a) / (b - a) : 0f;
    }

    [Serializable]
    public struct GCodeLayer
    {
        public float LayerHeight;
        public int LayerIndex;
        public List<GCodeInstruction> Instructions;
        public Vec2 EndPosition;
        public float TravelLength;
        public float RapidTravelLength;
        public float WeightedLength;
        public float TStart;
        public float TEnd;

        public GCodeLayer(float layerHeight, int layerIndex, List<GCodeInstruction> instructions,
            Vec2 endPosition, float travelLength, float rapidTravelLength)
        {
            LayerHeight = layerHeight;
            LayerIndex = layerIndex;
            Instructions = instructions;
            EndPosition = endPosition;
            TravelLength = travelLength;
            RapidTravelLength = rapidTravelLength;
            TStart = 0f;
            TEnd = 0f;

            WeightedLength = 0f;
            if (instructions != null)
                foreach (var instr in instructions)
                    WeightedLength += instr.WeightedLength;
        }

        public GCodeLayer WithGlobalT(float start, float end)
        {
            var copy = this;
            copy.TStart = start;
            copy.TEnd = end;
            return copy;
        }

        public float GetLayerDuration(float extrudeSpeed, float travelSpeed, float scaleFactor)
        {
            float extrudeTime = extrudeSpeed > 0f ? (TravelLength * scaleFactor) / extrudeSpeed : 0f;
            float rapidTime = travelSpeed > 0f ? (RapidTravelLength * scaleFactor) / travelSpeed : 0f;
            return extrudeTime + rapidTime;
        }

        public PrintSample Sample(float localT)
        {
            if (Instructions == null || Instructions.Count == 0)
                return new PrintSample(EndPosition, LayerHeight, LayerIndex, false);

            int lo = 0, hi = Instructions.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Instructions[mid].LocalTEnd < localT) lo = mid + 1;
                else hi = mid;
            }

            GCodeInstruction instr = Instructions[lo];
            float instrT = instr.LocalTEnd > instr.LocalTStart
                ? (localT - instr.LocalTStart) / (instr.LocalTEnd - instr.LocalTStart)
                : 1f;

            Vec2 nozzlePos = Vec2.Lerp(
                new Vec2(instr.StartPosition.X, instr.StartPosition.Y),
                new Vec2(instr.EndPosition.X, instr.EndPosition.Y),
                instrT);

            return new PrintSample(nozzlePos, LayerHeight, LayerIndex, instr.Extrude);
        }
    }

    [Serializable]
    public struct GCodeInstruction
    {
        public InstructionType Type;
        public Vec3 StartPosition;
        public Vec3 EndPosition;
        public bool Rapid;
        public bool Extrude;
        public float Length;
        public float WeightedLength;
        public float LocalTStart;
        public float LocalTEnd;

        public GCodeInstruction(InstructionType type, Vec3 startPosition, Vec3 endPosition,
            bool rapid, bool extrude, float length, float weightedLength)
        {
            Type = type;
            StartPosition = startPosition;
            EndPosition = endPosition;
            Rapid = rapid;
            Extrude = extrude;
            Length = length;
            WeightedLength = weightedLength;
            LocalTStart = 0f;
            LocalTEnd = 0f;
        }

        public GCodeInstruction WithLocalT(float start, float end)
        {
            var copy = this;
            copy.LocalTStart = start;
            copy.LocalTEnd = end;
            return copy;
        }

        public enum InstructionType
        {
            MoveHorizontal,
            MoveHeight,
            HomePosition
        }
    }

    /// <summary>
    /// The result of sampling the print at a given normalized t.
    /// All positions are in GCode millimeter space.
    /// </summary>
    [Serializable]
    public struct PrintSample
    {
        /// <summary>Nozzle XY position in GCode mm space.</summary>
        public Vec2 NozzlePosition;
        /// <summary>Current layer height in GCode mm.</summary>
        public float LayerHeight;
        /// <summary>Zero-based layer index.</summary>
        public int LayerIndex;
        /// <summary>True if the nozzle is currently extruding material.</summary>
        public bool IsExtruding;

        public PrintSample(Vec2 nozzlePosition, float layerHeight, int layerIndex, bool isExtruding)
        {
            NozzlePosition = nozzlePosition;
            LayerHeight = layerHeight;
            LayerIndex = layerIndex;
            IsExtruding = isExtruding;
        }
    }

    // -------------------------------------------------------------------------
    // Engine-agnostic math types
    // -------------------------------------------------------------------------

    [Serializable]
    public struct Vec2
    {
        public float X, Y;
        public Vec2(float x, float y) { X = x; Y = y; }
        public static Vec2 Lerp(Vec2 a, Vec2 b, float t)
            => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        public override string ToString() => $"({X:F3}, {Y:F3})";
    }

    [Serializable]
    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static float Distance(Vec3 a, Vec3 b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }
}
