// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Typed bindings to PaletteUISystem (C#).
// useValue/bindValue/trigger pattern follows Platter's gameBindings.ts.
import { bindValue, trigger } from "cs2/api";
// The mod's icon: a 2x2 grid of the game's four zone tiles (also written by ZoneIcons.cs).
import modIcon from "./images/ModIcon.svg";
import placeBuildingIcon from "./images/PlaceBuilding.svg";

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
  /** Height in metres of each level, 0 where there is none (index 0 = level 1). */
  levelHeights: number[];
  /** Per level: 2 = its own building, 1 = none (looks like the level below), 0 = nothing. */
  levelKinds: number[];
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
  iconSource: string; // game zone icon it is drawn from ("" = its base zone's)
  familyCount: number;
  buildingCount: number;
  levelUp: boolean;
  keepDesign: boolean;
  /** Growth mode: 0 = like the game, 1 = skip levels. */
  growth: number;
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
  /** Buildings are ticked but none can be born. */
  noBirth: boolean;
  /** Lot sizes whose buildings stop levelling up below level 5: "WxD|level". */
  stops: string[];
  /** Buildings that may be born but whose zone the city has not unlocked yet. */
  lockedBuildings: number;
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
  noBirth: false,
  stops: [],
  lockedBuildings: 0,
});
export const colorChoices$ = bindValue<string[]>(MOD_ID, "ColorChoices", []);
/** Game zone icons a custom zone can be drawn with. */
export const iconChoices$ = bindValue<string[]>(MOD_ID, "IconChoices", []);

/** A designed icon kept in the icon library. */
export interface SavedIcon {
  id: string;
  name: string;
  value: string;
  preview: string;
}
export const iconLibrary$ = bindValue<SavedIcon[]>(MOD_ID, "IconLibrary", []);
/** Keeps the zone's designed icon in the library (it outlives the zone). */
export const saveIconToLibrary = (zoneId: string) => trigger(MOD_ID, "SaveIconToLibrary", zoneId);
export const removeLibraryIcon = (id: string) => trigger(MOD_ID, "RemoveLibraryIcon", id);
export const bigInstalled$ = bindValue<boolean>(MOD_ID, "BigInstalled", false);

// Family browser (virtual scrolling: C# only sends the visible rows).
export const families$ = bindValue<FamilyEntry[]>(MOD_ID, "Families", []);
export const familyCount$ = bindValue<number>(MOD_ID, "FamilyCount", 0);
/** The building open in the large preview (0 or 1 entry), fresh even when its card is off screen. */
export const previewFamily$ = bindValue<FamilyEntry[]>(MOD_ID, "PreviewFamily", []);
export const setPreviewFamily = (key: string) => trigger(MOD_ID, "SetPreviewFamily", key);
export const scrollIndex$ = bindValue<number>(MOD_ID, "ScrollIndex", 0);
export const maxScrollIndex$ = bindValue<number>(MOD_ID, "MaxScrollIndex", 0);
export const gridColumns$ = bindValue<number>(MOD_ID, "GridColumns", 3);
export const gridRows$ = bindValue<number>(MOD_ID, "GridRows", 4);

// Filters (zone type and density come from the selected zone).
export const filterLotWidth$ = bindValue<number>(MOD_ID, "FilterLotWidth", 0);
export const filterLotDepth$ = bindValue<number>(MOD_ID, "FilterLotDepth", 0);
/** 0 = any; 1..5 = height bands (see HEIGHT_BANDS). */
export const filterHeight$ = bindValue<number>(MOD_ID, "FilterHeight", 0);
export const filterLevel$ = bindValue<number>(MOD_ID, "FilterLevel", 0);
export const filterPack$ = bindValue<string>(MOD_ID, "FilterPack", "");
export const filterSearch$ = bindValue<string>(MOD_ID, "FilterSearch", "");
export const filterSelectedOnly$ = bindValue<boolean>(MOD_ID, "FilterSelectedOnly", false);
export const filterGrowable$ = bindValue<boolean>(MOD_ID, "FilterGrowable", false);
/** Building type shown in the browser: "" = the zone's own type, "all", or "category:density". */
export const filterType$ = bindValue<string>(MOD_ID, "FilterType", "");
/** Painted cells of a zone, after countPaintedCells (-1 = not counted). */
export const paintedCells$ = bindValue<number>(MOD_ID, "PaintedCells", -1);
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
export const setZoneIcon = (id: string, icon: string) => trigger(MOD_ID, "SetPaletteIcon", id, icon);
/** Draws the zone's icon from one of its buildings (again = the next building). */
export const generateZoneIcon = (id: string) => trigger(MOD_ID, "GeneratePaletteIcon", id);
export const duplicateZone = (id: string) => trigger(MOD_ID, "DuplicatePalette", id);
export const deleteZone = (id: string) => trigger(MOD_ID, "DeletePalette", id);

// Sharing zones: files in ModsData/CustomZones/Exports.
export const importFiles$ = bindValue<string[]>(MOD_ID, "ImportFiles", []);
export const exportFolder$ = bindValue<string>(MOD_ID, "ExportFolder", "");
/** "file name|n" after each export ("" before the first one; empty file name = failed). */
export const exportResult$ = bindValue<string>(MOD_ID, "ExportResult", "");
/** Exports one zone, or every zone with "". */
export const exportZone = (id: string) => trigger(MOD_ID, "ExportZone", id);
export const refreshImportFiles = () => trigger(MOD_ID, "RefreshImportFiles");
export const importZone = (file: string) => trigger(MOD_ID, "ImportZone", file);

/** [painted cells, buildings, homes, households, jobs, workers] of the zone asked for last. */
export const zoneStats$ = bindValue<number[]>(MOD_ID, "ZoneStats", []);
export const refreshZoneStats = (id: string) => trigger(MOD_ID, "RefreshZoneStats", id);

/** Adds / removes one building (a card at one level) from the selected zone. */
export const toggleFamilyLevel = (key: string, level: number) =>
  trigger(MOD_ID, "ToggleFamilyLevel", key, level);
export const setAllVisible = (allowed: boolean) => trigger(MOD_ID, "SetAllVisible", allowed);
export const placeFamily = (key: string, level: number) => trigger(MOD_ID, "PlaceFamily", key, level);
/** Goes up by one when the player leaves "place it in the city" with Escape: the preview reopens. */
export const returnFromPlacing$ = bindValue<number>(MOD_ID, "ReturnFromPlacing", 0);
export const setZoneGrowth = (id: string, value: number) => trigger(MOD_ID, "SetPaletteGrowth", id, value);
/** Adds (true) or removes (false) every level of a building in the selected zone. */
export const setFamilyAllLevels = (key: string, allowed: boolean) => trigger(MOD_ID, "SetFamilyAllLevels", key, allowed);
/** Card click: the whole design with every level (click again: remove it). */
export const toggleFamilyCard = (key: string, level: number) => trigger(MOD_ID, "ToggleFamilyCard", key, level);
export const setZoneLevelUp = (id: string, value: boolean) => trigger(MOD_ID, "SetPaletteLevelUp", id, value);
export const setZoneKeepDesign = (id: string, value: boolean) =>
  trigger(MOD_ID, "SetPaletteKeepDesign", id, value);

export const setScrollIndex = (row: number) => trigger(MOD_ID, "SetScrollIndex", row);
export const setGridColumns = (columns: number) => trigger(MOD_ID, "SetGridColumns", columns);
export const setGridRows = (rows: number) => trigger(MOD_ID, "SetGridRows", rows);

export const setFilterLotWidth = (v: number) => trigger(MOD_ID, "SetFilterLotWidth", v);
export const setFilterLotDepth = (v: number) => trigger(MOD_ID, "SetFilterLotDepth", v);
export const setFilterHeight = (v: number) => trigger(MOD_ID, "SetFilterHeight", v);

/** Height bands of the height filter (same as FamilyFilters.HeightBands in C#). */
export const HEIGHT_BANDS = ["< 10 m", "10–20 m", "20–40 m", "40–80 m", "> 80 m"];
export const setFilterLevel = (v: number) => trigger(MOD_ID, "SetFilterLevel", v);
export const setFilterPack = (v: string) => trigger(MOD_ID, "SetFilterPack", v);
export const setFilterSearch = (v: string) => trigger(MOD_ID, "SetFilterSearch", v);
export const setFilterSelectedOnly = (v: boolean) => trigger(MOD_ID, "SetFilterSelectedOnly", v);
export const setFilterGrowable = (v: boolean) => trigger(MOD_ID, "SetFilterGrowable", v);
export const setFilterType = (v: string) => trigger(MOD_ID, "SetFilterType", v);
export const countPaintedCells = (id: string) => trigger(MOD_ID, "CountPaintedCells", id);
export const resetFilters = () => trigger(MOD_ID, "ResetFilters");

// Selected building's info panel: "Level up" button in its header.
export const selectedCanLevelUp$ = bindValue<boolean>(MOD_ID, "SelectedCanLevelUp", false);
export const levelUpSelected = () => trigger(MOD_ID, "LevelUpSelected");
/** Level selector of the selected building: [level - 1] = 2 current, 1 available, 0 none; empty = not a zoned building. */
export const selectedLevels$ = bindValue<number[]>(MOD_ID, "SelectedLevels", []);
export const setSelectedLevel = (level: number) => trigger(MOD_ID, "SetSelectedLevel", level);

/** The game's icon of a resource ("Lodging" -> its icon). */
export const resourceIcon = (goods: string) => `Media/Game/Resources/${goods}.svg`;

/** The game's localization key of a resource name. */
export const resourceNameKey = (goods: string) => `Resources.TITLE[${goods}]`;

// ---------- Icons: the game's own glyphs and icons ----------

export const ICONS = {
  tab: modIcon,
  plus: "Media/Glyphs/Plus.svg",
  pen: "Media/Glyphs/Pen.svg",
  copy: "Media/Glyphs/Copy.svg",
  trash: "Media/Glyphs/Trash.svg",
  close: "Media/Glyphs/Close.svg",
  clear: "Media/Glyphs/Clear.svg",
  check: "Media/Glyphs/Checkmark.svg",
  reset: "Media/Glyphs/ArrowCircular.svg",
  place: "Media/Glyphs/ViewInfo.svg",
  placeBuilding: placeBuildingIcon,
  help: "Media/Glyphs/Info.svg",
  zoom: "Media/Glyphs/ViewInfo.svg",
  generate: "Media/Glyphs/ArrowCircular.svg",
  warning: "Media/Glyphs/Warning.svg",
  mod: "Media/Glyphs/ParadoxMods.svg",
  baseGame: "Media/Glyphs/Logo_white.svg",
  arrowLeft: "Media/Glyphs/ThickStrokeArrowLeft.svg",
  arrowRight: "Media/Glyphs/ThickStrokeArrowRight.svg",
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
  { category: ZoneCategory.Mixed, density: DensityType.Any, icon: "Media/Game/Icons/ZoneResidentialMixed.svg", labelKey: "TypeMixed", label: "Mixed housing" },
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
