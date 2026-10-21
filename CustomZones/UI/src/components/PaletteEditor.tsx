// Building browser with FindIt-style VIRTUAL scrolling: C# only sends the
// visible window of the filtered list (max ~42 entries), the scrollbar is
// simulated from ScrollIndex/MaxScrollIndex, and the mouse wheel triggers
// SetScrollIndex. This keeps the panel instant even with thousands of
// spawnable buildings indexed.
//
// The filter panel mirrors FindIt's: a labelled filter block (search, the
// width/depth/level steppers and the asset-pack icon row) plus the palette
// selection actions. Category + density are NOT shown: a palette is locked to
// its own zone type, so those are fixed by C#.
import { useEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Tooltip } from "cs2/ui";
import classNames from "classnames";
import {
  buildings$,
  buildingCount$,
  iconProgress$,
  iconPending$,
  scrollIndex$,
  maxScrollIndex$,
  gridColumns$,
  setScrollIndex,
  setGridColumns,
  GRID_ROWS,
  packs$,
  packIcons$,
  filterPack$,
  setFilterPack,
  PACK_NONE,
  PACK_MOD,
  PACK_ALL,
  MOD_ICON,
  filterLotWidth$,
  filterLotDepth$,
  filterLevel$,
  filterSearch$,
  filterSelectedOnly$,
  setFilterLotWidth,
  setFilterLotDepth,
  setFilterLevel,
  setFilterSearch,
  setFilterSelectedOnly,
  toggleBuilding,
  placeBuilding,
  setAllVisible,
  resetFilters,
  measureCapture,
  UIL,
  BuildingEntry,
  ZoneCategory,
} from "../bindings";
import styles from "../styles.module.scss";

const LOT_SIZES = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]; // 0 = any, 10 = "10+"
const LEVELS = [0, 1, 2, 3, 4, 5];
const CARD_TRACK_WIDTH = 158;
const MIN_GRID_COLUMNS = 3;

export const PaletteEditor = () => {
  const buildings = useValue(buildings$);
  const buildingCount = useValue(buildingCount$);
  const iconProgress = useValue(iconProgress$);
  const iconPending = useValue(iconPending$);
  const scrollIndex = useValue(scrollIndex$);
  const maxScrollIndex = useValue(maxScrollIndex$);
  const boundGridColumns = useValue(gridColumns$);
  const packs = useValue(packs$);
  const packIcons = useValue(packIcons$);
  const pack = useValue(filterPack$);
  const lotWidth = useValue(filterLotWidth$);
  const lotDepth = useValue(filterLotDepth$);
  const level = useValue(filterLevel$);
  const search = useValue(filterSearch$);
  const selectedOnly = useValue(filterSelectedOnly$);
  const { translate } = useLocalization();

  const trackRef = useRef<HTMLDivElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);
  const [localGridColumns, setLocalGridColumns] = useState(MIN_GRID_COLUMNS + 1);

  const t = (key: string, fallback: string) =>
    translate(`CustomZones.${key}`, fallback) ?? fallback;

  useEffect(() => {
    let lastColumns = 0;

    const updateColumns = () => {
      const width = gridRef.current?.getBoundingClientRect().width ?? 0;
      if (width <= 0) return;

      const columns = Math.max(
        MIN_GRID_COLUMNS,
        Math.floor(width / CARD_TRACK_WIDTH)
      );

      if (columns !== lastColumns) {
        lastColumns = columns;
        setLocalGridColumns(columns);
        setGridColumns(columns);
      }
    };

    updateColumns();
    window.addEventListener("resize", updateColumns);

    const ResizeObserverCtor = window.ResizeObserver;
    const observer = ResizeObserverCtor ? new ResizeObserverCtor(updateColumns) : null;
    if (observer && gridRef.current) {
      observer.observe(gridRef.current);
    }

    return () => {
      window.removeEventListener("resize", updateColumns);
      observer?.disconnect();
    };
  }, []);

  // ----- Virtual scroll interactions -----
  const scrollTo = (row: number) =>
    setScrollIndex(Math.max(0, Math.min(row, maxScrollIndex)));

  const onWheel = (e: React.WheelEvent) =>
    scrollTo(scrollIndex + (e.deltaY > 0 ? 1 : -1));

  const onTrackMouseDown = (e: React.MouseEvent) => {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect || maxScrollIndex <= 0) return;

    const jump = (clientY: number) => {
      const ratio = (clientY - rect.top) / rect.height;
      scrollTo(Math.round(ratio * maxScrollIndex));
    };

    jump(e.clientY);

    const onMove = (ev: MouseEvent) => jump(ev.clientY);
    const onUp = () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const totalRows = maxScrollIndex + GRID_ROWS;
  const thumbHeightPct = Math.max(8, (GRID_ROWS / Math.max(1, totalRows)) * 100);
  const thumbTopPct =
    maxScrollIndex > 0
      ? (scrollIndex / maxScrollIndex) * (100 - thumbHeightPct)
      : 0;
  const visibleGridColumns = Math.max(
    MIN_GRID_COLUMNS,
    Number.isFinite(boundGridColumns) && boundGridColumns > 0
      ? boundGridColumns
      : localGridColumns
  );

  const sizeLabel = (v: number) =>
    v === 0 ? t("Any", "Any") : v === 10 ? "10+" : String(v);
  const levelLabel = (v: number) => (v === 0 ? t("Any", "Any") : String(v));

  // Map pack name -> icon for the per-card descriptor badges.
  const packIconMap: Record<string, string> = {};
  packs.forEach((p, i) => {
    packIconMap[p] = packIcons[i];
  });

  return (
    <div className={styles.editor}>
      {/* ---------------------------- Filter panel ---------------------------- */}
      <div className={styles.filterPanel}>
        <div className={styles.filterHeader}>
          <img className={styles.filterHeaderIcon} src={UIL("FunnelFilter")} />
          <span className={styles.filterHeaderTitle}>{t("Filters", "Filters")}</span>
          <Tooltip
            tooltip={t(
              "MeasureCapture",
              "Measure icon capture speed (results in the log)"
            )}
          >
            <Button variant="icon" className={styles.iconBtn} onSelect={measureCapture}>
              <img src={UIL("Camera")} />
            </Button>
          </Tooltip>
          <Tooltip tooltip={t("ResetFilters", "Reset filters")}>
            <Button variant="icon" className={styles.iconBtn} onSelect={resetFilters}>
              <img src={UIL("Reset")} />
            </Button>
          </Tooltip>
        </div>

        {/* Search */}
        <div className={styles.searchWrap}>
          <img className={styles.searchIcon} src={UIL("Magnifier")} />
          <input
            className={styles.searchInput}
            placeholder={t("Search", "Search buildings...")}
            value={search}
            onChange={(e) => setFilterSearch(e.target.value)}
          />
          {search.length > 0 && (
            <Button
              variant="icon"
              className={styles.searchClear}
              onSelect={() => setFilterSearch("")}
            >
              <img src={UIL("CircleXClose")} />
            </Button>
          )}
        </div>

        {/* Size + level steppers */}
        <div className={styles.stepperRow}>
          <Stepper
            label={t("LotWidth", "Width")}
            value={lotWidth}
            values={LOT_SIZES}
            format={sizeLabel}
            onChange={setFilterLotWidth}
          />
          <Stepper
            label={t("LotDepth", "Depth")}
            value={lotDepth}
            values={LOT_SIZES}
            format={sizeLabel}
            onChange={setFilterLotDepth}
          />
          <Stepper
            label={t("Level", "Level")}
            value={level}
            values={LEVELS}
            format={levelLabel}
            onChange={setFilterLevel}
          />
        </div>

        {/* Source + asset-pack row: All / base game / mod / region packs. */}
        <div className={styles.iconFilterRow}>
          <span className={styles.iconFilterLabel}>{t("Pack", "Pack")}</span>
          <div className={styles.chipGroup}>
              <PackChip
                icon={UIL("StarAll")}
                tip={t("PackAll", "All packs")}
                active={pack === PACK_ALL}
                onSelect={() => setFilterPack(PACK_ALL)}
              />
              <PackChip
                icon={UIL("BaseGame")}
                tip={t("PackNone", "Base game")}
                active={pack === PACK_NONE}
                onSelect={() => setFilterPack(PACK_NONE)}
              />
              <PackChip
                icon={MOD_ICON}
                tip={t("PackMod", "From a mod (Paradox Mods)")}
                active={pack === PACK_MOD}
                onSelect={() => setFilterPack(PACK_MOD)}
              />
              {packs.map((p, i) => (
                <PackChip
                  key={p}
                  icon={packIcons[i]}
                  tip={p}
                  active={pack === p}
                  onSelect={() => setFilterPack(p)}
                />
              ))}
            </div>
          </div>

        {/* Selection actions + result count */}
        <div className={styles.actionRow}>
          <Button
            variant="flat"
            className={classNames(styles.actionChip, {
              [styles.actionChipActive]: selectedOnly,
            })}
            onSelect={() => setFilterSelectedOnly(!selectedOnly)}
          >
            <img src={UIL(selectedOnly ? "ToggleRightOn" : "ToggleLeftOff")} />
            <span>{t("SelectedOnly", "Selected only")}</span>
          </Button>
          <Button
            variant="flat"
            className={styles.actionChip}
            onSelect={() => setAllVisible(true)}
          >
            <img src={UIL("Checkmark")} />
            <span>{t("SelectAll", "Select all")}</span>
          </Button>
          <Button
            variant="flat"
            className={styles.actionChip}
            onSelect={() => setAllVisible(false)}
          >
            <img src={UIL("CircleXClose")} />
            <span>{t("DeselectAll", "Deselect all")}</span>
          </Button>
          <span className={styles.resultCount}>
            {buildingCount} {t("Results", "results")}
          </span>
        </div>
      </div>

      {/* Icon generation progress. Renders are produced in the background (they
          start as soon as the game has loaded its prefabs, i.e. already on the
          main menu), so this shows how far along that is. Hidden once done. */}
      {iconProgress >= 0 && (
        <div className={styles.iconProgress}>
          <div className={styles.iconProgressLabel}>
            <span>
              {t("GeneratingIcons", "Generating building icons")} — {iconProgress}%
            </span>
            <span className={styles.iconProgressCount}>
              {iconPending} {t("Remaining", "remaining")}
            </span>
          </div>
          <div className={styles.iconProgressTrack}>
            <div
              className={styles.iconProgressFill}
              style={{ width: iconProgress + "%" }}
            />
          </div>
        </div>
      )}

      {/* ---------------------------- Building grid ---------------------------- */}
      <div className={styles.gridArea}>
        <div ref={gridRef} className={styles.grid} onWheel={onWheel}>
          {buildings.map((b: BuildingEntry) => (
            <BuildingCard key={b.name} b={b} packIconMap={packIconMap} />
          ))}
        </div>

        {/* custom scrollbar driven by the virtual scroll state */}
        {maxScrollIndex > 0 && (
          <div className={styles.scrollbar}>
            <Button
              variant="icon"
              className={styles.scrollArrow}
              onSelect={() => scrollTo(scrollIndex - 1)}
            >
              <img src={UIL("ArrowUp")} />
            </Button>
            <div
              ref={trackRef}
              className={styles.scrollTrack}
              onMouseDown={onTrackMouseDown}
            >
              <div
                className={styles.scrollThumb}
                style={{
                  top: thumbTopPct + "%",
                  height: thumbHeightPct + "%",
                }}
              />
            </div>
            <Button
              variant="icon"
              className={styles.scrollArrow}
              onSelect={() => scrollTo(scrollIndex + 1)}
            >
              <img src={UIL("ArrowDown")} />
            </Button>
          </div>
        )}
      </div>
    </div>
  );
};

// A compact FindIt-style spinner: label + value with up/down chevrons.
const Stepper = (props: {
  label: string;
  value: number;
  values: number[];
  format: (v: number) => string;
  onChange: (v: number) => void;
}) => {
  const idx = Math.max(0, props.values.indexOf(props.value));
  const atMin = idx <= 0;
  const atMax = idx >= props.values.length - 1;

  return (
    <div className={styles.stepper}>
      <span className={styles.stepperLabel}>{props.label}</span>
      <div className={styles.stepperControl}>
        <Button
          variant="icon"
          className={classNames(styles.stepperBtn, { [styles.stepperBtnDim]: atMin })}
          onSelect={() => !atMin && props.onChange(props.values[idx - 1])}
        >
          <img src={UIL("ArrowDown")} />
        </Button>
        <span className={styles.stepperValue}>{props.format(props.value)}</span>
        <Button
          variant="icon"
          className={classNames(styles.stepperBtn, { [styles.stepperBtnDim]: atMax })}
          onSelect={() => !atMax && props.onChange(props.values[idx + 1])}
        >
          <img src={UIL("ArrowUp")} />
        </Button>
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Building card
//
// Thumbnail resolution is layered ("firewall" so a card is NEVER blank):
//   1. b.thumbnail — the real image: the prefab's own icon (what AssetIconLibrary
//      provides) or, for buildings the game has no icon for, the PNG our
//      IconGeneratorSystem rendered and cached (coui://czicons/...).
//   2. If that is missing or fails to load, we draw a PROCEDURAL icon built from
//      the building's own data (zone colour + initials + lot size + level), so
//      every building still gets a distinctive, recognisable tile instead of the
//      identical grey placeholder every other mod shows.
// ---------------------------------------------------------------------------
const CATEGORY_COLORS: Record<number, string> = {
  [ZoneCategory.Residential]: "#3f9d4a",
  [ZoneCategory.Mixed]: "#8e6bb5",
  [ZoneCategory.Commercial]: "#3b7fc4",
  [ZoneCategory.Industrial]: "#c9a227",
  [ZoneCategory.Office]: "#2fa8a0",
};

/** Compact initials for a prefab, e.g. EU_MixedOldTown01_L1_1x2 -> "MOT". */
const initialsOf = (b: BuildingEntry) => {
  const source = b.name.replace(/^[A-Z]{2}_/, "");
  const capitals = source.match(/[A-Z]/g);
  if (capitals && capitals.length >= 2) {
    return capitals.slice(0, 3).join("");
  }
  return (b.displayName || b.name).replace(/[^A-Za-z]/g, "").slice(0, 3).toUpperCase();
};

const BuildingCard = (props: {
  b: BuildingEntry;
  packIconMap: Record<string, string>;
}) => {
  const { b, packIconMap } = props;

  // Source chain, best first: whatever C# resolved (a real icon from the game
  // or AssetIconLibrary, else our generated PNG), then the procedural tile.
  //
  // NOTE: we deliberately do NOT probe AssetIconLibrary URLs directly. Doing so
  // spammed the game log with hundreds of HTTP 404s for the ~1200 buildings AIL
  // does not cover (it even tripped the engine's "Backtrace report limit hit"
  // guard). C# already yields AIL's URL once AIL has rewritten the prefab icon,
  // and BuildingIndexSystem re-indexes on a schedule to catch that.
  const sources: string[] = b.thumbnail ? [b.thumbnail] : [];

  const [sourceIndex, setSourceIndex] = useState(0);
  const showProcedural = sourceIndex >= sources.length;
  const { translate } = useLocalization();
  const t = (key: string, fallback: string) =>
    translate(`CustomZones.${key}`, fallback) ?? fallback;

  return (
    <Tooltip
      tooltip={`${b.displayName} · ${b.lotWidth}x${b.lotDepth} · L${b.level}`}
    >
      <div
        className={classNames(styles.card, {
          [styles.cardSelected]: b.selected,
        })}
        onClick={() => toggleBuilding(b.name)}
      >
        {showProcedural ? (
          <div
            className={styles.cardProcedural}
            style={{
              backgroundColor: CATEGORY_COLORS[b.category] ?? "#5a6472",
            }}
          >
            <span className={styles.cardProceduralText}>{initialsOf(b)}</span>
            <span className={styles.cardProceduralSub}>
              {b.lotWidth}x{b.lotDepth} · L{b.level}
            </span>
          </div>
        ) : (
          <img
            className={styles.cardThumb}
            src={sources[sourceIndex]}
            onError={() => setSourceIndex(sourceIndex + 1)}
          />
        )}

        {/* Top-left: zone icon + lot size. */}
        <div className={styles.cardTopLeft}>
          {b.fallbackThumbnail && (
            <img className={styles.cardZoneBadge} src={b.fallbackThumbnail} />
          )}
          <span className={styles.cardSize}>
            {b.lotWidth}x{b.lotDepth}
          </span>
        </div>

        {/* Top-right descriptors (FindIt): theme, packs, then mod flag. */}
        {(b.themeIcon || b.packs.length > 0 || b.isMod) && (
          <div className={styles.cardDescriptors}>
            {b.themeIcon && (
              <img className={styles.cardDescIcon} src={b.themeIcon} />
            )}
            {b.packs.map((p) =>
              packIconMap[p] ? (
                <img key={p} className={styles.cardDescIcon} src={packIconMap[p]} />
              ) : null
            )}
            {b.isMod && <img className={styles.cardDescIcon} src={MOD_ICON} />}
          </div>
        )}

        {/* Drop this building on the map to see how it really looks, the way
            Find It lets you plop an asset. stopPropagation keeps the click from
            also toggling the building in/out of the palette. */}
        <div
          className={styles.cardPlace}
          onClick={(e: React.MouseEvent) => e.stopPropagation()}
        >
          <Tooltip tooltip={t("PlaceOnMap", "Place on the map")}>
            <Button
              variant="icon"
              className={styles.cardPlaceButton}
              onSelect={() => placeBuilding(b.name)}
            >
              <img src={UIL("House")} />
            </Button>
          </Tooltip>
        </div>

        <div className={styles.cardLabel}>{b.displayName}</div>
        {b.selected && (
          <img
            className={styles.cardCheck}
            src="coui://uil/Colored/Checkmark.svg"
          />
        )}
      </div>
    </Tooltip>
  );
};

const PackChip = (props: {
  icon: string;
  tip: string;
  active: boolean;
  onSelect: () => void;
}) => (
  <Tooltip tooltip={props.tip}>
    <Button
      variant="icon"
      className={classNames(styles.packChip, {
        [styles.packChipActive]: props.active,
      })}
      onSelect={props.onSelect}
    >
      <img src={props.icon} />
    </Button>
  </Tooltip>
);
