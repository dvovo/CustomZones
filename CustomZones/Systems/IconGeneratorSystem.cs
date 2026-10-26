// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Pictures for the buildings the game has no icon for.
//
// The game's thumbnail cache (Game.UI.Thumbnails.ThumbnailCache) does not render
// anything: it only looks icons up in pre-baked atlases, so a building that is in
// no atlas never gets a picture from the game. When Building Icon Generator is
// not installed, this system draws a simple one for those buildings:
//   - the building's meshes (RenderPrefab.ObtainMeshes / ObtainMaterials) are
//     rendered by an off-screen camera into a RenderTexture;
//   - the result is keyed out, trimmed and saved as a PNG in
//     {UserData}/ModsData/CustomZones/GeneratedIcons/BuildingPrefab.<name>.png;
//   - the folder is served to the UI as coui://czicons/ (the same mechanism and
//     file naming Asset Icon Library uses for coui://ail/).
//
// The PNGs stay on disk, so each building is drawn only once; deleting the
// folder (or a file) draws them again. Missing pictures are drawn while the
// loading screen is up, and later ones a few per frame, with the buildings on
// screen first. A building that cannot be drawn is not tried again and gets a
// procedural placeholder in the UI.
//
// Why the pictures are a neutral "white model": the textures of most building
// surfaces exist only in the game's virtual-texturing (VT) store. Their
// TextureAssets are placeholders without data, and the VT store is only fed
// through the game's own rendering of the world, never to an off-screen camera.
// Those surfaces are drawn with a neutral matte material instead. The few
// surfaces whose textures are ordinary assets are drawn with them, in colour.

namespace CustomZones.Systems
{
    using Colossal.IO.AssetDatabase;
    using Colossal.PSI.Environment;
    using Colossal.Serialization.Entities;
    using Colossal.UI;
    using Game;
    using Game.Prefabs;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Game.UI;
    using Unity.Collections;
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.Rendering.HighDefinition;

    /// <summary>
    /// Draws and caches a picture of every building the game has no icon for
    /// (see the file header).
    /// </summary>
    public partial class IconGeneratorSystem : GameSystemBase
    {
        /// <summary>coui host name; icons resolve as coui://czicons/&lt;file&gt;.</summary>
        public const string HostName = "czicons";

        /// <summary>File name prefix, as Asset Icon Library names its files.</summary>
        private const string FilePrefix = "BuildingPrefab.";

        /// <summary>
        /// Bump this whenever the rendering/encoding pipeline changes; cached
        /// icons from an older version are then discarded and regenerated.
        /// </summary>
        private const int PipelineVersion = 15;

        /// <summary>
        /// Render resolution, in pixels. The cards draw the pictures at about
        /// 150 px; trimming and quantising keep the files small.
        /// </summary>
        private const int IconSize = 256;

        /// <summary>Shader keyword that switches CS2 surfaces to virtual texturing.</summary>
        private const string VirtualTexturingKeyword = "ENABLE_VT";

        /// <summary>
        /// SurfaceAsset -> the material used to draw it (a clone without virtual
        /// texturing, or the neutral material). Many buildings share surfaces,
        /// so each one is converted once.
        /// </summary>
        private readonly Dictionary<SurfaceAsset, Material> m_IconMaterials = new();

        /// <summary>Surfaces whose real textures could be bound (rare; render in colour).</summary>
        private int m_TexturedSurfaces;

        /// <summary>Surfaces that could not be cloned; never retried.</summary>
        private readonly HashSet<SurfaceAsset> m_UnconvertibleSurfaces = new();

        /// <summary>Alpha at or below this counts as background.</summary>
        private const byte AlphaCutoff = 10;

        /// <summary>
        /// Summed per-channel colour spread under which the render is considered
        /// a flat, meaningless square (e.g. the all-white results).
        /// </summary>
        private const int UniformTolerance = 24;

        /// <summary>Pixels of margin kept around the trimmed building.</summary>
        private const int CropPadding = 2;

        /// <summary>
        /// Backdrop colour, keyed out after rendering. Magenta, because no
        /// building surface has that colour (Asset Icon Creator uses a magenta
        /// backdrop for the same reason); a dark backdrop would also key out
        /// dark roofs and shaded walls.
        /// </summary>
        private static readonly Color ChromaKeyColor = new(1f, 0f, 1f, 1f);

        /// <summary>Summed RGB distance at or below which a pixel IS backdrop.</summary>
        private const int BackgroundHardTolerance = 60;

        /// <summary>
        /// Distance above which a pixel is fully opaque. Between the two bounds
        /// the pixel fades proportionally, feathering the silhouette instead of
        /// leaving a hard aliased cut (AssetIconCreator feathers likewise).
        /// </summary>
        private const int BackgroundSoftTolerance = 150;

        /// <summary>How many individual failures get an explanatory log line.</summary>
        private const int MaxLoggedFailures = 25;

        private int m_LoggedFailures;

        /// <summary>
        /// Records why a building could not be turned into an icon. Always
        /// returns false so call sites can `return Fail(...)`. Only the first
        /// few are logged, to explain the failures without flooding the log.
        /// </summary>
        private bool Fail(string name, string reason)
        {
            if (m_LoggedFailures < MaxLoggedFailures)
            {
                m_LoggedFailures++;
                Mod.Log.Info($"IconGenerator: no icon for '{name}' — {reason}.");
            }

            return false;
        }

        /// <summary>Summed absolute RGB difference between two colours.</summary>
        private static int Distance(Color32 a, Color32 b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        /// <summary>True when a colour is close enough to count as backdrop.</summary>
        private static bool Similar(Color32 a, Color32 b)
            => Distance(a, b) <= BackgroundHardTolerance;

        /// <summary>Total bytes written this session (diagnostics).</summary>
        private long m_BytesWritten;

        /// <summary>
        /// Frames each building is rendered before it is captured. One is enough:
        /// waiting more frames does not bring virtual textures to an off-screen
        /// camera (see the file header). With more than one, the blocking pass
        /// during loading is skipped, because it cannot wait for frames.
        /// </summary>
        private static readonly int WarmupFrames = 1;

        private GameObject m_Subject;
        private string m_SubjectName;
        private int m_SubjectFramesLeft;
        private bool m_SubjectIsPriority;

        /// <summary>Unity layer used exclusively by the off-screen render.</summary>
        private const int RenderLayer = 31;

        /// <summary>Main light, in lux (HDRP directional lights are physical).</summary>
        private const float KeyLightLux = 25000f;

        /// <summary>Opposite-side light that keeps shadowed faces readable instead of black.</summary>
        private const float FillLightLux = 9000f;

        /// <summary>Soft light from above, so roofs separate from walls.</summary>
        private const float RimLightLux = 5000f;

        /// <summary>
        /// Fixed exposure (EV100). A surface of albedo ~0.5 lit by the key light
        /// reflects about lux * 0.5 / π ≈ 4000 nits, and middle grey sits at
        /// EV100 = log2(nits * 100 / 12.5) ≈ 15; the value is set slightly
        /// brighter than that. Raise it to darken the render, lower it to brighten.
        /// </summary>
        private const float FixedExposureEV = 14.6f;

        private string m_Folder;
        private bool m_HostRegistered;

        /// <summary>Prefab names already available on disk (cache index).</summary>
        private readonly HashSet<string> m_Cached = new();

        /// <summary>Prefab names whose render failed; never retried this session.</summary>
        private readonly HashSet<string> m_Failed = new();

        /// <summary>Background work list, consumed via <see cref="m_QueueCursor"/>.</summary>
        private readonly List<BuildingPrefab> m_Queue = new();

        /// <summary>Index of the next item to process in <see cref="m_Queue"/>.</summary>
        private int m_QueueCursor;

        /// <summary>
        /// Buildings the player is looking at now. They are drawn before the
        /// background queue, so whatever is on screen gets its picture within a
        /// frame or two.
        /// </summary>
        private readonly List<BuildingPrefab> m_Priority = new();

        /// <summary>Name -> prefab for everything still pending, for prioritisation.</summary>
        private readonly Dictionary<string, BuildingPrefab> m_PendingByName = new();

        /// <summary>Guards against queueing the same prefab twice.</summary>
        private readonly HashSet<string> m_Queued = new();

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_SpawnableQuery;

        private Camera m_Camera;
        private RenderTexture m_Target;
        private Texture2D m_ReadTexture;
        private GameObject m_Rig;
        private Light m_Light;

        /// <summary>How many icons to generate before letting the UI refresh.</summary>
        private const int NotifyBatch = 250;

        private int m_SinceNotify;
        private bool m_TookPriority;
        private BuildingIndexSystem m_IndexSystem;

        /// <summary>
        /// Resolved lazily (NOT in OnCreate): BuildingIndexSystem creates this
        /// system in its own OnCreate, so resolving it there would recurse.
        /// </summary>
        private BuildingIndexSystem IndexSystem
            => m_IndexSystem ??= World.GetExistingSystemManaged<BuildingIndexSystem>();

        /// <summary>Number of buildings still waiting to be rendered.</summary>
        public int Pending => (m_Queue.Count - m_QueueCursor) + m_Priority.Count;

        /// <summary>How many have been handled (generated or failed) this session.</summary>
        public int Processed { get; private set; }

        /// <summary>Total queued this session, for the progress readout.</summary>
        public int Total => Processed + Pending;

        /// <summary>
        /// Moves a building to the front of the line because it just became
        /// visible in the browser. No-op if it is already done or unknown.
        /// </summary>
        public void Prioritize(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) ||
                m_Cached.Contains(prefabName) ||
                m_Failed.Contains(prefabName) ||
                !m_PendingByName.TryGetValue(prefabName, out var prefab))
            {
                return;
            }

            if (!m_Priority.Contains(prefab))
            {
                m_Priority.Add(prefab);
            }
        }

        /// <summary>True once the queue has been fully processed.</summary>
        public bool IsIdle => Pending == 0;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_SpawnableQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingData>());

            try
            {
                m_Folder = Path.Combine(EnvPath.kUserDataPath, "ModsData", Mod.Id, "GeneratedIcons");
                Directory.CreateDirectory(m_Folder);

                // Pictures made by an older version of the drawing are dropped
                // and drawn again.
                InvalidateStaleCache();

                // Index what previous sessions already generated (the cache).
                foreach (var file in Directory.GetFiles(m_Folder, FilePrefix + "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith(FilePrefix, StringComparison.Ordinal))
                    {
                        m_Cached.Add(name.Substring(FilePrefix.Length));
                    }
                }

                // Serve the folder to the UI (the same mechanism Asset Icon Library uses).
                UIManager.defaultUISystem.AddHostLocation(HostName, m_Folder);
                m_HostRegistered = true;

                Mod.Log.Info($"IconGenerator: cache at '{m_Folder}' with {m_Cached.Count} icon(s) already generated; host coui://{HostName}/ registered.");
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "IconGenerator: could not initialize the icon cache/host.");
            }
        }

        /// <summary>
        /// Empties the cache when it was made with another
        /// <see cref="PipelineVersion"/>, so improvements reach existing pictures.
        /// </summary>
        private void InvalidateStaleCache()
        {
            var stamp = Path.Combine(m_Folder, "cache.version");

            try
            {
                if (File.Exists(stamp) &&
                    int.TryParse(File.ReadAllText(stamp).Trim(), out var version) &&
                    version == PipelineVersion)
                {
                    return;
                }

                var stale = Directory.GetFiles(m_Folder, FilePrefix + "*.png");
                foreach (var file in stale)
                {
                    File.Delete(file);
                }

                File.WriteAllText(stamp, PipelineVersion.ToString());

                if (stale.Length > 0)
                {
                    Mod.Log.Info($"IconGenerator: cleared {stale.Length} icon(s) from an older pipeline; they will be regenerated.");
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: cache invalidation skipped ({ex.Message}).");
            }
        }

        /// <summary>File of a building's generated picture, when it has one.</summary>
        public bool TryGetGeneratedPath(string prefabName, out string path)
        {
            path = m_Folder != null && m_Cached.Contains(prefabName ?? string.Empty)
                ? Path.Combine(m_Folder, $"{FilePrefix}{prefabName}.png")
                : null;
            return path != null;
        }

        /// <summary>Coui URL of a generated icon, or empty when this building has none yet.</summary>
        public string GetGeneratedIcon(string prefabName)
            => m_Cached.Contains(prefabName)
                ? $"coui://{HostName}/{FilePrefix}{prefabName}.png"
                : string.Empty;

        /// <summary>
        /// Queues a building for icon generation unless it is already cached,
        /// already queued, or previously failed. Called by BuildingIndexSystem
        /// for every building the normal icon mechanism cannot resolve.
        /// </summary>
        public void RequestIcon(BuildingPrefab prefab)
        {
            if (prefab == null || m_Folder == null)
            {
                return;
            }

            var name = prefab.name;
            if (m_Cached.Contains(name) || m_Failed.Contains(name) || !m_Queued.Add(name))
            {
                return;
            }

            m_Queue.Add(prefab);
            m_PendingByName[name] = prefab;
        }

        /// <summary>Takes the next building to render, priority first.</summary>
        private BuildingPrefab TakeNext()
        {
            if (m_Priority.Count > 0)
            {
                var index = m_Priority.Count - 1;
                var prioritized = m_Priority[index];
                m_Priority.RemoveAt(index);
                m_TookPriority = true;
                return prioritized;
            }

            m_TookPriority = false;

            while (m_QueueCursor < m_Queue.Count)
            {
                var next = m_Queue[m_QueueCursor++];

                // Skip anything already handled through the priority path.
                if (next != null && !m_Cached.Contains(next.name) && !m_Failed.Contains(next.name))
                {
                    return next;
                }
            }

            return null;
        }

        /// <summary>
        /// Draws every missing picture in one blocking pass while the loading
        /// screen is still up. The first time this can take about a minute;
        /// afterwards the pictures are on disk and there is little or nothing
        /// left to draw. OnUpdate draws whatever shows up later, a few per frame.
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            if ((mode & GameMode.GameOrEditor) == 0 || m_Folder == null)
            {
                return;
            }

            // A multi-frame warm-up cannot happen inside one blocking pass: leave
            // everything to OnUpdate then.
            if (WarmupFrames > 1)
            {
                Mod.Log.Info($"IconGenerator: preload skipped (VT warm-up needs real frames); {Pending} building(s) will be rendered in the background.");
                return;
            }

            var entities = m_SpawnableQuery.ToEntityArray(Allocator.Temp);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int done = 0, failed = 0;

            try
            {
                EnsureRig();

                foreach (var entity in entities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<BuildingPrefab>(entity, out var prefab))
                    {
                        continue;
                    }

                    var name = prefab.name;
                    if (string.IsNullOrEmpty(name) ||
                        m_Cached.Contains(name) ||
                        m_Failed.Contains(name) ||
                        name.StartsWith(PaletteZoneSystem.ClonePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Only buildings the game cannot show an icon for: anything
                    // that resolves to a real icon (AIL, asset database) is left
                    // untouched.
                    var existing = ImageSystem.GetThumbnail(prefab);
                    if (!string.IsNullOrEmpty(existing) &&
                        !existing.StartsWith("thumbnail://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (GenerateImmediately(prefab, name))
                    {
                        m_Cached.Add(name);
                        done++;
                    }
                    else
                    {
                        m_Failed.Add(name);
                        failed++;
                    }

                    if ((done + failed) % 500 == 0)
                    {
                        Mod.Log.Info($"IconGenerator: preload {done} generated, {failed} failed, {watch.Elapsed.TotalSeconds:F0}s elapsed.");
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "IconGenerator: preload pass aborted; the per-frame generator will finish the rest.");
            }
            finally
            {
                entities.Dispose();
                ReleaseRig();
            }

            // Anything queued by an earlier index pass is now done.
            m_Queue.Clear();
            m_QueueCursor = 0;
            m_Priority.Clear();
            m_PendingByName.Clear();
            m_Queued.Clear();

            if (done > 0 || failed > 0)
            {
                Mod.Log.Info($"IconGenerator: preload complete — {done} generated, {failed} failed, {m_Cached.Count} total cached, {m_BytesWritten / 1024} KB written in {watch.Elapsed.TotalSeconds:F1}s.");

                // Re-resolve thumbnails so the browser uses the new PNGs.
                IndexSystem?.ForceReindex();
            }
        }

        /// <summary>
        /// Draws the waiting buildings one at a time: each one is placed in front
        /// of the camera, rendered for <see cref="WarmupFrames"/> frames, then
        /// captured and saved.
        /// </summary>
        protected override void OnUpdate()
        {
            if (m_Subject != null)
            {
                RenderSubject();

                if (--m_SubjectFramesLeft > 0)
                {
                    return; // not captured yet
                }

                CompleteSubject();
                return;
            }

            if (Pending == 0)
            {
                return;
            }

            BeginNextSubject();
        }

        /// <summary>Picks the next building and puts it in front of the camera.</summary>
        private void BeginNextSubject()
        {
            var prefab = TakeNext();
            var name = prefab != null ? prefab.name : null;

            if (string.IsNullOrEmpty(name))
            {
                CheckDrained();
                return;
            }

            m_Queued.Remove(name);
            m_PendingByName.Remove(name);
            Processed++;

            m_SubjectName = name;
            m_SubjectIsPriority = m_TookPriority;

            EnsureRig();
            m_Subject = BuildSubject(prefab, name);

            if (m_Subject == null)
            {
                m_Failed.Add(name);
                CheckDrained();
                return;
            }

            m_SubjectFramesLeft = WarmupFrames;
        }

        /// <summary>Captures the warmed-up subject, saves it and cleans up.</summary>
        private void CompleteSubject()
        {
            var name = m_SubjectName;
            var wasPriority = m_SubjectIsPriority;
            var saved = false;

            try
            {
                saved = WritePng(name);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: '{name}' failed to capture ({ex.Message}).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(m_Subject);
                m_Subject = null;
            }

            m_SubjectName = null;

            if (saved)
            {
                m_Cached.Add(name);

                // Only this building's entry is updated: rebuilding the whole
                // index for every picture would be far too slow.
                IndexSystem?.ApplyGeneratedIcon(name, GetGeneratedIcon(name));
                m_SinceNotify++;
            }
            else
            {
                m_Failed.Add(name);
            }

            // A building on screen was just drawn: refresh the UI now instead of
            // waiting for the next batch.
            if (saved && wasPriority)
            {
                m_SinceNotify = 0;
                IndexSystem?.NotifyIconsChanged();
            }
            else if (m_SinceNotify >= NotifyBatch)
            {
                m_SinceNotify = 0;
                IndexSystem?.NotifyIconsChanged();
                Mod.Log.Info($"IconGenerator: {m_Cached.Count} generated, {Pending} pending, {m_Failed.Count} failed.");
            }

            CheckDrained();
        }

        /// <summary>Releases everything once there is no work left.</summary>
        private void CheckDrained()
        {
            if (Pending != 0 || m_Subject != null)
            {
                return;
            }

            m_Queue.Clear();
            m_QueueCursor = 0;
            m_Priority.Clear();
            m_PendingByName.Clear();
            m_SinceNotify = 0;
            ReleaseRig();
            IndexSystem?.NotifyIconsChanged();
            Mod.Log.Info($"IconGenerator: queue drained. {m_Cached.Count} icon(s) cached, {m_TexturedSurfaces} textured surface(s), {m_Failed.Count} failed, {m_BytesWritten / 1024} KB written this session.");
        }

        // ------------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the renderable stand-in for a building and frames the camera on
        /// it. Returns null (and logs why) when the prefab cannot be rendered.
        /// The caller owns the returned GameObject and must destroy it.
        /// </summary>
        private GameObject BuildSubject(BuildingPrefab prefab, string name)
        {
            GameObject root = null;

            try
            {
                if (prefab is not ObjectGeometryPrefab geometry || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0)
                {
                    Fail(name, "prefab has no geometry");
                    return null;
                }

                EnsureRig();

                root = new GameObject("CZ_IconSubject") { hideFlags = HideFlags.HideAndDontSave };
                root.transform.SetParent(m_Rig.transform, false);

                var hasBounds = false;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                // Meshes tied to a specific ObjectState are conditional variants
                // (left/right-hand traffic, seasons, damaged...). Drawing them
                // all at once overlaps geometry, so keep only the default-state
                // ones — unless the prefab has nothing else to offer.
                var hasDefaultState = false;
                foreach (var info in geometry.m_Meshes)
                {
                    if (info != null && info.m_RequireState == global::Game.Objects.ObjectState.None)
                    {
                        hasDefaultState = true;
                        break;
                    }
                }

                foreach (var info in geometry.m_Meshes)
                {
                    if (info?.m_Mesh is not RenderPrefab renderPrefab)
                    {
                        continue;
                    }

                    if (hasDefaultState && info.m_RequireState != global::Game.Objects.ObjectState.None)
                    {
                        continue;
                    }

                    var meshes = renderPrefab.ObtainMeshes();
                    var materials = ObtainIconMaterials(renderPrefab);
                    if (meshes == null || meshes.Length == 0 || materials == null || materials.Length == 0)
                    {
                        continue;
                    }

                    var position = new Vector3(info.m_Position.x, info.m_Position.y, info.m_Position.z);
                    var rotation = new Quaternion(
                        info.m_Rotation.value.x, info.m_Rotation.value.y, info.m_Rotation.value.z, info.m_Rotation.value.w);

                    // As in RenderPrefab.ObtainMesh: a prefab may have several
                    // meshes, and its material list runs on across every sub-mesh
                    // of every mesh, so the material index carries over.
                    var materialCursor = 0;

                    foreach (var mesh in meshes)
                    {
                        if (mesh == null)
                        {
                            continue;
                        }

                        var subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                        var subMaterials = new Material[subMeshCount];

                        for (var s = 0; s < subMeshCount; s++)
                        {
                            subMaterials[s] = materialCursor < materials.Length
                                ? materials[materialCursor]
                                : materials[materials.Length - 1];
                            materialCursor++;
                        }

                        var part = new GameObject(mesh.name) { hideFlags = HideFlags.HideAndDontSave };
                        part.transform.SetParent(root.transform, false);
                        part.transform.localPosition = position;
                        part.transform.localRotation = rotation;
                        part.layer = RenderLayer;

                        part.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var renderer = part.AddComponent<MeshRenderer>();
                        renderer.sharedMaterials = subMaterials;
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }

                    var b = renderPrefab.bounds;
                    min = Vector3.Min(min, new Vector3(b.min.x, b.min.y, b.min.z) + position);
                    max = Vector3.Max(max, new Vector3(b.max.x, b.max.y, b.max.z) + position);
                    hasBounds = true;
                }

                if (!hasBounds)
                {
                    Fail(name, "no usable RenderPrefab meshes/materials");
                    UnityEngine.Object.DestroyImmediate(root);
                    return null;
                }

                FrameCamera(min, max);
                root.SetActive(true);
                return root;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: '{name}' failed to build ({ex.Message}).");

                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                return null;
            }
        }

        /// <summary>
        /// Build, render and capture in a single call, for the blocking pass
        /// during loading.
        /// </summary>
        private bool GenerateImmediately(BuildingPrefab prefab, string name)
        {
            EnsureRig();
            var subject = BuildSubject(prefab, name);

            if (subject == null)
            {
                return false;
            }

            try
            {
                RenderSubject();
                return WritePng(name);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: '{name}' failed to render ({ex.Message}).");
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(subject);
            }
        }

        /// <summary>Renders the subject currently in flight into the target texture.</summary>
        private void RenderSubject()
        {
            m_Rig.SetActive(true);
            m_Camera.Render();
            m_Rig.SetActive(false);
        }

        /// <summary>
        /// Materials that can be drawn by the off-screen camera.
        /// </summary>
        /// <remarks>
        /// With virtual texturing on, SurfaceAsset.SyncToUnityMaterial does not
        /// bind the surface's textures: it enables the ENABLE_VT keyword and lets
        /// the streaming system feed them, which never happens for an off-screen
        /// camera. SurfaceAsset.Load also builds its material only once, so a
        /// surface already loaded for the city cannot be asked for without VT.
        /// Each material is therefore cloned, with ENABLE_VT turned off and the
        /// textures bound directly when they can be loaded (see GetIconMaterial).
        /// </remarks>
        private Material[] ObtainIconMaterials(RenderPrefab renderPrefab)
        {
            var source = renderPrefab.ObtainMaterials();
            if (source == null || source.Length == 0)
            {
                return source;
            }

            var surfaces = renderPrefab.surfaceAssets.ToArray();
            var result = new Material[source.Length];

            for (var i = 0; i < source.Length; i++)
            {
                var surface = i < surfaces.Length ? surfaces[i] : null;
                result[i] = surface == null ? source[i] : GetIconMaterial(surface, source[i]);
            }

            return result;
        }

        // TextureAsset's private instance and reference count, read through reflection.
        private static readonly System.Reflection.FieldInfo s_TextureInstance =
            typeof(TextureAsset).GetField("m_Instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static readonly System.Reflection.FieldInfo s_TextureRefCount =
            typeof(TextureAsset).GetField("m_ObjectInstanceRefCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>Already-resolved textures, so each one is touched at most once.</summary>
        private readonly Dictionary<TextureAsset, Texture> m_Textures = new();

        /// <summary>
        /// Gets the Unity texture of a TextureAsset: its existing instance when it
        /// has one, otherwise a fresh load. Placeholder assets of virtual textures
        /// (no data) are skipped.
        /// </summary>
        /// <remarks>
        /// TextureAsset.Load logs "reference count is out of sync" when the asset
        /// has a reference count but no instance (common for virtual textures), and
        /// then loads it anyway with a count of 1. Setting the count to 0 first
        /// reaches the same state without the error.
        /// </remarks>
        private bool TryResolveTexture(TextureAsset asset, out Texture texture)
        {
            if (m_Textures.TryGetValue(asset, out texture))
            {
                return texture != null;
            }

            texture = null;

            try
            {
                texture = s_TextureInstance?.GetValue(asset) as Texture;

                // A placeholder asset (isDummy: no database) has no data to load;
                // Load would throw. These are the virtual textures, whose pixels
                // exist only inside the VT store.
                if (texture == null && asset.isDummy)
                {
                    m_Textures[asset] = null;
                    return false;
                }

                if (texture == null)
                {
                    // See the remarks: a count without an instance is reset first, so
                    // Load takes its normal path.
                    if (s_TextureRefCount != null && (int)s_TextureRefCount.GetValue(asset) != 0)
                    {
                        s_TextureRefCount.SetValue(asset, 0);
                    }

                    texture = asset.Load(-1, TextureAsset.KeepOnCPU.Dont);
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: could not resolve texture '{asset?.name}' ({ex.Message}).");
                texture = null;
            }

            m_Textures[asset] = texture;
            return texture != null;
        }

        private Material m_ClayMaterial;
        private bool m_ClayMaterialTried;

        /// <summary>
        /// The neutral matte material used for surfaces whose textures cannot be
        /// loaded (most of them: see the file header), for a clean "white model"
        /// picture. Null when no usable shader is found; the game's own material
        /// is then kept.
        /// </summary>
        private Material ClayMaterial
        {
            get
            {
                if (m_ClayMaterialTried)
                {
                    return m_ClayMaterial;
                }

                m_ClayMaterialTried = true;

                foreach (var shaderName in new[] { "HDRP/Lit", "HDRP/Unlit", "Standard" })
                {
                    var shader = Shader.Find(shaderName);
                    if (shader == null)
                    {
                        continue;
                    }

                    m_ClayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    TrySet(m_ClayMaterial, "_BaseColor", new Color(0.82f, 0.80f, 0.78f, 1f));
                    TrySet(m_ClayMaterial, "_Color", new Color(0.82f, 0.80f, 0.78f, 1f));
                    TrySetFloat(m_ClayMaterial, "_Smoothness", 0.12f);
                    TrySetFloat(m_ClayMaterial, "_Metallic", 0f);
                    TrySetFloat(m_ClayMaterial, "_Glossiness", 0.12f);

                    Mod.Log.Info($"IconGenerator: using '{shaderName}' for the neutral model material.");
                    break;
                }

                if (m_ClayMaterial == null)
                {
                    Mod.Log.Warn("IconGenerator: no usable shader found for the neutral model material; keeping the game's materials.");
                }

                return m_ClayMaterial;
            }
        }

        private static void TrySet(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
            {
                material.SetColor(property, value);
            }
        }

        private static void TrySetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private Material GetIconMaterial(SurfaceAsset surface, Material original)
        {
            if (m_IconMaterials.TryGetValue(surface, out var cached) && cached != null)
            {
                return cached;
            }

            // Surfaces that could not be cloned are not tried again for every
            // building that uses them.
            if (m_UnconvertibleSurfaces.Contains(surface))
            {
                return original;
            }

            Material material;

            try
            {
                material = new Material(original) { hideFlags = HideFlags.HideAndDontSave };
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: could not clone surface '{surface?.name}' ({ex.Message}); using it as-is.");
                m_UnconvertibleSurfaces.Add(surface);
                return original;
            }

            // Bind the textures the VT path leaves unassigned (albedo, normal,
            // mask...), one by one so a bad entry does not lose the material.
            var textures = surface.textures;
            var bound = 0;

            if (textures != null)
            {
                foreach (var texture in textures)
                {
                    try
                    {
                        if (!material.HasProperty(texture.Key))
                        {
                            continue;
                        }

                        // Only textures that are ordinary assets can be bound;
                        // surfaces without any use ClayMaterial (below).
                        if (texture.Value != null && TryResolveTexture(texture.Value, out var loaded))
                        {
                            material.SetTexture(texture.Key, loaded);
                            bound++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"IconGenerator: texture '{texture.Key}' of '{surface.name}' skipped ({ex.Message}).");
                    }
                }
            }

            if (bound == 0)
            {
                // Nothing could be bound: every texture of this surface exists only
                // in the virtual-texturing store. The game's material would draw
                // dark grey off-screen, and without ENABLE_VT black, so the neutral
                // material is used: a "white model" picture, the style Asset Icon
                // Library also uses for many buildings.
                UnityEngine.Object.DestroyImmediate(material);

                var clay = ClayMaterial;
                if (clay != null)
                {
                    m_IconMaterials[surface] = clay;
                    return clay;
                }

                m_UnconvertibleSurfaces.Add(surface);
                return original;
            }

            // The textures are bound: the shader must read them instead of the
            // virtual-texturing path.
            material.DisableKeyword(VirtualTexturingKeyword);

            // Counted for the log: only these surfaces are drawn in colour.
            m_TexturedSurfaces++;

            if (m_TexturedSurfaces <= MaxLoggedFailures)
            {
                Mod.Log.Info($"IconGenerator: surface '{surface.name}' has {bound} REAL texture(s) — it will render in colour.");
            }

            // Only the mod's own clones are cached: the cache is destroyed on
            // cleanup, and the game's materials must never be destroyed.
            m_IconMaterials[surface] = material;
            return material;
        }

        /// <summary>
        /// Places the camera at a 3/4 isometric angle framing the whole building,
        /// the usual angle of building icons.
        /// </summary>
        private void FrameCamera(Vector3 min, Vector3 max)
        {
            var center = (min + max) * 0.5f;
            var extent = Mathf.Max(0.5f, Vector3.Distance(min, max) * 0.5f);

            // Looking at the building's front: lots face +Z (the street side), so
            // the camera sits on the +X / +Z side.
            var direction = new Vector3(0.7f, 0.55f, 0.7f).normalized;

            m_Camera.orthographicSize = extent * 0.95f;
            m_Camera.transform.position = center + (direction * (extent * 4f));
            m_Camera.transform.LookAt(center);
            m_Camera.nearClipPlane = 0.01f;
            m_Camera.farClipPlane = extent * 12f;
        }

        /// <summary>
        /// Reads the render back, rejects useless results, then shrinks the image
        /// as much as possible before saving:
        ///   1. Trim: crop to the building's opaque bounds (about half the pixels,
        ///      and the building fills the card better).
        ///   2. Flatten: every transparent pixel becomes (0,0,0,0); a uniform area
        ///      costs almost nothing in PNG's DEFLATE.
        ///   3. Quantise: drop the low 2 bits of each colour channel, invisible at
        ///      icon size but many more repeated bytes for DEFLATE.
        /// PNG keeps the alpha channel (Unity cannot encode WebP or AVIF).
        /// </summary>
        private bool WritePng(string name)
        {
            var previous = RenderTexture.active;

            try
            {
                RenderTexture.active = m_Target;

                // One reused texture and a raw view of its pixels, instead of a new
                // Texture2D and pixel array for each of thousands of buildings.
                m_ReadTexture.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                m_ReadTexture.Apply(false);

                var pixels = m_ReadTexture.GetRawTextureData<Color32>();

                // --- Background key ------------------------------------------
                // HDRP does not reliably give a transparent alpha channel, so the
                // background is keyed out by colour. The four corners are always
                // background (the building is centred and framed to fit), so when
                // they agree their colour is the key, whatever exact colour HDRP
                // cleared to.
                var c0 = pixels[0];
                var c1 = pixels[IconSize - 1];
                var c2 = pixels[(IconSize - 1) * IconSize];
                var c3 = pixels[(IconSize * IconSize) - 1];

                var hasKey = Similar(c0, c1) && Similar(c0, c2) && Similar(c0, c3);
                var key = c0;

                // Whatever the corners say, a clearly magenta pixel is backdrop (a
                // building filling the whole frame leaves no background corner).
                // A relative test, so it holds when exposure shifts the shade.
                static bool IsChromaKey(Color32 p)
                    => p.r - p.g > 60 && p.b - p.g > 60;

                bool IsBackground(Color32 p)
                    => p.a <= AlphaCutoff || IsChromaKey(p) || (hasKey && Similar(p, key));

                // --- Analyse: subject bounds + colour spread ------------------
                int minX = IconSize, minY = IconSize, maxX = -1, maxY = -1, subject = 0;
                byte rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;

                for (var y = 0; y < IconSize; y++)
                {
                    for (var x = 0; x < IconSize; x++)
                    {
                        var p = pixels[(y * IconSize) + x];
                        if (IsBackground(p))
                        {
                            continue;
                        }

                        subject++;

                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;

                        if (p.r < rMin) rMin = p.r;
                        if (p.r > rMax) rMax = p.r;
                        if (p.g < gMin) gMin = p.g;
                        if (p.g > gMax) gMax = p.g;
                        if (p.b < bMin) bMin = p.b;
                        if (p.b > bMax) bMax = p.b;
                    }
                }

                var total = IconSize * IconSize;

                // Nothing drawn at all.
                if (maxX < 0 || subject < total / 100)
                {
                    return Fail(name, $"empty render (subject={subject}px, keyed={hasKey})");
                }

                // A flat single-colour slab that fills the whole frame is a failed
                // render. A flat-coloured object with a real shape (beach or
                // terrain spawners, plain warehouses) is fine, so a uniform colour
                // alone does not reject it.
                if (subject > total * 98 / 100 &&
                    (rMax - rMin) + (gMax - gMin) + (bMax - bMin) < UniformTolerance)
                {
                    return Fail(name, "flat single-colour slab filling the frame");
                }

                // --- Trim with a small margin --------------------------------
                minX = Mathf.Max(0, minX - CropPadding);
                minY = Mathf.Max(0, minY - CropPadding);
                maxX = Mathf.Min(IconSize - 1, maxX + CropPadding);
                maxY = Mathf.Min(IconSize - 1, maxY + CropPadding);

                var width = maxX - minX + 1;
                var height = maxY - minY + 1;
                var cropped = new Color32[width * height];

                for (var y = 0; y < height; y++)
                {
                    var sourceRow = (minY + y) * IconSize;
                    var targetRow = y * width;

                    for (var x = 0; x < width; x++)
                    {
                        var p = pixels[sourceRow + minX + x];

                        if (IsBackground(p))
                        {
                            // Flatten: one uniform value compresses to almost
                            // nothing, and this is what makes the icon's
                            // background genuinely transparent.
                            cropped[targetRow + x] = new Color32(0, 0, 0, 0);
                            continue;
                        }

                        // Feather the rim: pixels that are part backdrop (the
                        // anti-aliased edge of the silhouette) fade out instead
                        // of producing a hard, jagged cut.
                        var alpha = p.a;
                        var r = p.r;
                        var g = p.g;
                        var b = p.b;

                        if (hasKey)
                        {
                            var distance = Distance(p, key);
                            if (distance < BackgroundSoftTolerance)
                            {
                                var fade = (distance - BackgroundHardTolerance)
                                           / (float)(BackgroundSoftTolerance - BackgroundHardTolerance);
                                alpha = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * fade), 0, 255);
                            }
                        }

                        // Despill, on every kept pixel: the magenta backdrop bleeds
                        // into the building through anti-aliasing and translucent
                        // surfaces (windows, fences, railings). Only pixels where R
                        // and B both exceed G, the mark of magenta, are corrected;
                        // orange (r > g > b) or cyan pixels keep their colour.
                        if (r > g && b > g)
                        {
                            var spill = Mathf.Min(r - g, b - g);
                            r = (byte)(r - spill);
                            b = (byte)(b - spill);
                        }

                        // 6-bit quantisation: helps DEFLATE while keeping the detail.
                        cropped[targetRow + x] = new Color32(
                            (byte)(r & 0xFC),
                            (byte)(g & 0xFC),
                            (byte)(b & 0xFC),
                            alpha > 247 ? (byte)255 : alpha);
                    }
                }

                var bytes = ImageConversion.EncodeArrayToPNG(
                    cropped,
                    UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)width,
                    (uint)height);

                if (bytes == null || bytes.Length == 0)
                {
                    return Fail(name, "PNG encoding returned nothing");
                }

                File.WriteAllBytes(Path.Combine(m_Folder, $"{FilePrefix}{name}.png"), bytes);
                m_BytesWritten += bytes.Length;
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        // ------------------------------------------------------------------
        // Render rig lifecycle (created lazily, released when the queue drains)
        // ------------------------------------------------------------------

        private void EnsureRig()
        {
            if (m_Rig != null)
            {
                return;
            }

            m_Rig = new GameObject("CZ_IconRig") { hideFlags = HideFlags.HideAndDontSave };
            m_Rig.SetActive(false);

            m_Target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
            };
            m_Target.Create();

            m_ReadTexture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);

            var cameraObject = new GameObject("CZ_IconCamera") { hideFlags = HideFlags.HideAndDontSave };
            cameraObject.transform.SetParent(m_Rig.transform, false);

            m_Camera = cameraObject.AddComponent<Camera>();
            m_Camera.orthographic = true;
            m_Camera.cullingMask = 1 << RenderLayer;
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.backgroundColor = ChromaKeyColor;
            m_Camera.targetTexture = m_Target;
            m_Camera.enabled = false; // rendered manually via Render()

            // HDRP settings of the camera: its own clear colour and exposure, not
            // the scene's sky, fog and exposure volumes. If any of it fails, the
            // camera still renders with the defaults.
            try
            {
                var hdCamera = cameraObject.AddComponent<HDAdditionalCameraData>();
                hdCamera.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;

                // The magenta backdrop (see ChromaKeyColor), keyed out by colour.
                hdCamera.backgroundColorHDR = ChromaKeyColor;

                // Only the mod's own volume, never the world's (its exposure and
                // fog belong to the city and the time of day).
                hdCamera.volumeLayerMask = 1 << RenderLayer;

                // Virtual texturing enabled in the camera's frame settings, as in
                // the game's own cameras.
                hdCamera.customRenderingSettings = true;
                hdCamera.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.VirtualTexturing, true);

                var overrides = hdCamera.renderingPathCustomFrameSettingsOverrideMask;
                overrides.mask[(uint)FrameSettingsField.VirtualTexturing] = true;
                hdCamera.renderingPathCustomFrameSettingsOverrideMask = overrides;

                CreateExposureVolume();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: HDRP camera setup skipped ({ex.Message}).");
            }

            // Three-point lighting: an off-screen render has no sky to light the
            // faces turned away from the sun, so a key, a fill and a top light are
            // used. The key comes from behind the camera, so the front of the
            // building is the well-lit side.
            m_Light = CreateLight("CZ_IconKey", new Vector3(35f, 215f, 0f), KeyLightLux);
            CreateLight("CZ_IconFill", new Vector3(15f, 60f, 0f), FillLightLux);
            CreateLight("CZ_IconRim", new Vector3(70f, 130f, 0f), RimLightLux);
        }

        /// <summary>
        /// Creates one directional light restricted to the icon layer, so it can
        /// never leak onto the actual game world.
        /// </summary>
        private Light CreateLight(string name, Vector3 euler, float lux)
        {
            var lightObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            lightObject.transform.SetParent(m_Rig.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(euler.x, euler.y, euler.z);

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.cullingMask = 1 << RenderLayer;
            light.shadows = LightShadows.None;

            // In HDRP a bare Light contributes almost nothing: intensity is
            // physical (lux for a directional light) and lives on the HD data.
            try
            {
                lightObject.AddComponent<HDAdditionalLightData>().SetIntensity(lux);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"IconGenerator: HDRP light setup skipped ({ex.Message}).");
            }

            return light;
        }

        /// <summary>
        /// Gives the off-screen camera a fixed exposure matched to its lights.
        /// HDRP's default (Fixed, EV100 = 0) suits an almost dark scene and would
        /// turn a building lit at 25 000 lux into a white silhouette.
        /// </summary>
        private void CreateExposureVolume()
        {
            var volumeObject = new GameObject("CZ_IconExposure") { hideFlags = HideFlags.HideAndDontSave };
            volumeObject.transform.SetParent(m_Rig.transform, false);
            volumeObject.layer = RenderLayer;

            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;

            var exposure = profile.Add<Exposure>(true);
            exposure.mode.overrideState = true;
            exposure.mode.value = ExposureMode.Fixed;
            exposure.fixedExposure.overrideState = true;
            exposure.fixedExposure.value = FixedExposureEV;

            var volume = volumeObject.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 10000f;
            volume.profile = profile;
        }

        private void ReleaseRig()
        {
            if (m_Rig != null)
            {
                UnityEngine.Object.DestroyImmediate(m_Rig);
                m_Rig = null;
                m_Camera = null;
                m_Light = null;
            }

            if (m_Target != null)
            {
                m_Target.Release();
                UnityEngine.Object.DestroyImmediate(m_Target);
                m_Target = null;
            }

            if (m_ReadTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(m_ReadTexture);
                m_ReadTexture = null;
            }

            // Only the mod's own materials (clones and the neutral one) are
            // destroyed, each once: the neutral material is shared by many
            // surfaces. The game's materials are never touched.
            var destroyed = new HashSet<Material>();

            foreach (var material in m_IconMaterials.Values)
            {
                if (material != null && destroyed.Add(material))
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            m_IconMaterials.Clear();
            m_UnconvertibleSurfaces.Clear();
            m_ClayMaterial = null;
            m_ClayMaterialTried = false;

            // Textures belong to the asset database: only the references are dropped.
            m_Textures.Clear();
        }

        protected override void OnDestroy()
        {
            ReleaseRig();

            if (m_HostRegistered)
            {
                try
                {
                    UIManager.defaultUISystem.RemoveHostLocation(HostName);
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"IconGenerator: could not remove host location ({ex.Message}).");
                }

                m_HostRegistered = false;
            }

            base.OnDestroy();
        }
    }
}
