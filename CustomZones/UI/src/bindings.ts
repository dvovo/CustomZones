// Typed bindings to PaletteUISystem (C#).
// useValue/bindValue/trigger pattern follows Platter's gameBindings.ts.
import { bindValue, trigger } from "cs2/api";

export const MOD_ID = "CustomZones";

// ---------- Data shapes mirrored from C# UIBinding DTOs ----------

export interface BuildingEntry {
  name: string;
  displayName: string;
  thumbnail: string;
  fallbackThumbnail: string;
  category: number; // ZoneCategory
  density: number; // DensityType
  level: number;
  lotWidth: number;
  lotDepth: number;
  selected: boolean;
  // Descriptor badges (FindIt style): theme flag + asset-pack flags + mod flag.
  theme: string;
  themeIcon: string;
  isMod: boolean;
  packs: string[];
}

export interface PaletteEntry {
  id: string;
  name: string;
  buildingCount: number;
  category: number; // ZoneCategory
  density: number; // DensityType
}

export enum ZoneCategory {
  Any = 0,
  Residential = 1,
  Mixed = 2,
  Commercial = 3,
  Industrial = 4,
  Office = 5,
}

export enum DensityType {
  Any = 0,
  Low = 1,
  Row = 2,
  Medium = 3,
  High = 4,
  Signature = 5,
}

// ---------- Value bindings ----------

export const panelVisible$ = bindValue<boolean>(MOD_ID, "PanelVisible", false);
// True while the palette painter is the active zone tool (drives the
// contextual left-panel button).
export const painterToolActive$ = bindValue<boolean>(MOD_ID, "PainterToolActive", false);
export const palettes$ = bindValue<PaletteEntry[]>(MOD_ID, "Palettes", []);
export const buildings$ = bindValue<BuildingEntry[]>(MOD_ID, "Buildings", []);
export const selectedPalette$ = bindValue<string>(MOD_ID, "SelectedPalette", "");

// ---------- Filters (only the ones kept: width, depth, level, pack) ----------
// Category + density are NOT filters here: a palette is locked to its own zone
// type, so C# forces them; the UI never exposes them.

export const filterLotWidth$ = bindValue<number>(MOD_ID, "FilterLotWidth", 0);
export const filterLotDepth$ = bindValue<number>(MOD_ID, "FilterLotDepth", 0);
export const filterLevel$ = bindValue<number>(MOD_ID, "FilterLevel", 0);
export const filterSearch$ = bindValue<string>(MOD_ID, "FilterSearch", "");
export const filterSelectedOnly$ = bindValue<boolean>(MOD_ID, "FilterSelectedOnly", false);

// Asset-pack filter (region packs via AssetPackItem): the available pack names
// and a parallel icon list, plus the current selection.
export const packs$ = bindValue<string[]>(MOD_ID, "Packs", []);
export const packIcons$ = bindValue<string[]>(MOD_ID, "PackIcons", []);
export const filterPack$ = bindValue<string>(MOD_ID, "FilterPack", "");
export const setFilterPack = (v: string) => trigger(MOD_ID, "SetFilterPack", v);
// Sentinel (mirrors C# BrowserFilters.PackNone) = base game asset.
export const PACK_NONE = "__CZ_NONE__";
// Sentinel (mirrors C# BrowserFilters.PackMod) = from a subscribed mod (Paradox Mods).
export const PACK_MOD = "__CZ_MOD__";
// "" = all packs.
export const PACK_ALL = "";

// Virtual scrolling (FindIt-style): C# only sends the visible window.
export const scrollIndex$ = bindValue<number>(MOD_ID, "ScrollIndex", 0);
export const maxScrollIndex$ = bindValue<number>(MOD_ID, "MaxScrollIndex", 0);
export const buildingCount$ = bindValue<number>(MOD_ID, "BuildingCount", 0);

// Icon generation progress: 0-100 while rendering, -1 when there is nothing
// left to do (the UI hides the bar in that case).
export const iconProgress$ = bindValue<number>(MOD_ID, "IconProgress", -1);
export const iconPending$ = bindValue<number>(MOD_ID, "IconPending", 0);
export const gridColumns$ = bindValue<number>(MOD_ID, "GridColumns", 4);

export const GRID_ROWS = 5;

// ---------- Triggers ----------

export const setPanelVisible = (visible: boolean) =>
  trigger(MOD_ID, "SetPanelVisible", visible);

export const setSelectedPalette = (id: string) =>
  trigger(MOD_ID, "SetSelectedPalette", id);

export const createPalette = (
  name: string,
  category: ZoneCategory,
  density: DensityType
) => trigger(MOD_ID, "CreatePalette", name, category, density);

export const renamePalette = (id: string, name: string) =>
  trigger(MOD_ID, "RenamePalette", id, name);

export const duplicatePalette = (id: string) =>
  trigger(MOD_ID, "DuplicatePalette", id);

export const deletePalette = (id: string) =>
  trigger(MOD_ID, "DeletePalette", id);

export const toggleBuilding = (prefabName: string) =>
  trigger(MOD_ID, "ToggleBuilding", prefabName);

/** Hands the building to the game's placement tool so it can be dropped on the map. */
export const placeBuilding = (prefabName: string) =>
  trigger(MOD_ID, "PlaceBuilding", prefabName);

export const setAllVisible = (allowed: boolean) =>
  trigger(MOD_ID, "SetAllVisible", allowed);

export const resetFilters = () => trigger(MOD_ID, "ResetFilters");

/** Dev probe: measures how fast the screenshot-based icon pipeline could run. */
export const measureCapture = () => trigger(MOD_ID, "MeasureCapture");

export const setScrollIndex = (row: number) =>
  trigger(MOD_ID, "SetScrollIndex", row);

export const setGridColumns = (columns: number) =>
  trigger(MOD_ID, "SetGridColumns", columns);

export const setFilterLotWidth = (v: number) => trigger(MOD_ID, "SetFilterLotWidth", v);
export const setFilterLotDepth = (v: number) => trigger(MOD_ID, "SetFilterLotDepth", v);
export const setFilterLevel = (v: number) => trigger(MOD_ID, "SetFilterLevel", v);
export const setFilterSearch = (v: string) => trigger(MOD_ID, "SetFilterSearch", v);
export const setFilterSelectedOnly = (v: boolean) =>
  trigger(MOD_ID, "SetFilterSelectedOnly", v);

// ---------- Icons (Unified Icon Library, same host FindIt uses) ----------

/** Build a Unified Icon Library URL: UIL("Reset") -> coui://uil/Standard/Reset.svg */
export const UIL = (name: string) => `coui://uil/Standard/${name}.svg`;

/** Colored UIL variant: UIL_COLORED("PDXPlatypusHexagon") -> coui://uil/Colored/PDXPlatypusHexagon.svg */
export const UIL_COLORED = (name: string) => `coui://uil/Colored/${name}.svg`;

/**
 * AssetIconLibrary's own pre-rendered thumbnail for a building, if it ships one.
 * AIL covers ~7 992 buildings with real photo-like renders, so it always beats
 * anything we can produce off-screen. We ask for the file directly instead of
 * waiting for AIL to rewrite the prefab's icon (that happens at an unpredictable
 * moment during load); if AIL is not installed or lacks this building the image
 * simply fails to load and the card falls back to our own icon.
 */
export const AIL_ICON = (prefabName: string) =>
  `coui://ail/Colored/BuildingPrefab.${prefabName}.png`;

/** True when the URL is one of the icons our own generator produced. */
export const isGeneratedIcon = (url: string) => url.startsWith("coui://czicons/");

/** Paradox Mods badge/filter icon (colored) — the platypus-in-hexagon logo. */
export const MOD_ICON = UIL_COLORED("PDXPlatypusHexagon");
