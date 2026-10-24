// <copyright file="ZoneIcons.cs">
// Custom Zones - Licensed under the MIT license.
// </copyright>

namespace CustomZones.Utilities
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text.RegularExpressions;
    using Colossal.PSI.Environment;
    using Colossal.UI;
    using UnityEngine;

    /// <summary>
    /// Builds each custom zone's toolbar icon from the game's own icon for its
    /// base zone (e.g. Media/Game/Icons/ZoneResidentialLow.svg), with the zone
    /// colour swapped for the custom zone's colour. The result looks exactly like
    /// a vanilla zone button, only in the custom zone's colour.
    /// </summary>
    /// <remarks>
    /// Only the colours close to the base zone's own hue are changed (the zone
    /// tile and its shading), so roofs, walls and outlines keep their colours.
    /// Files are written to ModsData/CustomZones/ZoneIcons and served as
    /// coui://customzones/&lt;file&gt;.
    /// </remarks>
    public static class ZoneIcons
    {
        /// <summary>coui host name for the generated icons.</summary>
        public const string HostName = "customzones";

        /// <summary>Icon used for the toolbar tab (a game icon, no other mod needed).</summary>
        public const string CategoryIcon = "Media/Game/Icons/ColorCustomization.svg";

        private const float MinSaturation = 0.3f;
        private const float MaxHueDistance = 0.1f;

        private static readonly Regex HexColor = new("#([0-9a-fA-F]{6})(?![0-9a-fA-F])");

        private static string s_Folder;
        private static bool s_HostRegistered;

        /// <summary>Folder the generated icons are written to.</summary>
        public static string Folder =>
            s_Folder ??= Path.Combine(EnvPath.kUserDataPath, "ModsData", Mod.Id, "ZoneIcons");

        /// <summary>Makes coui://customzones/ point at <see cref="Folder"/>.</summary>
        public static void RegisterHost()
        {
            if (s_HostRegistered)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Folder);
                UIManager.defaultUISystem.AddHostLocation(HostName, Folder);
                s_HostRegistered = true;
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "ZoneIcons: could not register the icon host.");
            }
        }

        /// <summary>Removes the coui host (mod unload).</summary>
        public static void UnregisterHost()
        {
            if (!s_HostRegistered)
            {
                return;
            }

            try
            {
                UIManager.defaultUISystem.RemoveHostLocation(HostName);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "ZoneIcons: could not remove the icon host.");
            }

            s_HostRegistered = false;
        }

        /// <summary>
        /// Writes the recoloured icon and returns its coui:// address, or null when
        /// the base icon cannot be read (the caller then keeps the base icon).
        /// </summary>
        /// <param name="fileStem">File name without extension (unique per zone).</param>
        /// <param name="baseIcon">The base zone's UIObject icon path.</param>
        /// <param name="baseColor">The base zone's colour (decides which colours get replaced).</param>
        /// <param name="zoneColor">The custom zone's colour.</param>
        public static string Create(string fileStem, string baseIcon, Color baseColor, Color zoneColor)
        {
            var source = ResolveGameUiFile(baseIcon);
            if (source == null || !File.Exists(source))
            {
                Mod.Log.Warn($"ZoneIcons: base icon '{baseIcon}' not found on disk; using it unchanged.");
                return null;
            }

            try
            {
                var svg = File.ReadAllText(source);

                Color.RGBToHSV(baseColor, out var baseHue, out var baseSaturation, out var baseValue);
                Color.RGBToHSV(zoneColor, out var zoneHue, out var zoneSaturation, out var zoneValue);

                // Shades keep their relation to the base colour: a darker / greyer
                // zone colour gives a darker / greyer tile with the same shading.
                var saturationScale = baseSaturation > 0.01f ? zoneSaturation / baseSaturation : 1f;
                var valueScale = baseValue > 0.01f ? zoneValue / baseValue : 1f;

                var replaced = 0;
                svg = HexColor.Replace(svg, match =>
                {
                    var rgb = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var color = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
                    Color.RGBToHSV(color, out var hue, out var saturation, out var value);

                    if (saturation < MinSaturation || HueDistance(hue, baseHue) > MaxHueDistance)
                    {
                        return match.Value;
                    }

                    replaced++;
                    var recoloured = Color.HSVToRGB(
                        zoneHue,
                        Mathf.Clamp01(saturation * saturationScale),
                        Mathf.Clamp01(value * valueScale));
                    return "#" + ColorUtility.ToHtmlStringRGB(recoloured).ToLowerInvariant();
                });

                RegisterHost();
                var fileName = fileStem + ".svg";
                File.WriteAllText(Path.Combine(Folder, fileName), svg);

                Mod.Log.Info($"ZoneIcons: '{fileName}' from '{baseIcon}' ({replaced} colour(s) recoloured).");

                // The version query makes the UI reload the file when the colour changes.
                return $"coui://{HostName}/{fileName}?v={DateTime.UtcNow.Ticks}";
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, $"ZoneIcons: could not build the icon for '{fileStem}'.");
                return null;
            }
        }

        /// <summary>Maps a game UI path (Media/...) to the file inside the game install.</summary>
        private static string ResolveGameUiFile(string icon)
        {
            if (string.IsNullOrEmpty(icon) ||
                icon.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                !icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = icon.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(Application.dataPath, "Content", "Game", "UI", relative);
        }

        private static float HueDistance(float a, float b)
        {
            var d = Math.Abs(a - b);
            return Math.Min(d, 1f - d);
        }
    }
}
