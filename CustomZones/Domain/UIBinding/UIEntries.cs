// <copyright file="UIEntries.cs">
// Custom Zones - Licensed under the MIT license.
// IJsonWritable binding DTO pattern follows FindIt's Domain/UIBinding classes.
// </copyright>

namespace CustomZones.Domain.UIBinding
{
    using Colossal.UI.Binding;

    /// <summary>One building family card in the zone editor.</summary>
    public class FamilyUIEntry : IJsonWritable
    {
        public string Key;
        public string Name;
        public string Thumbnail;
        public string FallbackThumbnail;
        public string[] LotSizes = System.Array.Empty<string>();
        public int MinLevel;
        public int MaxLevel;
        public int MemberCount;
        public bool Selected;
        public string Theme;
        public string ThemeIcon;
        public bool IsMod;
        public string[] Packs = System.Array.Empty<string>();

        /// <summary>Large picture for the hover preview (BIG's full-size icon when available).</summary>
        public string Picture;

        /// <summary>Level the card shows (the level filter, else the lowest chosen / existing level).</summary>
        public int ShownLevel = 1;

        /// <summary>Levels of this building chosen for the zone.</summary>
        public int[] SelectedLevels = System.Array.Empty<int>();

        /// <summary>Large picture of each level 1..5 ("" where the level does not exist).</summary>
        public string[] LevelPictures = System.Array.Empty<string>();

        /// <summary>Thumbnail of each level of this building, "" where there is none (index 0 = level 1).</summary>
        public string[] LevelThumbnails = System.Array.Empty<string>();

        /// <summary>Height in metres of each level of this building, 0 where there is none (index 0 = level 1).</summary>
        public int[] LevelHeights = System.Array.Empty<int>();

        /// <summary>
        /// What each level is (index 0 = level 1): 2 = this design has a building
        /// at that level; 1 = it has none, so it looks like the level below (the
        /// game draws two levels with one model: L1 = L2, L3 = L4); 0 = nothing
        /// below it either.
        /// </summary>
        public int[] LevelKinds = System.Array.Empty<int>();

        /// <summary>Goods the shown building sells / makes / stores (game resource name, "" for housing).</summary>
        public string Goods = string.Empty;

        /// <summary>Whether the city currently wants more of the shown building (it only grows then).</summary>
        public bool HasDemand = true;

        /// <summary>What it does with the goods: "sells", "makes", "stores", "office" ("" for housing).</summary>
        public string GoodsRole = string.Empty;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("goods");
            writer.Write(Goods);
            writer.PropertyName("hasDemand");
            writer.Write(HasDemand);
            writer.PropertyName("goodsRole");
            writer.Write(GoodsRole);
            writer.PropertyName("picture");
            writer.Write(Picture);
            writer.PropertyName("levelThumbnails");
            WriteStrings(writer, LevelThumbnails);
            writer.PropertyName("levelPictures");
            WriteStrings(writer, LevelPictures);
            writer.PropertyName("levelKinds");
            writer.ArrayBegin(LevelKinds?.Length ?? 0);
            if (LevelKinds != null)
            {
                foreach (var kind in LevelKinds)
                {
                    writer.Write(kind);
                }
            }

            writer.ArrayEnd();
            writer.PropertyName("levelHeights");
            writer.ArrayBegin(LevelHeights?.Length ?? 0);
            if (LevelHeights != null)
            {
                foreach (var height in LevelHeights)
                {
                    writer.Write(height);
                }
            }

            writer.ArrayEnd();
            writer.PropertyName("shownLevel");
            writer.Write(ShownLevel);
            writer.PropertyName("selectedLevels");
            writer.ArrayBegin(SelectedLevels?.Length ?? 0);
            if (SelectedLevels != null)
            {
                foreach (var level in SelectedLevels)
                {
                    writer.Write(level);
                }
            }

            writer.ArrayEnd();
            writer.PropertyName("key");
            writer.Write(Key);
            writer.PropertyName("name");
            writer.Write(Name);
            writer.PropertyName("thumbnail");
            writer.Write(Thumbnail);
            writer.PropertyName("fallbackThumbnail");
            writer.Write(FallbackThumbnail);
            writer.PropertyName("lotSizes");
            WriteStrings(writer, LotSizes);
            writer.PropertyName("minLevel");
            writer.Write(MinLevel);
            writer.PropertyName("maxLevel");
            writer.Write(MaxLevel);
            writer.PropertyName("memberCount");
            writer.Write(MemberCount);
            writer.PropertyName("selected");
            writer.Write(Selected);
            writer.PropertyName("theme");
            writer.Write(Theme);
            writer.PropertyName("themeIcon");
            writer.Write(ThemeIcon);
            writer.PropertyName("isMod");
            writer.Write(IsMod);
            writer.PropertyName("packs");
            WriteStrings(writer, Packs);
            writer.TypeEnd();
        }

        internal static void WriteStrings(IJsonWriter writer, string[] values)
        {
            writer.ArrayBegin(values?.Length ?? 0);
            if (values != null)
            {
                foreach (var value in values)
                {
                    writer.Write(value);
                }
            }

            writer.ArrayEnd();
        }
    }

    /// <summary>One custom zone in the editor's zone list.</summary>
    public class PaletteUIEntry : IJsonWritable
    {
        public string Id;
        public string Name;
        public int Category;
        public int Density;
        public string Color;
        public string Icon;

        /// <summary>Game zone icon chosen for the zone (empty = its base zone's).</summary>
        public string IconSource;
        public int FamilyCount;
        public int BuildingCount;
        public bool LevelUp;
        public bool KeepDesign;

        /// <summary>0 = born at level 1 like the game, 1 = born at any ticked level.</summary>
        public int Growth;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("id");
            writer.Write(Id);
            writer.PropertyName("name");
            writer.Write(Name);
            writer.PropertyName("category");
            writer.Write(Category);
            writer.PropertyName("density");
            writer.Write(Density);
            writer.PropertyName("color");
            writer.Write(Color);
            writer.PropertyName("icon");
            writer.Write(Icon);
            writer.PropertyName("iconSource");
            writer.Write(IconSource ?? string.Empty);
            writer.PropertyName("familyCount");
            writer.Write(FamilyCount);
            writer.PropertyName("buildingCount");
            writer.Write(BuildingCount);
            writer.PropertyName("levelUp");
            writer.Write(LevelUp);
            writer.PropertyName("keepDesign");
            writer.Write(KeepDesign);
            writer.PropertyName("growth");
            writer.Write(Growth);
            writer.TypeEnd();
        }
    }

    /// <summary>What the selected zone can and cannot grow (shown under its settings).</summary>
    public class CoverageUIEntry : IJsonWritable
    {
        /// <summary>Level-1 prefabs the spawner can choose from.</summary>
        public int GrowableBuildings;

        /// <summary>Lot widths 1..6+ the zone has at least one building for.</summary>
        public int[] CoveredWidths = System.Array.Empty<int>();

        /// <summary>Highest building level any family reaches.</summary>
        public int MaxLevel;

        /// <summary>Chosen buildings the city currently has demand for.</summary>
        public int DemandReady;

        /// <summary>Goods of the chosen buildings without demand right now (game resource names).</summary>
        public string[] BlockedGoods = System.Array.Empty<string>();

        /// <summary>Growth diagnosis (PaletteZoneSpawnSystem.GrowthStage; -2 = not known yet).</summary>
        public int GrowthStage = -2;

        /// <summary>Free lots of the zone in the last spawn pass.</summary>
        public int GrowthLots;

        /// <summary>Painted cells of the zone in the city (shown before deleting it).</summary>
        public int PaintedCells;

        /// <summary>Buildings are ticked but none can be born (no level 1 while born like the game).</summary>
        public bool NoBirth;

        /// <summary>Lot sizes whose buildings stop levelling up below level 5: "WxD|level".</summary>
        public string[] Stops = System.Array.Empty<string>();

        /// <summary>Buildings that may be born but belong to a zone the city has not unlocked yet.</summary>
        public int LockedBuildings;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("growableBuildings");
            writer.Write(GrowableBuildings);
            writer.PropertyName("coveredWidths");
            writer.ArrayBegin(CoveredWidths?.Length ?? 0);
            if (CoveredWidths != null)
            {
                foreach (var width in CoveredWidths)
                {
                    writer.Write(width);
                }
            }

            writer.ArrayEnd();
            writer.PropertyName("maxLevel");
            writer.Write(MaxLevel);
            writer.PropertyName("demandReady");
            writer.Write(DemandReady);
            writer.PropertyName("blockedGoods");
            FamilyUIEntry.WriteStrings(writer, BlockedGoods);
            writer.PropertyName("growthStage");
            writer.Write(GrowthStage);
            writer.PropertyName("growthLots");
            writer.Write(GrowthLots);
            writer.PropertyName("paintedCells");
            writer.Write(PaintedCells);
            writer.PropertyName("noBirth");
            writer.Write(NoBirth);
            writer.PropertyName("stops");
            FamilyUIEntry.WriteStrings(writer, Stops);
            writer.PropertyName("lockedBuildings");
            writer.Write(LockedBuildings);
            writer.TypeEnd();
        }
    }

    /// <summary>An icon kept in the icon library.</summary>
    public class SavedIconUIEntry : IJsonWritable
    {
        public string Id;
        public string Name;

        /// <summary>The designer's value, applied to a zone as its icon.</summary>
        public string Value;

        /// <summary>coui:// address of its preview.</summary>
        public string Preview;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("id");
            writer.Write(Id ?? string.Empty);
            writer.PropertyName("name");
            writer.Write(Name ?? string.Empty);
            writer.PropertyName("value");
            writer.Write(Value ?? string.Empty);
            writer.PropertyName("preview");
            writer.Write(Preview ?? string.Empty);
            writer.TypeEnd();
        }
    }
}
