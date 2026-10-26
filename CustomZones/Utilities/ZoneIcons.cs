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
        public static string CategoryIcon
        {
            get
            {
                // The mod's own icon (a 2x2 grid of the game's four zone tiles), written
                // next to the zone icons the first time it is needed.
                if (s_CategoryIcon == null)
                {
                    try
                    {
                        RegisterHost();
                        File.WriteAllText(Path.Combine(Folder, "CustomZones.svg"), CategoryIconSvg);
                        s_CategoryIcon = $"coui://{HostName}/CustomZones.svg";
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"ZoneIcons: could not write the mod icon ({ex.Message}).");
                        s_CategoryIcon = "Media/Game/Icons/Zones.svg";
                    }
                }

                return s_CategoryIcon;
            }
        }

        private static string s_CategoryIcon;

        // Same drawing as UI/src/images/ModIcon.svg.
        private const string CategoryIconSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""32"" height=""32"" viewBox=""0 0 32 32""><path d=""M16.00,11.95 L23.53,16.30 L16.00,20.65 L8.47,16.30 Z"" fill=""#b400ff""/><path d=""M11.03,16.30 L16.00,19.17 L16.00,11.17 L11.03,8.30 Z"" fill=""#dd8cff""/><path d=""M20.97,16.30 L16.00,19.17 L16.00,11.17 L20.97,8.30 Z"" fill=""#7902ab""/><path d=""M16.00,5.43 L20.97,8.30 L16.00,11.17 L11.03,8.30 Z"" fill=""#ecbfff""/><path d=""M24.14,16.65 L31.67,21.00 L24.14,25.35 L16.61,21.00 Z"" fill=""#00c1ff""/><path d=""M19.17,21.00 L24.14,23.87 L24.14,20.27 L19.17,17.40 Z"" fill=""#8ce3ff""/><path d=""M29.11,21.00 L24.14,23.87 L24.14,20.27 L29.11,17.40 Z"" fill=""#0071a9""/><path d=""M24.14,14.53 L29.11,17.40 L24.14,20.27 L19.17,17.40 Z"" fill=""#bfefff""/><path d=""M7.86,16.65 L15.39,21.00 L7.86,25.35 L0.33,21.00 Z"" fill=""#ffdc00""/><path d=""M2.89,21.00 L7.86,23.87 L7.86,20.67 L2.89,17.80 Z"" fill=""#ffef8c""/><path d=""M12.83,21.00 L7.86,23.87 L7.86,20.67 L12.83,17.80 Z"" fill=""#a48700""/><path d=""M7.86,14.93 L12.83,17.80 L7.86,20.67 L2.89,17.80 Z"" fill=""#fff6bf""/><path d=""M7.86,16.08 L8.61,16.51 L8.61,14.31 L7.86,13.88 Z"" fill=""#ffef8c""/><path d=""M16.00,21.35 L23.53,25.70 L16.00,30.05 L8.47,25.70 Z"" fill=""#23eb4e""/><path d=""M11.03,25.70 L16.00,28.57 L16.00,24.77 L11.03,21.90 Z"" fill=""#9cf6af""/><path d=""M20.97,25.70 L16.00,28.57 L16.00,24.77 L20.97,21.90 Z"" fill=""#008f15""/><path d=""M11.03,21.90 L16.00,24.77 L18.49,20.54 L13.51,17.66 Z"" fill=""#269f38""/><path d=""M20.97,21.90 L16.00,24.77 L18.49,20.54 Z"" fill=""#008f15""/><path d=""M16.00,19.03 L20.97,21.90 L18.49,20.54 L13.51,17.66 Z"" fill=""#269f38""/></svg>";

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
                        return match.Value; // not the zone colour
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
