// Typed bindings to PaletteUISystem (C#).
// useValue/bindValue/trigger pattern follows Platter's gameBindings.ts.
import { bindValue, trigger } from "cs2/api";

export const MOD_ID = "CustomZones";

// ---------- Data shapes mirrored from the C# UIBinding DTOs ----------

/** One building family card (every level and lot size of one building style). */
export interface FamilyEntry {
  key: string;
  name: string;
  thumbnail: string;
  fallbackThumbnail: string;
  lotSizes: string[];
  minLevel: number;
  maxLevel: number;
  memberCount: number;
  selected: boolean;
  theme: string;
  themeIcon: string;
  isMod: boolean;
  packs: string[];
  /** Large picture for the hover preview. */
  picture: string;
  /** Thumbnail of levels 1..5 ("" where the level does not exist). */
  levelThumbnails: string[];
  /** Large picture of levels 1..5 ("" where the level does not exist). */
  levelPictures: string[];
  /** Level the card shows (level filter, else lowest chosen, else lowest existing). */
  shownLevel: number;
  /** Levels of this building chosen for the zone. */
  selectedLevels: number[];
  /** Goods the shown building sells / makes / stores (game resource name, "" for housing). */
  goods: string;
  /** Whether the city currently wants more of the shown building (it only grows then). */
  hasDemand: boolean;
  /** What it does with the goods: "sells", "makes", "stores", "office" ("" for housing). */
  goodsRole: string;
}

/** One custom zone in the zone list. */
export interface ZoneEntry {
  id: string;
  name: string;
  category: number; // ZoneCategory
  density: number; // DensityType
  color: string; // "#rrggbb"
  icon: string; // toolbar icon
  familyCount: number;
  buildingCount: number;
  levelUp: boolean;
  keepDesign: boolean;
}

/** What the selected zone can grow. */
export interface Coverage {
  growableBuildings: number;
  coveredWidths: number[];
  maxLevel: number;
  /** Chosen buildings the city currently has demand for. */
  demandReady: number;
  /** Goods of the chosen buildings without demand right now. */
  blockedGoods: string[];
  /** Growth diagnosis from the last spawn pass (GrowthStage; -2 = not known yet). */
  growthStage: number;
  /** Free lots of the zone in that pass. */
  growthLots: number;
}

/** Mirrors C# PaletteZoneSpawnSystem.GrowthStage. */
export enum GrowthStage {
  Unknown = -2,
  NoLots = -1,
  NothingFits = 0,
  NoDemand = 1,
  LowScore = 2,
  Growing = 3,
  TypeHasNoDemand = 4,
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
// True while the vanilla zoning tool is active (shows the "Custom Zones" tool-options row).
export const zoneToolActive$ = bindValue<boolean>(MOD_ID, "ZoneToolActive", false);
// Id of the custom zone selected in the toolbar ("" = none).
export const activeZonePalette$ = bindValue<string>(MOD_ID, "ActiveZonePalette", "");

export const zones$ = bindValue<ZoneEntry[]>(MOD_ID, "Palettes", []);
export const selectedZone$ = bindValue<string>(MOD_ID, "SelectedPalette", "");
export const coverage$ = bindValue<Coverage>(MOD_ID, "Coverage", {
  growableBuildings: 0,
  coveredWidths: [],
  maxLevel: 0,
  demandReady: 0,
  blockedGoods: [],
  growthStage: -2,
  growthLots: 0,
});
export const colorChoices$ = bindValue<string[]>(MOD_ID, "ColorChoices", []);
export const bigInstalled$ = bindValue<boolean>(MOD_ID, "BigInstalled", false);

// Family browser (virtual scrolling: C# only sends the visible rows).
export const families$ = bindValue<FamilyEntry[]>(MOD_ID, "Families", []);
export const familyCount$ = bindValue<number>(MOD_ID, "FamilyCount", 0);
export const scrollIndex$ = bindValue<number>(MOD_ID, "ScrollIndex", 0);
export const maxScrollIndex$ = bindValue<number>(MOD_ID, "MaxScrollIndex", 0);
export const gridColumns$ = bindValue<number>(MOD_ID, "GridColumns", 3);
export const gridRows$ = bindValue<number>(MOD_ID, "GridRows", 4);

// Filters (zone type and density come from the selected zone).
export const filterLotWidth$ = bindValue<number>(MOD_ID, "FilterLotWidth", 0);
export const filterLevel$ = bindValue<number>(MOD_ID, "FilterLevel", 0);
export const filterPack$ = bindValue<string>(MOD_ID, "FilterPack", "");
export const filterSearch$ = bindValue<string>(MOD_ID, "FilterSearch", "");
export const filterSelectedOnly$ = bindValue<boolean>(MOD_ID, "FilterSelectedOnly", false);
export const filterGrowable$ = bindValue<boolean>(MOD_ID, "FilterGrowable", false);
export const packs$ = bindValue<string[]>(MOD_ID, "Packs", []);
export const packIcons$ = bindValue<string[]>(MOD_ID, "PackIcons", []);

// Sentinels mirrored from C# BrowserFilters.
export const PACK_ALL = "";
export const PACK_NONE = "__CZ_NONE__";
export const PACK_MOD = "__CZ_MOD__";

// ---------- Triggers ----------

export const setPanelVisible = (visible: boolean) => trigger(MOD_ID, "SetPanelVisible", visible);
export const setSelectedZone = (id: string) => trigger(MOD_ID, "SetSelectedPalette", id);

export const createZone = (name: string, category: ZoneCategory, density: DensityType) =>
  trigger(MOD_ID, "CreatePalette", name, category, density);
export const renameZone = (id: string, name: string) => trigger(MOD_ID, "RenamePalette", id, name);
export const setZoneColor = (id: string, color: string) =>
  trigger(MOD_ID, "SetPaletteColor", id, color);
export const duplicateZone = (id: string) => trigger(MOD_ID, "DuplicatePalette", id);
export const deleteZone = (id: string) => trigger(MOD_ID, "DeletePalette", id);

/** Adds / removes one building (a card at one level) from the selected zone. */
export const toggleFamilyLevel = (key: string, level: number) =>
  trigger(MOD_ID, "ToggleFamilyLevel", key, level);
export const setAllVisible = (allowed: boolean) => trigger(MOD_ID, "SetAllVisible", allowed);
export const placeFamily = (key: string, level: number) => trigger(MOD_ID, "PlaceFamily", key, level);
export const setZoneLevelUp = (id: string, value: boolean) => trigger(MOD_ID, "SetPaletteLevelUp", id, value);
export const setZoneKeepDesign = (id: string, value: boolean) =>
  trigger(MOD_ID, "SetPaletteKeepDesign", id, value);

export const setScrollIndex = (row: number) => trigger(MOD_ID, "SetScrollIndex", row);
export const setGridColumns = (columns: number) => trigger(MOD_ID, "SetGridColumns", columns);
export const setGridRows = (rows: number) => trigger(MOD_ID, "SetGridRows", rows);

export const setFilterLotWidth = (v: number) => trigger(MOD_ID, "SetFilterLotWidth", v);
export const setFilterLevel = (v: number) => trigger(MOD_ID, "SetFilterLevel", v);
export const setFilterPack = (v: string) => trigger(MOD_ID, "SetFilterPack", v);
export const setFilterSearch = (v: string) => trigger(MOD_ID, "SetFilterSearch", v);
export const setFilterSelectedOnly = (v: boolean) => trigger(MOD_ID, "SetFilterSelectedOnly", v);
export const setFilterGrowable = (v: boolean) => trigger(MOD_ID, "SetFilterGrowable", v);
export const resetFilters = () => trigger(MOD_ID, "ResetFilters");

// Selected building's info panel: "Level up" button in its header.
export const selectedCanLevelUp$ = bindValue<boolean>(MOD_ID, "SelectedCanLevelUp", false);
export const levelUpSelected = () => trigger(MOD_ID, "LevelUpSelected");

/** The game's icon of a resource ("Lodging" -> its icon). */
export const resourceIcon = (goods: string) => `Media/Game/Resources/${goods}.svg`;

/** The game's localization key of a resource name. */
export const resourceNameKey = (goods: string) => `Resources.TITLE[${goods}]`;

// ---------- Icons: the game's own glyphs and icons ----------

export const ICONS = {
  tab: "Media/Game/Icons/ColorCustomization.svg",
  plus: "Media/Glyphs/Plus.svg",
  pen: "Media/Glyphs/Pen.svg",
  copy: "Media/Glyphs/Copy.svg",
  trash: "Media/Glyphs/Trash.svg",
  close: "Media/Glyphs/Close.svg",
  clear: "Media/Glyphs/Clear.svg",
  check: "Media/Glyphs/Checkmark.svg",
  reset: "Media/Glyphs/ArrowCircular.svg",
  place: "Media/Glyphs/ViewInfo.svg",
  help: "Media/Glyphs/Info.svg",
  zoom: "Media/Glyphs/ViewInfo.svg",
  levelUp: "Media/Glyphs/ThickStrokeArrowUp.svg",
  warning: "Media/Glyphs/Warning.svg",
  mod: "Media/Glyphs/ParadoxMods.svg",
  baseGame: "Media/Glyphs/Logo_white.svg",
  arrowUp: "Media/Glyphs/ThickStrokeArrowUp.svg",
  arrowDown: "Media/Glyphs/ThickStrokeArrowDown.svg",
};

/** Icon of each zone type the player can base a custom zone on (game icons). */
export const ZONE_TYPES: {
  category: ZoneCategory;
  density: DensityType;
  icon: string;
  labelKey: string;
  label: string;
}[] = [
  { category: ZoneCategory.Residential, density: DensityType.Low, icon: "Media/Game/Icons/ZoneResidentialLow.svg", labelKey: "TypeResidentialLow", label: "Low density residential" },
  { category: ZoneCategory.Residential, density: DensityType.Row, icon: "Media/Game/Icons/ZoneResidentialMediumRow.svg", labelKey: "TypeResidentialRow", label: "Medium density row housing" },
  { category: ZoneCategory.Residential, density: DensityType.Medium, icon: "Media/Game/Icons/ZoneResidentialMedium.svg", labelKey: "TypeResidentialMedium", label: "Medium density residential" },
  { category: ZoneCategory.Residential, density: DensityType.High, icon: "Media/Game/Icons/ZoneResidentialHigh.svg", labelKey: "TypeResidentialHigh", label: "High density residential" },
  { category: ZoneCategory.Commercial, density: DensityType.Any, icon: "Media/Game/Icons/ZoneCommercial.svg", labelKey: "TypeCommercial", label: "Commercial" },
  { category: ZoneCategory.Industrial, density: DensityType.Any, icon: "Media/Game/Icons/ZoneIndustrial.svg", labelKey: "TypeIndustrial", label: "Industrial" },
  { category: ZoneCategory.Office, density: DensityType.Any, icon: "Media/Game/Icons/ZoneOffice.svg", labelKey: "TypeOffice", label: "Office" },
];

/** Zone type entry for a zone (falls back to the first one). */
export const zoneTypeOf = (zone: { category: number; density: number }) =>
  ZONE_TYPES.find((t) => t.category === zone.category && t.density === zone.density) ??
  ZONE_TYPES.find((t) => t.category === zone.category) ??
  ZONE_TYPES[0];

// ---------- UI-only events (between our own components) ----------

/** Building opened in the large preview (magnifier button of a card). */
export interface PreviewRequest {
  family: FamilyEntry;
  level: number;
}

type PreviewListener = (request: PreviewRequest | null) => void;
const previewListeners = new Set<PreviewListener>();

/** Opens (or, with null, closes) the large preview. */
export const openPreview = (request: PreviewRequest | null) =>
  previewListeners.forEach((listener) => listener(request));

/** Subscribes to preview requests; returns the unsubscribe function. */
export const onPreviewRequested = (listener: PreviewListener) => {
  previewListeners.add(listener);
  return () => {
    previewListeners.delete(listener);
  };
};

type Listener = () => void;
const newZoneListeners = new Set<Listener>();

/** Asks the panel to start the "new custom zone" flow. */
export const requestNewZone = () => newZoneListeners.forEach((listener) => listener());

/** Subscribes to new-zone requests; returns the unsubscribe function. */
export const onNewZoneRequested = (listener: Listener) => {
  newZoneListeners.add(listener);
  return () => {
    newZoneListeners.delete(listener);
  };
};
