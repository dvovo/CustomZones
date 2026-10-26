// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace CustomZones.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using UnityEngine;

    /// <summary>
    /// Draws a zone icon in the style of the game's zone icons: a flat tile in the
    /// zone colour with one simple isometric building on it, lit from the left
    /// (light walls on the left face, the zone's dark tone on the right face), in
    /// the game's icon palette. The building is built from independent parts - a
    /// body (one or more volumes), a roof, a facade, materials, the ground of the
    /// tile, trees and extras - so any part can go with any other. Presets fill
    /// the parts for the usual kinds of building. The result is a vector SVG.
    /// </summary>
    public static class BuildingZoneIcon
    {
        /// <summary>The volumes of the building.</summary>
        public enum Body
        {
            Box,
            Twin,
            Podium,
            Stepped,
            Stacked,
            LShape,
            Row,
            Silo,
            Spheres,
            Canopy,
            Round,
            Terraced,
            Courtyard,
        }

        /// <summary>Roof of the top volume(s).</summary>
        public enum RoofStyle
        {
            Flat,
            Gable,
            Hip,
            Shed,
            Mansard,
            Gambrel,
            Sawtooth,
            Barrel,
            Butterfly,
            Pyramid,
            Spire,
        }

        /// <summary>Openings of the walls.</summary>
        public enum Facade
        {
            None,
            Windows,
            Bands,
            Large,
            Glass,
            Vertical,
            Wood,
        }

        /// <summary>What lies on the tile around the building.</summary>
        public enum Ground
        {
            None,
            Road,
            Water,
            Parking,
            Plaza,
            Field,
            Garden,
            Pool,
        }

        /// <summary>How the ground floor looks.</summary>
        public enum GroundFloor
        {
            Same,
            Shops,
            Arcade,
            Garages,
        }

        /// <summary>A second, smaller building beside the main one.</summary>
        public enum Annex
        {
            None,
            House,
            Garage,
            Block,
            Tower,
            Shed,
            Silo,
        }

        /// <summary>Trees on the tile.</summary>
        public enum TreeStyle
        {
            None,
            Round,
            Conifer,
            Palm,
        }

        /// <summary>Extras, any combination.</summary>
        [Flags]
        public enum Details
        {
            None = 0,
            Door = 1,
            Chimney = 2,
            Smoke = 4,
            Balconies = 8,
            Awning = 16,
            Sign = 32,
            RoofUnits = 64,
            Solar = 128,
            RoofGarden = 256,
            Antenna = 512,
            Storefront = 1024,
            Garage = 2048,
            Smokestack = 4096,
            VariedRoofs = 8192,
            Helipad = 16384,
            Hedge = 32768,
            WaterTower = 65536,
            Billboard = 131072,
            Flag = 262144,
            Crane = 524288,
            Cars = 1048576,
            Lamps = 2097152,
            RoofPool = 4194304,
        }

        /// <summary>Ready-made combinations of parts for the usual kinds of building.</summary>
        public enum Preset
        {
            House,
            ModernHouse,
            Villa,
            CubeHouse,
            Townhouses,
            ModernTownhouses,
            Block,
            BalconyBlock,
            Tower,
            TwinTowers,
            PodiumTower,
            SteppedTower,
            GlassTower,
            MixedUse,
            Shop,
            Station,
            Office,
            Factory,
            Warehouse,
            Farm,
            Silos,
            GasTanks,
            OldTown,
            ModernVilla,
            RoundTower,
            Skyscraper,
            EcoTerraces,
            CourtyardBlock,
            Headquarters,
        }

        // The game's zone-icon colours.
        private static readonly Color Cream = Hex("fff6d6");
        private static readonly Color Window = Hex("5c7a7e");
        private static readonly Color DarkGlass = Hex("3f5f6a");
        private static readonly Color GlassColor = Hex("73d0ff");
        private static readonly Color DoorColor = Hex("6b4a35");
        private static readonly Color Stone = Hex("e9dfc0");
        private static readonly Color SignOrange = Hex("f39c2b");
        private static readonly Color[] RowRoofs = { Hex("bc8459"), Hex("946949"), Hex("a4483a") };

        private static readonly float Cos30 = Mathf.Cos(30f * Mathf.Deg2Rad);

        /// <summary>The building drawn on an icon (metres, colours "rrggbb").</summary>
        public struct Model
        {
            public Body Body;
            public RoofStyle Roof;
            public Facade Facade;
            public float Width;
            public float Depth;
            public float Height;

            /// <summary>Houses of a row, tanks, ...</summary>
            public int Units;
            public Color RoofColor;
            public Color WallColor;

            /// <summary>Signs, awnings and trims; alpha 0 = the zone colour.</summary>
            public Color Accent;
            public Details Details;
            public Ground Ground;
            public TreeStyle Trees;

            /// <summary>Roof steepness, 0.15 .. 1 (0 = the usual 0.5).</summary>
            public float Pitch;

            /// <summary>Number of floors drawn (0 = from the height).</summary>
            public int Floors;

            /// <summary>Window colour; alpha 0 = the game's blue-grey.</summary>
            public Color WindowColor;
            public GroundFloor GroundFloor;
            public Annex Annex;

            /// <summary>Size of the building on the tile, 0.6 .. 1 (0 = 1).</summary>
            public float Fill;
        }

        /// <summary>The parts of a preset, at a size.</summary>
        public static Model FromPreset(Preset preset, float width, float depth, float height)
        {
            var m = new Model
            {
                Body = Body.Box,
                Roof = RoofStyle.Flat,
                Facade = Facade.Windows,
                Width = width,
                Depth = depth,
                Height = height,
                Units = 3,
                RoofColor = Hex("cec6ac"),
                WallColor = Cream,
                Accent = Color.clear,
            };

            switch (preset)
            {
                case Preset.House:
                    m.Roof = RoofStyle.Gable;
                    m.RoofColor = Hex("bc8459");
                    m.Details = Details.Door | Details.Chimney;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.ModernHouse:
                    m.Body = Body.Stacked;
                    m.Facade = Facade.Large;
                    m.WallColor = Hex("ffffff");
                    m.RoofColor = Hex("8c8676");
                    m.Details = Details.Door | Details.Garage;
                    m.Ground = Ground.Pool;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.Villa:
                    m.Body = Body.LShape;
                    m.Roof = RoofStyle.Hip;
                    m.RoofColor = Hex("a4483a");
                    m.Details = Details.Door;
                    m.Ground = Ground.Pool;
                    m.Trees = TreeStyle.Palm;
                    break;
                case Preset.CubeHouse:
                    m.Roof = RoofStyle.Shed;
                    m.Facade = Facade.Large;
                    m.WallColor = Hex("d2cdc4");
                    m.RoofColor = Hex("6e7378");
                    m.Details = Details.Door | Details.Solar;
                    m.Ground = Ground.Garden;
                    break;
                case Preset.Townhouses:
                    m.Body = Body.Row;
                    m.Roof = RoofStyle.Gable;
                    m.RoofColor = Hex("bc8459");
                    m.Details = Details.Door | Details.VariedRoofs;
                    break;
                case Preset.ModernTownhouses:
                    m.Body = Body.Row;
                    m.Facade = Facade.Large;
                    m.WallColor = Hex("e4d8b3");
                    m.Details = Details.Door | Details.RoofGarden;
                    break;
                case Preset.Block:
                    m.Details = Details.RoofUnits | Details.Door;
                    break;
                case Preset.BalconyBlock:
                    m.WallColor = Hex("e8c4b4");
                    m.Details = Details.Balconies | Details.Door | Details.RoofGarden;
                    break;
                case Preset.Tower:
                    m.Facade = Facade.Bands;
                    m.Details = Details.RoofUnits;
                    break;
                case Preset.TwinTowers:
                    m.Body = Body.Twin;
                    m.WallColor = Hex("e8c4b4");
                    m.Details = Details.RoofUnits;
                    break;
                case Preset.PodiumTower:
                    m.Body = Body.Podium;
                    m.Facade = Facade.Bands;
                    m.Details = Details.Storefront | Details.Awning | Details.Door;
                    break;
                case Preset.SteppedTower:
                    m.Body = Body.Stepped;
                    m.Facade = Facade.Glass;
                    m.Details = Details.Antenna;
                    break;
                case Preset.GlassTower:
                    m.Facade = Facade.Glass;
                    m.Details = Details.RoofUnits | Details.Door;
                    break;
                case Preset.MixedUse:
                    m.Details = Details.Storefront | Details.Sign | Details.Awning | Details.Door;
                    break;
                case Preset.Shop:
                    m.Facade = Facade.None;
                    m.Details = Details.Storefront | Details.Sign | Details.Awning | Details.Door;
                    m.Ground = Ground.Parking;
                    break;
                case Preset.Station:
                    m.Body = Body.Canopy;
                    m.RoofColor = Hex("f2f2f2");
                    m.Details = Details.Sign;
                    m.Ground = Ground.Parking;
                    break;
                case Preset.Office:
                    m.Facade = Facade.Large;
                    m.WallColor = Hex("d2cdc4");
                    m.Details = Details.Door | Details.Solar;
                    m.Ground = Ground.Parking;
                    break;
                case Preset.Factory:
                    m.Roof = RoofStyle.Sawtooth;
                    m.Facade = Facade.None;
                    m.RoofColor = Hex("b9b2a2");
                    m.Details = Details.Door | Details.Smokestack | Details.Smoke;
                    break;
                case Preset.Warehouse:
                    m.Roof = RoofStyle.Barrel;
                    m.Facade = Facade.None;
                    m.RoofColor = Hex("8c8676");
                    m.Details = Details.Door;
                    m.Ground = Ground.Road;
                    break;
                case Preset.Farm:
                    m.Roof = RoofStyle.Gambrel;
                    m.Facade = Facade.None;
                    m.WallColor = Hex("c0473a");
                    m.RoofColor = Hex("6e7378");
                    m.Details = Details.Door;
                    m.Ground = Ground.Field;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.Silos:
                    m.Body = Body.Silo;
                    m.WallColor = Hex("e9e6dc");
                    m.Units = 2;
                    break;
                case Preset.GasTanks:
                    m.Body = Body.Spheres;
                    m.WallColor = Hex("cfd8dc");
                    m.Units = 3;
                    break;
                case Preset.ModernVilla:
                    m.Roof = RoofStyle.Butterfly;
                    m.Facade = Facade.Wood;
                    m.WallColor = Hex("ffffff");
                    m.RoofColor = Hex("6e7378");
                    m.Details = Details.Door | Details.Hedge;
                    m.Ground = Ground.Pool;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.RoundTower:
                    m.Body = Body.Round;
                    m.Facade = Facade.Bands;
                    m.Details = Details.RoofGarden;
                    break;
                case Preset.Skyscraper:
                    m.Roof = RoofStyle.Spire;
                    m.Facade = Facade.Vertical;
                    m.WallColor = Hex("d2cdc4");
                    m.Details = Details.Door;
                    m.Ground = Ground.Plaza;
                    break;
                case Preset.EcoTerraces:
                    m.Body = Body.Terraced;
                    m.Facade = Facade.Large;
                    m.WallColor = Hex("ffffff");
                    m.Details = Details.RoofGarden | Details.Solar;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.CourtyardBlock:
                    m.Body = Body.Courtyard;
                    m.WallColor = Hex("e4d8b3");
                    m.Details = Details.Storefront | Details.RoofGarden;
                    break;
                case Preset.Headquarters:
                    m.Body = Body.Podium;
                    m.Roof = RoofStyle.Pyramid;
                    m.Facade = Facade.Glass;
                    m.Details = Details.Helipad | Details.Storefront;
                    m.Ground = Ground.Plaza;
                    m.Trees = TreeStyle.Round;
                    break;
                case Preset.OldTown:
                    m.Roof = RoofStyle.Mansard;
                    m.WallColor = Hex("f9f3dd");
                    m.RoofColor = Hex("806f69");
                    m.Details = Details.Door | Details.Chimney | Details.Storefront;
                    break;
            }

            return m;
        }

        /// <summary>The preset that suits a building of this kind and height.</summary>
        public static Preset PresetFor(Domain.ZoneCategory category, Domain.DensityType density, float height, string prefabName)
        {
            bool Named(string part) => (prefabName ?? string.Empty).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

            if (Named("GasStation")) return Preset.Station;
            if (Named("StorageOil") || Named("OilHub")) return Preset.GasTanks;
            if (Named("StorageAgriculture")) return Preset.Silos;
            if (Named("AgricultureHub")) return Preset.Farm;
            if (Named("Warehouse") || Named("StorageOre")) return Preset.Warehouse;
            if (Named("OldTown")) return Preset.OldTown;
            if (Named("LowRent")) return Preset.TwinTowers;

            switch (category)
            {
                case Domain.ZoneCategory.Industrial:
                    return Preset.Factory;
                case Domain.ZoneCategory.Office:
                    return height < 20f ? Preset.Office : Preset.GlassTower;
                case Domain.ZoneCategory.Commercial:
                    return height < 12f ? Preset.Shop : Preset.PodiumTower;
                case Domain.ZoneCategory.Mixed:
                    return Preset.MixedUse;
                default:
                    return density switch
                    {
                        Domain.DensityType.Low => height < 16f ? Preset.House : Preset.Block,
                        Domain.DensityType.Row => Preset.Townhouses,
                        Domain.DensityType.Medium => Preset.Block,
                        _ => height >= 24f ? Preset.Tower : Preset.Block,
                    };
            }
        }

        /// <summary>
        /// Roof colour of a building from its picture (the top of an isometric render
        /// is roof), toned to sit with the icon colours. Null when it cannot be read.
        /// </summary>
        public static Color? SampleRoof(string picturePath)
        {
            if (string.IsNullOrEmpty(picturePath) || !File.Exists(picturePath))
            {
                return null;
            }

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(picturePath)))
                {
                    return null;
                }

                var pixels = texture.GetPixels32();
                int width = texture.width, height = texture.height;
                int top = -1, bottom = -1;
                for (var y = height - 1; y >= 0; y--)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (pixels[(y * width) + x].a > 200)
                        {
                            if (top < 0)
                            {
                                top = y;
                            }

                            bottom = y;
                            break;
                        }
                    }
                }

                if (top < 0 || bottom >= top)
                {
                    return null;
                }

                // Texture rows go bottom-up: the roof is in the highest rows.
                var from = top - Mathf.Max(1, Mathf.RoundToInt((top - bottom) * 0.2f));
                float r = 0, g = 0, b = 0;
                var n = 0;
                for (var y = from; y <= top; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var p = pixels[(y * width) + x];
                        if (p.a > 200)
                        {
                            r += p.r;
                            g += p.g;
                            b += p.b;
                            n++;
                        }
                    }
                }

                if (n == 0)
                {
                    return null;
                }

                Color.RGBToHSV(new Color(r / n / 255f, g / n / 255f, b / n / 255f), out var hue, out var saturation, out var value);
                return Color.HSVToRGB(hue, Mathf.Min(saturation, 0.55f), Mathf.Clamp(value * 1.15f, 0.38f, 0.85f));
            }
            catch (Exception ex)
            {
                Mod.Log.Warn($"ZoneIcons: could not read '{picturePath}': {ex.Message}");
                return null;
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        /// <summary>Writes the icon next to the other zone icons and returns its coui:// address, or null on failure.</summary>
        public static string Create(string fileStem, IList<Model> buildings, Color zoneColor)
        {
            if (buildings == null || buildings.Count == 0)
            {
                return null;
            }

            try
            {
                var svg = Draw(buildings[0], zoneColor);
                ZoneIcons.RegisterHost();
                var fileName = fileStem + ".svg";
                File.WriteAllText(Path.Combine(ZoneIcons.Folder, fileName), svg);
                Mod.Log.Info($"ZoneIcons: '{fileName}' drawn ({buildings[0].Body}, {buildings[0].Roof} roof).");
                return $"coui://{ZoneIcons.HostName}/{fileName}?v={DateTime.UtcNow.Ticks}";
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, $"ZoneIcons: could not draw the icon '{fileStem}'.");
                return null;
            }
        }

        // ----------------------------------------------------------- text form

        /// <summary>Text form of a model ("key=value,..."), stored with the zone and edited by the UI.</summary>
        public static string Write(Model m) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "body={0},roof={1},fac={2},w={3:0},d={4:0},h={5:0},units={6},rc={7},wc={8},ac={9},fl={10},gr={11},tr={12},pitch={13:0.##},floors={14},winc={15},gf={16},annex={17},fill={18:0.##}",
                m.Body,
                m.Roof,
                m.Facade,
                m.Width,
                m.Depth,
                m.Height,
                m.Units,
                HexOf(m.RoofColor),
                HexOf(m.WallColor),
                m.Accent.a < 0.5f ? string.Empty : HexOf(m.Accent),
                (int)m.Details,
                m.Ground,
                m.Trees,
                m.Pitch,
                m.Floors,
                m.WindowColor.a < 0.5f ? string.Empty : HexOf(m.WindowColor),
                m.GroundFloor,
                m.Annex,
                m.Fill);

        /// <summary>Reads a model written by <see cref="Write"/> (or by an earlier designer).</summary>
        public static bool TryRead(string text, out Model model)
        {
            model = default;
            if (string.IsNullOrEmpty(text) || text.IndexOf('=') < 0)
            {
                return false;
            }

            var values = text.Split(',')
                .Select(p => p.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());

            float Number(string key, float fallback) =>
                values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
            T Choice<T>(string key, T fallback)
                where T : struct => values.TryGetValue(key, out var v) && Enum.TryParse(v, out T e) ? e : fallback;

            float w = Number("w", 12f), d = Number("d", 10f), h = Number("h", 8f);
            if (!values.ContainsKey("body"))
            {
                // Earlier designer ("shape=..."): its kinds are presets now.
                if (!values.TryGetValue("shape", out var shape))
                {
                    return false;
                }

                var preset = shape switch
                {
                    "House" => Preset.House,
                    "Row" => Preset.Townhouses,
                    "Tower" => Preset.Tower,
                    "Podium" => Preset.PodiumTower,
                    "Glass" => Preset.GlassTower,
                    "Shop" => Preset.Shop,
                    "Station" => Preset.Station,
                    "Factory" => Preset.Factory,
                    "Warehouse" => Preset.Warehouse,
                    "Farm" => Preset.Farm,
                    "Tanks" => Preset.Silos,
                    "Spheres" => Preset.GasTanks,
                    _ => Preset.Block,
                };
                model = FromPreset(preset, w, d, h);
                return true;
            }

            model = new Model
            {
                Body = Choice("body", Body.Box),
                Roof = Choice("roof", RoofStyle.Flat),
                Facade = Choice("fac", Facade.Windows),
                Width = w,
                Depth = d,
                Height = h,
                Units = Mathf.Clamp(Mathf.RoundToInt(Number("units", 3f)), 1, 6),
                RoofColor = values.TryGetValue("rc", out var rc) && ParseHex(rc) is { } roof ? roof : Hex("cec6ac"),
                WallColor = values.TryGetValue("wc", out var wc) && ParseHex(wc) is { } walls ? walls : Cream,
                Accent = values.TryGetValue("ac", out var ac) && ParseHex(ac) is { } accent ? accent : Color.clear,
                Details = (Details)Mathf.RoundToInt(Number("fl", 0f)),
                Ground = Choice("gr", Ground.None),
                Trees = Choice("tr", TreeStyle.None),
                Pitch = Number("pitch", 0f),
                Floors = Mathf.Clamp(Mathf.RoundToInt(Number("floors", 0f)), 0, 60),
                WindowColor = values.TryGetValue("winc", out var winc) && ParseHex(winc) is { } windowColor ? windowColor : Color.clear,
                GroundFloor = Choice("gf", GroundFloor.Same),
                Annex = Choice("annex", Annex.None),
                Fill = Number("fill", 0f),
            };
            return true;
        }

        /// <summary>"rrggbb" (with or without '#') to a colour; null when it is not one.</summary>
        public static Color? ParseHex(string hex)
        {
            hex = (hex ?? string.Empty).Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return null;
            }

            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }

        /// <summary>A colour as "rrggbb".</summary>
        public static string HexOf(Color color) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:x2}{1:x2}{2:x2}",
                Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
                Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f));

        // ------------------------------------------------------------- drawing

        private sealed class Canvas
        {
            public readonly List<(Vector2[] points, Color fill)> Shapes = new();

            // Isometric point at metres: u runs right-down, v left-down, t up.
            public static Vector2 P(float u, float v, float t) => new((u - v) * Cos30, ((u + v) * 0.5f) - t);

            public void Polygon(Color fill, params Vector2[] points) => Shapes.Add((points, fill));

            public void Quad(Color fill, Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
                Polygon(fill, P(a.x, a.y, a.z), P(b.x, b.y, b.z), P(c.x, c.y, c.z), P(d.x, d.y, d.z));

            public void Box(float u, float v, float t, float w, float d, float h, Color lit, Color shade, Color? top)
            {
                Quad(lit, new Vector3(u, v + d, t), new Vector3(u + w, v + d, t), new Vector3(u + w, v + d, t + h), new Vector3(u, v + d, t + h));
                Quad(shade, new Vector3(u + w, v, t), new Vector3(u + w, v + d, t), new Vector3(u + w, v + d, t + h), new Vector3(u + w, v, t + h));
                if (top.HasValue)
                {
                    Quad(top.Value, new Vector3(u, v, t + h), new Vector3(u + w, v, t + h), new Vector3(u + w, v + d, t + h), new Vector3(u, v + d, t + h));
                }
            }

            /// <summary>Rectangle on the front-left face (plane v), from a0 to a1 along u.</summary>
            public void Left(float v, float a0, float a1, float t0, float t1, Color fill) =>
                Quad(fill, new Vector3(a0, v, t0), new Vector3(a1, v, t0), new Vector3(a1, v, t1), new Vector3(a0, v, t1));

            /// <summary>Rectangle on the front-right face (plane u), from a0 to a1 along v.</summary>
            public void Right(float u, float a0, float a1, float t0, float t1, Color fill) =>
                Quad(fill, new Vector3(u, a1, t0), new Vector3(u, a0, t0), new Vector3(u, a0, t1), new Vector3(u, a1, t1));

            public void Ellipse(Vector2 centre, float rx, float ry, Color fill, int points = 16)
            {
                var pts = new Vector2[points];
                for (var i = 0; i < points; i++)
                {
                    var a = 2f * Mathf.PI * i / points;
                    pts[i] = centre + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
                }

                Polygon(fill, pts);
            }

            /// <summary>Vertical cylinder standing at (u, v): lit left half, shaded right half, top.</summary>
            public void Cylinder(float u, float v, float radius, float height, Color lit, Color shade, Color top)
            {
                // A ground circle of radius R projects to an ellipse R*sqrt(2)*cos30 wide, R*sqrt(2)*0.5 high.
                float rx = radius * 1.2247f, ry = radius * 0.7071f;
                var bottom = P(u, v, 0);
                var up = new Vector2(0, -height);
                const int Steps = 8;
                var arc = new List<Vector2>();
                for (var i = 0; i <= Steps; i++)
                {
                    var a = Mathf.PI * (1f - ((float)i / Steps));
                    arc.Add(bottom + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry));
                }

                var whole = new List<Vector2> { bottom + up + new Vector2(-rx, 0) };
                whole.AddRange(arc);
                whole.Add(bottom + up + new Vector2(rx, 0));
                Polygon(shade, whole.ToArray());

                var litPart = new List<Vector2> { bottom + up + new Vector2(-rx, 0) };
                litPart.AddRange(arc.Take((Steps / 2) + 1));
                litPart.Add(bottom + up + new Vector2(0, ry));
                Polygon(lit, litPart.ToArray());
                Ellipse(bottom + up, rx, ry, top);
            }
        }

        /// <summary>A volume of the building: position, size and whether it carries the roof.</summary>
        private struct Volume
        {
            public float U;
            public float V;
            public float T;
            public float W;
            public float D;
            public float H;
            public bool Top;
            public bool Ground;

            /// <summary>Its top is a terrace (green with a roof garden).</summary>
            public bool Terrace;
        }

        /// <summary>Colours of one drawing.</summary>
        private struct Palette
        {
            public Color Zone;
            public Color Walls;
            public Color Shade;
            public Color Dark;
            public Color Roof;
            public Color RoofShade;
            public Color Accent;
            public Color Window;
            public Color LargeWindow;

            /// <summary>Roof rise as a share of the smaller side.</summary>
            public float Pitch;

            /// <summary>Height of a floor (metres).</summary>
            public float FloorHeight;
        }

        internal static string Draw(Model m, Color zone)
        {
            var c = new Canvas();
            float w = Mathf.Max(m.Width, 2f), d = Mathf.Max(m.Depth, 2f), h = Mathf.Max(m.Height, 2f);
            h = Mathf.Min(h, (w + d) * 2f);

            var p = new Palette
            {
                Zone = zone,
                Walls = m.WallColor,
                Shade = Scale(zone, 0.62f),
                Dark = Scale(zone, 0.42f),
                Roof = m.RoofColor,
                RoofShade = Scale(m.RoofColor, 0.78f),
                // Signs and awnings: the game's orange shop-sign colour unless one is chosen.
                Accent = m.Accent.a < 0.5f ? SignOrange : m.Accent,
                Window = m.WindowColor.a < 0.5f ? Window : m.WindowColor,
                LargeWindow = m.WindowColor.a < 0.5f ? DarkGlass : m.WindowColor,
                Pitch = m.Pitch > 0.05f ? Mathf.Clamp(m.Pitch, 0.15f, 1f) : 0.5f,
                FloorHeight = m.Floors > 0 ? Mathf.Max(1.2f, h / m.Floors) : 3.3f,
            };
            bool Has(Details detail) => (m.Details & detail) != 0;

            if (Has(Details.Crane))
            {
                // Tower crane behind the building, its jib over the roof.
                var mast = Mathf.Max(h * 1.25f, 12f);
                var yellow = Hex("f2c230");
                c.Box(w * 0.8f, -2.5f, 0, 1.2f, 1.2f, mast, yellow, Scale(yellow, 0.7f), null);
                c.Box(-w * 0.15f, -2.2f, mast - 1f, w * 1.3f, 0.6f, 0.8f, yellow, Scale(yellow, 0.7f), Scale(yellow, 0.9f));
                c.Box(w * 0.82f, -2.4f, mast - 0.2f, 0.8f, 0.8f, 2.2f, yellow, Scale(yellow, 0.7f), null);
                c.Box(w * 0.05f, -2.0f, mast - 4f, 0.15f, 0.15f, 3f, Hex("4a4f52"), Hex("4a4f52"), null);
            }

            DrawAnnex(c, m, p, w, d, h);

            switch (m.Body)
            {
                case Body.Silo:
                {
                    var count = Mathf.Clamp(m.Units, 1, 4);
                    var radius = Mathf.Min(w / count, d) * 0.42f;
                    for (var i = 0; i < count; i++)
                    {
                        c.Cylinder((w / count) * (i + 0.5f), d / 2f, radius, h, p.Walls, p.Shade, p.Roof);
                    }

                    break;
                }

                case Body.Spheres:
                {
                    var count = Mathf.Clamp(m.Units, 1, 4);
                    var radius = Mathf.Min(w / count, d) * 0.4f;
                    for (var i = 0; i < count; i++)
                    {
                        var cu = (w / count) * (i + 0.5f);
                        var legs = radius * 0.6f;
                        c.Box(cu - (radius * 0.5f), (d / 2f) - 0.2f, 0, 0.4f, 0.4f, legs + radius, Stone, p.Shade, null);
                        c.Box(cu + (radius * 0.4f), (d / 2f) - 0.2f, 0, 0.4f, 0.4f, legs + radius, Stone, p.Shade, null);
                        var centre = Canvas.P(cu, d / 2f, legs + radius);
                        var px = radius * 1.2247f;
                        c.Ellipse(centre, px, px, p.Shade, 20);
                        c.Ellipse(centre + new Vector2(-px * 0.18f, -px * 0.18f), px * 0.8f, px * 0.8f, p.Walls, 20);
                        c.Ellipse(centre + new Vector2(-px * 0.38f, -px * 0.38f), px * 0.28f, px * 0.28f, Scale(p.Walls, 1.08f), 12);
                    }

                    break;
                }

                case Body.Round:
                {
                    DrawRound(c, m, p, w, d, h);
                    break;
                }

                case Body.Canopy:
                {
                    // Small shop at the back, a canopy on pillars over the pumps in front.
                    var shopHeight = Mathf.Min(h, 5f);
                    c.Box(0, 0, 0, w * 0.45f, d * 0.4f, shopHeight, p.Walls, p.Shade, p.Roof);
                    c.Left(d * 0.4f, w * 0.08f, w * 0.38f, 0.3f, shopHeight * 0.55f, GlassColor);
                    var canopy = Mathf.Min(h, 6f);
                    foreach (var (fu, fv) in new[] { (0.15f, 0.55f), (0.85f, 0.55f), (0.15f, 0.95f), (0.85f, 0.95f) })
                    {
                        c.Box((w * fu) - 0.3f, (d * fv) - 0.3f, 0, 0.6f, 0.6f, canopy, Stone, p.Shade, null);
                    }

                    c.Box(w * 0.4f, d * 0.7f, 0, 1.2f, 0.8f, 1.6f, Scale(p.Dark, 1.6f), p.Dark, Stone);
                    c.Box(0, d * 0.45f, canopy, w, d * 0.55f, 0.9f, p.Accent, p.Dark, p.Roof);
                    break;
                }

                case Body.Row:
                {
                    var units = Mathf.Clamp(m.Units, 2, 6);
                    var unit = w / units;
                    for (var i = 0; i < units; i++)
                    {
                        var unitHeight = h * (1f + (0.12f * (((i * 7) % 3) - 1)));
                        var unitPalette = p;
                        if (Has(Details.VariedRoofs))
                        {
                            unitPalette.Roof = RowRoofs[i % RowRoofs.Length];
                            unitPalette.RoofShade = Scale(unitPalette.Roof, 0.78f);
                        }

                        var volume = new Volume { U = i * unit, W = unit, D = d, H = unitHeight, Top = true, Ground = true };
                        DrawVolume(c, volume, m, unitPalette, roofAxisAlongDepth: true);
                    }

                    break;
                }

                default:
                {
                    foreach (var volume in Volumes(m.Body, w, d, h))
                    {
                        DrawVolume(c, volume, m, p, roofAxisAlongDepth: false);
                    }

                    if (Has(Details.Garage) && m.Body != Body.LShape)
                    {
                        // Low garage on the right side, with its door.
                        var gh = Mathf.Min(3f, h * 0.45f);
                        var gd = Mathf.Min(d * 0.6f, 6f);
                        c.Box(w, d - gd, 0, 4f, gd, gh, p.Walls, p.Shade, p.Roof);
                        c.Right(w + 4f, d - gd + 0.6f, d - 0.6f, 0, gh * 0.8f, p.Dark);
                    }

                    break;
                }
            }

            if (Has(Details.Smokestack))
            {
                var stackHeight = Mathf.Max(h * 2.3f, 10f);
                c.Box(w * 0.78f, d * 0.12f, 0, 2.2f, 2.2f, stackHeight, Stone, p.Shade, Scale(p.Shade, 0.8f));
                if (Has(Details.Smoke))
                {
                    Smoke(c, Canvas.P((w * 0.78f) + 1.1f, (d * 0.12f) + 1.1f, stackHeight), 1.6f);
                }
            }

            return ToSvg(c, w, d, zone, m);
        }

        /// <summary>The smaller building on the back left, drawn before the main one.</summary>
        private static void DrawAnnex(Canvas c, Model m, Palette p, float w, float d, float h)
        {
            if (m.Annex == Annex.None)
            {
                return;
            }

            if (m.Annex == Annex.Silo)
            {
                var radius = Mathf.Max(1.5f, Mathf.Min(w, d) * 0.2f);
                c.Cylinder(-radius - 1.5f, radius + 0.5f, radius, Mathf.Max(h * 1.3f, 8f), Hex("e9e6dc"), p.Shade, Hex("cec6ac"));
                return;
            }

            float aw, ad, ah;
            var annex = m;
            annex.Body = Body.Box;
            annex.Details = m.Details & Details.Door;
            annex.GroundFloor = GroundFloor.Same;
            switch (m.Annex)
            {
                case Annex.House:
                    aw = Mathf.Max(6f, w * 0.45f);
                    ad = Mathf.Max(6f, d * 0.6f);
                    ah = Mathf.Min(h * 0.45f, 5f);
                    annex.Roof = RoofStyle.Gable;
                    annex.Facade = Facade.Windows;
                    break;
                case Annex.Garage:
                    aw = 5f;
                    ad = 6f;
                    ah = 3f;
                    annex.Roof = RoofStyle.Flat;
                    annex.Facade = Facade.None;
                    annex.Details = Details.None;
                    annex.GroundFloor = GroundFloor.Garages;
                    break;
                case Annex.Block:
                    aw = Mathf.Max(6f, w * 0.6f);
                    ad = Mathf.Max(6f, d * 0.7f);
                    ah = Mathf.Max(4f, h * 0.55f);
                    annex.Roof = RoofStyle.Flat;
                    break;
                case Annex.Tower:
                    aw = Mathf.Max(5f, w * 0.4f);
                    ad = Mathf.Max(5f, d * 0.4f);
                    ah = Mathf.Max(h * 1.3f, 12f);
                    annex.Roof = RoofStyle.Flat;
                    annex.Facade = m.Facade == Facade.None ? Facade.Bands : m.Facade;
                    break;
                default: // Shed
                    aw = Mathf.Max(6f, w * 0.5f);
                    ad = Mathf.Max(6f, d * 0.6f);
                    ah = Mathf.Min(h * 0.5f, 6f);
                    annex.Roof = RoofStyle.Barrel;
                    annex.Facade = Facade.None;
                    break;
            }

            var volume = new Volume { U = -aw - 1.5f, V = 0, W = aw, D = ad, H = ah, Top = true, Ground = true };
            DrawVolume(c, volume, annex, p, roofAxisAlongDepth: false);
        }

        /// <summary>The volumes of a body, back to front.</summary>
        private static IEnumerable<Volume> Volumes(Body body, float w, float d, float h)
        {
            switch (body)
            {
                case Body.Twin:
                {
                    var half = w * 0.46f;
                    yield return new Volume { U = 0, W = half, D = d, H = h, Top = true, Ground = true };
                    yield return new Volume { U = w - half, W = half, D = d, H = h * 0.82f, Top = true, Ground = true };
                    break;
                }

                case Body.Podium:
                {
                    var podium = Mathf.Min(h * 0.25f, 8f);
                    yield return new Volume { W = w, D = d, H = podium, Ground = true };
                    yield return new Volume { U = w * 0.18f, V = d * 0.18f, T = podium, W = w * 0.64f, D = d * 0.64f, H = h - podium, Top = true };
                    break;
                }

                case Body.Stepped:
                {
                    const int Tiers = 3;
                    var tier = h / Tiers;
                    for (var i = 0; i < Tiers; i++)
                    {
                        var inset = i * 0.14f;
                        yield return new Volume
                        {
                            U = w * inset,
                            V = d * inset,
                            T = i * tier,
                            W = w * (1f - (2f * inset)),
                            D = d * (1f - (2f * inset)),
                            H = tier,
                            Top = i == Tiers - 1,
                            Ground = i == 0,
                        };
                    }

                    break;
                }

                case Body.Stacked:
                {
                    // Lower floor, upper floor pushed out to the left over a terrace.
                    var lower = h * 0.5f;
                    yield return new Volume { U = w * 0.25f, W = w * 0.75f, D = d, H = lower, Top = false, Ground = true };
                    yield return new Volume { U = 0, V = d * 0.15f, T = lower, W = w * 0.7f, D = d * 0.85f, H = h - lower, Top = true };
                    break;
                }

                case Body.Terraced:
                {
                    // Floors stepping back from the front, each terrace open to the sky.
                    const int Tiers = 3;
                    var tier = h / Tiers;
                    for (var i = 0; i < Tiers; i++)
                    {
                        yield return new Volume { T = i * tier, W = w, D = d * (1f - (0.25f * i)), H = tier, Top = i == Tiers - 1, Ground = i == 0, Terrace = i < Tiers - 1 };
                    }

                    break;
                }

                case Body.Courtyard:
                {
                    // Back wing and two side wings around a courtyard open to the front.
                    yield return new Volume { W = w, D = d * 0.35f, H = h, Top = true, Ground = true };
                    yield return new Volume { V = d * 0.35f, W = w * 0.3f, D = d * 0.65f, H = h, Top = true, Ground = true };
                    yield return new Volume { U = w * 0.7f, V = d * 0.35f, W = w * 0.3f, D = d * 0.65f, H = h, Top = true, Ground = true };
                    break;
                }

                case Body.LShape:
                {
                    // A back wing along the width and a front wing on the right.
                    yield return new Volume { W = w, D = d * 0.5f, H = h, Top = true, Ground = true };
                    yield return new Volume { U = w * 0.55f, V = d * 0.5f, W = w * 0.45f, D = d * 0.5f, H = h, Top = true, Ground = true };
                    break;
                }

                default:
                    yield return new Volume { W = w, D = d, H = h, Top = true, Ground = true };
                    break;
            }
        }

        private static void DrawVolume(Canvas c, Volume vol, Model m, Palette p, bool roofAxisAlongDepth)
        {
            bool Has(Details detail) => (m.Details & detail) != 0;
            float u = vol.U, v = vol.V, t = vol.T, w = vol.W, d = vol.D, h = vol.H;
            var flatTop = !vol.Top || m.Roof == RoofStyle.Flat || m.Roof == RoofStyle.Spire;
            c.Box(u, v, t, w, d, h, p.Walls, p.Shade, flatTop ? p.Roof : (Color?)null);
            if (vol.Terrace && Has(Details.RoofGarden))
            {
                var e = Mathf.Min(w, d) * 0.06f;
                c.Quad(Hex("6fbf4a"), new Vector3(u + e, v + e, t + h), new Vector3(u + w - e, v + e, t + h), new Vector3(u + w - e, v + d - e, t + h), new Vector3(u + e, v + d - e, t + h));
            }

            // Facade: floors of ~3.3 m, the ground floor a shop window when asked.
            var floors = Mathf.Max(1, Mathf.RoundToInt(h / p.FloorHeight));
            var fh = h / floors;
            var first = 0;
            var shops = Has(Details.Storefront) || m.GroundFloor == GroundFloor.Shops;
            if (vol.Ground && m.GroundFloor == GroundFloor.Arcade)
            {
                // Arcade: a shaded walkway behind columns.
                var top = t + Mathf.Min(3f, h * 0.45f);
                c.Left(v + d, u, u + w, t, top, p.Dark);
                c.Right(u + w, v, v + d, t, top, Scale(p.Dark, 0.8f));
                var columns = Mathf.Max(3, Mathf.RoundToInt(w / 2.5f));
                for (var i = 0; i <= columns; i++)
                {
                    var a = u + (w * i / columns);
                    c.Left(v + d, a - 0.3f, a + 0.3f, t, top, p.Walls);
                }

                first = 1;
            }
            else if (vol.Ground && m.GroundFloor == GroundFloor.Garages)
            {
                // A row of garage doors.
                var top = t + Mathf.Min(2.4f, h * 0.4f);
                var doors = Mathf.Max(1, Mathf.RoundToInt(w / 3.2f));
                for (var i = 0; i < doors; i++)
                {
                    var a = u + (w * (i + 0.5f) / doors);
                    var half = w / doors * 0.36f;
                    c.Left(v + d, a - half, a + half, t, top, Hex("8c8676"));
                    c.Left(v + d, a - half, a + half, top - 0.25f, top, Scale(Hex("8c8676"), 0.8f));
                }

                first = 1;
            }
            else if (vol.Ground && shops)
            {
                var top = t + Mathf.Min(2.6f, h * 0.45f);
                c.Left(v + d, u + (w * 0.06f), u + (w * 0.94f), t + 0.3f, top, GlassColor);
                c.Right(u + w, v + (d * 0.06f), v + (d * 0.94f), t + 0.3f, top, p.Dark);
                if (Has(Details.Awning))
                {
                    c.Quad(p.Accent, new Vector3(u, v + d, top), new Vector3(u + w, v + d, top), new Vector3(u + w, v + d + 1.4f, top - 0.9f), new Vector3(u, v + d + 1.4f, top - 0.9f));
                }

                first = 1;
            }

            var doorAt = u + (w / 2f);
            var doorHere = vol.Ground && Has(Details.Door) && !shops && m.GroundFloor != GroundFloor.Garages;
            DrawFacade(c, m.Facade, u, v, t, w, d, h, floors, first, p, doorHere ? doorAt : (float?)null);

            if (Has(Details.Balconies) && floors > 1)
            {
                for (var f = Math.Max(1, first); f < floors; f++)
                {
                    var tt = t + (fh * f);
                    c.Quad(Scale(p.Walls, 0.9f), new Vector3(u + (w * 0.1f), v + d, tt), new Vector3(u + (w * 0.9f), v + d, tt), new Vector3(u + (w * 0.9f), v + d + 0.9f, tt), new Vector3(u + (w * 0.1f), v + d + 0.9f, tt));
                }
            }

            if (doorHere)
            {
                c.Left(v + d, doorAt - 0.75f, doorAt + 0.75f, t, t + Mathf.Min(2.4f, h * 0.5f), DoorColor);
            }

            if (!vol.Top)
            {
                return;
            }

            if (flatTop)
            {
                var e = Mathf.Min(w, d) * 0.1f;
                c.Quad(Scale(p.Roof, 0.88f), new Vector3(u + e, v + e, t + h), new Vector3(u + w - e, v + e, t + h), new Vector3(u + w - e, v + d - e, t + h), new Vector3(u + e, v + d - e, t + h));
                RoofExtras(c, m, p, u, v, w, d, t + h);
                if (m.Roof == RoofStyle.Spire)
                {
                    var crown = Mathf.Min(w, d) * 0.4f;
                    c.Box(u + ((w - crown) / 2f), v + ((d - crown) / 2f), t + h, crown, crown, h * 0.08f, p.Walls, p.Shade, p.Roof);
                    c.Box(u + (w / 2f) - 0.3f, v + (d / 2f) - 0.3f, t + h + (h * 0.08f), 0.6f, 0.6f, h * 0.3f, Stone, p.Shade, null);
                }
            }
            else
            {
                DrawRoof(c, m.Roof, m, p, u, v, t + h, w, d, roofAxisAlongDepth);
            }

            if (Has(Details.Sign))
            {
                // Sign board in the accent colour on the roof edge, like the game's shop signs.
                var signW = Mathf.Min(w * 0.6f, 10f);
                var signH = Mathf.Max(1.4f, Mathf.Min(w, d) * 0.18f);
                var u0 = u + ((w - signW) / 2f);
                var top = t + h;
                c.Left(v + d - 0.2f, u0, u0 + signW, top, top + signH, p.Accent);
                c.Left(v + d - 0.21f, u0 + (signW * 0.12f), u0 + (signW * 0.88f), top + (signH * 0.35f), top + (signH * 0.6f), Scale(p.Accent, 1.35f));
            }
        }

        private static void DrawFacade(Canvas c, Facade facade, float u, float v, float t, float w, float d, float h, int floors, int first, Palette p, float? doorAt)
        {
            var fh = h / floors;
            switch (facade)
            {
                case Facade.Glass:
                {
                    c.Left(v + d, u, u + w, t + (first * fh), t + h, GlassColor);
                    c.Right(u + w, v, v + d, t + (first * fh), t + h, Scale(p.Zone, 0.55f));
                    for (var f = Math.Max(1, first); f < floors; f++)
                    {
                        var tt = t + (fh * f);
                        c.Left(v + d, u, u + w, tt - 0.15f, tt + 0.15f, Scale(GlassColor, 0.8f));
                        c.Right(u + w, v, v + d, tt - 0.15f, tt + 0.15f, p.Dark);
                    }

                    break;
                }

                case Facade.None:
                    break;

                case Facade.Vertical:
                {
                    // Window strips between slim vertical fins (modern).
                    c.Left(v + d, u, u + w, t + (first * fh), t + h, p.LargeWindow);
                    c.Right(u + w, v, v + d, t + (first * fh), t + h, p.Dark);
                    var fins = Mathf.Max(3, Mathf.RoundToInt(w / 1.8f));
                    for (var i = 0; i <= fins; i++)
                    {
                        var a = u + (w * i / fins);
                        c.Left(v + d, a - 0.25f, a + 0.25f, t + (first * fh), t + h, p.Walls);
                    }

                    var sideFins = Mathf.Max(3, Mathf.RoundToInt(d / 1.8f));
                    for (var i = 0; i <= sideFins; i++)
                    {
                        var a = v + (d * i / sideFins);
                        c.Right(u + w, a - 0.25f, a + 0.25f, t + (first * fh), t + h, p.Shade);
                    }

                    break;
                }

                case Facade.Wood:
                {
                    // Timber cladding with large windows.
                    var wood = Hex("c08a55");
                    c.Left(v + d, u, u + w, t + (first * fh), t + h, wood);
                    c.Right(u + w, v, v + d, t + (first * fh), t + h, Scale(wood, 0.7f));
                    for (var f = first; f < floors; f++)
                    {
                        for (var k = 1; k < 4; k++)
                        {
                            var tt = t + (f * fh) + (fh * k / 4f);
                            c.Left(v + d, u, u + w, tt - 0.05f, tt + 0.05f, Scale(wood, 0.85f));
                        }
                    }

                    DrawFacade(c, Facade.Large, u, v, t, w, d, h, floors, first, p, doorAt);
                    break;
                }

                default:
                {
                    for (var f = first; f < floors; f++)
                    {
                        var t0 = t + (f * fh) + (fh * (facade == Facade.Large ? 0.18f : 0.3f));
                        var t1 = t + (f * fh) + (fh * (facade == Facade.Large ? 0.82f : 0.72f));
                        if (facade == Facade.Bands)
                        {
                            c.Left(v + d, u + (w * 0.06f), u + (w * 0.94f), t0, t1, p.Window);
                            c.Right(u + w, v + (d * 0.06f), v + (d * 0.94f), t0, t1, p.Dark);
                            continue;
                        }

                        var spacing = facade == Facade.Large ? 5f : 3.2f;
                        var fill = facade == Facade.Large ? 0.36f : 0.22f;
                        var colour = facade == Facade.Large ? p.LargeWindow : p.Window;
                        var columns = Mathf.Max(1, Mathf.RoundToInt(w / spacing));
                        for (var col = 0; col < columns; col++)
                        {
                            var a = u + (w * (col + 0.5f) / columns);
                            var half = w / columns * fill;
                            if (f == 0 && doorAt.HasValue && Mathf.Abs(a - doorAt.Value) < half + 0.8f)
                            {
                                continue;
                            }

                            c.Left(v + d, a - half, a + half, t0, t1, colour);
                        }

                        var sideColumns = Mathf.Max(1, Mathf.RoundToInt(d / spacing));
                        for (var col = 0; col < sideColumns; col++)
                        {
                            var a = v + (d * (col + 0.5f) / sideColumns);
                            var half = d / sideColumns * fill;
                            c.Right(u + w, a - half, a + half, t0, t1, p.Dark);
                        }
                    }

                    break;
                }
            }
        }

        private static void DrawRoof(Canvas c, RoofStyle style, Model m, Palette p, float u, float v, float h, float w, float d, bool ridgeAlongDepth)
        {
            bool Has(Details detail) => (m.Details & detail) != 0;
            var alongWidth = ridgeAlongDepth ? false : w >= d;
            var rise = Mathf.Min(w, d) * p.Pitch;

            if (Has(Details.Chimney) && style != RoofStyle.Sawtooth && style != RoofStyle.Barrel)
            {
                c.Box(u + (w * 0.25f), v + (d * 0.2f), h, 1.3f, 1.3f, rise + 1.4f, Stone, p.Shade, Scale(p.Shade, 0.8f));
            }

            switch (style)
            {
                case RoofStyle.Hip:
                {
                    var inset = Mathf.Min(w, d) / 2f;
                    if (alongWidth)
                    {
                        var a = new Vector3(u + inset, v + (d / 2f), h + rise);
                        var b = new Vector3(u + w - inset, v + (d / 2f), h + rise);
                        c.Quad(p.Roof, new Vector3(u, v + d, h), new Vector3(u + w, v + d, h), b, a);
                        c.Polygon(p.RoofShade, Canvas.P(u + w, v, h), Canvas.P(u + w, v + d, h), Canvas.P(b.x, b.y, b.z));
                    }
                    else
                    {
                        var a = new Vector3(u + (w / 2f), v + inset, h + rise);
                        var b = new Vector3(u + (w / 2f), v + d - inset, h + rise);
                        c.Polygon(p.Roof, Canvas.P(u, v + d, h), Canvas.P(u + w, v + d, h), Canvas.P(b.x, b.y, b.z));
                        c.Quad(p.RoofShade, new Vector3(u + w, v, h), new Vector3(u + w, v + d, h), b, a);
                    }

                    break;
                }

                case RoofStyle.Shed:
                {
                    // Single slope rising to the back (modern).
                    var high = Mathf.Min(w, d) * 0.35f;
                    c.Quad(p.Roof, new Vector3(u, v + d, h), new Vector3(u + w, v + d, h), new Vector3(u + w, v, h + high), new Vector3(u, v, h + high));
                    c.Polygon(p.Shade, Canvas.P(u + w, v + d, h), Canvas.P(u + w, v, h), Canvas.P(u + w, v, h + high));
                    break;
                }

                case RoofStyle.Mansard:
                {
                    // Steep slopes all round, a flat top.
                    var inset = Mathf.Min(w, d) * 0.18f;
                    var up = Mathf.Min(w, d) * 0.4f;
                    var top = h + up;
                    c.Quad(p.RoofShade, new Vector3(u, v, h), new Vector3(u + w, v, h), new Vector3(u + w - inset, v + inset, top), new Vector3(u + inset, v + inset, top));
                    c.Quad(p.Roof, new Vector3(u, v + d, h), new Vector3(u + w, v + d, h), new Vector3(u + w - inset, v + d - inset, top), new Vector3(u + inset, v + d - inset, top));
                    c.Quad(p.RoofShade, new Vector3(u + w, v, h), new Vector3(u + w, v + d, h), new Vector3(u + w - inset, v + d - inset, top), new Vector3(u + w - inset, v + inset, top));
                    c.Quad(Scale(p.Roof, 0.9f), new Vector3(u + inset, v + inset, top), new Vector3(u + w - inset, v + inset, top), new Vector3(u + w - inset, v + d - inset, top), new Vector3(u + inset, v + d - inset, top));

                    // Dormers on the front slope.
                    var dormers = Mathf.Max(1, Mathf.RoundToInt(w / 5f));
                    for (var i = 0; i < dormers; i++)
                    {
                        var a = u + (w * (i + 0.5f) / dormers);
                        c.Left(v + d - (inset * 0.5f), a - 0.6f, a + 0.6f, h + (up * 0.2f), h + (up * 0.65f), p.Walls);
                        c.Left(v + d - (inset * 0.5f) + 0.01f, a - 0.35f, a + 0.35f, h + (up * 0.25f), h + (up * 0.55f), Window);
                    }

                    break;
                }

                case RoofStyle.Gambrel:
                {
                    var profile = new[] { new Vector2(0, 0), new Vector2(0.18f, 0.62f), new Vector2(0.5f, 1f), new Vector2(0.82f, 0.62f), new Vector2(1f, 0) };
                    var gr = Mathf.Min(w, d) * 0.55f;
                    if (alongWidth)
                    {
                        for (var k = 0; k < profile.Length - 1; k++)
                        {
                            Vector2 a = profile[k], b = profile[k + 1];
                            c.Quad(k < 2 ? p.RoofShade : p.Roof, new Vector3(u, v + (a.x * d), h + (a.y * gr)), new Vector3(u + w, v + (a.x * d), h + (a.y * gr)), new Vector3(u + w, v + (b.x * d), h + (b.y * gr)), new Vector3(u, v + (b.x * d), h + (b.y * gr)));
                        }

                        c.Polygon(Scale(p.Walls, 0.7f), profile.Select(q => Canvas.P(u + w, v + (q.x * d), h + (q.y * gr))).ToArray());
                    }
                    else
                    {
                        for (var k = 0; k < profile.Length - 1; k++)
                        {
                            Vector2 a = profile[k], b = profile[k + 1];
                            c.Quad(k < 2 ? p.Roof : p.RoofShade, new Vector3(u + (a.x * w), v, h + (a.y * gr)), new Vector3(u + (a.x * w), v + d, h + (a.y * gr)), new Vector3(u + (b.x * w), v + d, h + (b.y * gr)), new Vector3(u + (b.x * w), v, h + (b.y * gr)));
                        }

                        c.Polygon(p.Walls, profile.Select(q => Canvas.P(u + (q.x * w), v + d, h + (q.y * gr))).ToArray());
                    }

                    break;
                }

                case RoofStyle.Sawtooth:
                {
                    var teeth = Mathf.Max(2, Mathf.RoundToInt(w / 6f));
                    var tooth = w / teeth;
                    var toothRise = Mathf.Min(4f, Mathf.Min(w, d) * 0.3f);
                    c.Quad(Scale(p.Roof, 0.85f), new Vector3(u, v, h), new Vector3(u + w, v, h), new Vector3(u + w, v + d, h), new Vector3(u, v + d, h));
                    for (var i = 0; i < teeth; i++)
                    {
                        var a = u + (i * tooth);
                        var peak = a + (tooth * 0.75f);
                        c.Quad(p.Roof, new Vector3(a, v, h), new Vector3(a, v + d, h), new Vector3(peak, v + d, h + toothRise), new Vector3(peak, v, h + toothRise));
                        c.Quad(GlassColor, new Vector3(peak, v, h + toothRise), new Vector3(peak, v + d, h + toothRise), new Vector3(a + tooth, v + d, h), new Vector3(a + tooth, v, h));
                        c.Polygon(p.Walls, Canvas.P(a, v + d, h), Canvas.P(a + tooth, v + d, h), Canvas.P(peak, v + d, h + toothRise));
                    }

                    break;
                }

                case RoofStyle.Barrel:
                {
                    const int Segments = 8;
                    var barrel = d * 0.35f;
                    var profile = new List<Vector2>();
                    for (var k = 0; k <= Segments; k++)
                    {
                        profile.Add(new Vector2(d * k / Segments, barrel * Mathf.Sin(Mathf.PI * k / Segments)));
                    }

                    for (var k = 0; k < Segments; k++)
                    {
                        Vector2 a = profile[k], b = profile[k + 1];
                        c.Quad(k >= Segments / 2 ? p.Roof : p.RoofShade, new Vector3(u, v + a.x, h + a.y), new Vector3(u + w, v + a.x, h + a.y), new Vector3(u + w, v + b.x, h + b.y), new Vector3(u, v + b.x, h + b.y));
                    }

                    var end = profile.Select(q => Canvas.P(u + w, v + q.x, h + q.y)).ToList();
                    end.Add(Canvas.P(u + w, v + d, h));
                    end.Add(Canvas.P(u + w, v, h));
                    c.Polygon(p.Shade, end.ToArray());
                    break;
                }

                case RoofStyle.Butterfly:
                {
                    // Two slopes falling to a central valley (modern).
                    var up = Mathf.Min(w, d) * 0.22f;
                    c.Quad(p.RoofShade, new Vector3(u, v, h + up), new Vector3(u + w, v, h + up), new Vector3(u + w, v + (d / 2f), h), new Vector3(u, v + (d / 2f), h));
                    c.Quad(p.Roof, new Vector3(u, v + (d / 2f), h), new Vector3(u + w, v + (d / 2f), h), new Vector3(u + w, v + d, h + up), new Vector3(u, v + d, h + up));
                    c.Polygon(p.Shade, Canvas.P(u + w, v, h), Canvas.P(u + w, v, h + up), Canvas.P(u + w, v + (d / 2f), h));
                    c.Polygon(p.Shade, Canvas.P(u + w, v + (d / 2f), h), Canvas.P(u + w, v + d, h + up), Canvas.P(u + w, v + d, h));
                    c.Quad(p.Walls, new Vector3(u, v + d, h), new Vector3(u + w, v + d, h), new Vector3(u + w, v + d, h + up), new Vector3(u, v + d, h + up));
                    c.Quad(Scale(p.Roof, 0.6f), new Vector3(u, v + d, h + up - 0.25f), new Vector3(u + w, v + d, h + up - 0.25f), new Vector3(u + w, v + d, h + up), new Vector3(u, v + d, h + up));
                    break;
                }

                case RoofStyle.Pyramid:
                {
                    // Glass pyramid on top.
                    var apex = new Vector3(u + (w / 2f), v + (d / 2f), h + (Mathf.Min(w, d) * 0.6f));
                    c.Polygon(GlassColor, Canvas.P(u, v + d, h), Canvas.P(u + w, v + d, h), Canvas.P(apex.x, apex.y, apex.z));
                    c.Polygon(Scale(p.Zone, 0.55f), Canvas.P(u + w, v, h), Canvas.P(u + w, v + d, h), Canvas.P(apex.x, apex.y, apex.z));
                    break;
                }

                default: // Gable
                    if (alongWidth)
                    {
                        c.Quad(p.RoofShade, new Vector3(u, v, h), new Vector3(u + w, v, h), new Vector3(u + w, v + (d / 2f), h + rise), new Vector3(u, v + (d / 2f), h + rise));
                        c.Quad(p.Roof, new Vector3(u, v + d, h), new Vector3(u + w, v + d, h), new Vector3(u + w, v + (d / 2f), h + rise), new Vector3(u, v + (d / 2f), h + rise));
                        c.Polygon(p.Shade, Canvas.P(u + w, v, h), Canvas.P(u + w, v + d, h), Canvas.P(u + w, v + (d / 2f), h + rise));
                    }
                    else
                    {
                        c.Quad(p.Roof, new Vector3(u, v, h), new Vector3(u, v + d, h), new Vector3(u + (w / 2f), v + d, h + rise), new Vector3(u + (w / 2f), v, h + rise));
                        c.Quad(p.RoofShade, new Vector3(u + w, v, h), new Vector3(u + w, v + d, h), new Vector3(u + (w / 2f), v + d, h + rise), new Vector3(u + (w / 2f), v, h + rise));
                        c.Polygon(p.Walls, Canvas.P(u, v + d, h), Canvas.P(u + w, v + d, h), Canvas.P(u + (w / 2f), v + d, h + rise));
                    }

                    break;
            }

            if (Has(Details.Chimney) && Has(Details.Smoke) && style != RoofStyle.Sawtooth && style != RoofStyle.Barrel)
            {
                Smoke(c, Canvas.P(u + (w * 0.25f) + 0.65f, v + (d * 0.2f) + 0.65f, h + rise + 1.4f), 0.9f);
            }

            if (Has(Details.Solar) && style != RoofStyle.Flat)
            {
                // Panels on the lit slope.
                var a0 = u + (w * 0.2f);
                var a1 = u + (w * 0.8f);
                c.Quad(Hex("2f5f8a"), new Vector3(a0, v + (d * 0.8f), h + (rise * 0.4f)), new Vector3(a1, v + (d * 0.8f), h + (rise * 0.4f)), new Vector3(a1, v + (d * 0.62f), h + (rise * 0.76f)), new Vector3(a0, v + (d * 0.62f), h + (rise * 0.76f)));
            }
        }

        /// <summary>Round tower: a cylinder with bands of windows (or glass) and a flat top.</summary>
        private static void DrawRound(Canvas c, Model m, Palette p, float w, float d, float h)
        {
            var radius = Mathf.Min(w, d) / 2f;
            float cu = w / 2f, cv = d / 2f;
            var glass = m.Facade == Facade.Glass;
            c.Cylinder(cu, cv, radius, h, glass ? GlassColor : p.Walls, glass ? Scale(p.Zone, 0.55f) : p.Shade, p.Roof);

            if (m.Facade != Facade.None)
            {
                float rx = radius * 1.2247f, ry = radius * 0.7071f;
                var bottom = Canvas.P(cu, cv, 0);
                var floors = Mathf.Max(2, Mathf.RoundToInt(h / 3.3f));
                const int Steps = 8;
                for (var f = 0; f < floors; f++)
                {
                    var t0 = (f + (glass ? 0.95f : 0.3f)) * h / floors;
                    var t1 = (f + (glass ? 1.05f : 0.72f)) * h / floors;
                    if (t1 > h)
                    {
                        continue;
                    }

                    // Front arc of the band, left half (lit) and right half (shade).
                    for (var half = 0; half < 2; half++)
                    {
                        var lower = new List<Vector2>();
                        var upper = new List<Vector2>();
                        for (var i = 0; i <= Steps / 2; i++)
                        {
                            var a = Mathf.PI * (1f - ((float)((half * Steps / 2) + i) / Steps));
                            var offset = new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
                            lower.Add(bottom + offset + new Vector2(0, -t0));
                            upper.Add(bottom + offset + new Vector2(0, -t1));
                        }

                        upper.Reverse();
                        var colour = glass ? (half == 0 ? Scale(GlassColor, 0.8f) : p.Dark) : (half == 0 ? Window : p.Dark);
                        c.Polygon(colour, lower.Concat(upper).ToArray());
                    }
                }
            }

            var topCentre = Canvas.P(cu, cv, h);
            if ((m.Details & Details.RoofGarden) != 0)
            {
                c.Ellipse(topCentre, radius * 1.05f, radius * 0.6f, Hex("6fbf4a"));
                c.Ellipse(topCentre + new Vector2(-radius * 0.2f, -radius * 0.25f), radius * 0.35f, radius * 0.3f, Hex("2f8a3e"));
            }

            if (m.Roof == RoofStyle.Spire || (m.Details & Details.Antenna) != 0)
            {
                c.Box(cu - 0.3f, cv - 0.3f, h, 0.6f, 0.6f, h * 0.3f, Stone, p.Shade, null);
            }
        }

        private static void RoofExtras(Canvas c, Model m, Palette p, float u, float v, float w, float d, float top)
        {
            bool Has(Details detail) => (m.Details & detail) != 0;
            var e = Mathf.Min(w, d) * 0.18f;
            if (Has(Details.RoofGarden))
            {
                c.Quad(Hex("6fbf4a"), new Vector3(u + e, v + e, top + 0.1f), new Vector3(u + w - e, v + e, top + 0.1f), new Vector3(u + w - e, v + d - e, top + 0.1f), new Vector3(u + e, v + d - e, top + 0.1f));
                var r = Mathf.Min(w, d) * 0.12f;
                foreach (var (fu, fv) in new[] { (0.35f, 0.35f), (0.65f, 0.6f), (0.35f, 0.7f) })
                {
                    var at = Canvas.P(u + (w * fu), v + (d * fv), top + r);
                    c.Ellipse(at, r * 1.2f, r, Hex("2f8a3e"));
                    c.Ellipse(at + new Vector2(-r * 0.25f, -r * 0.25f), r * 0.9f, r * 0.75f, Hex("5cc463"));
                }
            }
            else if (Has(Details.Solar))
            {
                const int Rows = 3;
                for (var i = 0; i < Rows; i++)
                {
                    var v0 = v + e + ((d - (2 * e)) * i / Rows);
                    var v1 = v0 + ((d - (2 * e)) / Rows * 0.8f);
                    c.Quad(Hex("2f5f8a"), new Vector3(u + e, v0, top + 0.2f), new Vector3(u + w - e, v0, top + 0.2f), new Vector3(u + w - e, v1, top + 0.2f), new Vector3(u + e, v1, top + 0.2f));
                    c.Quad(Hex("8fb8d8"), new Vector3(u + e, v1 - 0.12f, top + 0.21f), new Vector3(u + w - e, v1 - 0.12f, top + 0.21f), new Vector3(u + w - e, v1, top + 0.21f), new Vector3(u + e, v1, top + 0.21f));
                }
            }

            if (Has(Details.Helipad))
            {
                var centre = Canvas.P(u + (w / 2f), v + (d / 2f), top + 0.15f);
                var r = Mathf.Min(w, d) * 0.32f;
                c.Ellipse(centre, r * 1.2247f, r * 0.7071f, Hex("4a4f52"), 20);
                c.Ellipse(centre, r * 1.05f, r * 0.6f, Hex("f2c230"), 20);
                c.Ellipse(centre, r * 0.95f, r * 0.53f, Hex("4a4f52"), 20);
                var s = r * 0.35f;
                c.Quad(Hex("ffffff"), new Vector3(u + (w / 2f) - s, v + (d / 2f) - (s * 0.15f), top + 0.2f), new Vector3(u + (w / 2f) + s, v + (d / 2f) - (s * 0.15f), top + 0.2f), new Vector3(u + (w / 2f) + s, v + (d / 2f) + (s * 0.15f), top + 0.2f), new Vector3(u + (w / 2f) - s, v + (d / 2f) + (s * 0.15f), top + 0.2f));
            }

            if (Has(Details.RoofPool))
            {
                c.Quad(Hex("f2ede0"), new Vector3(u + (w * 0.15f), v + (d * 0.5f), top + 0.15f), new Vector3(u + (w * 0.6f), v + (d * 0.5f), top + 0.15f), new Vector3(u + (w * 0.6f), v + (d * 0.88f), top + 0.15f), new Vector3(u + (w * 0.15f), v + (d * 0.88f), top + 0.15f));
                c.Quad(Hex("4fc3f0"), new Vector3(u + (w * 0.19f), v + (d * 0.55f), top + 0.2f), new Vector3(u + (w * 0.56f), v + (d * 0.55f), top + 0.2f), new Vector3(u + (w * 0.56f), v + (d * 0.83f), top + 0.2f), new Vector3(u + (w * 0.19f), v + (d * 0.83f), top + 0.2f));
            }

            if (Has(Details.WaterTower))
            {
                // Wooden water tank on legs, like the rooftops of North American blocks.
                var r = Mathf.Max(1.2f, Mathf.Min(w, d) * 0.14f);
                float cu = u + (w * 0.3f), cv = v + (d * 0.3f);
                var legs = r * 1.2f;
                c.Box(cu - (r * 0.7f), cv, top, 0.3f, 0.3f, legs, Hex("6b4a35"), Hex("4a3324"), null);
                c.Box(cu + (r * 0.5f), cv, top, 0.3f, 0.3f, legs, Hex("6b4a35"), Hex("4a3324"), null);
                var wood = Hex("a0703f");
                c.Cylinder(cu, cv, r, 0.01f, wood, Scale(wood, 0.7f), wood);
                var baseCentre = Canvas.P(cu, cv, top + legs);
                float rx = r * 1.2247f, ry = r * 0.7071f;
                var tankHeight = r * 1.6f;
                c.Polygon(Scale(wood, 0.7f), baseCentre + new Vector2(-rx, 0), baseCentre + new Vector2(-rx, -tankHeight), baseCentre + new Vector2(rx, -tankHeight), baseCentre + new Vector2(rx, 0), baseCentre + new Vector2(0, ry));
                c.Polygon(wood, baseCentre + new Vector2(-rx, 0), baseCentre + new Vector2(-rx, -tankHeight), baseCentre + new Vector2(0, -tankHeight + ry), baseCentre + new Vector2(0, ry));
                c.Polygon(Hex("5c4030"), baseCentre + new Vector2(-rx * 1.05f, -tankHeight), baseCentre + new Vector2(0, -tankHeight - (r * 1.1f)), baseCentre + new Vector2(rx * 1.05f, -tankHeight), baseCentre + new Vector2(0, -tankHeight + ry));
            }

            if (Has(Details.Billboard))
            {
                // Billboard facing the front, on two posts.
                var bw = Mathf.Min(w * 0.7f, 12f);
                var bh = Mathf.Max(2f, bw * 0.35f);
                var u0 = u + ((w - bw) / 2f);
                var vb = v + (d * 0.35f);
                c.Box(u0 + (bw * 0.2f), vb, top, 0.3f, 0.3f, 1.4f, Stone, p.Shade, null);
                c.Box(u0 + (bw * 0.75f), vb, top, 0.3f, 0.3f, 1.4f, Stone, p.Shade, null);
                c.Left(vb + 0.3f, u0, u0 + bw, top + 1.4f, top + 1.4f + bh, p.Accent);
                c.Left(vb + 0.31f, u0 + (bw * 0.1f), u0 + (bw * 0.6f), top + 1.4f + (bh * 0.55f), top + 1.4f + (bh * 0.75f), Hex("ffffff"));
                c.Left(vb + 0.31f, u0 + (bw * 0.1f), u0 + (bw * 0.45f), top + 1.4f + (bh * 0.25f), top + 1.4f + (bh * 0.4f), Scale(Hex("ffffff"), 0.9f));
            }

            if (Has(Details.Flag))
            {
                var pole = Mathf.Max(3f, Mathf.Min(w, d) * 0.6f);
                c.Box(u + (w * 0.85f), v + (d * 0.15f), top, 0.25f, 0.25f, pole, Stone, p.Shade, null);
                c.Left(v + (d * 0.15f) + 0.25f, u + (w * 0.85f) - 2.2f, u + (w * 0.85f), top + pole - 1.3f, top + pole, p.Accent);
            }

            if (Has(Details.RoofUnits))
            {
                c.Box(u + (w * 0.55f), v + (d * 0.2f), top, w * 0.22f, d * 0.22f, 1.4f, Stone, p.Shade, Scale(p.Roof, 0.95f));
            }

            if (Has(Details.Antenna))
            {
                var mast = Mathf.Max(3f, Mathf.Min(w, d) * 0.8f);
                c.Box(u + (w * 0.45f), v + (d * 0.45f), top, 0.35f, 0.35f, mast, Stone, p.Shade, null);
                c.Box(u + (w * 0.45f) - 0.6f, v + (d * 0.45f), top + (mast * 0.7f), 1.55f, 0.25f, 0.25f, Stone, p.Shade, null);
            }
        }

        /// <summary>Three grey puffs rising to the right, like the game's pollution icons.</summary>
        private static void Smoke(Canvas c, Vector2 from, float size)
        {
            c.Ellipse(from + new Vector2(0.3f * size, -1.2f * size), 1.0f * size, 0.9f * size, Hex("c9c9c9"));
            c.Ellipse(from + new Vector2(1.4f * size, -2.6f * size), 1.3f * size, 1.15f * size, Hex("b5b5b5"));
            c.Ellipse(from + new Vector2(2.9f * size, -4.2f * size), 1.6f * size, 1.4f * size, Hex("d8d8d8"));
        }

        // ------------------------------------------------------------- the tile

        /// <summary>Fits the building over the tile and writes the SVG.</summary>
        private static string ToSvg(Canvas c, float w, float d, Color zone, Model m)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pt in c.Shapes.SelectMany(s => s.points))
            {
                minX = Mathf.Min(minX, pt.x);
                maxX = Mathf.Max(maxX, pt.x);
                minY = Mathf.Min(minY, pt.y);
                maxY = Mathf.Max(maxY, pt.y);
            }

            // Something on the tile: the building makes room for it.
            var room = m.Ground switch
            {
                Ground.Road => new Vector2(-1.4f, -1.0f),
                Ground.Water => new Vector2(1.4f, -1.0f),
                Ground.Parking => new Vector2(-2.2f, 0.2f),
                Ground.Field => new Vector2(2.2f, 0.2f),
                Ground.Pool => new Vector2(2.0f, 0.1f),
                _ => Vector2.zero,
            };
            var fit = m.Ground == Ground.None || m.Ground == Ground.Plaza || m.Ground == Ground.Garden ? 1f : 0.86f;

            var ground = Canvas.P(w / 2f, d / 2f, 0);
            var share = m.Fill > 0.05f ? Mathf.Clamp(m.Fill, 0.5f, 1f) : 1f;
            var scale = fit * share * Mathf.Min(22f / Mathf.Max(maxX - minX, 0.01f), 24f / Mathf.Max(maxY - minY, 0.01f));
            var centreX = (minX + maxX) / 2f;
            Vector2 Map(Vector2 q) => new Vector2(16f + ((q.x - centreX) * scale), 22.5f + ((q.y - ground.y) * scale)) + room;

            float top = Map(new Vector2(0, minY)).y, bottom = Map(new Vector2(0, maxY)).y;
            var shift = top < 2.5f ? 2.5f - top : 0f;
            if (bottom + shift > 29.8f)
            {
                shift = 29.8f - bottom;
            }

            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\" viewBox=\"0 0 32 32\">");

            // The tile: left, top, right and bottom corners.
            Vector2 L = new(1f, 22.5f), T = new(16f, 14.8f), R = new(31f, 22.5f), B = new(16f, 30.2f);
            AppendPath(svg, ToHex(zone), new[] { L, T, R, B });
            DrawGround(svg, m.Ground, L, T, R, B);
            if ((m.Details & Details.Hedge) != 0)
            {
                // Hedge along the back edges of the tile.
                AppendPath(svg, "#2f8a3e", new[] { L, T, T + new Vector2(0, 1.4f), L + new Vector2(0, 1.4f) });
                AppendPath(svg, "#24702f", new[] { T, R, R + new Vector2(0, 1.4f), T + new Vector2(0, 1.4f) });
                AppendPath(svg, "#5cc463", new[] { L + new Vector2(0, -0.6f), T + new Vector2(0, -0.6f), T, L });
                AppendPath(svg, "#4aa852", new[] { T + new Vector2(0, -0.6f), R + new Vector2(0, -0.6f), R, T });
            }

            foreach (var (points, fill) in c.Shapes)
            {
                AppendPath(svg, ToHex(fill), points.Select(q => Map(q) + new Vector2(0, shift)).ToArray());
            }

            if (m.Trees != TreeStyle.None)
            {
                var spots = m.Ground switch
                {
                    Ground.Parking => new[] { new Vector2(5.2f, 24.2f), new Vector2(11.5f, 28.2f) },
                    Ground.Field or Ground.Pool => new[] { new Vector2(26.8f, 24.2f), new Vector2(20.5f, 28.2f) },
                    _ => new[] { new Vector2(5.2f, 24.2f), new Vector2(26.8f, 24.2f) },
                };
                foreach (var at in spots)
                {
                    DrawTree(svg, m.Trees, at);
                }
            }

            if ((m.Details & Details.Cars) != 0)
            {
                var spots = m.Ground switch
                {
                    Ground.Parking => new[] { new Vector2(25.2f, 24.6f), new Vector2(22.4f, 26.4f) },
                    Ground.Road => new[] { new Vector2(20.5f, 28.2f), new Vector2(27.0f, 25.0f) },
                    _ => new[] { new Vector2(22.5f, 27.2f) },
                };
                var colours = new[] { "#e8505b", "#2e86de", "#f2f2f2" };
                for (var i = 0; i < spots.Length; i++)
                {
                    DrawCar(svg, spots[i], colours[i % colours.Length]);
                }
            }

            if ((m.Details & Details.Lamps) != 0)
            {
                foreach (var at in new[] { new Vector2(9.5f, 27.3f), new Vector2(22.5f, 27.3f) })
                {
                    AppendPath(svg, "#4a4f52", new[] { at + new Vector2(-0.18f, -4.6f), at + new Vector2(0.18f, -4.6f), at + new Vector2(0.18f, 0), at + new Vector2(-0.18f, 0) });
                    AppendPath(svg, "#4a4f52", new[] { at + new Vector2(-0.18f, -4.6f), at + new Vector2(1.2f, -4.9f), at + new Vector2(1.2f, -4.6f), at + new Vector2(0.18f, -4.3f) });
                    svg.Append(string.Format(CultureInfo.InvariantCulture, "<circle cx=\"{0:0.##}\" cy=\"{1:0.##}\" r=\"0.55\" fill=\"#ffe58a\"/>", at.x + 1.2f, at.y - 4.45f));
                }
            }

            svg.Append("</svg>");
            return svg.ToString();
        }

        /// <summary>A small isometric car (svg units, centred on its ground point).</summary>
        private static void DrawCar(StringBuilder svg, Vector2 at, string colour)
        {
            const float K = 0.55f;
            Vector2 Q(float u, float v, float t) => at + (Canvas.P(u - 1.2f, v - 0.6f, t) * K);
            var body = Hex(colour.TrimStart('#'));
            AppendPath(svg, ToHex(body), new[] { Q(0, 1.2f, 0), Q(2.4f, 1.2f, 0), Q(2.4f, 1.2f, 0.7f), Q(0, 1.2f, 0.7f) });
            AppendPath(svg, ToHex(Scale(body, 0.7f)), new[] { Q(2.4f, 0, 0), Q(2.4f, 1.2f, 0), Q(2.4f, 1.2f, 0.7f), Q(2.4f, 0, 0.7f) });
            AppendPath(svg, ToHex(Scale(body, 0.9f)), new[] { Q(0, 0, 0.7f), Q(2.4f, 0, 0.7f), Q(2.4f, 1.2f, 0.7f), Q(0, 1.2f, 0.7f) });
            AppendPath(svg, "#3f5f6a", new[] { Q(0.6f, 0.15f, 0.7f), Q(1.8f, 0.15f, 0.7f), Q(1.7f, 0.25f, 1.2f), Q(0.7f, 0.25f, 1.2f) });
            AppendPath(svg, ToHex(Scale(body, 0.95f)), new[] { Q(0.7f, 0.25f, 1.2f), Q(1.7f, 0.25f, 1.2f), Q(1.7f, 0.95f, 1.2f), Q(0.7f, 0.95f, 1.2f) });
        }

        private static void DrawGround(StringBuilder svg, Ground ground, Vector2 L, Vector2 T, Vector2 R, Vector2 B)
        {
            // Point on the tile: a along B->R (front-right edge), b along B->L (front-left edge).
            Vector2 At(float a, float b) => B + ((R - B) * a) + ((L - B) * b);

            switch (ground)
            {
                case Ground.Road:
                    AppendPath(svg, "#5d6264", new[] { At(0, 0), At(1, 0), At(1, 0.2f), At(0, 0.2f) });
                    AppendPath(svg, "#b8b5aa", new[] { At(0, 0.2f), At(1, 0.2f), At(1, 0.23f), At(0, 0.23f) });
                    for (var i = 0; i < 4; i++)
                    {
                        var a0 = 0.08f + (i * 0.24f);
                        AppendPath(svg, "#f2c230", new[] { At(a0, 0.095f), At(a0 + 0.12f, 0.095f), At(a0 + 0.12f, 0.11f), At(a0, 0.11f) });
                    }

                    break;

                case Ground.Water:
                    AppendPath(svg, "#3fa9e0", new[] { At(0, 0), At(0.22f, 0), At(0.22f, 1), At(0, 1) });
                    AppendPath(svg, "#d2cdc4", new[] { At(0.22f, 0), At(0.25f, 0), At(0.25f, 1), At(0.22f, 1) });
                    foreach (var b in new[] { 0.25f, 0.55f, 0.8f })
                    {
                        AppendPath(svg, "#8fd3f5", new[] { At(0.08f, b), At(0.11f, b), At(0.11f, b + 0.12f), At(0.08f, b + 0.12f) });
                    }

                    break;

                case Ground.Parking:
                    AppendPath(svg, "#6b6f72", new[] { At(0.55f, 0), At(1, 0), At(1, 0.45f), At(0.55f, 0.45f) });
                    for (var i = 1; i < 4; i++)
                    {
                        var a = 0.55f + (i * 0.11f);
                        AppendPath(svg, "#e8e8e8", new[] { At(a, 0.05f), At(a + 0.012f, 0.05f), At(a + 0.012f, 0.4f), At(a, 0.4f) });
                    }

                    break;

                case Ground.Plaza:
                    AppendPath(svg, "#e4d8b3", new[] { At(0.12f, 0.12f), At(0.88f, 0.12f), At(0.88f, 0.88f), At(0.12f, 0.88f) });
                    AppendPath(svg, "#d2c6a1", new[] { At(0.5f, 0.12f), At(0.53f, 0.12f), At(0.53f, 0.88f), At(0.5f, 0.88f) });
                    AppendPath(svg, "#d2c6a1", new[] { At(0.12f, 0.5f), At(0.88f, 0.5f), At(0.88f, 0.53f), At(0.12f, 0.53f) });
                    break;

                case Ground.Field:
                {
                    var tones = new[] { "#8fcf4f", "#6fae3a", "#d9c25a", "#8fcf4f" };
                    for (var i = 0; i < tones.Length; i++)
                    {
                        var a0 = 0.55f + (i * 0.11f);
                        AppendPath(svg, tones[i], new[] { At(0.02f, a0), At(0.45f, a0), At(0.45f, a0 + 0.1f), At(0.02f, a0 + 0.1f) });
                    }

                    break;
                }

                case Ground.Garden:
                    // Lawn with a path to the front.
                    AppendPath(svg, "#7fcf5a", new[] { At(0.08f, 0.08f), At(0.92f, 0.08f), At(0.92f, 0.92f), At(0.08f, 0.92f) });
                    AppendPath(svg, "#e4d8b3", new[] { At(0.0f, 0.44f), At(0.5f, 0.44f), At(0.5f, 0.54f), At(0.0f, 0.54f) });
                    break;

                case Ground.Pool:
                    // Pool with a paved rim on the left corner.
                    AppendPath(svg, "#f2ede0", new[] { At(0.04f, 0.5f), At(0.42f, 0.5f), At(0.42f, 0.96f), At(0.04f, 0.96f) });
                    AppendPath(svg, "#4fc3f0", new[] { At(0.09f, 0.56f), At(0.37f, 0.56f), At(0.37f, 0.9f), At(0.09f, 0.9f) });
                    AppendPath(svg, "#9fe0fa", new[] { At(0.15f, 0.62f), At(0.3f, 0.62f), At(0.3f, 0.66f), At(0.15f, 0.66f) });
                    break;
            }
        }

        private static void DrawTree(StringBuilder svg, TreeStyle style, Vector2 at)
        {
            switch (style)
            {
                case TreeStyle.Conifer:
                    AppendPath(svg, "#6b4a35", new[] { at + new Vector2(-0.35f, -0.6f), at + new Vector2(0.35f, -0.6f), at + new Vector2(0.35f, 1.0f), at + new Vector2(-0.35f, 1.0f) });
                    AppendPath(svg, "#1f6b33", new[] { at + new Vector2(0, -6.2f), at + new Vector2(2.4f, -0.4f), at + new Vector2(-2.4f, -0.4f) });
                    AppendPath(svg, "#3d9a4a", new[] { at + new Vector2(0, -6.2f), at + new Vector2(-2.4f, -0.4f), at + new Vector2(-0.2f, -0.6f) });
                    break;

                case TreeStyle.Palm:
                    AppendPath(svg, "#9a7a52", new[] { at + new Vector2(-0.3f, 1.0f), at + new Vector2(0.3f, 1.0f), at + new Vector2(0.8f, -4.4f), at + new Vector2(0.3f, -4.4f) });
                    foreach (var (dx, dy, angle) in new[] { (-1.9f, -4.0f, 25f), (2.5f, -4.0f, -25f), (-1.5f, -5.0f, -20f), (2.1f, -5.0f, 20f), (0.55f, -5.4f, 0f) })
                    {
                        svg.Append(string.Format(
                            CultureInfo.InvariantCulture,
                            "<ellipse cx=\"{0:0.##}\" cy=\"{1:0.##}\" rx=\"2.0\" ry=\"0.55\" transform=\"rotate({2:0} {0:0.##} {1:0.##})\" fill=\"{3}\"/>",
                            at.x + dx,
                            at.y + dy,
                            angle,
                            dy < -4.5f ? "#5cc463" : "#2f8a3e"));
                    }

                    break;

                default:
                    AppendPath(svg, "#6b4a35", new[] { at + new Vector2(-0.4f, -0.6f), at + new Vector2(0.4f, -0.6f), at + new Vector2(0.4f, 1.2f), at + new Vector2(-0.4f, 1.2f) });
                    svg.Append(string.Format(CultureInfo.InvariantCulture, "<circle cx=\"{0:0.##}\" cy=\"{1:0.##}\" r=\"2.5\" fill=\"#2f8a3e\"/>", at.x, at.y - 2.2f));
                    svg.Append(string.Format(CultureInfo.InvariantCulture, "<circle cx=\"{0:0.##}\" cy=\"{1:0.##}\" r=\"1.95\" fill=\"#5cc463\"/>", at.x - 0.5f, at.y - 2.6f));
                    break;
            }
        }

        private static void AppendPath(StringBuilder svg, string fill, Vector2[] points)
        {
            svg.Append("<path d=\"");
            for (var i = 0; i < points.Length; i++)
            {
                svg.Append(i == 0 ? "M" : " L");
                svg.Append(points[i].x.ToString("0.##", CultureInfo.InvariantCulture));
                svg.Append(',');
                svg.Append(points[i].y.ToString("0.##", CultureInfo.InvariantCulture));
            }

            svg.Append(" Z\" fill=\"").Append(fill).Append("\"/>");
        }

        private static Color Scale(Color color, float factor) =>
            new(Mathf.Clamp01(color.r * factor), Mathf.Clamp01(color.g * factor), Mathf.Clamp01(color.b * factor), 1f);

        private static Color Hex(string hex) => ParseHex(hex) ?? Color.white;

        private static string ToHex(Color color) => "#" + HexOf(color);
    }
}
