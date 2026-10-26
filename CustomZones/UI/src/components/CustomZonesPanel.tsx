// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Custom Zones editor, built from the game's UI components:
//
//   main panel (docked right, like the game's info panels)
//     Zones         list of custom zones + "New", pen = edit the zone
//     Buildings     the building-style browser (FamilyBrowser), filling the rest
//   zone window (left of the main panel, top-aligned, only while editing)
//     new zone type chooser, or name / colour / levels / growth / duplicate / delete
import { useEffect, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Dropdown, DropdownToggle, Panel, Scrollable, Tooltip } from "cs2/ui";
import { FocusDisabled } from "cs2/input";
import {
  colorChoices$,
  iconChoices$,
  iconLibrary$,
  removeLibraryIcon,
  saveIconToLibrary,
  setZoneIcon,
  generateZoneIcon,
  countPaintedCells,
  paintedCells$,
  coverage$,
  createZone,
  deleteZone,
  duplicateZone,
  exportFolder$,
  exportResult$,
  exportZone,
  importFiles$,
  importZone,
  refreshImportFiles,
  refreshZoneStats,
  zoneStats$,
  GrowthStage,
  ICONS,
  panelVisible$,
  renameZone,
  selectedZone$,
  setPanelVisible,
  setSelectedZone,
  setZoneColor,
  setZoneKeepDesign,
  setZoneGrowth,
  setZoneLevelUp,
  ZONE_TYPES,
  ZoneEntry,
  zones$,
  zoneTypeOf,
  ZoneCategory,
  DensityType,
} from "../bindings";
import { FamilyBrowser, FamilyPreview } from "./FamilyBrowser";
import { Checkbox, ColorField, DropdownItem, gameDropdownTheme, Slider, intTransformer } from "./vanilla";
import styles from "../styles.module.scss";
import saveIcon from "../images/Save.svg";
import exportIcon from "../images/Export.svg";
import importIcon from "../images/Import.svg";

type Translate = (key: string, fallback: string) => string;

const useT = (): Translate => {
  const { translate } = useLocalization();
  return (key, fallback) => translate(`CustomZones.${key}`, fallback) ?? fallback;
};

export const CustomZonesPanel = () => {
  const visible = useValue(panelVisible$);
  const zones = useValue(zones$);
  const selectedId = useValue(selectedZone$);
  const t = useT();
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState(false);

  // The zone window closes with the panel.
  useEffect(() => {
    if (!visible) {
      setCreating(false);
      setEditing(false);
    }
  }, [visible]);

  // Always have a zone selected when there is one. A zone just created is
  // selected before the list that contains it arrives: give the list a moment.
  useEffect(() => {
    if (!visible || zones.length === 0 || zones.some((z) => z.id === selectedId)) return;
    const timer = setTimeout(() => setSelectedZone(zones[0].id), 500);
    return () => clearTimeout(timer);
  }, [visible, zones, selectedId]);

  if (!visible) return null;

  const selected = zones.find((z) => z.id === selectedId);
  const showChooser = creating || zones.length === 0;
  const showEditor = !showChooser && editing && selected !== undefined;

  // Mouse-driven editor: kept out of the game's gamepad focus tree, whose nodes
  // only host a single child (else "Cannot register second child" errors).
  return (
    <FocusDisabled>
      <FamilyPreview />

      {(showChooser || showEditor) && (
        <Panel
          className={styles.zoneWindow}
          contentClassName={styles.zoneWindowContent}
          header={
            <div className={styles.title}>
              <img className={styles.titleIcon} src={showChooser ? ICONS.plus : ICONS.pen} />
              <span>{showChooser ? t("NewZoneTitle", "New custom zone") : selected!.name}</span>
            </div>
          }
          onClose={zones.length > 0 ? () => (showChooser ? setCreating(false) : setEditing(false)) : undefined}
        >
          <FocusDisabled>
            {showChooser ? (
              <NewZoneChooser
                onDone={() => {
                  setCreating(false);
                  setEditing(true);
                }}
                t={t}
              />
            ) : (
              // Tall content (the icon designer) scrolls inside the window.
              <Scrollable className={styles.zoneWindowScroll} vertical trackVisibility="scrollable">
                <ZoneSettings key={selected!.id} zone={selected!} t={t} />
              </Scrollable>
            )}
          </FocusDisabled>
        </Panel>
      )}

      <Panel
        className={styles.panel}
        contentClassName={styles.panelContent}
        header={
          <div className={styles.title}>
            <img className={styles.titleIcon} src={ICONS.tab} />
            <span>{t("Title", "Custom Zones")}</span>
          </div>
        }
        onClose={() => setPanelVisible(false)}
      >
        {/* The game's Panel opens its own focus scope, which hosts a single child:
            keep our many buttons out of it (else "Cannot register second child"). */}
        <FocusDisabled>
          <ZoneList
            zones={zones}
            selectedId={selectedId}
            editingId={showEditor ? selectedId : ""}
            onSelect={(id) => setSelectedZone(id)}
            onEdit={(id) => {
              setCreating(false);
              if (id === selectedId && showEditor) {
                setEditing(false);
              } else {
                setSelectedZone(id);
                setEditing(true);
              }
            }}
            onNew={() => setCreating(true)}
            t={t}
          />
          {selected && <FamilyBrowser zone={selected} />}
        </FocusDisabled>
      </Panel>
    </FocusDisabled>
  );
};

// ---------------------------------------------------------------------------
// Zone list
// ---------------------------------------------------------------------------

const ZoneList = (props: {
  zones: ZoneEntry[];
  selectedId: string;
  editingId: string;
  onSelect: (id: string) => void;
  onEdit: (id: string) => void;
  onNew: () => void;
  t: Translate;
}) => {
  const { zones, selectedId, editingId, onSelect, onEdit, onNew, t } = props;
  const [sharing, setSharing] = useState(false);

  return (
    <div className={styles.section}>
      <div className={styles.sectionHeader}>
        <span className={styles.sectionTitle}>{t("Zones", "Zones")}</span>
        <Tooltip tooltip={t("ShareZones", "Import zones shared by others, or export all of yours")}>
          <Button
            className={sharing ? styles.iconButtonActive : styles.iconButton}
            variant="icon"
            onSelect={() => {
              if (!sharing) refreshImportFiles();
              setSharing(!sharing);
            }}
          >
            <img src={importIcon} />
          </Button>
        </Tooltip>
        <Button className={styles.headerButton} variant="flat" onSelect={onNew}>
          <img className={styles.buttonIcon} src={ICONS.plus} />
          <span>{t("NewZone", "New zone")}</span>
        </Button>
      </div>

      {sharing && <SharePanel t={t} onDone={() => setSharing(false)} />}

      <Scrollable className={styles.zoneList} vertical trackVisibility="scrollable">
        <div className={styles.zoneListContent}>
        {zones.map((zone) => (
          <div key={zone.id} className={styles.zoneRowLine}>
          <Button
            variant="flat"
            className={zone.id === selectedId ? styles.zoneRowSelected : styles.zoneRow}
            onSelect={() => onSelect(zone.id)}
          >
            <img className={styles.zoneRowIcon} src={zone.icon || zoneTypeOf(zone).icon} />
            <div className={styles.zoneRowText}>
              <div className={styles.zoneRowName}>{zone.name}</div>
              {/* One string: the game's UI lays out every loose text node as its own block. */}
              <div className={styles.zoneRowMeta}>
                {`${t(zoneTypeOf(zone).labelKey, zoneTypeOf(zone).label)} · ${zone.buildingCount} ${zone.buildingCount === 1 ? t("BuildingOne", "building") : t("BuildingMany", "buildings")}`}
              </div>
            </div>
            <div className={styles.zoneRowSwatch} style={{ backgroundColor: zone.color }} />
          </Button>
          <Tooltip tooltip={t("EditZone", "Edit zone")}>
            <Button
              className={zone.id === editingId ? styles.zoneEditButtonActive : styles.zoneEditButton}
              variant="icon"
              onSelect={() => onEdit(zone.id)}
            >
              <img src={ICONS.pen} />
            </Button>
          </Tooltip>
          </div>
        ))}
        </div>
      </Scrollable>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Sharing zones: import an exported file, export every zone
// ---------------------------------------------------------------------------

const SharePanel = (props: { t: Translate; onDone: () => void }) => {
  const { t, onDone } = props;
  const files = useValue(importFiles$);
  const folder = useValue(exportFolder$);

  return (
    <div className={styles.sharePanel}>
      <div className={styles.shareHeader}>
        <span className={styles.shareTitle}>{t("ImportTitle", "Import a zone")}</span>
        <Tooltip tooltip={t("ExportAllHint", "Save every zone to one file you can share.")}>
          <Button className={styles.headerButton} variant="flat" onSelect={() => exportZone("")}>
            <img className={styles.buttonIcon} src={exportIcon} />
            <span>{t("ExportAll", "Export all")}</span>
          </Button>
        </Tooltip>
      </div>
      <ExportFeedback t={t} />
      {files.length === 0 ? (
        <div className={styles.hint}>{t("ImportNone", "No zone files yet. Export a zone, or put .json files shared by others in this folder:")}</div>
      ) : (
        <Scrollable className={styles.shareList} vertical trackVisibility="scrollable">
          {files.map((file) => (
            <Tooltip key={file} tooltip={t("ImportHint", "Add the zones in this file as new zones. Buildings of assets you do not have are kept but never grow.")}>
              <div
                className={styles.shareFile}
                onClick={() => {
                  importZone(file);
                  onDone();
                }}
              >
                <img className={styles.inlineIcon} src={importIcon} />
                <span>{file.replace(/\.json$/i, "")}</span>
              </div>
            </Tooltip>
          ))}
        </Scrollable>
      )}
      <div className={styles.shareFolder}>{folder}</div>
    </div>
  );
};

/** "Exported to X.json" for a few seconds after an export. */
const ExportFeedback = (props: { t: Translate }) => {
  const { t } = props;
  const result = useValue(exportResult$);
  const [shown, setShown] = useState("");

  useEffect(() => {
    if (!result) return;
    setShown(result);
    const timer = setTimeout(() => setShown(""), 4000);
    return () => clearTimeout(timer);
  }, [result]);

  if (!shown) return null;
  const file = shown.split("|")[0];
  return (
    <div className={file ? styles.exportDone : styles.exportFailed}>
      {file ? `${t("Exported", "Exported to")} ${file}` : t("ExportFailed", "Could not export (see the mod's log).")}
    </div>
  );
};

// ---------------------------------------------------------------------------
// New zone: choose what it is based on
// ---------------------------------------------------------------------------

const NewZoneChooser = (props: { onDone: () => void; t: Translate }) => {
  const { onDone, t } = props;

  return (
    <div className={styles.section}>
      <div className={styles.hint}>
        {t(
          "NewZoneHint",
          "Choose the zone it behaves like: it uses that zone's demand, and you then pick which of its buildings may grow."
        )}
      </div>
      <div className={styles.typeGrid}>
        {ZONE_TYPES.map((type) => (
          <Button
            key={type.labelKey}
            variant="flat"
            className={styles.typeButton}
            onSelect={() => {
              createZone("", type.category, type.density);
              onDone();
            }}
          >
            <img className={styles.typeIcon} src={type.icon} />
            <span className={styles.typeLabel}>{t(type.labelKey, type.label)}</span>
          </Button>
        ))}
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Growth status: what the game's spawner found in its last pass over this zone
// ---------------------------------------------------------------------------

const GrowthStatus = (props: { stage: number; lots: number; t: Translate }) => {
  const { stage, lots, t } = props;

  let text: string;
  let ok = false;
  switch (stage) {
    case GrowthStage.Growing:
      ok = true;
      text = `${t("GrowthGrowing", "Growing: the city is choosing buildings for its free lots")} (${lots})`;
      break;
    case GrowthStage.NoLots:
      text = t("GrowthNoLots", "No free lots right now: paint this zone along a road (or every lot is already built).");
      break;
    case GrowthStage.NothingFits:
      // Usual once the zone is nearly full (leftover gaps): only a hint.
      return (
        <div className={styles.growthNeutral}>
          <span>{t("GrowthNothingFits", "Its buildings do not fit the free gaps left. Normal when the zone is nearly full; if it is empty, add buildings of other sizes or paint deeper zones.")}</span>
        </div>
      );
    case GrowthStage.NoDemand:
      text = t("GrowthNoDemand", "Its buildings fit, but the city has no demand for them right now.");
      break;
    case GrowthStage.LowScore:
      text = t("GrowthLowScore", "The city wants them, but not here yet: this spot scores too low for them (workers, education, services, land value, pollution or road access).");
      break;
    case GrowthStage.TypeHasNoDemand:
      text = t("GrowthTypeNoDemand", "The city has no demand for this type of zone right now (its demand bar is empty), so nothing of this type grows anywhere.");
      break;
    default:
      return null;
  }

  return (
    <div className={ok ? styles.growthOk : styles.growthProblem}>
      <img className={styles.inlineIcon} src={ok ? ICONS.check : ICONS.warning} />
      <span>{text}</span>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Colour: any colour through the game's own colour picker
// ---------------------------------------------------------------------------

const hexToColor = (hex: string) => {
  const value = parseInt((hex || "#ffffff").replace("#", ""), 16) || 0;
  return { r: ((value >> 16) & 255) / 255, g: ((value >> 8) & 255) / 255, b: (value & 255) / 255, a: 1 };
};

const colorToHex = (color: { r: number; g: number; b: number }) =>
  "#" +
  [color.r, color.g, color.b]
    .map((c) => Math.round(Math.max(0, Math.min(1, c)) * 255).toString(16).padStart(2, "0"))
    .join("");

const CustomColor = (props: { zone: ZoneEntry; custom: boolean; t: Translate }) => {
  const { zone, custom, t } = props;
  const [value, setValue] = useState(() => hexToColor(zone.color));

  useEffect(() => setValue(hexToColor(zone.color)), [zone.color]);

  // The picker reports every step of a drag: apply the colour once it settles.
  useEffect(() => {
    const hex = colorToHex(value);
    if (hex === (zone.color || "").toLowerCase()) return;
    const timer = setTimeout(() => setZoneColor(zone.id, hex), 250);
    return () => clearTimeout(timer);
  }, [value]);

  if (!ColorField) return null;

  return (
    <Tooltip tooltip={t("CustomColor", "Any colour")}>
      <div className={custom ? styles.customColorSelected : styles.customColor}>
        <ColorField value={value} onChange={setValue} colorWheel hexInput popupDirection="down" />
      </div>
    </Tooltip>
  );
};

// ---------------------------------------------------------------------------
// Icon: any of the game's zone icons, drawn in the zone's colour
// ---------------------------------------------------------------------------

// Game zone icons shown before "+N" (one row with the designer and default tiles).
const COLLAPSED_ICONS = 5;

const IconChooser = (props: { zone: ZoneEntry; t: Translate }) => {
  const { zone, t } = props;
  const icons = useValue(iconChoices$);
  const baseIcon = zoneTypeOf(zone).icon;
  const current = zone.iconSource || "";
  const generated = current.startsWith("gen:");
  const [designing, setDesigning] = useState(false);
  // The game's other zone icons: one short row, the rest behind "+N".
  const [showAllIcons, setShowAllIcons] = useState(false);
  const others = icons.filter((icon) => icon !== baseIcon);
  const shown = showAllIcons ? others : others.slice(0, COLLAPSED_ICONS);
  if (!showAllIcons && current && !current.startsWith("gen:") && others.includes(current) && !shown.includes(current)) {
    shown[shown.length - 1] = current;
  }

  const openDesigner = () => {
    if (designing) {
      setDesigning(false);
      return;
    }

    // The designer draws a building of its own: start from the zone's buildings,
    // or a typical building of its type when it has none yet.
    if (!generated) {
      if (zone.buildingCount > 0) generateZoneIcon(zone.id);
      else setZoneIcon(zone.id, writeIconModel(defaultIconModel(zone)));
    }

    setDesigning(true);
  };

  const pickGameIcon = (icon: string) => {
    setDesigning(false);
    setZoneIcon(zone.id, icon === baseIcon ? "" : icon);
  };

  return (
    <>
      <div className={styles.fieldTop}>
        <span className={styles.fieldLabelTop}>{t("IconField", "Icon")}</span>
        <div className={styles.iconChoices}>
          <Tooltip tooltip={t("IconDesign", "Design an icon of your own, in the style of the game's zone icons.")}>
            <div className={generated ? styles.iconGenerateSelected : styles.iconGenerate} onClick={openDesigner}>
              <img className={styles.iconGenerateImage} src={generated ? zone.icon : baseIcon} />
              <img className={styles.iconGenerateBadge} src={ICONS.pen} />
            </div>
          </Tooltip>
          <Tooltip tooltip={t("IconDefault", "The icon of the zone it behaves like")}>
            <div className={current === "" ? styles.iconChoiceSelected : styles.iconChoice} onClick={() => pickGameIcon(baseIcon)}>
              <img src={baseIcon} />
            </div>
          </Tooltip>
          {shown.map((icon) => (
            <div
              key={icon}
              className={icon === current ? styles.iconChoiceSelected : styles.iconChoice}
              onClick={() => pickGameIcon(icon)}
            >
              <img src={icon} />
            </div>
          ))}
          {others.length > COLLAPSED_ICONS && (
            <Tooltip tooltip={showAllIcons ? t("FewerIcons", "Show fewer icons") : t("MoreIcons", "Show all the game's zone icons")}>
              <div className={styles.iconChoiceMore} onClick={() => setShowAllIcons(!showAllIcons)}>
                {showAllIcons ? "−" : `+${others.length - COLLAPSED_ICONS}`}
              </div>
            </Tooltip>
          )}
        </div>
      </div>
      <SavedIcons zone={zone} t={t} onPick={() => setDesigning(false)} />
      {designing && generated && <IconDesigner zone={zone} t={t} />}
    </>
  );
};

// ---------------------------------------------------------------------------
// Icon library: designed icons kept apart from the zones
// ---------------------------------------------------------------------------

const SavedIcons = (props: { zone: ZoneEntry; t: Translate; onPick: () => void }) => {
  const { zone, t, onPick } = props;
  const library = useValue(iconLibrary$);
  const [confirm, setConfirm] = useState("");

  if (library.length === 0) return null;

  return (
    <div className={styles.fieldTop}>
      <Tooltip tooltip={t("SavedIconsHint", "Icons you saved from the designer. Click one to use it on this zone; it takes the zone colour.")}>
        <span className={styles.fieldLabelTop}>{t("SavedIcons", "Saved")}</span>
      </Tooltip>
      <div className={styles.iconChoices}>
        {library.map((icon) => (
          <Tooltip key={icon.id} tooltip={icon.name}>
            <div
              className={icon.value === zone.iconSource ? styles.savedIconSelected : styles.savedIcon}
              onClick={() => {
                onPick();
                setZoneIcon(zone.id, icon.value);
              }}
            >
              <img className={styles.savedIconImage} src={icon.preview} />
              <div
                className={confirm === icon.id ? styles.savedIconRemoveConfirm : styles.savedIconRemove}
                onClick={(e) => {
                  e.stopPropagation();
                  if (confirm === icon.id) {
                    removeLibraryIcon(icon.id);
                    setConfirm("");
                  } else {
                    setConfirm(icon.id);
                  }
                }}
              >
                <img src={ICONS.close} />
              </div>
            </div>
          </Tooltip>
        ))}
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Icon designer: a building of its own, drawn like the game's zone icons, from
// independent parts (same model and text format as BuildingZoneIcon in C#)
// ---------------------------------------------------------------------------

const F = {
  Door: 1,
  Chimney: 2,
  Smoke: 4,
  Balconies: 8,
  Awning: 16,
  Sign: 32,
  RoofUnits: 64,
  Solar: 128,
  RoofGarden: 256,
  Antenna: 512,
  Storefront: 1024,
  Garage: 2048,
  Smokestack: 4096,
  VariedRoofs: 8192,
  Helipad: 16384,
  Hedge: 32768,
  WaterTower: 65536,
  Billboard: 131072,
  Flag: 262144,
  Crane: 524288,
  Cars: 1048576,
  Lamps: 2097152,
  RoofPool: 4194304,
};

interface IconModel {
  seed: number;
  body: string;
  roof: string;
  fac: string;
  w: number;
  d: number;
  h: number;
  units: number;
  rc: string; // roof colour rrggbb
  wc: string; // wall colour rrggbb
  ac: string; // accent colour rrggbb ("" = sign orange)
  fl: number;
  gr: string;
  tr: string;
  pitch: number; // 0 = usual; 0.15 .. 1
  floors: number; // 0 = from the height
  winc: string; // window colour rrggbb ("" = the game's)
  gf: string; // Same | Shops | Arcade | Garages
  annex: string; // None | House | Garage | Block | Tower | Shed | Silo
  fill: number; // 0 = whole tile; 0.5 .. 1
}

type Parts = Omit<IconModel, "seed" | "w" | "d" | "h">;

const BASE_PARTS: Parts = {
  body: "Box", roof: "Flat", fac: "Windows", units: 3, rc: "CEC6AC", wc: "FFF6D6", ac: "", fl: 0, gr: "None", tr: "None",
  pitch: 0, floors: 0, winc: "", gf: "Same", annex: "None", fill: 0,
};

/** Presets: ready-made parts (as BuildingZoneIcon.FromPreset). The size is kept. */
const PRESETS: Record<string, Partial<Parts>> = {
  House: { roof: "Gable", rc: "BC8459", fl: F.Door | F.Chimney, tr: "Round" },
  ModernHouse: { body: "Stacked", fac: "Large", wc: "FFFFFF", rc: "8C8676", fl: F.Door | F.Garage, gr: "Pool", tr: "Round" },
  Villa: { body: "LShape", roof: "Hip", rc: "A4483A", fl: F.Door, gr: "Pool", tr: "Palm" },
  CubeHouse: { roof: "Shed", fac: "Large", wc: "D2CDC4", rc: "6E7378", fl: F.Door | F.Solar, gr: "Garden" },
  Townhouses: { body: "Row", roof: "Gable", rc: "BC8459", fl: F.Door | F.VariedRoofs },
  ModernTownhouses: { body: "Row", fac: "Large", wc: "E4D8B3", fl: F.Door | F.RoofGarden },
  Block: { fl: F.RoofUnits | F.Door },
  BalconyBlock: { wc: "E8C4B4", fl: F.Balconies | F.Door | F.RoofGarden },
  Tower: { fac: "Bands", fl: F.RoofUnits },
  TwinTowers: { body: "Twin", wc: "E8C4B4", fl: F.RoofUnits },
  PodiumTower: { body: "Podium", fac: "Bands", fl: F.Storefront | F.Awning | F.Door },
  SteppedTower: { body: "Stepped", fac: "Glass", fl: F.Antenna },
  GlassTower: { fac: "Glass", fl: F.RoofUnits | F.Door },
  MixedUse: { fl: F.Storefront | F.Sign | F.Awning | F.Door },
  Shop: { fac: "None", fl: F.Storefront | F.Sign | F.Awning | F.Door, gr: "Parking" },
  Station: { body: "Canopy", rc: "F2F2F2", fl: F.Sign, gr: "Parking" },
  Office: { fac: "Large", wc: "D2CDC4", fl: F.Door | F.Solar, gr: "Parking" },
  Factory: { roof: "Sawtooth", fac: "None", rc: "B9B2A2", fl: F.Door | F.Smokestack | F.Smoke },
  Warehouse: { roof: "Barrel", fac: "None", rc: "8C8676", fl: F.Door, gr: "Road" },
  Farm: { roof: "Gambrel", fac: "None", wc: "C0473A", rc: "6E7378", fl: F.Door, gr: "Field", tr: "Round" },
  Silos: { body: "Silo", wc: "E9E6DC", units: 2 },
  GasTanks: { body: "Spheres", wc: "CFD8DC", units: 3 },
  OldTown: { roof: "Mansard", wc: "F9F3DD", rc: "806F69", fl: F.Door | F.Chimney | F.Storefront },
  ModernVilla: { roof: "Butterfly", fac: "Wood", wc: "FFFFFF", rc: "6E7378", fl: F.Door | F.Hedge, gr: "Pool", tr: "Round" },
  RoundTower: { body: "Round", fac: "Bands", fl: F.RoofGarden },
  Skyscraper: { roof: "Spire", fac: "Vertical", wc: "D2CDC4", fl: F.Door, gr: "Plaza" },
  EcoTerraces: { body: "Terraced", fac: "Large", wc: "FFFFFF", fl: F.RoofGarden | F.Solar, tr: "Round" },
  CourtyardBlock: { body: "Courtyard", wc: "E4D8B3", fl: F.Storefront | F.RoofGarden },
  Headquarters: { body: "Podium", roof: "Pyramid", fac: "Glass", fl: F.Helipad | F.Storefront, gr: "Plaza", tr: "Round" },
};

const PRESET_SIZES: Record<string, [number, number, number]> = {
  House: [11, 9, 5], ModernHouse: [12, 10, 7], Villa: [14, 12, 5], CubeHouse: [10, 9, 6],
  Townhouses: [18, 9, 8], ModernTownhouses: [18, 9, 9], Block: [14, 12, 16], BalconyBlock: [14, 12, 16],
  Tower: [12, 12, 40], TwinTowers: [16, 10, 36], PodiumTower: [18, 16, 45], SteppedTower: [16, 16, 45],
  GlassTower: [14, 14, 42], MixedUse: [14, 12, 14], Shop: [14, 10, 6], Station: [16, 14, 5],
  Office: [18, 14, 10], Factory: [24, 16, 7], Warehouse: [22, 14, 7], Farm: [14, 10, 6],
  Silos: [16, 10, 12], GasTanks: [20, 10, 10], OldTown: [12, 10, 10],
  ModernVilla: [14, 10, 6], RoundTower: [14, 14, 40], Skyscraper: [14, 14, 60], EcoTerraces: [16, 16, 15],
  CourtyardBlock: [18, 16, 14], Headquarters: [20, 18, 40],
};

const parseIconModel = (value: string): IconModel | null => {
  if (!value.startsWith("gen:")) return null;
  const [seedText, body] = value.substring(4).split("|");
  if (!body || body.indexOf("body=") < 0) return null;
  const v: Record<string, string> = {};
  body.split(",").forEach((pair) => {
    const [k, x] = pair.split("=");
    if (k && x !== undefined) v[k.trim()] = x.trim();
  });
  return {
    seed: parseInt(seedText, 10) || 0,
    body: v.body || "Box",
    roof: v.roof || "Flat",
    fac: v.fac || "Windows",
    w: parseFloat(v.w) || 12,
    d: parseFloat(v.d) || 10,
    h: parseFloat(v.h) || 8,
    units: parseInt(v.units, 10) || 3,
    rc: v.rc || "CEC6AC",
    wc: v.wc || "FFF6D6",
    ac: v.ac || "",
    fl: parseInt(v.fl, 10) || 0,
    gr: v.gr || "None",
    tr: v.tr || "None",
    pitch: parseFloat(v.pitch) || 0,
    floors: parseInt(v.floors, 10) || 0,
    winc: v.winc || "",
    gf: v.gf || "Same",
    annex: v.annex || "None",
    fill: parseFloat(v.fill) || 0,
  };
};

const writeIconModel = (m: IconModel) =>
  `gen:${m.seed}|body=${m.body},roof=${m.roof},fac=${m.fac},w=${Math.round(m.w)},d=${Math.round(m.d)},h=${Math.round(m.h)},units=${m.units},rc=${m.rc.toUpperCase()},wc=${m.wc.toUpperCase()},ac=${m.ac.toUpperCase()},fl=${m.fl},gr=${m.gr},tr=${m.tr},pitch=${m.pitch},floors=${m.floors},winc=${m.winc.toUpperCase()},gf=${m.gf},annex=${m.annex},fill=${m.fill}`;

const presetModel = (name: string, seed = 0, size?: [number, number, number]): IconModel => {
  const [w, d, h] = size ?? PRESET_SIZES[name] ?? [12, 10, 8];
  return { seed, w, d, h, ...BASE_PARTS, ...PRESETS[name] };
};

const defaultIconModel = (zone: ZoneEntry): IconModel => {
  if (zone.category === ZoneCategory.Industrial) return presetModel("Factory");
  if (zone.category === ZoneCategory.Office) return presetModel("GlassTower");
  if (zone.category === ZoneCategory.Commercial) return presetModel("Shop");
  if (zone.category === ZoneCategory.Mixed) return presetModel("MixedUse");
  if (zone.density === DensityType.Low) return presetModel("House");
  if (zone.density === DensityType.Row) return presetModel("Townhouses");
  if (zone.density === DensityType.High) return presetModel("Tower");
  return presetModel("Block");
};

type Option = [string, string, string]; // value, locale key, English

const PRESET_OPTIONS: Option[] = [
  ["House", "PresetHouse", "House"], ["ModernHouse", "PresetModernHouse", "Modern house"], ["Villa", "PresetVilla", "Villa with pool"],
  ["CubeHouse", "PresetCubeHouse", "Cube house"], ["Townhouses", "PresetTownhouses", "Row houses"], ["ModernTownhouses", "PresetModernTownhouses", "Modern row houses"],
  ["Block", "PresetBlock", "Block of flats"], ["BalconyBlock", "PresetBalconyBlock", "Block with balconies"], ["Tower", "PresetTower", "Tower"],
  ["TwinTowers", "PresetTwinTowers", "Twin towers"], ["PodiumTower", "PresetPodiumTower", "Tower on a base"], ["SteppedTower", "PresetSteppedTower", "Stepped tower"],
  ["GlassTower", "PresetGlassTower", "Glass tower"], ["MixedUse", "PresetMixedUse", "Flats over shops"], ["Shop", "PresetShop", "Shop"],
  ["Station", "PresetStation", "Petrol station"], ["Office", "PresetOffice", "Office building"], ["Factory", "PresetFactory", "Factory"],
  ["Warehouse", "PresetWarehouse", "Warehouse"], ["Farm", "PresetFarm", "Farm"], ["Silos", "PresetSilos", "Silos"],
  ["GasTanks", "PresetGasTanks", "Gas tanks"], ["OldTown", "PresetOldTown", "Old town house"],
  ["ModernVilla", "PresetModernVilla", "Modern villa"], ["RoundTower", "PresetRoundTower", "Round tower"],
  ["Skyscraper", "PresetSkyscraper", "Skyscraper"], ["EcoTerraces", "PresetEcoTerraces", "Green terraces"],
  ["CourtyardBlock", "PresetCourtyardBlock", "Courtyard block"], ["Headquarters", "PresetHeadquarters", "Headquarters"],
];
const BODY_OPTIONS: Option[] = [
  ["Box", "BodyBox", "Single block"], ["Twin", "BodyTwin", "Two towers"], ["Podium", "BodyPodium", "Tower on a base"],
  ["Stepped", "BodyStepped", "Stepped"], ["Stacked", "BodyStacked", "Stacked (cantilever)"], ["LShape", "BodyLShape", "L-shaped"],
  ["Row", "BodyRow", "Row of houses"], ["Silo", "BodySilo", "Silos"], ["Spheres", "BodySpheres", "Spherical tanks"], ["Canopy", "BodyCanopy", "Canopy"],
  ["Round", "BodyRound", "Round tower"], ["Terraced", "BodyTerraced", "Terraced"], ["Courtyard", "BodyCourtyard", "Around a courtyard"],
];
const ROOF_OPTIONS: Option[] = [
  ["Flat", "RoofFlat", "Flat"], ["Gable", "RoofGable", "Gabled"], ["Hip", "RoofHip", "Hipped"], ["Shed", "RoofShed", "Single slope"],
  ["Mansard", "RoofMansard", "Mansard"], ["Gambrel", "RoofGambrel", "Barn"], ["Sawtooth", "RoofSawtooth", "Sawtooth"], ["Barrel", "RoofBarrel", "Barrel"],
  ["Butterfly", "RoofButterfly", "Butterfly"], ["Pyramid", "RoofPyramid", "Glass pyramid"], ["Spire", "RoofSpire", "Spire"],
];
const FACADE_OPTIONS: Option[] = [
  ["None", "FacadeNone", "Plain walls"], ["Windows", "FacadeWindows", "Windows"], ["Bands", "FacadeBands", "Window bands"],
  ["Large", "FacadeLarge", "Large windows"], ["Glass", "FacadeGlass", "Glass curtain"],
  ["Vertical", "FacadeVertical", "Vertical fins"], ["Wood", "FacadeWood", "Timber cladding"],
];
const GROUND_OPTIONS: Option[] = [
  ["None", "GroundNone", "Plain"], ["Road", "GroundRoad", "Road"], ["Water", "GroundWater", "Water"], ["Parking", "GroundParking", "Car park"],
  ["Plaza", "GroundPlaza", "Paving"], ["Field", "GroundField", "Field"], ["Garden", "GroundGarden", "Garden"], ["Pool", "GroundPool", "Pool"],
];
const TREE_OPTIONS: Option[] = [
  ["None", "TreesNone", "None"], ["Round", "TreesRound", "Round"], ["Conifer", "TreesConifer", "Conifers"], ["Palm", "TreesPalm", "Palms"],
];
const GROUND_FLOOR_OPTIONS: Option[] = [
  ["Same", "GroundFloorSame", "Like the floors above"], ["Shops", "GroundFloorShops", "Shop windows"],
  ["Arcade", "GroundFloorArcade", "Arcade"], ["Garages", "GroundFloorGarages", "Garage doors"],
];
const ANNEX_OPTIONS: Option[] = [
  ["None", "AnnexNone", "None"], ["House", "AnnexHouse", "Small house"], ["Garage", "AnnexGarage", "Garages"],
  ["Block", "AnnexBlock", "Low block"], ["Tower", "AnnexTower", "Taller tower"], ["Shed", "AnnexShed", "Shed"], ["Silo", "AnnexSilo", "Silo"],
];
const EXTRA_OPTIONS: [number, string, string][] = [
  [F.Door, "DetailDoor", "Door"], [F.Garage, "DetailGarage", "Garage"], [F.Storefront, "DetailStorefront", "Shop windows"],
  [F.Awning, "DetailAwning", "Awning"], [F.Sign, "DetailSign", "Sign"], [F.Balconies, "DetailBalconies", "Balconies"],
  [F.Chimney, "DetailChimney", "Chimney"], [F.Smokestack, "DetailSmokestack", "Smokestack"], [F.Smoke, "DetailSmoke", "Smoke"],
  [F.RoofUnits, "DetailRooftop", "Roof units"], [F.Solar, "DetailSolar", "Solar panels"], [F.RoofGarden, "DetailRoofGarden", "Roof garden"],
  [F.Antenna, "DetailAntenna", "Antenna"], [F.Helipad, "DetailHelipad", "Helipad"], [F.Hedge, "DetailHedge", "Hedge"],
  [F.VariedRoofs, "DetailVaried", "Varied roofs"], [F.WaterTower, "DetailWaterTower", "Water tank"],
  [F.Billboard, "DetailBillboard", "Billboard"], [F.Flag, "DetailFlag", "Flag"], [F.RoofPool, "DetailRoofPool", "Roof pool"],
  [F.Crane, "DetailCrane", "Crane"], [F.Cars, "DetailCars", "Cars"], [F.Lamps, "DetailLamps", "Street lamps"],
];
const WINDOW_COLORS = ["", "3F5F6A", "73D0FF", "FFE58A", "FFFFFF", "2E3A40"];

// Colours in the game's icon palette.
const ROOF_COLORS = ["BC8459", "946949", "A4483A", "6E7378", "CEC6AC", "8C8676"];
const WALL_COLORS = ["FFF6D6", "FFFFFF", "E4D8B3", "E8C4B4", "C0473A", "D2CDC4"];
const ACCENT_COLORS = ["", "F39C2B", "E8505B", "2E86DE", "1ABC9C", "FFFFFF"];

/** Compact dropdown of the game's style. */
const Pick = (props: { label: string; value: string; options: Option[]; t: Translate; onChange: (v: string) => void }) => {
  const { label, value, options, t, onChange } = props;
  const current = options.find((o) => o[0] === value) ?? options[0];
  return (
    <div className={styles.designerCell}>
      <span className={styles.designerCellLabel}>{label}</span>
      <Dropdown
        theme={gameDropdownTheme}
        content={options.map(([v, key, english]) => (
          <DropdownItem key={v} value={v} selected={v === value} onChange={() => onChange(v)}>
            {t(key, english)}
          </DropdownItem>
        ))}
      >
        <DropdownToggle className={styles.designerDropdown}>{t(current[1], current[2])}</DropdownToggle>
      </Dropdown>
    </div>
  );
};

/** Colour chips + the game's colour picker ("" chip = the default colour). */
const ColorRow = (props: { label: string; value: string; colors: string[]; defaultLabel?: string; onChange: (hex: string) => void }) => {
  const { label, value, colors, defaultLabel, onChange } = props;
  const upper = value.toUpperCase();
  return (
    <div className={styles.designerColorRow}>
      <span className={styles.designerCellLabel}>{label}</span>
      <div className={styles.swatches}>
        {colors.map((c) =>
          c === "" ? (
            <div key="default" className={upper === "" ? styles.originalChipSelected : styles.originalChip} onClick={() => onChange("")}>
              {defaultLabel}
            </div>
          ) : (
            <div
              key={c}
              className={c === upper ? styles.swatchSmallSelected : styles.swatchSmall}
              style={{ backgroundColor: `#${c}` }}
              onClick={() => onChange(c)}
            />
          )
        )}
        {ColorField && (
          <div className={upper !== "" && !colors.includes(upper) ? styles.customColorSelected : styles.customColor}>
            <ColorField
              value={hexToColor(`#${value || "F39C2B"}`)}
              onChange={(c: { r: number; g: number; b: number }) => onChange(colorToHex(c).substring(1).toUpperCase())}
              colorWheel
              hexInput
              popupDirection="down"
            />
          </div>
        )}
      </div>
    </div>
  );
};

const IconDesigner = (props: { zone: ZoneEntry; t: Translate }) => {
  const { zone, t } = props;
  const [model, setModel] = useState<IconModel | null>(parseIconModel(zone.iconSource || ""));
  const [preset, setPreset] = useState("");

  // Follow the zone when the icon changes elsewhere ("from its buildings").
  useEffect(() => setModel(parseIconModel(zone.iconSource || "")), [zone.iconSource]);

  // Sliders and the colour picker report every step: send once the value settles.
  useEffect(() => {
    if (!model) return;
    const value = writeIconModel(model);
    if (value === zone.iconSource) return;
    const timer = setTimeout(() => setZoneIcon(zone.id, value), 150);
    return () => clearTimeout(timer);
  }, [model]);

  if (!model) return null;

  const update = (change: Partial<IconModel>) => setModel({ ...model, ...change });
  const toggle = (flag: number) => update({ fl: model.fl ^ flag });
  const withUnits = model.body === "Row" || model.body === "Silo" || model.body === "Spheres";

  const sliders: { key: "w" | "d" | "h" | "units"; labelKey: string; label: string; min: number; max: number; unit: string }[] = [
    { key: "w", labelKey: "IconWidth", label: "Width", min: 2, max: 40, unit: " m" },
    { key: "d", labelKey: "IconDepth", label: "Depth", min: 2, max: 40, unit: " m" },
    { key: "h", labelKey: "IconHeight", label: "Height", min: 2, max: 80, unit: " m" },
  ];
  if (withUnits) {
    sliders.push({ key: "units", labelKey: "IconUnits", label: "How many", min: model.body === "Row" ? 2 : 1, max: model.body === "Row" ? 6 : 4, unit: "" });
  }

  // Percent sliders: roof steepness and size on the tile (0 in the model = the usual).
  const percents: { key: "pitch" | "fill"; labelKey: string; label: string; min: number; usual: number }[] = [];
  if (model.roof !== "Flat" && model.roof !== "Spire") {
    percents.push({ key: "pitch", labelKey: "IconPitch", label: "Roof pitch", min: 15, usual: 50 });
  }
  percents.push({ key: "fill", labelKey: "IconFill", label: "Size on tile", min: 50, usual: 100 });

  return (
    <div className={styles.designer}>
      <div className={styles.designerTop}>
        <div className={styles.designerPreview}>
          <img src={zone.icon} />
        </div>
        <div className={styles.designerIntro}>
          <Pick
            label={t("IconPreset", "Start from")}
            value={preset}
            options={[["", "PresetChoose", "Choose a model..."], ...PRESET_OPTIONS]}
            t={t}
            onChange={(name) => {
              if (!name) return;
              setPreset(name);
              setModel(presetModel(name, model.seed));
            }}
          />
          <div className={styles.designerButtons}>
            <Button className={styles.headerButton} variant="flat" onSelect={() => zone.buildingCount > 0 && generateZoneIcon(zone.id)}>
              <img className={styles.buttonIcon} src={ICONS.generate} />
              <span>{t("IconFromBuildings", "From its buildings")}</span>
            </Button>
            <Tooltip tooltip={t("IconSaveHint", "Keep this icon in your library: it stays there after the zone is deleted and can be used for any zone.")}>
              <Button className={styles.headerButton} variant="flat" onSelect={() => saveIconToLibrary(zone.id)}>
                <img className={styles.buttonIcon} src={saveIcon} />
                <span>{t("IconSave", "Save")}</span>
              </Button>
            </Tooltip>
          </div>
        </div>
      </div>

      <div className={styles.designerGrid}>
        <Pick label={t("IconBody", "Shape")} value={model.body} options={BODY_OPTIONS} t={t} onChange={(body) => update({ body })} />
        <Pick label={t("IconRoofShape", "Roof")} value={model.roof} options={ROOF_OPTIONS} t={t} onChange={(roof) => update({ roof })} />
        <Pick label={t("IconWindows", "Facade")} value={model.fac} options={FACADE_OPTIONS} t={t} onChange={(fac) => update({ fac })} />
        <Pick label={t("IconGround", "Ground")} value={model.gr} options={GROUND_OPTIONS} t={t} onChange={(gr) => update({ gr })} />
        <Pick label={t("IconTrees", "Trees")} value={model.tr} options={TREE_OPTIONS} t={t} onChange={(tr) => update({ tr })} />
        <Pick label={t("IconGroundFloor", "Ground floor")} value={model.gf} options={GROUND_FLOOR_OPTIONS} t={t} onChange={(gf) => update({ gf })} />
        <Pick label={t("IconAnnex", "Second building")} value={model.annex} options={ANNEX_OPTIONS} t={t} onChange={(annex) => update({ annex })} />
      </div>

      {Slider &&
        sliders.map((s) => (
          <div key={s.key} className={styles.designerRow}>
            <span className={styles.designerLabel}>{t(s.labelKey, s.label)}</span>
            <Slider
              className={styles.designerSlider}
              value={model[s.key]}
              start={s.min}
              end={s.max}
              valueTransformer={intTransformer}
              onChange={(v: number) => update({ [s.key]: v } as Partial<IconModel>)}
            />
            <span className={styles.designerValue}>{`${Math.round(model[s.key])}${s.unit}`}</span>
          </div>
        ))}

      {Slider && (
        <div className={styles.designerRow}>
          <span className={styles.designerLabel}>{t("IconFloors", "Floors")}</span>
          <Slider
            className={styles.designerSlider}
            value={model.floors}
            start={0}
            end={40}
            valueTransformer={intTransformer}
            onChange={(v: number) => update({ floors: v })}
          />
          <span className={styles.designerValue}>{model.floors === 0 ? t("Auto", "auto") : `${model.floors}`}</span>
        </div>
      )}

      {Slider &&
        percents.map((s) => {
          const value = model[s.key] > 0 ? Math.round(model[s.key] * 100) : s.usual;
          return (
            <div key={s.key} className={styles.designerRow}>
              <span className={styles.designerLabel}>{t(s.labelKey, s.label)}</span>
              <Slider
                className={styles.designerSlider}
                value={value}
                start={s.min}
                end={100}
                valueTransformer={intTransformer}
                onChange={(v: number) => update({ [s.key]: v / 100 } as Partial<IconModel>)}
              />
              <span className={styles.designerValue}>{`${value}%`}</span>
            </div>
          );
        })}

      <ColorRow label={t("IconRoof", "Roof")} value={model.rc} colors={ROOF_COLORS} onChange={(rc) => update({ rc })} />
      <ColorRow label={t("IconWalls", "Walls")} value={model.wc} colors={WALL_COLORS} onChange={(wc) => update({ wc })} />
      <ColorRow
        label={t("IconAccent", "Signs")}
        value={model.ac}
        colors={ACCENT_COLORS}
        defaultLabel={t("AccentDefault", "Orange")}
        onChange={(ac) => update({ ac })}
      />
      <ColorRow
        label={t("IconWindowColor", "Windows")}
        value={model.winc}
        colors={WINDOW_COLORS}
        defaultLabel={t("WindowDefault", "Usual")}
        onChange={(winc) => update({ winc })}
      />

      <div className={styles.designerExtras}>
        {EXTRA_OPTIONS.map(([flag, key, english]) => (
          <div key={flag} className={styles.designerExtra}>
            <Checkbox checked={(model.fl & flag) !== 0} onChange={() => toggle(flag)} />
            <span onClick={() => toggle(flag)}>{t(key, english)}</span>
          </div>
        ))}
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Selected zone settings
// ---------------------------------------------------------------------------

const WIDTHS = [1, 2, 3, 4, 5, 6];

const ZoneSettings = (props: { zone: ZoneEntry; t: Translate }) => {
  const { zone, t } = props;
  const colors = useValue(colorChoices$);
  const coverage = useValue(coverage$);
  const [name, setName] = useState(zone.name);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const paintedCells = useValue(paintedCells$);

  useEffect(() => setName(zone.name), [zone.name]);

  const commitName = () => {
    const trimmed = name.trim();
    if (trimmed.length > 0 && trimmed !== zone.name) {
      renameZone(zone.id, trimmed);
    } else {
      setName(zone.name);
    }
  };

  const type = zoneTypeOf(zone);
  const missingWidths = WIDTHS.filter((w) => !coverage.coveredWidths.includes(w));

  return (
    <div className={styles.section}>
      <div className={styles.sectionHeader}>
        <span className={styles.sectionTitle}>{t("Zone", "Zone")}</span>
        <div className={styles.headerActions}>
          <Tooltip tooltip={t("ExportZone", "Export this zone to a file you can share (Exports folder of the mod).")}>
            <Button className={styles.iconButton} variant="icon" onSelect={() => exportZone(zone.id)}>
              <img src={exportIcon} />
            </Button>
          </Tooltip>
          <Tooltip tooltip={t("Duplicate", "Duplicate")}>
            <Button className={styles.iconButton} variant="icon" onSelect={() => duplicateZone(zone.id)}>
              <img src={ICONS.copy} />
            </Button>
          </Tooltip>
          {confirmDelete ? (
            <>
              <Button
                className={styles.dangerButton}
                variant="flat"
                onSelect={() => {
                  setConfirmDelete(false);
                  deleteZone(zone.id);
                }}
              >
                {t("ConfirmDelete", "Delete zone")}
              </Button>
              <Button className={styles.headerButton} variant="flat" onSelect={() => setConfirmDelete(false)}>
                {t("Cancel", "Cancel")}
              </Button>
            </>
          ) : (
            <Tooltip tooltip={t("Delete", "Delete")}>
              <Button
                className={styles.iconButton}
                variant="icon"
                onSelect={() => {
                  countPaintedCells(zone.id);
                  setConfirmDelete(true);
                }}
              >
                <img src={ICONS.trash} />
              </Button>
            </Tooltip>
          )}
        </div>
      </div>

      <ExportFeedback t={t} />

      {confirmDelete && (
        <div className={styles.deleteWarning}>
          <img className={styles.inlineIcon} src={ICONS.warning} />
          <span>
            {paintedCells > 0
              ? `${t("DeletePainted", "Painted cells that will be unzoned:")} ${paintedCells}. ${t("DeleteCondemn", "Like erasing zoning in the game, the buildings on them will be condemned.")}`
              : t("DeleteNotPainted", "This zone is not painted anywhere in this city.")}
          </span>
        </div>
      )}

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("Name", "Name")}</span>
        <input
          className={styles.textInput}
          value={name}
          maxLength={48}
          onChange={(e) => setName(e.target.value)}
          onBlur={commitName}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
            if (e.key === "Escape") setName(zone.name);
          }}
        />
      </div>

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("Color", "Colour")}</span>
        <div className={styles.swatches}>
          {colors.map((color) => (
            <div
              key={color}
              className={
                color.toLowerCase() === (zone.color || "").toLowerCase()
                  ? styles.swatchSelected
                  : styles.swatch
              }
              style={{ backgroundColor: color }}
              onClick={() => setZoneColor(zone.id, color)}
            />
          ))}
          <CustomColor zone={zone} custom={!colors.some((c) => c.toLowerCase() === (zone.color || "").toLowerCase())} t={t} />
        </div>
      </div>

      <IconChooser zone={zone} t={t} />

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("BasedOn", "Behaves like")}</span>
        <div className={styles.fieldValue}>
          <img className={styles.inlineIcon} src={type.icon} />
          <span>{t(type.labelKey, type.label)}</span>
        </div>
      </div>

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("Growth", "Growth")}</span>
        <div className={styles.growthOptions}>
          <Tooltip
            tooltip={t(
              "GrowthLikeGameHint",
              "New buildings are born at level 1 and level up one level at a time into a ticked building of the next level, like in the game."
            )}
          >
            <div
              className={zone.growth === 1 ? styles.filterToggle : styles.filterToggleActive}
              onClick={() => setZoneGrowth(zone.id, 0)}
            >
              {t("GrowthLikeGame", "Like the game")}
            </div>
          </Tooltip>
          <Tooltip
            tooltip={
              <div className={styles.helpTooltip}>
                {t("GrowthSkipHint", "Each building keeps its own design: it is born at its lowest ticked level and levels up into its next ticked level, jumping over the unticked ones (never into another design). A skipped level still takes the time it takes in the game.\nMind the economy: a building born above level 1 asks that level's rent from the start, and its jobs want more educated workers, so it may struggle in a young or poor city.")
                  .split("\n")
                  .map((line, i) => (
                    <div key={i} className={styles.helpLine}>
                      {line}
                    </div>
                  ))}
              </div>
            }
          >
            <div
              className={zone.growth === 1 ? styles.filterToggleActive : styles.filterToggle}
              onClick={() => setZoneGrowth(zone.id, 1)}
            >
              {t("GrowthSkip", "Skip levels")}
            </div>
          </Tooltip>
        </div>
      </div>

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("LevelsField", "Levels")}</span>
        <div className={styles.levelOptions}>
          <div className={styles.checkboxField}>
            <Checkbox checked={zone.levelUp} onChange={(v: boolean) => setZoneLevelUp(zone.id, v)} />
            <Tooltip
              tooltip={t(
                "LevelUpHint",
                "When off, buildings in this zone keep the level they were born with, while other zones keep levelling up."
              )}
            >
              <span onClick={() => setZoneLevelUp(zone.id, !zone.levelUp)}>{t("LevelUp", "Level up")}</span>
            </Tooltip>
          </div>
          <div className={zone.levelUp && zone.growth !== 1 ? styles.checkboxField : styles.checkboxFieldDisabled}>
            <Checkbox
              checked={zone.keepDesign || zone.growth === 1}
              disabled={!zone.levelUp || zone.growth === 1}
              onChange={(v: boolean) => setZoneKeepDesign(zone.id, v)}
            />
            <Tooltip
              tooltip={zone.growth === 1 ? t("KeepDesignSkipHint", "Zones that skip levels always keep each building's own design.") : t(
                "KeepDesignHint",
                "On: a building levels up into its own design when that level is ticked; otherwise into another ticked building of the next level. Off: into any ticked building of the next level, at random, like the game."
              )}
            >
              <span onClick={() => zone.levelUp && zone.growth !== 1 && setZoneKeepDesign(zone.id, !zone.keepDesign)}>
                {t("KeepDesign", "Prefer same design")}
              </span>
            </Tooltip>
          </div>
        </div>
      </div>

      <div className={styles.coverage}>
        {zone.buildingCount === 0 ? (
          <div className={styles.warning}>
            <img className={styles.inlineIcon} src={ICONS.warning} />
            <span>{t("NoBuildings", "Nothing can grow yet: choose at least one building below.")}</span>
          </div>
        ) : (
          <>
            <div className={styles.coverageLine}>
              <span>{`${coverage.growableBuildings} ${t("CanGrow", "buildings can grow")}`}</span>
            </div>
            {coverage.noBirth && (
              <div className={styles.warning}>
                <img className={styles.inlineIcon} src={ICONS.warning} />
                <span>
                  {t(
                    "WarnNoBirth",
                    "Nothing can be born: tick level 1 of some buildings, or choose \"Skip levels\" so they are born at their lowest ticked level."
                  )}
                </span>
              </div>
            )}
            {coverage.lockedBuildings > 0 && (
              <div className={styles.warning}>
                <img className={styles.inlineIcon} src={ICONS.warning} />
                <span>
                  {`${coverage.lockedBuildings} ${t(
                    "WarnLocked",
                    "of its buildings belong to a zone this city has not unlocked yet (milestones). Like in the game, they only grow once it is unlocked."
                  )}`}
                </span>
              </div>
            )}
            <GrowthStatus stage={coverage.growthStage} lots={coverage.growthLots} t={t} />
            {coverage.stops.length > 0 && (
              <Tooltip
                tooltip={t(
                  "WarnStopsHint",
                  "A building only levels up into a ticked building that fits its lot (of the next level, or with \"Skip levels\" its own next ticked level). Tick more levels or designs of these lot sizes to let them go higher."
                )}
              >
                <div className={styles.hint}>
                  {`${t("WarnStops", "Stop levelling up:")} ${coverage.stops
                    .map((s) => {
                      const [lot, level] = s.split("|");
                      return `${lot} ${t("AtLevel", "at level")} ${level}`;
                    })
                    .join(", ")}`}
                </div>
              </Tooltip>
            )}
            {missingWidths.length > 0 && (
              <Tooltip
                tooltip={t(
                  "MissingWidthsHint",
                  "The game splits a zone along a road into lots 1 to 6+ cells wide. A lot only gets a building when the zone has one of exactly that width; add buildings of those widths if you want them built."
                )}
              >
                <div className={styles.hint}>
                  {`${t("MissingWidthsText", "No building of this zone fits lots this many cells wide, so they stay empty:")} ${missingWidths
                    .map((w) => (w === 6 ? "6+" : `${w}`))
                    .join(", ")}`}
                </div>
              </Tooltip>
            )}
          </>
        )}
        <ZoneStats zone={zone} t={t} />
      </div>
    </div>
  );
};

// What the zone holds in this city, refreshed while the window is open.
const ZoneStats = (props: { zone: ZoneEntry; t: Translate }) => {
  const { zone, t } = props;
  const stats = useValue(zoneStats$);

  useEffect(() => {
    refreshZoneStats(zone.id);
    const timer = setInterval(() => refreshZoneStats(zone.id), 5000);
    return () => clearInterval(timer);
  }, [zone.id]);

  if (stats.length !== 6) return null;
  const [cells, buildings, homes, households, jobs, workers] = stats;
  if (cells === 0) {
    return <div className={styles.hint}>{t("StatsNotPainted", "Not painted anywhere in this city yet.")}</div>;
  }

  const items: string[] = [
    `${t("StatsCells", "Cells")} ${cells}`,
    `${t("StatsBuildings", "Buildings")} ${buildings}`,
  ];
  if (homes > 0) items.push(`${t("StatsHouseholds", "Households")} ${households}/${homes}`);
  if (jobs > 0) items.push(`${t("StatsWorkers", "Workers")} ${workers}/${jobs}`);

  return (
    <Tooltip tooltip={t("StatsHint", "In this city: painted cells, buildings standing on this zone, households in its homes and workers in its jobs.")}>
      <div className={styles.zoneStats}>{items.join(" · ")}</div>
    </Tooltip>
  );
};
