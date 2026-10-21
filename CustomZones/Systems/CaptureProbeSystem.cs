// <copyright file="CaptureProbeSystem.cs">
// CustomZones - Licensed under the MIT license.
//
// MEASUREMENT SPIKE for the "photograph the real game" icon pipeline.
//
// WHY: rendering buildings off-screen can never produce textures (proven — see
// IconGeneratorSystem's header). The only route to real, textured icons is the
// one AssetIconCreator uses: put the building in the world and screenshot the
// game. Before building that whole feature (input blocking, camera/weather
// control, entity spawning, progress UI, restore-on-failure) we need to know
// whether it can hit the target of ~8000 icons in minutes rather than hours.
//
// WHAT IS MEASURED: the two costs that dominate and that we cannot avoid —
//   1. ScreenCapture.CaptureScreenshotAsTexture() — a full-resolution readback.
//   2. Cropping + PNG-encoding one icon out of that capture.
// The frame cost of spawning/settling buildings is per BATCH, not per building,
// so the projection below scales it by the grid size.
//
// THE KEY OPTIMISATION this measures: photographing a GRID of buildings in a
// single capture. One screenshot yields GridSize icons, so the expensive
// per-frame work is divided by GridSize. That is what decides between "a few
// minutes" and "a couple of hours".
//
// Nothing is spawned and nothing is written: this only reads the screen the
// player is already looking at, so it is safe to run at any time.
// </copyright>

namespace CustomZones.Systems
{
    using Game;
    using System;
    using System.Diagnostics;
    using UnityEngine;

    public partial class CaptureProbeSystem : GameSystemBase
    {
        /// <summary>Buildings photographed per screenshot in the real pipeline.</summary>
        private const int GridSize = 16;

        /// <summary>Icons the real run would have to produce.</summary>
        private const int TargetIcons = 8000;

        /// <summary>Frames per batch to spawn, settle and stream the buildings.</summary>
        private const int FramesPerBatch = 12;

        /// <summary>Captures averaged, to smooth out one-off spikes.</summary>
        private const int Samples = 3;

        /// <summary>Final icon resolution (matches the generator).</summary>
        private const int IconSize = 256;

        private bool m_Subscribed;

        /// <summary>
        /// Queues a measurement (called from the UI).
        ///
        /// CRITICAL: ScreenCapture.CaptureScreenshotAsTexture() reads the BACK
        /// BUFFER, so it only returns anything once the frame has actually been
        /// rendered. Calling it from a normal system update — which runs BEFORE
        /// rendering — just returns null (that is why the first attempt logged
        /// "the screen capture returned nothing"). So we hook the render
        /// pipeline's end-of-frame event instead and measure from there.
        /// </summary>
        public void Request()
        {
            if (m_Subscribed)
            {
                return;
            }

            m_Subscribed = true;
            UnityEngine.Rendering.RenderPipelineManager.endFrameRendering += OnFrameRendered;
        }

        private void OnFrameRendered(UnityEngine.Rendering.ScriptableRenderContext context, Camera[] cameras)
        {
            UnityEngine.Rendering.RenderPipelineManager.endFrameRendering -= OnFrameRendered;
            m_Subscribed = false;

            try
            {
                Measure();
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "CaptureProbe: measurement failed.");
            }
        }

        protected override void OnUpdate()
        {
        }

        protected override void OnDestroy()
        {
            if (m_Subscribed)
            {
                UnityEngine.Rendering.RenderPipelineManager.endFrameRendering -= OnFrameRendered;
                m_Subscribed = false;
            }

            base.OnDestroy();
        }

        private void Measure()
        {
            double captureMs = 0;
            double cropEncodeMs = 0;
            var bytes = 0;
            var width = 0;
            var height = 0;

            for (var sample = 0; sample < Samples; sample++)
            {
                var watch = Stopwatch.StartNew();
                var screen = ScreenCapture.CaptureScreenshotAsTexture();
                captureMs += watch.Elapsed.TotalMilliseconds;

                if (screen == null)
                {
                    Mod.Log.Warn("CaptureProbe: the screen capture returned nothing.");
                    return;
                }

                width = screen.width;
                height = screen.height;

                try
                {
                    // Crop + encode exactly one icon, the same work the real
                    // pipeline does GridSize times per screenshot.
                    watch.Restart();

                    var size = Mathf.Min(IconSize, Mathf.Min(width, height));
                    var pixels = screen.GetPixels(0, 0, size, size);

                    var icon = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    icon.SetPixels(pixels);
                    icon.Apply(false);

                    var png = icon.EncodeToPNG();
                    bytes = png?.Length ?? 0;

                    UnityEngine.Object.DestroyImmediate(icon);
                    cropEncodeMs += watch.Elapsed.TotalMilliseconds;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(screen);
                }
            }

            captureMs /= Samples;
            cropEncodeMs /= Samples;

            // Per batch: one screenshot + GridSize crops, plus the frames spent
            // spawning/settling the grid (assumed at 60 fps).
            var batches = Math.Ceiling(TargetIcons / (double)GridSize);
            var frameMs = FramesPerBatch * (1000.0 / 60.0);
            var perBatchMs = captureMs + (cropEncodeMs * GridSize) + frameMs;
            var totalSeconds = perBatchMs * batches / 1000.0;

            Mod.Log.Info(
                $"CaptureProbe: screen {width}x{height} | capture {captureMs:F1} ms | crop+encode {cropEncodeMs:F1} ms/icon ({bytes / 1024} KB) | " +
                $"batch of {GridSize} = {perBatchMs:F0} ms | {TargetIcons} icons in {batches:F0} batches => " +
                $"~{totalSeconds / 60.0:F1} min (~{totalSeconds * 1000.0 / TargetIcons:F0} ms/icon).");

            // Same projection without the grid trick, for comparison: this is the
            // naive one-screenshot-per-building approach AssetIconCreator uses.
            var naiveSeconds = (captureMs + cropEncodeMs + frameMs) * TargetIcons / 1000.0;
            Mod.Log.Info($"CaptureProbe: without the grid (1 capture per building) it would be ~{naiveSeconds / 60.0:F1} min.");
        }
    }
}
