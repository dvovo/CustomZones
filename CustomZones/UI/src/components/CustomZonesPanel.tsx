// Custom Zones editor: a side panel docked to the right edge of the screen
// (like the game's own info panels), built from the game's UI components.
//
//   Zones         list of custom zones + "New"
//   Zone          name, colour, type, what it can grow, duplicate / delete
//   Buildings     the building-style browser (FamilyBrowser)
import { useEffect, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Panel, Tooltip } from "cs2/ui";
import { FocusDisabled } from "cs2/input";
import {
  colorChoices$,
  coverage$,
  createZone,
  deleteZone,
  duplicateZone,
  GrowthStage,
  ICONS,
  onNewZoneRequested,
  panelVisible$,
  renameZone,
  resourceIcon,
  resourceNameKey,
  selectedZone$,
  setPanelVisible,
  setSelectedZone,
  setZoneColor,
  setZoneKeepDesign,
  setZoneLevelUp,
  ZONE_TYPES,
  ZoneEntry,
  zones$,
  zoneTypeOf,
} from "../bindings";
import { FamilyBrowser, FamilyPreview } from "./FamilyBrowser";
import { Checkbox } from "./vanilla";
import styles from "../styles.module.scss";

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

  // "+" in the zoning tool options opens the panel straight into the new-zone flow.
  useEffect(() => onNewZoneRequested(() => setCreating(true)), []);

  // Always have a zone selected when there is one.
  useEffect(() => {
    if (visible && zones.length > 0 && !zones.some((z) => z.id === selectedId)) {
      setSelectedZone(zones[0].id);
    }
  }, [visible, zones, selectedId]);

  if (!visible) return null;

  const selected = zones.find((z) => z.id === selectedId);
  const showChooser = creating || zones.length === 0;

  // Mouse-driven editor: kept out of the game's gamepad focus tree, whose nodes
  // only host a single child (else "Cannot register second child" errors).
  return (
    <FocusDisabled>
    <FamilyPreview />
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
        selectedId={creating ? "" : selectedId}
        onSelect={(id) => {
          setCreating(false);
          setSelectedZone(id);
        }}
        onNew={() => setCreating(true)}
        t={t}
      />

      {showChooser ? (
        <NewZoneChooser
          canCancel={zones.length > 0}
          onDone={() => setCreating(false)}
          t={t}
        />
      ) : selected ? (
        <>
          <ZoneSettings key={selected.id} zone={selected} t={t} />
          <FamilyBrowser zone={selected} />
        </>
      ) : null}
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
  onSelect: (id: string) => void;
  onNew: () => void;
  t: Translate;
}) => {
  const { zones, selectedId, onSelect, onNew, t } = props;

  return (
    <div className={styles.section}>
      <div className={styles.sectionHeader}>
        <span className={styles.sectionTitle}>{t("Zones", "Zones")}</span>
        <Button className={styles.headerButton} variant="flat" onSelect={onNew}>
          <img className={styles.buttonIcon} src={ICONS.plus} />
          <span>{t("NewZone", "New zone")}</span>
        </Button>
      </div>

      <div className={styles.zoneList}>
        {zones.map((zone) => (
          <Button
            key={zone.id}
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
        ))}
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// New zone: choose what it is based on
// ---------------------------------------------------------------------------

const NewZoneChooser = (props: { canCancel: boolean; onDone: () => void; t: Translate }) => {
  const { canCancel, onDone, t } = props;

  return (
    <div className={styles.section}>
      <div className={styles.sectionHeader}>
        <span className={styles.sectionTitle}>{t("NewZoneTitle", "New custom zone")}</span>
        {canCancel && (
          <Button className={styles.headerButton} variant="flat" onSelect={onDone}>
            {t("Cancel", "Cancel")}
          </Button>
        )}
      </div>
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
// Demand: buildings only grow while the city wants more of them
// ---------------------------------------------------------------------------

const DemandSummary = (props: { ready: number; total: number; blockedGoods: string[]; t: Translate }) => {
  const { ready, total, blockedGoods, t } = props;
  const { translate } = useLocalization();

  if (total === 0 || ready >= total) return null;

  const goods = blockedGoods.length > 0 && (
    <div className={styles.goodsList}>
      {blockedGoods.map((g) => (
        <Tooltip key={g} tooltip={translate(resourceNameKey(g), g) ?? g}>
          <img className={styles.goodsIcon} src={resourceIcon(g)} />
        </Tooltip>
      ))}
    </div>
  );

  if (ready === 0) {
    return (
      <div className={styles.demandWarning}>
        <img className={styles.inlineIcon} src={ICONS.warning} />
        <span>
          {blockedGoods.length > 0
            ? t("DemandNoneGoods", "Nothing can grow right now: the city has no demand for")
            : t("DemandNoneHousing", "Nothing can grow right now: the city has no demand for this kind of housing.")}
        </span>
        {goods}
      </div>
    );
  }

  return (
    <div className={styles.demandHint}>
      <span>{`${ready} / ${total} ${t("DemandSome", "can grow right now · no demand for")}`}</span>
      {goods}
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
              <Button className={styles.iconButton} variant="icon" onSelect={() => setConfirmDelete(true)}>
                <img src={ICONS.trash} />
              </Button>
            </Tooltip>
          )}
        </div>
      </div>

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
        </div>
      </div>

      <div className={styles.field}>
        <span className={styles.fieldLabel}>{t("BasedOn", "Behaves like")}</span>
        <div className={styles.fieldValue}>
          <img className={styles.inlineIcon} src={type.icon} />
          <span>{t(type.labelKey, type.label)}</span>
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
          <div className={zone.levelUp ? styles.checkboxField : styles.checkboxFieldDisabled}>
            <Checkbox
              checked={zone.keepDesign}
              disabled={!zone.levelUp}
              onChange={(v: boolean) => setZoneKeepDesign(zone.id, v)}
            />
            <Tooltip
              tooltip={t(
                "KeepDesignHint",
                "On: a building only becomes the next level of itself (same name). Off: it may become another building of this zone at the next level, like the game does with every building."
              )}
            >
              <span onClick={() => zone.levelUp && setZoneKeepDesign(zone.id, !zone.keepDesign)}>
                {t("KeepDesign", "Keep design")}
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
              <span>
                {`${coverage.growableBuildings} ${t("CanGrow", "buildings can grow")} · ${t("LotWidths", "lot widths")}`}
              </span>
              <div className={styles.widthChips}>
                {WIDTHS.map((w) => (
                  <span
                    key={w}
                    className={coverage.coveredWidths.includes(w) ? styles.widthChip : styles.widthChipMissing}
                  >
                    {w === 6 ? "6+" : w}
                  </span>
                ))}
              </div>
            </div>
            <GrowthStatus stage={coverage.growthStage} lots={coverage.growthLots} t={t} />
            <DemandSummary
              ready={coverage.demandReady}
              total={coverage.growableBuildings}
              blockedGoods={coverage.blockedGoods}
              t={t}
            />
            {missingWidths.length > 0 && (
              <div className={styles.hint}>
                {t(
                  "MissingWidths",
                  "Lots of the dimmed widths stay empty: add a style that has them if you want those lots built."
                )}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
};
