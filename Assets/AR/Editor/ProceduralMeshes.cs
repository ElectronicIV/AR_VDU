using System.Collections.Generic;
using UnityEngine;

namespace ARVDU.EditorTools
{
    /// <summary>
    /// Mesh builders for the parts of the marker models that Unity's primitives can't express.
    /// Everything is authored around the origin, one unit tall or wide, so the generated prefabs
    /// scale cleanly to whatever physical size the tracked picture turns out to be.
    /// </summary>
    public static class ProceduralMeshes
    {
        /// <summary>
        /// Accumulates independent quads and triangles. Vertices are never shared between faces,
        /// which is what gives the generated parts their faceted, machined look once
        /// <see cref="ToMesh"/> recalculates normals.
        /// </summary>
        class Builder
        {
            readonly List<Vector3> m_Vertices = new();
            readonly List<Vector2> m_Uvs = new();
            readonly List<int> m_Triangles = new();

            public void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
            {
                var i = m_Vertices.Count;
                m_Vertices.Add(a);
                m_Vertices.Add(b);
                m_Vertices.Add(c);
                m_Uvs.Add(new Vector2(0f, 0f));
                m_Uvs.Add(new Vector2(1f, 0f));
                m_Uvs.Add(new Vector2(0.5f, 1f));
                m_Triangles.Add(i);
                m_Triangles.Add(i + 1);
                m_Triangles.Add(i + 2);
            }

            public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var i = m_Vertices.Count;
                m_Vertices.Add(a);
                m_Vertices.Add(b);
                m_Vertices.Add(c);
                m_Vertices.Add(d);
                m_Uvs.Add(new Vector2(0f, 0f));
                m_Uvs.Add(new Vector2(1f, 0f));
                m_Uvs.Add(new Vector2(1f, 1f));
                m_Uvs.Add(new Vector2(0f, 1f));
                m_Triangles.Add(i);
                m_Triangles.Add(i + 1);
                m_Triangles.Add(i + 2);
                m_Triangles.Add(i);
                m_Triangles.Add(i + 2);
                m_Triangles.Add(i + 3);
            }

            public Mesh ToMesh(string name, bool smoothNormals = false)
            {
                var mesh = new Mesh { name = name };
                if (m_Vertices.Count > 65000)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

                mesh.SetVertices(m_Vertices);
                mesh.SetUVs(0, m_Uvs);
                mesh.SetTriangles(m_Triangles, 0);
                mesh.RecalculateNormals();

                if (smoothNormals)
                    WeldNormals(mesh);

                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }

            /// <summary>
            /// Faces are built from unshared vertices, so Unity's per-face normals come out
            /// faceted. For the curved parts we average the normals of every vertex that sits at
            /// the same position, which gives the same result as welding the seam first.
            /// </summary>
            static void WeldNormals(Mesh mesh)
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var accumulated = new Dictionary<Vector3Int, Vector3>();

                // Quantise to a 0.1 mm grid so floating-point drift doesn't split a seam.
                static Vector3Int Key(Vector3 v) => new(
                    Mathf.RoundToInt(v.x * 10000f),
                    Mathf.RoundToInt(v.y * 10000f),
                    Mathf.RoundToInt(v.z * 10000f));

                for (var i = 0; i < vertices.Length; i++)
                {
                    var key = Key(vertices[i]);
                    accumulated[key] = accumulated.TryGetValue(key, out var sum)
                        ? sum + normals[i]
                        : normals[i];
                }

                for (var i = 0; i < vertices.Length; i++)
                    normals[i] = accumulated[Key(vertices[i])].normalized;

                mesh.normals = normals;
            }
        }

        /// <summary>
        /// A spur gear lying in the XZ plane, extruded along Y, with a central bore.
        /// </summary>
        /// <param name="teeth">Number of teeth. Drives the gear ratio in the gear-train model.</param>
        /// <param name="pitchRadius">Radius of the pitch circle; teeth straddle it.</param>
        /// <param name="toothHeight">Total radial tooth depth, root to tip.</param>
        /// <param name="thickness">Extrusion along Y.</param>
        /// <param name="boreRadius">Radius of the central hole.</param>
        public static Mesh CreateGear(
            int teeth, float pitchRadius, float toothHeight, float thickness, float boreRadius)
        {
            teeth = Mathf.Max(6, teeth);
            var rootRadius = pitchRadius - toothHeight * 0.5f;
            var tipRadius = pitchRadius + toothHeight * 0.5f;
            var halfThickness = thickness * 0.5f;

            // Fraction of one tooth pitch, and the radius at that fraction. The flanks are the
            // gaps between these samples, so the tooth comes out trapezoidal.
            var profile = new[]
            {
                (t: 0.00f, r: rootRadius),
                (t: 0.30f, r: rootRadius),
                (t: 0.38f, r: tipRadius),
                (t: 0.62f, r: tipRadius),
                (t: 0.70f, r: rootRadius),
            };

            var count = teeth * profile.Length;
            var outerTop = new Vector3[count];
            var outerBottom = new Vector3[count];
            var boreTop = new Vector3[count];
            var boreBottom = new Vector3[count];

            for (var i = 0; i < teeth; i++)
            {
                for (var p = 0; p < profile.Length; p++)
                {
                    var index = i * profile.Length + p;
                    var angle = (i + profile[p].t) / teeth * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                    outerTop[index] = dir * profile[p].r + Vector3.up * halfThickness;
                    outerBottom[index] = dir * profile[p].r - Vector3.up * halfThickness;
                    boreTop[index] = dir * boreRadius + Vector3.up * halfThickness;
                    boreBottom[index] = dir * boreRadius - Vector3.up * halfThickness;
                }
            }

            var builder = new Builder();
            for (var i = 0; i < count; i++)
            {
                var j = (i + 1) % count;

                builder.AddQuad(boreTop[i], boreTop[j], outerTop[j], outerTop[i]);      // top face
                builder.AddQuad(outerBottom[i], outerBottom[j], boreBottom[j], boreBottom[i]); // bottom face
                builder.AddQuad(outerTop[i], outerTop[j], outerBottom[j], outerBottom[i]); // tooth flanks
                builder.AddQuad(boreBottom[i], boreBottom[j], boreTop[j], boreTop[i]);   // bore wall
            }

            return builder.ToMesh($"Gear_{teeth}T");
        }

        /// <summary>
        /// A parabolic dish opening along +Y, generated as a surface of revolution. Single-sided:
        /// the material it is given is expected to disable backface culling.
        /// </summary>
        public static Mesh CreateParabolicDish(
            float radius, float depth, int radialSegments = 48, int rings = 10)
        {
            var builder = new Builder();

            // y = depth * (r / radius)^2 -- a paraboloid, deepest at the rim.
            Vector3 Point(int ring, int seg)
            {
                var r = radius * ring / rings;
                var angle = seg / (float)radialSegments * Mathf.PI * 2f;
                var y = depth * (r / radius) * (r / radius);
                return new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r);
            }

            for (var ring = 0; ring < rings; ring++)
            {
                for (var seg = 0; seg < radialSegments; seg++)
                {
                    var nextSeg = (seg + 1) % radialSegments;
                    var a = Point(ring, seg);
                    var b = Point(ring, nextSeg);
                    var c = Point(ring + 1, nextSeg);
                    var d = Point(ring + 1, seg);

                    if (ring == 0)
                        builder.AddTriangle(a, c, d); // the innermost ring collapses to the apex
                    else
                        builder.AddQuad(a, b, c, d);
                }
            }

            return builder.ToMesh("ParabolicDish", smoothNormals: true);
        }

        /// <summary>
        /// A wind-turbine blade running along +Y from the hub, tapering and twisting toward the tip.
        /// The cross-section is a flattened ellipse standing in for an aerofoil.
        /// </summary>
        public static Mesh CreateTurbineBlade(
            float length,
            float rootChord,
            float tipChord,
            float thicknessRatio = 0.22f,
            float twistDegrees = 18f,
            int spanSegments = 14,
            int sectionSegments = 12)
        {
            var builder = new Builder();

            Vector3 Point(int span, int section)
            {
                var t = span / (float)spanSegments;
                var y = length * t;

                // Elliptical taper keeps the tip from ending in a hard chisel edge.
                var chord = Mathf.Lerp(rootChord, tipChord, t) * Mathf.Sqrt(1f - t * t * 0.35f);
                var thickness = chord * thicknessRatio;
                var twist = Mathf.Deg2Rad * Mathf.Lerp(twistDegrees, 0f, t);

                var angle = section / (float)sectionSegments * Mathf.PI * 2f;
                var x = Mathf.Cos(angle) * chord * 0.5f;
                var z = Mathf.Sin(angle) * thickness * 0.5f;

                // Rotate the section about the span axis to build in the twist.
                return new Vector3(
                    x * Mathf.Cos(twist) - z * Mathf.Sin(twist),
                    y,
                    x * Mathf.Sin(twist) + z * Mathf.Cos(twist));
            }

            for (var span = 0; span < spanSegments; span++)
            {
                for (var section = 0; section < sectionSegments; section++)
                {
                    var nextSection = (section + 1) % sectionSegments;
                    builder.AddQuad(
                        Point(span, section),
                        Point(span, nextSection),
                        Point(span + 1, nextSection),
                        Point(span + 1, section));
                }
            }

            // Cap the root so the blade isn't hollow where it meets the hub.
            var centre = Vector3.zero;
            for (var section = 0; section < sectionSegments; section++)
            {
                var nextSection = (section + 1) % sectionSegments;
                builder.AddTriangle(centre, Point(0, nextSection), Point(0, section));
            }

            return builder.ToMesh("TurbineBlade", smoothNormals: true);
        }
    }
}
