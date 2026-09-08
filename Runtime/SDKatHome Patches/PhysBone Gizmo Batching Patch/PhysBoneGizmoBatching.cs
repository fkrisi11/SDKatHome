#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Makes PhysBone gizmo drawing cheap enough to leave on during play mode.
    ///
    /// What actually costs: measured on a real avatar, the bone capsules are NOT the bottleneck.
    /// Unity's immediate GL already merges consecutive same-state GL.Begin blocks, so collapsing
    /// them changes nothing. The cost is the ANGLE LIMIT gizmos, which the SDK draws with
    /// immediate-mode Handles calls, once per bone:
    ///
    ///   Handles.DrawSolidArc  -> Shader.SetGlobalColor + SetGlobalFloat + ApplyWireMaterial +
    ///                            GL.Begin(TRIANGLES) with 354 GL.Vertex / 59 GL.Color
    ///                            (every triangle emitted twice, once per winding)
    ///   Handles.DrawWireDisc  -> 4x material.SetVector + SetPass + DrawProceduralNow
    ///   Handles.DrawLine  x4  -> each one its own ApplyWireMaterial + GL.Begin/End
    ///
    /// That is ~420 GL interop calls and ~6 material state changes PER BONE.
    ///
    /// MEASURED RESULT (real avatar, tens of thousands of bone-draws sampled per mode, every
    /// sample cross-checked by predicting its vertex totals from the hit counters):
    ///
    ///   Stock                             16.70 us/bone   336 verts/bone
    ///   Batching alone                    16.69           336   (0%, i.e. noise)
    ///   + fast geometry, 60-seg arcs      14.73           336   (-12%)   = "Full detail arcs"
    ///   + 20-segment arcs                 10.06           150   (-40%)   = "Fast"
    ///   + no limit fills                   8.78            96   (-47%)   = "Fastest"
    ///   Gizmos skipped entirely            0.04             0   (ceiling)
    ///
    /// The first, second and last rows were measurement-only harnesses and are not selectable
    /// modes: "Stock" is what you get by disabling the patch, and skipping gizmos outright was
    /// only ever a way to establish the ceiling.
    ///
    /// Batching the submissions is NOT what pays off - Unity already merges same-state GL blocks,
    /// and the material state changes did not dominate either. Two things actually paid:
    ///
    ///   1. Emitting less geometry. Unity hardcodes 60 segments per arc and the SDK's polar limits
    ///      draw five arcs per bone, so 60 -> 20 segments is the single biggest win available.
    ///   2. Killing the 110 quaternion rotations per bone in AddCapsuleToBatch (see
    ///      AddCapsulePrefix). Worth a measured 1.96 us/bone on its own.
    ///
    /// Cost decomposition solved from the three clean data points:
    ///   fixed CPU work per bone  6.16 us (61% of the "Fast" total) - transform reads, curve
    ///                            evals, Matrix4x4.TRS, and the List<Vector3> round trip
    ///   line vertices            0.0274 us each
    ///   triangle vertices        0.0237 us each
    ///
    /// The interception machinery is kept because it is the MECHANISM that makes segment count,
    /// fill suppression and geometry generation controllable at all - the batching itself is
    /// roughly free, not a win.
    ///
    /// What is left: the 6.16 us fixed cost is the SDK's own per-bone loop. Bypassing the
    /// List<Vector3> round trip (~112 Add + 112 indexer reads per bone) is worth maybe 0.6 us but
    /// needs offset bookkeeping across all three Add*ToBatch writers. Beyond that the only lever
    /// is drawing fewer bones, i.e. screen-size culling, which changes what the user sees.
    ///
    /// Arc geometry is produced by Unity's own internal SetDiscSectionPoints, so shapes stay
    /// identical to stock apart from the deliberate segment reduction.
    ///
    /// Colliders are additionally de-duplicated: the SDK calls VRCPhysBoneColliderEditor.DrawGizmos
    /// from INSIDE the per-bone loop, so M colliders get drawn N times over.
    /// </summary>
    [HarmonyPatch]
    public class PhysBoneGizmoBatching : SDKPatchBase
    {
        public override string PatchName => "Faster PhysBone Gizmos";

        public override string Description => "Lowers PhysBone gizmo performance cost";

        public override string Category => "Avatar Tools";

        public override SDKatHomePatcher.PatchUIType UIType => SDKatHomePatcher.PatchUIType.SingleSelect;

        public override string[] Options => new[]
        {
            "Fast - low-detail arcs (recommended)",
            "Fastest - low-detail, no limit fills",
            "Full detail arcs"
        };

        public override int DefaultOption => 0;

        public override bool UsePrefix => true;
        public override bool UsePostfix => true;
        public override bool EnabledByDefault => true;

        public static MethodBase TargetMethod()
        {
            Type editorType = AccessTools.TypeByName("VRC.SDK3.Dynamics.PhysBone.VRCPhysBoneEditor");
            if (editorType == null)
            {
                Warn("could not find VRCPhysBoneEditor.");
                return null;
            }

            // private static void VRCPhysBoneEditor.Draw(VRCPhysBone script, bool selected)
            // Only caller is the [DrawGizmo] entry point OnDrawGizmos, so this is the whole
            // per-component gizmo pass and a safe place to open/close a batch scope.
            MethodInfo draw = AccessTools.Method(editorType, "Draw", new[] { typeof(VRCPhysBone), typeof(bool) });
            if (draw == null)
            {
                Warn("could not find VRCPhysBoneEditor.Draw(VRCPhysBone, bool).");
                return null;
            }

            PhysBoneGizmoBatcher.InstallInterceptors();
            return draw;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            PhysBoneGizmoBatcher.BeginScope();
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            PhysBoneGizmoBatcher.EndScope();
        }

        internal static void Warn(string message)
        {
            Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> Faster PhysBone Gizmos: {message}");
        }
    }

    /// <summary>
    /// Collects everything one PhysBone's gizmo pass wants to draw into a line buffer and a
    /// triangle buffer, both in world space, and submits each once. Baking Handles.matrix into
    /// the vertices on the CPU is what lets separate primitives share a single submission.
    /// </summary>
    internal static class PhysBoneGizmoBatcher
    {
        private const string HarmonyId = "com.tohruthedragon.sdkathome.physbonegizmobatching";

        private const int SphereVertexCount = 75;   // 3 strips of 25
        private const int CapsuleVertexCount = 110; // 8 side + 25 + 25 + 13 + 13 + 13 + 13
        // Unity hardcodes 60 points per arc (Handles.s_WireArcPoints). SetDiscSectionPoints fills
        // whatever array length it is given - Unity itself uses 30 elsewhere - so the segment
        // count is ours to choose once we generate the geometry.
        private const int ArcPointsFull = 60;
        private const int ArcPointsLow = 20;

        private const int ModeFast = 0;      // low-detail arcs, fills on
        private const int ModeFastest = 1;   // low-detail arcs, fills off
        private const int ModeFull = 2;      // 60-segment arcs, fills on

        // Only ever checked at a primitive boundary -- flushing mid-primitive would orphan indices.
        private const int MaxVerticesPerFlush = 200000;

        private static readonly Bounds HugeBounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);

        // Cached so the per-scope settings lookup is a plain dictionary hit; GetPatchInfo(Type)
        // would allocate via Activator.CreateInstance on every call.
        private static readonly string PatchName = new PhysBoneGizmoBatching().PatchName;

        // Line batch: indexed, so line strips cost no duplicated vertices.
        private static readonly List<Vector3> _lineVerts = new List<Vector3>(8192);
        private static readonly List<Color32> _lineColors = new List<Color32>(8192);
        private static int[] _lineIndices = new int[16384];
        private static int _lineIndexCount;

        // Triangle batch: solid arcs, emitted sequentially.
        private static readonly List<Vector3> _triVerts = new List<Vector3>(8192);
        private static readonly List<Color32> _triColors = new List<Color32>(8192);
        private static int[] _seqIndices = new int[0];

        // HandlesUtil regenerates capsule/sphere points with 110 Quaternion*Vector3 operations per
        // bone, reading a List<Quaternion> through a property getter each iteration. Rotation is
        // linear, so rotation * (axis * radius) == (rotation * axis) * radius and the rotated unit
        // vectors are constant. Precomputing them turns each point into three multiplies.
        private static Vector3[] _sphereFwd, _sphereUp, _capFwd, _capUp, _sideDirs;
        private static bool _unitTablesReady;

        private static readonly Vector3[] _arcFull = new Vector3[ArcPointsFull];
        private static readonly Vector3[] _arcLow = new Vector3[ArcPointsLow];
        private static Vector3[] _arcPoints = _arcFull;
        private static bool _drawFills = true;

        // DrawSolidArc sets these globals per call; we set them once per flush instead so the
        // wire material does not read a stale value.
        private static readonly int PropHandleColor = Shader.PropertyToID("_HandleColor");
        private static readonly int PropHandleSize = Shader.PropertyToID("_HandleSize");
        private static Color _firstSolidArcColor = Color.white;
        private static bool _hasSolidArcColor;

        private static readonly HashSet<int> _drawnColliders = new HashSet<int>();

        private static Mesh _lineMesh, _triMesh;
        private static bool _useMesh;
        private static bool _meshPathBroken;
        private static bool _installed;
        private static int _mode;

        private delegate void SetDiscPointsDelegate(
            Vector3[] dest, Vector3 center, Vector3 normal, Vector3 from, float angle, float radius);

        private static SetDiscPointsDelegate _setDiscPoints;

        private static Type _handlesUtilType;

        internal static bool Healthy { get; private set; }

        /// <summary>True when limit-gizmo interception is available (needs SetDiscSectionPoints).</summary>
        internal static bool LimitBatchingReady => _setDiscPoints != null;

        /// <summary>Set only for the duration of VRCPhysBoneEditor.Draw.</summary>
        internal static bool Active;

        #region Install

        internal static void InstallInterceptors()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                _handlesUtilType = AccessTools.TypeByName("HandlesUtil");
                if (_handlesUtilType == null)
                {
                    PhysBoneGizmoBatching.Warn("could not find HandlesUtil - batching disabled, SDK drawing left as-is.");
                    return;
                }

                Type bufferRef = typeof(List<Vector3>).MakeByRefType();

                MethodInfo lineMethod = AccessTools.Method(_handlesUtilType, "DrawLineBatched",
                    new[] { bufferRef, typeof(int), typeof(Color) });
                MethodInfo sphereMethod = AccessTools.Method(_handlesUtilType, "DrawSphereBatched",
                    new[] { bufferRef, typeof(int), typeof(Color), typeof(Matrix4x4) });
                MethodInfo capsuleMethod = AccessTools.Method(_handlesUtilType, "DrawCapsuleBatched",
                    new[] { bufferRef, typeof(int), typeof(Color), typeof(Matrix4x4) });

                if (lineMethod == null || sphereMethod == null || capsuleMethod == null)
                {
                    PhysBoneGizmoBatching.Warn(
                        $"could not resolve the HandlesUtil batched draw methods " +
                        $"(line={lineMethod != null}, sphere={sphereMethod != null}, capsule={capsuleMethod != null}) " +
                        "- batching disabled, SDK drawing left as-is.");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(lineMethod, Hook(nameof(LinePrefix)));
                harmony.Patch(sphereMethod, Hook(nameof(SpherePrefix)));
                harmony.Patch(capsuleMethod, Hook(nameof(CapsulePrefix)));

                InstallGeometryInterceptors(harmony);
                InstallLimitInterceptors(harmony);

                // The SDK redraws every collider once per bone. De-duplicating is a pure win.
                Type colliderEditor = AccessTools.TypeByName("VRC.SDK3.Dynamics.PhysBone.VRCPhysBoneColliderEditor");
                MethodInfo colliderDraw = colliderEditor == null
                    ? null
                    : AccessTools.Method(colliderEditor, "DrawGizmos",
                        new[] { typeof(VRCPhysBoneCollider), typeof(bool) });

                if (colliderDraw != null)
                    harmony.Patch(colliderDraw, Hook(nameof(ColliderPrefix)));
                else
                    PhysBoneGizmoBatching.Warn("could not find VRCPhysBoneColliderEditor.DrawGizmos; " +
                                               "colliders will still be redrawn once per bone.");

                AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
                AssemblyReloadEvents.beforeAssemblyReload += Cleanup;

                Healthy = true;
            }
            catch (Exception e)
            {
                PhysBoneGizmoBatching.Warn($"interceptor install failed, leaving stock drawing in place: {e}");
                Healthy = false;
            }
        }

        /// <summary>
        /// Replaces the per-bone capsule/sphere point generation with a precomputed-unit-vector
        /// version. Same output, without the 110 quaternion rotations per bone.
        /// </summary>
        private static void InstallGeometryInterceptors(Harmony harmony)
        {
            Type bufferRef = typeof(List<Vector3>).MakeByRefType();

            MethodInfo addCapsuleMethod = AccessTools.Method(_handlesUtilType, "AddCapsuleToBatch",
                new[] { bufferRef, typeof(float), typeof(float), typeof(float) });
            MethodInfo addSphereMethod = AccessTools.Method(_handlesUtilType, "AddSphereToBatch",
                new[] { bufferRef, typeof(float) });

            if (addCapsuleMethod == null && addSphereMethod == null)
            {
                PhysBoneGizmoBatching.Warn("could not resolve HandlesUtil.Add*ToBatch; " +
                                           "per-bone geometry generation stays stock.");
                return;
            }

            BuildUnitTables();

            if (addCapsuleMethod != null) harmony.Patch(addCapsuleMethod, Hook(nameof(AddCapsulePrefix)));
            if (addSphereMethod != null) harmony.Patch(addSphereMethod, Hook(nameof(AddSpherePrefix)));
        }

        /// <summary>
        /// The angle-limit path is where the time actually goes, so these are the important ones.
        /// Unity's internal SetDiscSectionPoints is reused verbatim to keep arc geometry identical;
        /// if it cannot be resolved we simply leave the limit drawing stock.
        /// </summary>
        private static void InstallLimitInterceptors(Harmony harmony)
        {
            MethodInfo setDiscPoints = AccessTools.Method(typeof(Handles), "SetDiscSectionPoints",
                new[] { typeof(Vector3[]), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });

            if (setDiscPoints == null)
            {
                PhysBoneGizmoBatching.Warn("could not resolve Handles.SetDiscSectionPoints; " +
                                           "angle-limit gizmos will stay unbatched (this is the expensive path).");
                return;
            }

            _setDiscPoints = (SetDiscPointsDelegate)Delegate.CreateDelegate(typeof(SetDiscPointsDelegate), setDiscPoints);

            MethodInfo solidArcMethod = AccessTools.Method(typeof(Handles), "DrawSolidArc",
                new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
            MethodInfo wireDiscMethod = AccessTools.Method(typeof(Handles), "DrawWireDisc",
                new[] { typeof(Vector3), typeof(Vector3), typeof(float) });
            MethodInfo wireArcMethod = AccessTools.Method(typeof(Handles), "DrawWireArc",
                new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
            MethodInfo coneMethod = AccessTools.Method(_handlesUtilType, "DrawWireAngleCone",
                new[] { typeof(Vector3), typeof(Quaternion), typeof(float), typeof(float), typeof(int) });

            if (solidArcMethod != null) harmony.Patch(solidArcMethod, Hook(nameof(SolidArcPrefix)));
            if (wireDiscMethod != null) harmony.Patch(wireDiscMethod, Hook(nameof(WireDiscPrefix)));
            if (wireArcMethod != null) harmony.Patch(wireArcMethod, Hook(nameof(WireArcPrefix)));
            if (coneMethod != null) harmony.Patch(coneMethod, Hook(nameof(WireAngleConePrefix)));
        }

        private static HarmonyMethod Hook(string methodName)
        {
            var method = typeof(PhysBoneGizmoBatcher)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
                throw new MissingMethodException($"PhysBoneGizmoBatcher.{methodName} not found");

            return new HarmonyMethod(method);
        }

        private static void Cleanup()
        {
            if (_lineMesh != null) { UnityEngine.Object.DestroyImmediate(_lineMesh); _lineMesh = null; }
            if (_triMesh != null) { UnityEngine.Object.DestroyImmediate(_triMesh); _triMesh = null; }
        }

        #endregion

        #region Scope

        internal static void BeginScope()
        {
            _mode = SDKatHomePatcher.GetPatchInfo(PatchName).SelectedOption;

            _useMesh = !_meshPathBroken;
            _arcPoints = _mode == ModeFull ? _arcFull : _arcLow;
            _drawFills = _mode != ModeFastest;

            Reset();
            _drawnColliders.Clear();

            Active = Healthy;
        }

        internal static void EndScope()
        {
            // The SDK calls HandlesUtil.ApplyWireMaterial() just before its batched bone draws, so
            // the wire material pass is still bound here and we can submit straight away.
            try
            {
                if (Active) Flush();
            }
            finally
            {
                Active = false;
            }
        }

        #endregion

        #region Bone interceptors

        private static bool LinePrefix(ref List<Vector3> buffer, int offset, Color color, ref int __result)
        {
            if (!Active) return true;
            FlushIfFull();

            int baseIndex = _lineVerts.Count;
            AddLineVertex(buffer[offset], color);
            AddLineVertex(buffer[offset + 1], color);
            AddSegment(baseIndex, baseIndex + 1);

            __result = 2;
            return false;
        }

        private static bool SpherePrefix(ref List<Vector3> buffer, int offset, Color color, Matrix4x4 matrix, ref int __result)
        {
            if (!Active) return true;
            FlushIfFull();

            int cursor = offset;
            for (int strip = 0; strip < 3; strip++)
                cursor = AppendStrip(buffer, cursor, 25, ref matrix, color);

            __result = offset + SphereVertexCount;
            return false;
        }

        private static bool CapsulePrefix(ref List<Vector3> buffer, int offset, Color color, Matrix4x4 matrix, ref int __result)
        {
            if (!Active) return true;
            FlushIfFull();

            // 4 cylinder side lines, stored as consecutive pairs.
            for (int i = 0; i < 8; i += 2)
            {
                int baseIndex = _lineVerts.Count;
                AddLineVertex(matrix.MultiplyPoint3x4(buffer[offset + i]), color);
                AddLineVertex(matrix.MultiplyPoint3x4(buffer[offset + i + 1]), color);
                AddSegment(baseIndex, baseIndex + 1);
            }

            int cursor = offset + 8;
            cursor = AppendStrip(buffer, cursor, 25, ref matrix, color); // begin cap ring
            cursor = AppendStrip(buffer, cursor, 25, ref matrix, color); // end cap ring
            cursor = AppendStrip(buffer, cursor, 13, ref matrix, color); // begin cap arcs
            cursor = AppendStrip(buffer, cursor, 13, ref matrix, color);
            cursor = AppendStrip(buffer, cursor, 13, ref matrix, color); // end cap arcs
            cursor = AppendStrip(buffer, cursor, 13, ref matrix, color);

            __result = offset + CapsuleVertexCount;
            return false;
        }

        private static bool ColliderPrefix(VRCPhysBoneCollider script)
        {
            // Outside a PhysBone draw this is the collider's own [DrawGizmo] pass - never suppress it.
            if (!Active || script == null) return true;

            // Inside one, the SDK asks for the same collider once per bone. Selected colliders draw
            // at full alpha, so collapsing the repeats is visually identical.
            return _drawnColliders.Add(script.GetInstanceID());
        }

        #endregion

        #region Geometry generation (replaces HandlesUtil's per-bone quaternion work)

        /// <summary>
        /// Drop-in replacement for HandlesUtil.AddCapsuleToBatch. Emits the same 110 points in the
        /// same order, but from precomputed unit vectors instead of 110 quaternion rotations.
        /// </summary>
        private static bool AddCapsulePrefix(ref List<Vector3> buffer, float beginRadius, float endRadius, float half, ref int __result)
        {
            if (!Active || !_unitTablesReady) return true;

            // 8 cylinder side points: rotation is about up, so y is just -/+ half.
            for (int i = 0; i < 4; i++)
            {
                Vector3 d = _sideDirs[i];
                buffer.Add(new Vector3(d.x * beginRadius, -half, d.z * beginRadius));
                buffer.Add(new Vector3(d.x * endRadius, half, d.z * endRadius));
            }

            // 50 cylinder cap points.
            for (int i = 0; i < 25; i++)
            {
                Vector3 u = _sphereFwd[i];
                buffer.Add(new Vector3(u.x * beginRadius, u.y * beginRadius - half, u.z * beginRadius));
            }
            for (int i = 0; i < 25; i++)
            {
                Vector3 u = _sphereFwd[i];
                buffer.Add(new Vector3(u.x * endRadius, u.y * endRadius + half, u.z * endRadius));
            }

            // 52 capsule cap points.
            AddCapPoints(buffer, _capFwd, 26, beginRadius, -half);
            AddCapPoints(buffer, _capFwd, 39, endRadius, half);
            AddCapPoints(buffer, _capUp, 52, beginRadius, -half);
            AddCapPoints(buffer, _capUp, 65, endRadius, half);

            __result = CapsuleVertexCount;
            return false;
        }

        private static void AddCapPoints(List<Vector3> buffer, Vector3[] table, int start, float radius, float yOffset)
        {
            for (int i = 0; i < 13; i++)
            {
                Vector3 u = table[start + i];
                buffer.Add(new Vector3(u.x * radius, u.y * radius + yOffset, u.z * radius));
            }
        }

        /// <summary>Drop-in replacement for HandlesUtil.AddSphereToBatch.</summary>
        private static bool AddSpherePrefix(ref List<Vector3> buffer, float radius, ref int __result)
        {
            if (!Active || !_unitTablesReady) return true;

            for (int i = 0; i < 25; i++)
            {
                Vector3 u = _sphereFwd[i];
                buffer.Add(new Vector3(u.x * radius, u.y * radius, u.z * radius));
            }
            for (int i = 25; i < 50; i++)
            {
                Vector3 u = _sphereFwd[i];
                buffer.Add(new Vector3(u.x * radius, u.y * radius, u.z * radius));
            }
            for (int i = 50; i < 75; i++)
            {
                Vector3 u = _sphereUp[i];
                buffer.Add(new Vector3(u.x * radius, u.y * radius, u.z * radius));
            }

            __result = SphereVertexCount;
            return false;
        }

        #endregion

        #region Limit interceptors (the expensive path)

        /// <summary>Replaces HandlesUtil.DrawWireAngleCone: 4x Handles.DrawLine + a DrawWireDisc,
        /// each of which would otherwise set up its own material pass.</summary>
        private static bool WireAngleConePrefix(Vector3 tip, Quaternion rotation, float radius, float angle, int segments)
        {
            if (!Active || _setDiscPoints == null) return true;
            if (!IsRepaint()) return false;
            FlushIfFull();

            Matrix4x4 m = Handles.matrix;
            Color c = Handles.color;

            // Mirrors the SDK's own geometry exactly.
            Vector3 rim = Quaternion.AngleAxis(angle, Vector3.forward) * new Vector3(0f, radius, 0f);
            for (int i = 0; i < segments; i++)
            {
                Vector3 spoke = Quaternion.AngleAxis((float)i / segments * 360f, Vector3.up) * rim;
                int baseIndex = _lineVerts.Count;
                AddLineVertex(m.MultiplyPoint3x4(tip), c);
                AddLineVertex(m.MultiplyPoint3x4(tip + rotation * spoke), c);
                AddSegment(baseIndex, baseIndex + 1);
            }

            AddDisc(m, c, tip + rotation * new Vector3(0f, rim.y, 0f), rotation * Vector3.up, rim.x);
            return false;
        }

        /// <summary>Replaces Handles.DrawSolidArc, the single most expensive call in the pass:
        /// 2 global shader sets + a SetPass + 354 GL.Vertex, per bone.</summary>
        private static bool SolidArcPrefix(Vector3 center, Vector3 normal, Vector3 from, float angle, float radius)
        {
            if (!Active || _setDiscPoints == null) return true;
            if (!IsRepaint()) return false;

            // Translucent fills are the heaviest thing on screen; suppressing them isolates
            // GPU fill rate from CPU geometry cost.
            if (!_drawFills) return false;

            FlushIfFull();

            Matrix4x4 m = Handles.matrix;
            Color c = Handles.color;

            if (!_hasSolidArcColor)
            {
                _firstSolidArcColor = c;
                _hasSolidArcColor = true;
            }

            _setDiscPoints(_arcPoints, center, normal, from, angle, radius);

            Vector3 hub = m.MultiplyPoint3x4(center);
            Vector3 previous = m.MultiplyPoint3x4(_arcPoints[0]);
            int arcCount = _arcPoints.Length;

            for (int i = 1; i < arcCount; i++)
            {
                Vector3 current = m.MultiplyPoint3x4(_arcPoints[i]);

                // Both windings, matching the original so the fill stays double-sided.
                AddTriangle(hub, previous, current, c);
                AddTriangle(hub, current, previous, c);

                previous = current;
            }

            return false;
        }

        private static bool WireDiscPrefix(Vector3 center, Vector3 normal, float radius)
        {
            if (!Active || _setDiscPoints == null) return true;
            if (!IsRepaint()) return false;
            FlushIfFull();

            AddDisc(Handles.matrix, Handles.color, center, normal, radius);
            return false;
        }

        private static bool WireArcPrefix(Vector3 center, Vector3 normal, Vector3 from, float angle, float radius)
        {
            if (!Active || _setDiscPoints == null) return true;
            if (!IsRepaint()) return false;
            FlushIfFull();

            AddArc(Handles.matrix, Handles.color, center, normal, from, angle, radius);
            return false;
        }

        #endregion

        #region Accumulation

        /// <summary>
        /// Rebuilds HandlesUtil's rotation tables exactly as it does, then bakes each rotation
        /// into the unit vector it is always applied to. Same construction order and the same
        /// Quaternion.AngleAxis calls, so the resulting geometry matches the SDK's.
        /// </summary>
        private static void BuildUnitTables()
        {
            if (_unitTablesReady) return;
            _unitTablesReady = true;

            // SphereGizmoRotations: 3 axes x 25 steps of 15 degrees.
            var sphereAxes = new[] { Vector3.up, Vector3.right, Vector3.forward };
            var sphereRot = new Quaternion[75];
            for (int i = 0, n = 0; i < 3; i++)
                for (int j = 0; j < 25; j++, n++)
                    sphereRot[n] = Quaternion.AngleAxis(15f * j, sphereAxes[i]);

            // CapsuleGizmoRotations: 6 (axis, offset) pairs x 13 steps of 15 degrees.
            var capAxes = new[] { Vector3.up, Vector3.up, Vector3.right, Vector3.right, Vector3.forward, Vector3.forward };
            var capOffsets = new[] { 90f, -90f, 0f, -180f, 90f, -90f };
            var capRot = new Quaternion[78];
            for (int i = 0, n = 0; i < 6; i++)
                for (int j = 0; j < 13; j++, n++)
                    capRot[n] = Quaternion.AngleAxis(15f * j + capOffsets[i], capAxes[i]);

            _sphereFwd = new Vector3[75];
            _sphereUp = new Vector3[75];
            for (int i = 0; i < 75; i++)
            {
                _sphereFwd[i] = sphereRot[i] * Vector3.forward;
                _sphereUp[i] = sphereRot[i] * Vector3.up;
            }

            _capFwd = new Vector3[78];
            _capUp = new Vector3[78];
            for (int i = 0; i < 78; i++)
            {
                _capFwd[i] = capRot[i] * Vector3.forward;
                _capUp[i] = capRot[i] * Vector3.up;
            }

            // Cylinder sides rotate about up, which leaves y untouched.
            _sideDirs = new Vector3[4];
            for (int i = 0; i < 4; i++)
                _sideDirs[i] = Quaternion.AngleAxis(i / 4f * 360f, Vector3.up) * Vector3.right;
        }

        /// <summary>The Handles arc methods no-op outside Repaint; mirror that so we never
        /// accumulate geometry Unity would not have drawn.</summary>
        private static bool IsRepaint()
        {
            Event e = Event.current;
            return e != null && e.type == EventType.Repaint;
        }

        /// <summary>Mirrors Handles.DrawWireDisc's choice of start vector.</summary>
        private static void AddDisc(Matrix4x4 m, Color c, Vector3 center, Vector3 normal, float radius)
        {
            Vector3 from = Vector3.Cross(normal, Vector3.up);
            if (from.sqrMagnitude < 0.001f)
                from = Vector3.Cross(normal, Vector3.right);

            AddArc(m, c, center, normal, from, 360f, radius);
        }

        private static void AddArc(Matrix4x4 m, Color c, Vector3 center, Vector3 normal, Vector3 from, float angle, float radius)
        {
            int count = _arcPoints.Length;
            _setDiscPoints(_arcPoints, center, normal, from, angle, radius);

            int baseIndex = _lineVerts.Count;
            for (int i = 0; i < count; i++)
                AddLineVertex(m.MultiplyPoint3x4(_arcPoints[i]), c);

            for (int i = 0; i < count - 1; i++)
                AddSegment(baseIndex + i, baseIndex + i + 1);
        }

        private static int AppendStrip(List<Vector3> buffer, int offset, int count, ref Matrix4x4 matrix, Color color)
        {
            int baseIndex = _lineVerts.Count;

            for (int i = 0; i < count; i++)
                AddLineVertex(matrix.MultiplyPoint3x4(buffer[offset + i]), color);

            // GL.LINE_STRIP -> GL.LINES. Indices express the topology, so no vertex duplication.
            for (int i = 0; i < count - 1; i++)
                AddSegment(baseIndex + i, baseIndex + i + 1);

            return offset + count;
        }

        private static void AddLineVertex(Vector3 position, Color32 color)
        {
            _lineVerts.Add(position);
            _lineColors.Add(color);
        }

        private static void AddSegment(int a, int b)
        {
            if (_lineIndexCount + 2 > _lineIndices.Length)
                Array.Resize(ref _lineIndices, _lineIndices.Length * 2);

            _lineIndices[_lineIndexCount++] = a;
            _lineIndices[_lineIndexCount++] = b;
        }

        private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Color32 color)
        {
            _triVerts.Add(a); _triColors.Add(color);
            _triVerts.Add(b); _triColors.Add(color);
            _triVerts.Add(c); _triColors.Add(color);
        }

        /// <summary>Only safe between primitives - flushing mid-primitive would orphan indices.</summary>
        private static void FlushIfFull()
        {
            if (_lineVerts.Count >= MaxVerticesPerFlush || _triVerts.Count >= MaxVerticesPerFlush)
                Flush();
        }

        private static void Reset()
        {
            _lineVerts.Clear();
            _lineColors.Clear();
            _lineIndexCount = 0;
            _triVerts.Clear();
            _triColors.Clear();
            _hasSolidArcColor = false;
        }

        #endregion

        #region Submission

        private static void Flush()
        {
            if (_lineIndexCount == 0 && _triVerts.Count == 0)
            {
                Reset();
                return;
            }


            // DrawSolidArc would have set these per call; do it once for the whole batch so the
            // material never reads a stale handle colour.
            if (_triVerts.Count > 0 && _hasSolidArcColor)
            {
                Shader.SetGlobalColor(PropHandleColor, _firstSolidArcColor * new Color(1f, 1f, 1f, 0.5f));
                Shader.SetGlobalFloat(PropHandleSize, 1f);
            }

            try
            {
                if (_useMesh) DrawAsMesh();
                else DrawAsGL();
            }
            catch (Exception e)
            {
                // Sticky: without this the next BeginScope would read the pref again and retry
                // the mesh path on every single component.
                if (!_meshPathBroken)
                {
                    _meshPathBroken = true;
                    PhysBoneGizmoBatching.Warn($"batched mesh draw failed, using GL for the rest of this session: {e.Message}");
                }
                _useMesh = false;
            }
            finally
            {
                Reset();
            }
        }

        private static int[] SequentialIndices(int count)
        {
            if (_seqIndices.Length < count)
            {
                int size = Mathf.NextPowerOfTwo(Mathf.Max(count, 1024));
                _seqIndices = new int[size];
                for (int i = 0; i < size; i++) _seqIndices[i] = i;
            }
            return _seqIndices;
        }

        private static Mesh MakeMesh(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32
            };
            mesh.MarkDynamic();
            return mesh;
        }

        private static void DrawAsMesh()
        {
            // Fills first so the wireframes sit on top of them, as in the stock per-bone order.
            if (_triVerts.Count > 0)
            {
                if (_triMesh == null) _triMesh = MakeMesh("SDKatHome_PhysBoneGizmoTris");

                _triMesh.Clear(false);
                _triMesh.subMeshCount = 1;
                _triMesh.SetVertices(_triVerts);
                _triMesh.SetColors(_triColors);
                _triMesh.SetIndices(SequentialIndices(_triVerts.Count), 0, _triVerts.Count,
                    MeshTopology.Triangles, 0, false);
                _triMesh.bounds = HugeBounds;

                Graphics.DrawMeshNow(_triMesh, Matrix4x4.identity);
            }

            if (_lineIndexCount > 0)
            {
                if (_lineMesh == null) _lineMesh = MakeMesh("SDKatHome_PhysBoneGizmoLines");

                _lineMesh.Clear(false);
                _lineMesh.subMeshCount = 1;
                _lineMesh.SetVertices(_lineVerts);
                _lineMesh.SetColors(_lineColors);
                _lineMesh.SetIndices(_lineIndices, 0, _lineIndexCount, MeshTopology.Lines, 0, false);
                _lineMesh.bounds = HugeBounds;

                Graphics.DrawMeshNow(_lineMesh, Matrix4x4.identity);
            }
        }

        private static void DrawAsGL()
        {
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);

            if (_triVerts.Count > 0)
            {
                GL.Begin(GL.TRIANGLES);
                for (int i = 0; i < _triVerts.Count; i++)
                {
                    GL.Color(_triColors[i]);
                    GL.Vertex(_triVerts[i]);
                }
                GL.End();
            }

            if (_lineIndexCount > 0)
            {
                GL.Begin(GL.LINES);
                for (int i = 0; i < _lineIndexCount; i++)
                {
                    int index = _lineIndices[i];
                    GL.Color(_lineColors[index]);
                    GL.Vertex(_lineVerts[index]);
                }
                GL.End();
            }

            GL.PopMatrix();
        }

        #endregion

    }
}
#endif
