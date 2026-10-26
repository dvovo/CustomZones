// <copyright file="IconGeneratorSystem.cs">
// CustomZones - Licensed under the MIT license.
//
// RUNTIME ICON GENERATION — the "firewall" for buildings the game itself can
// never show an icon for.
//
// WHY THIS EXISTS (decompiled ground truth, 2026-08-02):
//   Game.UI.Thumbnails.ThumbnailCache.GetThumbnail() does NOT render anything.
//   It is a pure dictionary lookup over entries loaded from pre-baked *atlas*
//   assets, and its Update() is literally empty. So a `thumbnail://ThumbnailCamera/<id>`
//   URL only resolves if someone already baked that icon into an atlas. A
//   building that is in no atlas (e.g. EU_MixedOldTown01_L1_1x2) can NEVER get
//   an image from the game — which is why AssetIconLibrary ships ~8000
//   pre-rendered PNGs and why every mod (FindIt included) falls back to a
//   generic placeholder for the rest. The game has no runtime thumbnail
//   generation at all.
//
// WHAT WE DO INSTEAD (all public APIs):
//   RenderPrefab.ObtainMeshes()    -> UnityEngine.Mesh[]   (GeometryAsset : IAssetData<Mesh[]>)
//   RenderPrefab.ObtainMaterials() -> UnityEngine.Material[]
//   ... rendered by our own off-screen Camera into a RenderTexture, read back,
//   encoded to PNG and written to
//       {UserData}/ModsData/CustomZones/GeneratedIcons/BuildingPrefab.<name>.png
//   then served to the cohtml UI through our own host location
//       coui://czicons/BuildingPrefab.<name>.png
//   (UIManager.defaultUISystem.AddHostLocation — the very same mechanism
//   AssetIconLibrary uses for coui://ail/). The file naming mirrors AIL's
//   'PrefabType.PrefabName.png' convention on purpose.
//
// CACHING (per the user's design, which is the standard professional pattern):
//   The PNGs persist on disk, so generation is a ONE-TIME cost per building.
//   On every later start we just list the folder and skip everything already
//   there; delete the folder (or single files) and they are regenerated. The
//   flow is: no icon from the normal mechanism AND not on disk -> generate+save.
//
// COST CONTROL: work is spread over frames so a first run never stalls the
// game, and each render is individually guarded — a building that fails to
// render is remembered as failed and never retried, and the UI draws a
// distinctive procedural icon for it instead.
//
// WHY THE ICONS ARE A NEUTRAL "WHITE MODEL" AND NOT PHOTO-REALISTIC
// (three separate approaches were tried and disproved, do not retry them):
//   1. Use the game's materials as-is -> murky grey. CS2 surfaces are virtual-
//      textured: `SyncToUnityMaterial` deliberately does NOT bind those textures,
//      it enables the ENABLE_VT shader keyword and lets the streaming system feed
//      them. An off-screen camera never runs the VT resolve pass.
//   2. Load the textures and bind them ourselves -> impossible. The TextureAsset
//      of a VT property is a DUMMY (`isDummy => database == null`), so it has no
//      file and no data; `GetReadStream()` throws instantly.
//   3. Load the PRE-PROCESSED SOURCE textures by GUID -> also impossible. The
//      property -> stack -> layer -> GUID chain resolves correctly, but
//      `TryGetAsset` returns false for every GUID: the source textures are
//      build-time only, the shipped game contains just the baked VT store.
//   Multi-frame VT warm-up was also measured and failed: average chroma stayed
//   at ~0.1/255 (pure greyscale) on EVERY building.
//
//   IMPORTANT EXCEPTION (measured): a MINORITY of surfaces are NOT virtual-
//   textured — their TextureAssets are real, loadable files. Those DO bind and
//   DO render in full colour; IceflakeArena01 is one (its icon has genuine
//   orange/cyan/red pixels in the interior, no magenta). So the pipeline is
//   correct and does produce colour whenever the data exists; what is missing
//   for ordinary growables is the DATA, not the mechanism. `m_TexturedSurfaces`
//   counts how many surfaces hit this path.
//   At runtime the pixels exist ONLY inside the VT store, reachable solely
//   through the game's own rendering path — which is exactly why
//   AssetIconCreator spawns the building in the world and screenshots the game.
// </copyright>

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

    public partial class IconGeneratorSystem : GameSystemBase
    {
        /// <summary>coui host name; icons resolve as coui://czicons/&lt;file&gt;.</summary>
        public const string HostName = "czicons";

        /// <summary>AIL-compatible file prefix.</summary>
        private const string FilePrefix = "BuildingPrefab.";

        /// <summary>
        /// Bump this whenever the rendering/encoding pipeline changes; cached
        /// icons from an older version are then discarded and regenerated.
        /// </summary>
        private const int PipelineVersion = 15;

        /// <summary>
        /// Render resolution. Raised to 256 for detail (the cards draw at ~150 px
        /// and these are photo-like renders): quality was worth more than bytes
        /// here, and the trim/quantize pass keeps the files reasonable.
        /// </summary>
        private const int IconSize = 256;

        /// <summary>Shader keyword that switches CS2 surfaces to virtual texturing.</summary>
        private const string VirtualTexturingKeyword = "ENABLE_VT";

        /// <summary>
        /// SurfaceAsset -> our VT-free material clone. Thousands of buildings
        /// share the same surfaces, so converting each one once matters.
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
        /// Backdrop colour, keyed out after rendering. Magenta because no
        /// building surface is magenta — AssetIconCreator ships a Magenta.png
        /// backdrop for exactly the same reason. With a BLACK backdrop every
        /// dark roof and shadowed wall matched the key and got punched out,
        /// which is what produced the see-through walls.
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
        /// Frames each subject is rendered before capturing.
        /// Kept at 1: the multi-frame virtual-texturing warm-up was TESTED AND
        /// FAILED — measured chroma stayed at ~0.1/255 (pure greyscale) on every
        /// building, proving VT streaming never feeds a manually-rendered
        /// off-screen camera. Extra frames bought nothing but a 6x slowdown.
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
        /// Fixed exposure (EV100). Derived, not guessed: a surface of albedo ~0.5
        /// lit by the key reflects ≈ lux * 0.5 / π ≈ 4000 nits, and middle grey
        /// sits at EV100 = log2(nits * 100 / 12.5) ≈ 15. We aim slightly brighter
        /// than middle grey. The previous 13.3 was ~1.5 stops over, which is why
        /// lit roofs burned out to white.
        /// Raise this to darken the render, lower it to brighten.
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
        /// Buildings the user is looking at RIGHT NOW. Always drained before the
        /// background queue, so whatever is on screen gets its icon within a
        /// frame or two instead of waiting for thousands of unrelated prefabs.
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

                // Cache invalidation: icons produced by an older rendering
                // pipeline (different lighting, no trimming, no quantization)
                // are dropped so they get regenerated with the current one.
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

                // Serve the folder to the cohtml UI (same mechanism as AIL).
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
        /// Wipes the cache when it was produced by a different rendering
        /// pipeline, so visual/size improvements roll out automatically instead
        /// of leaving old icons frozen on disk forever.
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
        /// Generates EVERY missing icon in one blocking pass while the loading
        /// screen is still up. That is ~45-60 s of solid work the very first
        /// time, which would be an unacceptable freeze mid-game but is perfectly
        /// normal during loading — and it only ever happens once, because the
        /// PNGs are cached on disk from then on.
        /// The per-frame trickle in OnUpdate stays as a safety net for anything
        /// that shows up later (assets enabled mid-session, failed retries).
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            if ((mode & GameMode.GameOrEditor) == 0 || m_Folder == null)
            {
                return;
            }

            // DISABLED while the virtual-texturing warm-up is in use: this pass
            // is synchronous, and streaming only advances BETWEEN frames, so
            // everything it produced would be untextured — and worse, cached as
            // final, permanently denying those buildings a proper icon. The
            // per-frame generator in OnUpdate handles everything instead.
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
        /// One building at a time, rendered across SEVERAL consecutive frames.
        ///
        /// WHY MULTI-FRAME: CS2 building textures are streamed by HDRP's virtual
        /// texturing, and streaming only advances between frames — the first
        /// render of a subject samples tiles that are not resident yet, which is
        /// why single-shot renders came out untextured. Holding the subject in
        /// front of the camera for a few frames gives the VT feedback loop time
        /// to request and upload its tiles before we capture.
        /// </summary>
        protected override void OnUpdate()
        {
            if (m_Subject != null)
            {
                RenderSubject();

                if (--m_SubjectFramesLeft > 0)
                {
                    return; // still warming up the textures
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

                // Patch just this building's entry: rebuilding the whole index
                // per icon is what used to tank the framerate.
                IndexSystem?.ApplyGeneratedIcon(name, GetGeneratedIcon(name));
                m_SinceNotify++;
            }
            else
            {
                m_Failed.Add(name);
            }

            // Something the user is actually looking at just finished: refresh
            // now instead of making them wait for the next background batch.
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

                    // CRITICAL (mirrors RenderPrefab.ObtainMesh): a prefab is made
                    // of SEVERAL meshes and the material list is indexed
                    // CONTINUOUSLY across every sub-mesh of every mesh. Rendering
                    // only meshes[0] dropped whole walls, and handing it the full
                    // material array painted surfaces with the wrong material
                    // (that is why the icons came out grey).
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
        /// Build + render + capture in a single call. Only used by the blocking
        /// preload path (which cannot benefit from multi-frame VT streaming).
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
        /// Returns materials that render correctly OFF-SCREEN.
        ///
        /// WHY THIS IS NEEDED (decompiled SurfaceAsset, 2026-08-03):
        /// CS2 surfaces are virtual-textured. When VT is on, `SyncToUnityMaterial`
        /// deliberately does NOT bind those textures to the material — it enables
        /// the shader keyword ENABLE_VT and lets the streaming system feed them.
        /// Worse, `SurfaceAsset.Load` only builds a material on the FIRST load
        /// (`IncrementRefCount`), so once the game has loaded a surface to draw
        /// the city, asking for a non-VT variant silently returns the existing VT
        /// one — which is why passing useVT:false changed nothing.
        /// Our off-screen camera never runs the VT resolve pass, so those
        /// materials sample nothing and render as flat grey.
        ///
        /// So we clone each material, turn ENABLE_VT off, and bind the real
        /// textures ourselves — exactly what the engine does when VT is disabled.
        /// </summary>
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

        // Reflection handles onto TextureAsset's private state. Resolved once.
        private static readonly System.Reflection.FieldInfo s_TextureInstance =
            typeof(TextureAsset).GetField("m_Instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static readonly System.Reflection.FieldInfo s_TextureRefCount =
            typeof(TextureAsset).GetField("m_ObjectInstanceRefCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>Already-resolved textures, so each one is touched at most once.</summary>
        private readonly Dictionary<TextureAsset, Texture> m_Textures = new();

        /// <summary>
        /// Gets a TextureAsset's Unity texture WITHOUT disturbing the asset
        /// database's reference counting.
        ///
        /// WHY: TextureAsset.Load() does `if (IncrementRefCount(LoadState.Object))
        /// { load } else if (m_Instance == null) { log.Error("reference count is
        /// out of sync"); force-load }`. VT textures live in the streaming atlas
        /// and can sit at refcount &gt; 0 with NO Unity texture instance, so calling
        /// Load() on them lands straight in that error branch — that is the flood
        /// of "object reference count is out of sync" errors.
        ///
        /// So: use the existing instance when there is one, Load() only when the
        /// count is genuinely zero (the safe branch), and otherwise skip the
        /// texture rather than corrupt the count.
        /// </summary>
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

                // A dummy asset has NO database (`isDummy => database == null`),
                // so GetReadStream() — and therefore Load() — throws instantly.
                // These are exactly the virtual-texturing textures: their pixels
                // only exist pre-processed inside the VT store and cannot be
                // loaded standalone. Trying anyway is what produced thousands of
                // NullReferenceExceptions.
                if (texture == null && asset.isDummy)
                {
                    m_Textures[asset] = null;
                    return false;
                }

                if (texture == null)
                {
                    // A VT texture can sit at refCount > 0 with no Unity instance.
                    // Load() detects that, logs "reference count is out of sync"
                    // (the error flood) and RECOVERS by forcing the count to 1 and
                    // loading anyway — so loading is the right thing to do; only
                    // the noise is wrong. Resetting the count to 0 first makes
                    // Load() take its normal branch and reach the exact same end
                    // state (count 1 + instance) without the error.
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
        /// A neutral matte material used when a surface's real textures are
        /// unavailable (which is the norm: CS2 building textures are virtual-
        /// textured dummy assets that cannot be loaded standalone). Produces a
        /// clean architectural "white model" render instead of a murky grey one.
        /// Null if no usable shader could be found, in which case callers keep
        /// the game's own material.
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

            // Surfaces we already know we cannot clone: never retried, otherwise
            // the same failure repeats for every single building using them.
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

            // Bind what the VT path leaves unassigned (albedo, normal, mask...).
            // Guarded per texture: `textures` can be null and one bad entry must
            // not cost us the whole (otherwise perfectly good) material.
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

                        // Only surfaces that genuinely own their textures can be
                        // bound; virtual-textured ones are handled by ClayMaterial.
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
                // Nothing could be bound: every texture of this surface lives
                // ONLY inside the virtual-texturing store (they are dummy assets
                // with no database, so they can never be loaded standalone).
                // Keeping the game's VT material renders a murky dark grey, and
                // turning ENABLE_VT off renders pure black — so instead we fall
                // back to a clean neutral "model" material. The result is a
                // deliberate white-model render, the same style AssetIconLibrary
                // itself ships (its pack has ~7.9k "White" icons).
                UnityEngine.Object.DestroyImmediate(material);

                // PROVEN DEAD END (see the file header): these textures live
                // only inside the virtual-texturing store and there is no way to
                // sample it from an off-screen camera. Go straight to the neutral
                // model material — measured chroma of the VT material was ~0.1/255
                // on every single building, i.e. pure greyscale.
                var clay = ClayMaterial;
                if (clay != null)
                {
                    m_IconMaterials[surface] = clay;
                    return clay;
                }

                m_UnconvertibleSurfaces.Add(surface);
                return original;
            }

            // We have real textures, so the shader must read them instead of the
            // (unresolved) virtual-texturing path.
            material.DisableKeyword(VirtualTexturingKeyword);

            // Track how rare this is: these are the surfaces whose textures are
            // stored as normal assets rather than only inside the VT store, and
            // they are the ONLY ones that can render in real colour.
            m_TexturedSurfaces++;

            if (m_TexturedSurfaces <= MaxLoggedFailures)
            {
                Mod.Log.Info($"IconGenerator: surface '{surface.name}' has {bound} REAL texture(s) — it will render in colour.");
            }

            // Only ever cache OUR clones: the cache is destroyed on cleanup, and
            // destroying a material owned by the game would break the city's
            // rendering.
            m_IconMaterials[surface] = material;
            return material;
        }

        /// <summary>
        /// Places the camera on a 3/4 isometric angle framing the whole subject,
        /// which is the look AssetIconLibrary/FindIt icons use.
        /// </summary>
        private void FrameCamera(Vector3 min, Vector3 max)
        {
            var center = (min + max) * 0.5f;
            var extent = Mathf.Max(0.5f, Vector3.Distance(min, max) * 0.5f);

            // Look at the building's FRONT: CS2 lots face +Z (the street side),
            // so the camera has to sit on the +X/+Z side. The previous
            // (-0.7, .., -0.7) direction was showing the back of every house.
            var direction = new Vector3(0.7f, 0.55f, 0.7f).normalized;

            m_Camera.orthographicSize = extent * 0.95f;
            m_Camera.transform.position = center + (direction * (extent * 4f));
            m_Camera.transform.LookAt(center);
            m_Camera.nearClipPlane = 0.01f;
            m_Camera.farClipPlane = extent * 12f;
        }

        /// <summary>
        /// Reads the render back, rejects useless results, then shrinks the image
        /// as much as possible before saving. Three size levers, in order of gain:
        ///   1. TRIM — crop to the building's opaque bounds. Roughly halves the
        ///      pixel count and makes the building fill the card better.
        ///   2. FLATTEN — force every transparent pixel to a single value
        ///      (0,0,0,0). A uniform region is nearly free in PNG's DEFLATE,
        ///      whereas the noisy near-transparent fringe is not.
        ///   3. QUANTIZE — drop the low 3 bits of each colour channel. At icon
        ///      size this is invisible but massively increases the number of
        ///      repeated bytes, which is exactly what DEFLATE compresses.
        /// PNG (not JPG) because we need the alpha channel; Unity cannot encode
        /// WebP/AVIF, so this is the smallest lossless-alpha option available.
        /// </summary>
        private bool WritePng(string name)
        {
            var previous = RenderTexture.active;

            try
            {
                RenderTexture.active = m_Target;

                // One reused texture + a raw (copy-free) view of its pixels:
                // allocating a Texture2D and a 64 KB Color32[] per icon was pure
                // GC/driver churn across thousands of buildings.
                m_ReadTexture.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                m_ReadTexture.Apply(false);

                var pixels = m_ReadTexture.GetRawTextureData<Color32>();

                // --- Background key ------------------------------------------
                // HDRP does not reliably hand us a transparent alpha channel, so
                // instead of trusting alpha we work out what the background looks
                // like and key it out ourselves. The four corners are always
                // background (the subject is centred and framed to fit), so if
                // they agree we use that colour as the key. Self-calibrating:
                // it does not matter what colour HDRP actually cleared to.
                var c0 = pixels[0];
                var c1 = pixels[IconSize - 1];
                var c2 = pixels[(IconSize - 1) * IconSize];
                var c3 = pixels[(IconSize * IconSize) - 1];

                var hasKey = Similar(c0, c1) && Similar(c0, c2) && Similar(c0, c3);
                var key = c0;

                // Safety net: whatever happens with the corner sampling, a pixel
                // that is clearly magenta is backdrop. Without this, a subject
                // filling the whole frame would leave the corners non-background,
                // disable the key and ship an icon with a glaring magenta
                // background. Written as a relative test so it survives
                // tonemapping/exposure shifting the exact shade.
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

                // A flat single-colour slab that fills the WHOLE frame is a
                // failed render. A flat-coloured but properly shaped object
                // (beach/terrain spawners, plain warehouses) is legitimate, so
                // uniform colour alone must not disqualify it — that over-strict
                // rule is what rejected the BeachSpawn family.
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

                        // DESPILL, applied to EVERY kept pixel (not just the rim).
                        // The magenta backdrop bleeds into the subject through
                        // anti-aliasing and translucent surfaces — windows, fences,
                        // railings — leaving violet blotches INSIDE the building,
                        // which is what stained some icons. The test is deliberately
                        // narrow: only when R and B BOTH exceed G, the signature of
                        // magenta. A genuinely orange pixel (r > g > b) or a cyan one
                        // is left completely untouched, so real textures keep their
                        // colour.
                        if (r > g && b > g)
                        {
                            var spill = Mathf.Min(r - g, b - g);
                            r = (byte)(r - spill);
                            b = (byte)(b - spill);
                        }

                        // Gentle 6-bit quantization: still helps DEFLATE, but
                        // preserves far more of the texture detail than the
                        // previous 5-bit pass (quality now beats bytes).
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

            // HDRP owns clearing and exposure. Without this the camera inherits
            // the scene's sky/fog (an opaque murky background, which also wrecks
            // PNG compression) and the scene's exposure volumes (which is why the
            // first batch came out very dark). Guarded: if any of it throws we
            // still render, just with the previous look.
            try
            {
                var hdCamera = cameraObject.AddComponent<HDAdditionalCameraData>();
                hdCamera.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;

                // MAGENTA, exactly like AssetIconCreator's Magenta.png backdrop.
                // The background is keyed out by COLOUR, so it must be a colour
                // no building surface can have. With a black backdrop every dark
                // roof and shadowed wall matched the key and was punched out —
                // that is what produced the see-through walls.
                hdCamera.backgroundColorHDR = ChromaKeyColor;

                // See ONLY our own volume, never the world's (whose exposure/fog
                // belong to the actual scene and time of day).
                hdCamera.volumeLayerMask = 1 << RenderLayer;

                // Virtual texturing must be ON for this camera, otherwise the
                // building surfaces have no way of ever getting their textures
                // (they exist only inside the VT store). Combined with the
                // multi-frame warm-up in OnUpdate, this is what lets the tiles
                // stream in before we capture.
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

            // THREE-POINT LIGHTING. A single directional light left every face
            // pointing away from it at PURE BLACK (in the real game the sky's
            // ambient/GI fills those, but an isolated off-screen render has no
            // sky at all) while the face catching it blew out to white. The key
            // comes from behind the camera so the FRONT of the building — the
            // side we actually photograph — is the well-lit one.
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
        /// Gives the off-screen camera a deterministic exposure.
        /// CRITICAL: HDRP's Exposure defaults to Fixed at EV100 = 0, i.e. metered
        /// for an almost pitch-black scene. Rendering a 25 000 lux lit building
        /// under that produces a pure white silhouette — which is exactly what
        /// the first batches looked like. A fixed EV matched to the key light
        /// gives a correctly exposed, textured render.
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

            // Our cloned (de-VT'd) materials are ours to destroy; the originals
            // belong to the game and must never be touched.
            // De-duplicated: the neutral material is shared by many surfaces, so
            // a plain loop would try to destroy the same object over and over.
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

            // Textures belong to the asset database — we only ever borrowed
            // references, so drop them without destroying anything.
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
