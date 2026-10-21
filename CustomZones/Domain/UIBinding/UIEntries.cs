// <copyright file="UIEntries.cs">
// CustomZones - Licensed under the MIT license.
// IJsonWritable binding DTO pattern follows FindIt's Domain/UIBinding classes.
// </copyright>

namespace CustomZones.Domain.UIBinding
{
    using Colossal.UI.Binding;

    public class BuildingUIEntry : IJsonWritable
    {
        public string Name;
        public string DisplayName;
        public string Thumbnail;
        public string FallbackThumbnail;
        public int Category;
        public int Density;
        public int Level;
        public int LotWidth;
        public int LotDepth;
        public bool Selected;

        /// <summary>Theme name (tooltip) + flag icon URL for the card badge.</summary>
        public string Theme;
        public string ThemeIcon;

        /// <summary>True if the building is from a subscribed mod (shows the PDX badge).</summary>
        public bool IsMod;

        /// <summary>Asset-pack names of this building (card badges look up icons via the Packs/PackIcons bindings).</summary>
        public string[] Packs = System.Array.Empty<string>();

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("name");
            writer.Write(Name);
            writer.PropertyName("displayName");
            writer.Write(DisplayName);
            writer.PropertyName("thumbnail");
            writer.Write(Thumbnail);
            writer.PropertyName("fallbackThumbnail");
            writer.Write(FallbackThumbnail);
            writer.PropertyName("category");
            writer.Write(Category);
            writer.PropertyName("density");
            writer.Write(Density);
            writer.PropertyName("level");
            writer.Write(Level);
            writer.PropertyName("lotWidth");
            writer.Write(LotWidth);
            writer.PropertyName("lotDepth");
            writer.Write(LotDepth);
            writer.PropertyName("selected");
            writer.Write(Selected);
            writer.PropertyName("theme");
            writer.Write(Theme);
            writer.PropertyName("themeIcon");
            writer.Write(ThemeIcon);
            writer.PropertyName("isMod");
            writer.Write(IsMod);
            writer.PropertyName("packs");
            writer.ArrayBegin(Packs?.Length ?? 0);
            if (Packs != null)
            {
                foreach (var p in Packs)
                {
                    writer.Write(p);
                }
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }
    }

    public class PaletteUIEntry : IJsonWritable
    {
        public string Id;
        public string Name;
        public int BuildingCount;
        public int Category;
        public int Density;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin(GetType().FullName);
            writer.PropertyName("id");
            writer.Write(Id);
            writer.PropertyName("name");
            writer.Write(Name);
            writer.PropertyName("buildingCount");
            writer.Write(BuildingCount);
            writer.PropertyName("category");
            writer.Write(Category);
            writer.PropertyName("density");
            writer.Write(Density);
            writer.TypeEnd();
        }
    }

}
