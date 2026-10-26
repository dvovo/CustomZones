// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Building browser of the zone editor. One card per building (one style at one
// lot size); its levels L1-L5 are a strip under the picture: hover a level to
// see it, click the card to add the design with all its levels, click a level
// to add or remove just that one. The magnifier opens a large preview where
// every level can be looked at and placed in the city. Virtual scrolling: C#
// only sends the rows on screen; this component measures how many fit.
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Dropdown, DropdownToggle, Tooltip } from "cs2/ui";
import {
  zones$,
  previewFamily$,
  setPreviewFamily,
  selectedZone$,
  bigInstalled$,
  families$,
  FamilyEntry,
  familyCount$,
  filterLevel$,
  filterLotWidth$,
  filterLotDepth$,
  filterHeight$,
  setFilterLotDepth,
  setFilterHeight,
  HEIGHT_BANDS,
  filterPack$,
  filterSearch$,
  filterSelectedOnly$,
  filterGrowable$,
  filterType$,
  setFilterType,
  ZONE_TYPES,
  gridColumns$,
  gridRows$,
  ICONS,
  maxScrollIndex$,
  PACK_ALL,
  PACK_MOD,
  PACK_NONE,
  packIcons$,
  packs$,
  placeFamily,
  returnFromPlacing$,
  resetFilters,
  resourceIcon,
  resourceNameKey,
  scrollIndex$,
  setAllVisible,
  setFilterLevel,
  setFilterLotWidth,
  setFilterPack,
  setFilterSearch,
  setFilterSelectedOnly,
  setFilterGrowable,
  openPreview,
  onPreviewRequested,
  setGridColumns,
  setGridRows,
  setScrollIndex,
  toggleFamilyLevel,
  toggleFamilyCard,
  setFamilyAllLevels,
  ZoneEntry,
} from "../bindings";
import { Checkbox, DropdownItem, gameDropdownTheme } from "./vanilla";
import styles from "../styles.module.scss";
import widthIcon from "../images/FilterWidth.svg";
import depthIcon from "../images/FilterDepth.svg";
import heightIcon from "../images/FilterHeight.svg";
import levelIcon from "../images/FilterLevel.svg";
import sourceIcon from "../images/FilterSource.svg";
import inZoneIcon from "../images/FilterInZone.svg";
import growIcon from "../images/FilterGrow.svg";

const filterIcons = {
  type: "Media/Game/Icons/Zones.svg",
  width: widthIcon,
  depth: depthIcon,
  height: heightIcon,
  level: levelIcon,
  source: sourceIcon,
  inZone: inZoneIcon,
  grow: growIcon,
};

// Cards stretch to fill the grid: as many columns of at least MIN_CARD_WIDTH as
// fit, then the width is shared out (bigger pictures on wider panels). Sizes in rem.
const MIN_CARD_WIDTH = 140;
const CARD_GAP = 8;
// Card height besides the picture: padding + border, name, size line, level strip.
const CARD_TEXT_HEIGHT = 72;
const PICTURE_RATIO = 0.62;

/** Card and picture size, in rem. */
interface CardSize {
  width: number;
  height: number;
  picture: number;
}

const DEFAULT_CARD: CardSize = { width: 132, height: 144, picture: 72 };

// Last measured card size: the browser opens straight at it instead of
// starting small and growing (it is hidden until the first measure otherwise).
let lastCard: CardSize | null = null;

// The building open in the large preview and the level it shows: its card in
// the list is outlined and shows that level, so it is easy to see which one it is.
type Previewed = { key: string; level: number } | null;
let previewed: Previewed = null;
const previewedListeners = new Set<(p: Previewed) => void>();

const setPreviewed = (next: Previewed) => {
  if (next?.key === previewed?.key && next?.level === previewed?.level) return;
  previewed = next;
  previewedListeners.forEach((listener) => listener(next));
};

const usePreviewed = () => {
  const [value, setValue] = useState<Previewed>(previewed);
  useEffect(() => {
    previewedListeners.add(setValue);
    setValue(previewed);
    return () => {
      previewedListeners.delete(setValue);
    };
  }, []);
  return value;
};

// Distance from the panel's bottom edge to the screen bottom (.panel bottom), and
// the paddings between the grid and that edge, in rem.
const PANEL_BOTTOM = 72;
const BOTTOM_PADDING = 20;

export const FamilyBrowser = (props: { zone: ZoneEntry }) => {
  const { translate } = useLocalization();
  const t = (key: string, fallback: string) => translate(`CustomZones.${key}`, fallback) ?? fallback;

  const families = useValue(families$);
  const count = useValue(familyCount$);
  const columns = useValue(gridColumns$);
  const rows = useValue(gridRows$);
  const scroll = useValue(scrollIndex$);
  const maxScroll = useValue(maxScrollIndex$);
  const bigInstalled = useValue(bigInstalled$);
  const shown = usePreviewed();

  // ----- How many cards fit: measured from the grid area -----
  const gridRef = useRef<HTMLDivElement>(null);
  // Invisible 100rem-wide element: its width in px is the game's real rem scale
  // (the root font size does not tell it in the game's UI).
  const remProbeRef = useRef<HTMLDivElement>(null);
  const [card, setCard] = useState<CardSize>(lastCard ?? DEFAULT_CARD);
  const [measured, setMeasured] = useState(lastCard !== null);

  useLayoutEffect(() => {
    // The first layout pass can still be settling (fonts, the panel itself): a
    // measure is only trusted ("settled") from the next frames on. Until then the
    // grid keeps the last good size, or stays hidden the very first time.
    const measure = (settled: boolean) => {
      if (!settled && lastCard !== null) return;
      const grid = gridRef.current;
      const probe = remProbeRef.current;
      if (!grid || !probe) return;
      const pxPerRem = probe.getBoundingClientRect().width / 100;
      if (!(pxPerRem > 0)) return;
      const rect = grid.getBoundingClientRect();
      if (rect.width / pxPerRem < 2 * MIN_CARD_WIDTH) return;
      const width = rect.width / pxPerRem;
      const fitColumns = Math.max(2, Math.floor(width / (MIN_CARD_WIDTH + CARD_GAP)));
      const cardWidth = Math.floor(width / fitColumns - CARD_GAP - 0.5);
      let picture = Math.round((cardWidth - 12) * PICTURE_RATIO);

      // The grid takes everything from its top down to the panel's bottom edge.
      const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
      const available = (viewportHeight - rect.top) / pxPerRem - PANEL_BOTTOM - BOTTOM_PADDING;
      // At least two rows: the grid scrolls, it is never a single strip.
      const fitRows = Math.max(2, Math.floor(available / (picture + CARD_TEXT_HEIGHT + CARD_GAP)));
      // Hand the height left over to the pictures, so no gap stays under the grid.
      const spare = available / fitRows - (picture + CARD_TEXT_HEIGHT + CARD_GAP);
      if (spare > 0) picture += Math.floor(Math.min(spare, picture * 0.35));

      const next = { width: cardWidth, height: picture + CARD_TEXT_HEIGHT, picture };
      if (settled) {
        lastCard = next;
        setMeasured(true);
      }
      setCard((old) =>
        old.width === next.width && old.height === next.height && old.picture === next.picture ? old : next
      );
      if (fitColumns !== columns) setGridColumns(fitColumns);
      if (fitRows !== rows) setGridRows(fitRows);
    };

    measure(false);
    const late = setTimeout(() => measure(true), 120);
    const onResize = () => measure(true);
    window.addEventListener("resize", onResize);
    return () => {
      clearTimeout(late);
      window.removeEventListener("resize", onResize);
    };
  }, [columns, rows, props.zone.id, props.zone.familyCount]);

  const scrollBy = (delta: number) => {
    const next = Math.max(0, Math.min(maxScroll, Math.round(scroll) + delta));
    if (next !== scroll) setScrollIndex(next);
  };

  const onWheel = (e: React.WheelEvent) => scrollBy(e.deltaY > 0 ? 1 : -1);

  return (
    <div className={styles.browser}>
      <div className={styles.remProbe} ref={remProbeRef} />
      {/* Compact header: title, count and the actions on one line. */}
      <div className={styles.browserHeader}>
        <span className={styles.sectionTitle}>{t("Buildings", "Buildings")}</span>
        <BrowserHelp t={t} />
        <span className={styles.browserCount}>{`${count}`}</span>
        {!bigInstalled && (
          <Tooltip tooltip={t("BigHint", "Install Building Icon Generator to see real pictures of every building.")}>
            <img className={styles.inlineIconDim} src={ICONS.place} />
          </Tooltip>
        )}
        <div className={styles.browserActions}>
          <Tooltip tooltip={t("SelectAll", "Add all shown")}>
            <Button className={styles.compactButton} variant="icon" onSelect={() => setAllVisible(true)}>
              <img src={ICONS.check} />
            </Button>
          </Tooltip>
          <Tooltip tooltip={t("DeselectAll", "Remove all shown")}>
            <Button className={styles.compactButton} variant="icon" onSelect={() => setAllVisible(false)}>
              <img src={ICONS.clear} />
            </Button>
          </Tooltip>
          <Tooltip tooltip={t("ResetFilters", "Reset filters")}>
            <Button className={styles.compactButton} variant="icon" onSelect={resetFilters}>
              <img src={ICONS.reset} />
            </Button>
          </Tooltip>
        </div>
      </div>

      <Filters t={t} />

      <div
        className={styles.gridArea}
        style={{ height: `${rows * (card.height + CARD_GAP)}rem`, opacity: measured ? 1 : 0 }}
      >
        <div className={styles.grid} ref={gridRef} onWheel={onWheel}>
          {families.slice(0, columns * rows).map((family) => (
            <FamilyCard
              key={family.key}
              family={family}
              size={card}
              t={t}
              previewLevel={shown?.key === family.key ? shown.level : 0}
            />
          ))}
          {count === 0 && <div className={styles.emptyGrid}>{t("NoResults", "No building matches the filters.")}</div>}
        </div>
        <ScrollBar scroll={scroll} maxScroll={maxScroll} rows={rows} onChange={setScrollIndex} />
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Filters
// ---------------------------------------------------------------------------

const WIDTH_OPTIONS = [0, 1, 2, 3, 4, 5, 6];
const LEVEL_OPTIONS = [0, 1, 2, 3, 4, 5];
const HEIGHT_OPTIONS = [0, 1, 2, 3, 4, 5];

const Filters = (props: { t: (key: string, fallback: string) => string }) => {
  const { t } = props;
  const lotWidth = useValue(filterLotWidth$);
  const lotDepth = useValue(filterLotDepth$);
  const heightBand = useValue(filterHeight$);
  const level = useValue(filterLevel$);
  const pack = useValue(filterPack$);
  const search = useValue(filterSearch$);
  const selectedOnly = useValue(filterSelectedOnly$);
  const growable = useValue(filterGrowable$);
  const typeFilter = useValue(filterType$);
  const packs = useValue(packs$);
  const packIcons = useValue(packIcons$);

  const [text, setText] = useState(search);
  useEffect(() => setText(search), [search]);

  // Search as you type, but not on every keystroke.
  useEffect(() => {
    if (text === search) return;
    const handle = setTimeout(() => setFilterSearch(text), 200);
    return () => clearTimeout(handle);
  }, [text]);

  const widthLabel = (w: number) =>
    w === 0 ? t("AnyWidth", "Any lot width") : w === 6 ? t("Width6", "6+ cells wide") : `${w} ${t("CellsWide", "cells wide")}`;

  const depthLabel = (d: number) =>
    d === 0 ? t("AnyDepth", "Any lot depth") : d === 6 ? t("Depth6", "6+ cells deep") : `${d} ${t("CellsDeep", "cells deep")}`;

  const heightLabel = (h: number) => (h === 0 ? t("AnyHeight", "Any height") : HEIGHT_BANDS[h - 1]);

  const levelLabel = (l: number) => (l === 0 ? t("AnyLevel", "Any level") : `${t("Level", "Level")} ${l}`);

  const packLabel = (p: string) =>
    p === PACK_ALL ? t("AllSources", "All sources") : p === PACK_NONE ? t("BaseGame", "Base game") : p === PACK_MOD ? t("FromMods", "From mods") : p;

  const packOptions = [PACK_ALL, PACK_NONE, PACK_MOD, ...packs];

  const typeOptions: { value: string; label: string; icon?: string }[] = [
    { value: "", label: t("TypeOwn", "This zone's type") },
    { value: "all", label: t("TypeAll", "All types") },
    ...ZONE_TYPES.map((z) => ({ value: `${z.category}:${z.density}`, label: t(z.labelKey, z.label), icon: z.icon })),
  ];
  const packIcon = (p: string) =>
    p === PACK_NONE ? ICONS.baseGame : p === PACK_MOD ? ICONS.mod : packIcons[packs.indexOf(p)] || undefined;

  // One compact chip per filter: icon + short value, the full text in its tooltip.
  const chip = (icon: string | undefined, short: string, tooltip: string, active: boolean) => (
    <DropdownToggle className={active ? styles.filterChipActive : styles.filterChip} tooltip={tooltip}>
      <div className={styles.filterChipBody}>
        {icon && <img className={styles.filterChipIcon} src={icon} />}
        {short && <span className={styles.filterChipText}>{short}</span>}
      </div>
    </DropdownToggle>
  );

  const shortWidth = (w: number) => (w === 0 ? t("AnyShort", "any") : w === 6 ? "6+" : `${w}`);
  const shortHeight = (h: number) => (h === 0 ? t("AnyShort", "any") : HEIGHT_BANDS[h - 1].replace(" m", "m").replace("–", "-"));
  const typeEntry = typeOptions.find((o) => o.value === typeFilter) ?? typeOptions[0];

  return (
    <div className={styles.filters}>
      <div className={styles.searchBox}>
        <input className={styles.textInput} value={text} onChange={(e) => setText(e.target.value)} />
        {/* The game's UI does not draw placeholders: show the hint ourselves. */}
        {text.length === 0 && (
          <span className={styles.searchPlaceholder}>{t("Search", "Search building styles...")}</span>
        )}
        {text.length > 0 && (
          <Button className={styles.searchClear} variant="icon" onSelect={() => setText("")}>
            <img src={ICONS.clear} />
          </Button>
        )}
      </div>

      <div className={styles.filterChips}>
        <Dropdown
          theme={gameDropdownTheme}
          content={typeOptions.map((o) => (
            <DropdownItem key={o.value || "own"} value={o.value} icon={o.icon} selected={o.value === typeFilter} onChange={() => setFilterType(o.value)}>
              {o.label}
            </DropdownItem>
          ))}
        >
          {selectedOnly
            ? chip(filterIcons.type, t("AllShort", "all"), t("TypeInZone", "In this zone: every building chosen for this zone, whatever its type."), false)
            : chip(typeEntry.icon ?? filterIcons.type, typeEntry.icon ? "" : typeEntry.value === "all" ? t("AllShort", "all") : t("OwnShort", "zone"), `${t("FilterType", "Building type")}: ${typeEntry.label}`, typeFilter !== "")}
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={WIDTH_OPTIONS.map((w) => (
            <DropdownItem key={w} value={w} selected={w === lotWidth} onChange={() => setFilterLotWidth(w)}>
              {widthLabel(w)}
            </DropdownItem>
          ))}
        >
          {chip(filterIcons.width, shortWidth(lotWidth), widthLabel(lotWidth), lotWidth !== 0)}
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={WIDTH_OPTIONS.map((d) => (
            <DropdownItem key={d} value={d} selected={d === lotDepth} onChange={() => setFilterLotDepth(d)}>
              {depthLabel(d)}
            </DropdownItem>
          ))}
        >
          {chip(filterIcons.depth, shortWidth(lotDepth), depthLabel(lotDepth), lotDepth !== 0)}
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={HEIGHT_OPTIONS.map((h) => (
            <DropdownItem key={h} value={h} selected={h === heightBand} onChange={() => setFilterHeight(h)}>
              {heightLabel(h)}
            </DropdownItem>
          ))}
        >
          {chip(filterIcons.height, shortHeight(heightBand), heightLabel(heightBand), heightBand !== 0)}
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={LEVEL_OPTIONS.map((l) => (
            <DropdownItem key={l} value={l} selected={l === level} onChange={() => setFilterLevel(l)}>
              {levelLabel(l)}
            </DropdownItem>
          ))}
        >
          {chip(filterIcons.level, level === 0 ? t("AnyShort", "any") : `${level}`, levelLabel(level), level !== 0)}
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={packOptions.map((p) => (
            <DropdownItem key={p || "all"} value={p} icon={packIcon(p)} selected={p === pack} onChange={() => setFilterPack(p)}>
              {packLabel(p)}
            </DropdownItem>
          ))}
        >
          {chip(packIcon(pack) ?? filterIcons.source, pack === PACK_ALL ? t("AllShort", "all") : "", packLabel(pack), pack !== PACK_ALL)}
        </Dropdown>
      </div>

      {/* Second row: the two on / off filters. */}
      <div className={styles.filterToggles}>
        <Tooltip tooltip={t("SelectedOnlyHint", "Only the buildings already chosen for this zone.")}>
          <div className={selectedOnly ? styles.filterToggleActive : styles.filterToggle} onClick={() => setFilterSelectedOnly(!selectedOnly)}>
            <img className={styles.filterToggleIcon} src={filterIcons.inZone} />
            <span>{t("SelectedOnly", "In this zone")}</span>
          </div>
        </Tooltip>

        <Tooltip tooltip={t("GrowableHint", "Only buildings the city has demand for right now (the ones that can grow).")}>
          <div className={growable ? styles.filterToggleActive : styles.filterToggle} onClick={() => setFilterGrowable(!growable)}>
            <img className={styles.filterToggleIcon} src={filterIcons.grow} />
            <span>{t("Growable", "Can grow now")}</span>
          </div>
        </Tooltip>
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Card
// ---------------------------------------------------------------------------

const LEVELS = [1, 2, 3, 4, 5];

// Speed of the name sliding under the mouse, in px per second.
const NAME_SLIDE_SPEED = 45;

/**
 * One-line building name. A name longer than the card fades out on the right;
 * while the mouse is over the card it slides left to show the rest.
 */
const CardName = (props: { name: string; sliding: boolean; width: number }) => {
  const { name, sliding, width } = props;
  const boxRef = useRef<HTMLDivElement>(null);
  const textRef = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState(0);

  useLayoutEffect(() => {
    const measure = () => {
      const box = boxRef.current;
      const text = textRef.current;
      if (!box || !text) return;
      const extra = Math.max(0, Math.ceil(text.getBoundingClientRect().width - box.getBoundingClientRect().width));
      setOverflow((old) => (Math.abs(old - extra) <= 1 ? old : extra));
    };
    measure();
    const late = setTimeout(measure, 100);
    return () => clearTimeout(late);
    // Only when the name or the card size changes (not on every data update).
  }, [name, width]);

  const slide = sliding && overflow > 0;
  return (
    <div ref={boxRef} className={overflow > 0 && !slide ? styles.cardNameFaded : styles.cardName}>
      <div
        ref={textRef}
        className={styles.cardNameText}
        style={{
          transform: `translateX(${slide ? -overflow : 0}px)`,
          transitionProperty: "transform",
          transitionDuration: slide ? `${Math.max(0.6, overflow / NAME_SLIDE_SPEED)}s` : "0.25s",
          transitionTimingFunction: "linear",
          transitionDelay: slide ? "0.3s" : "0s",
        }}
      >
        {name}
      </div>
    </div>
  );
};

const FamilyCard = (props: {
  family: FamilyEntry;
  size: CardSize;
  t: (key: string, fallback: string) => string;
  /** Level shown in the large preview when this building is open there, else 0. */
  previewLevel: number;
}) => {
  const { family, size, t, previewLevel } = props;
  const inPreview = previewLevel > 0;
  // Level under the mouse on the strip (shown instead of the card's own level).
  const [hoverLevel, setHoverLevel] = useState<number | null>(null);
  const [hovered, setHovered] = useState(false);
  const [failed, setFailed] = useState(false);

  const level = hoverLevel ?? (inPreview ? previewLevel : family.shownLevel);
  const thumbnail = family.levelThumbnails[level - 1] || family.thumbnail;
  useEffect(() => setFailed(false), [thumbnail]);
  const picture = !failed && thumbnail ? thumbnail : family.fallbackThumbnail;

  const levelChosen = family.selectedLevels.includes(level);
  const anyChosen = family.selectedLevels.length > 0;

  return (
    <div
      className={inPreview ? (anyChosen ? styles.cardPreviewedSelected : styles.cardPreviewed) : anyChosen ? styles.cardSelected : styles.card}
      style={{ width: `${size.width}rem`, height: `${size.height}rem` }}
      onClick={() => toggleFamilyCard(family.key, realLevelBelow(family, level))}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => {
        setHovered(false);
        setHoverLevel(null);
      }}
    >
      <div className={styles.cardPicture} style={{ height: `${size.picture}rem` }}>
        <img
          className={picture === family.fallbackThumbnail ? styles.cardFallback : styles.cardImage}
          style={picture === family.fallbackThumbnail ? undefined : { maxHeight: `${size.picture}rem` }}
          src={picture}
          onError={() => setFailed(true)}
        />
        {levelChosen && <img className={styles.cardCheck} src={ICONS.check} />}
        <div className={styles.cardBadges}>
          {family.themeIcon && <img className={styles.cardBadge} src={family.themeIcon} />}
          {family.isMod && <img className={styles.cardBadge} src={ICONS.mod} />}
        </div>
        <NoDemandBadge family={family} t={t} />
        <div
          className={inPreview ? styles.cardPlaceActive : styles.cardPlace}
          onClick={(e) => {
            e.stopPropagation();
            openPreview({ family, level });
          }}
        >
          <Tooltip tooltip={inPreview ? t("InPreview", "Open in the large view") : t("Preview", "View it large")}>
            <img src={ICONS.zoom} />
          </Tooltip>
        </div>
      </div>
      <CardName name={family.name} sliding={hovered} width={size.width} />
      <CardMeta family={family} level={level} t={t} />
      <div className={styles.levelStrip}>
        {LEVELS.map((l) => {
          const kind = levelKind(family, l);
          const exists = kind === 2;
          const chosen = family.selectedLevels.includes(l);
          const className = kind === 0
            ? styles.levelPillMissing
            : kind === 1
            ? l === level
              ? styles.levelPillSharedShown
              : styles.levelPillShared
            : chosen
            ? l === level
              ? styles.levelPillChosenShown
              : styles.levelPillChosen
            : l === level
            ? styles.levelPillShown
            : styles.levelPill;

          return (
            <div
              key={l}
              className={className}
              onMouseEnter={() => kind > 0 && setHoverLevel(l)}
              onClick={(e) => {
                e.stopPropagation();
                if (exists) toggleFamilyLevel(family.key, l);
              }}
            >
              {`${l}`}
            </div>
          );
        })}
      </div>
    </div>
  );
};

type T = (key: string, fallback: string) => string;

/** 2 = the design has its own building at this level, 1 = it looks like the level below, 0 = nothing. */
const levelKind = (family: FamilyEntry, level: number) =>
  family.levelKinds?.[level - 1] ?? (family.levelThumbnails[level - 1] ? 2 : 0);

/** The design's own level that shows a level (itself, or the nearest one below). */
const realLevelBelow = (family: FamilyEntry, level: number) => {
  for (let l = level; l >= 1; l--) if (levelKind(family, l) === 2) return l;
  return level;
};

/** The design's next own level above a level, or 0. */
const realLevelAbove = (family: FamilyEntry, level: number) => {
  for (let l = level + 1; l <= 5; l++) if (levelKind(family, l) === 2) return l;
  return 0;
};

/** Explains a level the design has no building for (it looks like the level below). */
const sharedLevelText = (family: FamilyEntry, level: number, t: T, skips: boolean) => {
  const below = realLevelBelow(family, level);
  const above = realLevelAbove(family, level);
  const look = t("SharedLevelLook", "The game has no separate building for level {level} of this design: it looks like its level {below}.")
    .replace("{level}", `${level}`)
    .replace("{below}", `${below}`);
  const path = above && skips
    ? t("SharedLevelJump", "In a zone that skips levels, it goes from level {below} straight to its level {above}, taking the game's time for the levels in between.")
        .replace("{below}", `${below}`)
        .replace("{above}", `${above}`)
    : above
    ? t("SharedLevelOther", "A building of this design that reaches level {level} becomes another building ticked for that level, like in the game.")
        .replace("{level}", `${level}`)
    : t("SharedLevelTop", "This design goes no higher than level {below}.").replace("{below}", `${below}`);
  return `${look} ${path}`;
};

/** "Sells", "Makes", "Stores", "Offers" (offices). */
const roleLabel = (role: string, t: T) =>
  role === "sells"
    ? t("RoleSells", "Sells")
    : role === "makes"
    ? t("RoleMakes", "Makes")
    : role === "stores"
    ? t("RoleStores", "Stores")
    : role === "office"
    ? t("RoleOffice", "Offers")
    : "";

/** Text explaining whether the building can grow now, for its tooltips. */
const demandText = (family: FamilyEntry, goodsName: string, t: T) =>
  family.hasDemand
    ? `${roleLabel(family.goodsRole, t)} ${goodsName}. ${t("DemandYesLong", "The city needs more of it right now, so it can grow.")}`
    : family.goods
    ? `${roleLabel(family.goodsRole, t)} ${goodsName}. ${t("DemandNoLong", "The city does not need more of it right now: it will not grow, even in this zone, until there is demand.")}`
    : t("DemandNoHousing", "No housing demand for this density right now: it will not grow until there is");

const useGoodsName = (goods: string) => {
  const { translate } = useLocalization();
  return goods ? translate(resourceNameKey(goods), goods) ?? goods : "";
};

// Red warning on the picture, only when the building cannot grow now.
const NoDemandBadge = (props: { family: FamilyEntry; t: T }) => {
  const { family, t } = props;
  const goodsName = useGoodsName(family.goods);
  if (family.hasDemand) return null;

  return (
    <div className={styles.demandBadgeBlocked} onClick={(e) => e.stopPropagation()}>
      <Tooltip tooltip={demandText(family, goodsName, t)}>
        <img className={styles.demandBadgeWarning} src={ICONS.warning} />
      </Tooltip>
    </div>
  );
};

// Lot size and goods line under the name: "6x6 · [icon] Lodging".
const CardMeta = (props: { family: FamilyEntry; level: number; t: T }) => {
  const { family, level, t } = props;
  const goodsName = useGoodsName(family.goods);
  const height = family.levelHeights?.[level - 1] ?? 0;
  // Designs the game only made for some levels (e.g. only level 5): say so.
  const onlyLevel = family.minLevel === family.maxLevel && family.minLevel > 1 ? ` · ${t("OnlyLevel", "only L")}${family.minLevel}` : "";
  const size = `${family.lotSizes.join(" · ")}${height > 0 ? ` · ${height} m` : ""}${onlyLevel}`;

  if (!family.goods) {
    return <div className={styles.cardMeta}>{size}</div>;
  }

  return (
    <Tooltip tooltip={demandText(family, goodsName, t)}>
      <div className={styles.cardMetaRow}>
        <span className={styles.cardMetaLot}>{`${size} ·`}</span>
        <img className={styles.cardGoodsIcon} src={resourceIcon(family.goods)} />
        <span className={family.hasDemand ? styles.cardGoods : styles.cardGoodsBlocked}>{goodsName}</span>
      </div>
    </Tooltip>
  );
};

// Help next to the "Buildings" title: how the cards work.
const BrowserHelp = (props: { t: T }) => {
  const { t } = props;
  return (
    <Tooltip
      tooltip={
        <div className={styles.helpTooltip}>
          <div className={styles.helpLine}>{t("HelpCard", "Click a card to add the building to this zone with all its levels (click again to remove it). To leave a level out, untick its number.")}</div>
          <div className={styles.helpLine}>{t("HelpLevels", "The numbers 1-5 are its levels: click one to add or remove just that level. Like the game, buildings are born at level 1 and level up one by one; with \"Skip levels\" they are born at their lowest ticked level and jump over the unticked ones. Filled = in this zone, outlined = the one on the picture, dim = it does not exist.")}</div>
          <div className={styles.helpLine}>{t("HelpZoom", "The magnifier shows it large, with every level, and lets you place one in the city.")}</div>
          <div className={styles.helpLine}>{t("HelpGoods", "Under the name: its lot size (cells wide x deep), its height at the level shown and what it sells, makes or stores.")}</div>
          <div className={styles.helpLine}>{t("HelpDemand", "In red with a warning: the city does not need more of it right now. A building only grows when there is demand for what it sells or makes, even when this zone allows it.")}</div>
        </div>
      }
    >
      <img className={styles.helpIcon} src={ICONS.help} />
    </Tooltip>
  );
};

const levelRange = (family: FamilyEntry) =>
  family.minLevel === family.maxLevel ? `L${family.minLevel}` : `L${family.minLevel}–${family.maxLevel}`;

// ---------------------------------------------------------------------------
// Large preview (magnifier of a card): look at every level, place one
// ---------------------------------------------------------------------------

// The building last sent to "place it in the city": the preview reopens on it
// when the player leaves the placement with Escape (the panel is closed meanwhile).
let lastPlaced: { family: FamilyEntry; level: number } | null = null;
let handledReturn = 0;

export const FamilyPreview = () => {
  const { translate } = useLocalization();
  const t = (key: string, fallback: string) => translate(`CustomZones.${key}`, fallback) ?? fallback;
  const [request, setRequest] = useState<{ family: FamilyEntry; level: number } | null>(null);
  const [level, setLevel] = useState(1);
  const [failed, setFailed] = useState(false);
  const families = useValue(families$);
  const scroll = useValue(scrollIndex$);
  const maxScroll = useValue(maxScrollIndex$);
  const columns = useValue(gridColumns$);
  const count = useValue(familyCount$);
  const previewEntry = useValue(previewFamily$);
  const zones = useValue(zones$);
  const selectedZone = useValue(selectedZone$);
  const skips = zones.find((z) => z.id === selectedZone)?.growth === 1;
  // Stepping past the rows on screen scrolls the grid; the step finishes when the new rows arrive.
  const [pending, setPending] = useState<{ key: string; dir: number } | null>(null);

  const returned = useValue(returnFromPlacing$);
  useEffect(() => {
    if (returned <= handledReturn) return;
    handledReturn = returned;
    if (lastPlaced) {
      setRequest(lastPlaced);
      setLevel(lastPlaced.level);
    }
  }, [returned]);

  useEffect(
    () =>
      onPreviewRequested((r) => {
        setRequest(r);
        if (r) setLevel(r.level);
      }),
    []
  );

  const showFamily = (f: FamilyEntry) => {
    setRequest({ family: f, level: f.shownLevel });
    setLevel(f.shownLevel);
  };

  // Previous / next building of the filtered list.
  const step = (dir: number) => {
    if (!request) return;
    const index = families.findIndex((f) => f.key === request.family.key);
    const target = index + dir;
    if (index >= 0 && target >= 0 && target < families.length) {
      showFamily(families[target]);
    } else if ((dir > 0 && scroll < maxScroll) || (dir < 0 && scroll > 0)) {
      setPending({ key: request.family.key, dir });
      setScrollIndex(scroll + dir);
    }
  };

  useEffect(() => {
    if (!pending) return;
    const index = families.findIndex((f) => f.key === pending.key);
    const target = index + pending.dir;
    if (index >= 0 && target >= 0 && target < families.length) {
      setPending(null);
      showFamily(families[target]);
    }
  }, [families]);

  // Latest data of the building while it is on screen (chosen levels change).
  // (C# sends it apart from the list, so it is fresh even when its card is not on screen.)
  const family = request
    ? previewEntry.find((f) => f.key === request.family.key) ??
      families.find((f) => f.key === request.family.key) ??
      request.family
    : undefined;
  useEffect(() => setPreviewFamily(request ? request.family.key : ""), [request?.family.key]);
  useEffect(() => () => setPreviewFamily(""), []);

  // Its card in the list follows the preview (outlined, showing this level).
  useEffect(() => setPreviewed(family ? { key: family.key, level } : null), [family?.key, level]);
  useEffect(() => () => setPreviewed(null), []);

  // Keys: Escape closes, left / right step through the list, 1-5 show a level,
  // Space lets the level shown grow in the zone (or not), A does it for all levels.
  useEffect(() => {
    if (!family) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setRequest(null);
      else if (e.key === "ArrowLeft") step(-1);
      else if (e.key === "ArrowRight") step(1);
      else if (e.key >= "1" && e.key <= "5") {
        const l = Number(e.key);
        if (levelKind(family, l) > 0) setLevel(l);
      } else if (e.key === " " && levelKind(family, level) === 2) toggleFamilyLevel(family.key, level);
      else if (e.key === "a" || e.key === "A") {
        const all = [1, 2, 3, 4, 5].every((l) => levelKind(family, l) !== 2 || family.selectedLevels.includes(l));
        setFamilyAllLevels(family.key, !all);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [request, families, scroll, maxScroll, level, family?.key]);
  const large = family ? family.levelPictures[level - 1] || "" : "";
  useEffect(() => setFailed(false), [large, family?.key]);
  const goodsName = useGoodsName(family?.goods ?? "");

  if (!family) return null;

  const thumbnail = family.levelThumbnails[level - 1] || family.thumbnail;
  const picture = !failed && large ? large : thumbnail || family.fallbackThumbnail;
  const chosen = family.selectedLevels.includes(level);
  const allChosen = [1, 2, 3, 4, 5].every((l) => levelKind(family, l) !== 2 || family.selectedLevels.includes(l));
  const shared = levelKind(family, level) === 1;
  const close = () => setRequest(null);
  const onBackdropClick = () => {
    if (Date.now() - lastDragEnd > 300) close();
  };

  // Position in the filtered list ("12 / 96"): the cards on screen start at row `scroll`.
  const index = families.findIndex((f) => f.key === family.key);
  const position = index >= 0 ? scroll * columns + index + 1 : 0;

  // The pictures of the buildings either side, loaded ahead so stepping is instant.
  const neighbours = index >= 0 ? [families[index - 1], families[index + 1]] : [];

  return (
    <div className={styles.previewBackdrop} onClick={onBackdropClick}>
      <div className={styles.preview} onClick={(e) => e.stopPropagation()}>
        <div className={styles.previewHeader}>
          <div className={styles.previewHeading}>
            <div className={styles.previewTitle}>{`${family.name} · ${t("Level", "Level")} ${level}`}</div>
            <div className={styles.previewMeta}>
              {`${t("LotSize", "Lot size")} ${family.lotSizes.join(", ")} · ${t("Levels", "Levels")} ${levelRange(family)}${family.theme ? ` · ${family.theme}` : ""}`}
            </div>
          </div>
          {position > 0 && <span className={styles.previewPosition}>{`${position} / ${count}`}</span>}
          <Button className={styles.iconButton} variant="icon" onSelect={close}>
            <img src={ICONS.close} />
          </Button>
        </div>

        {(family.goods || !family.hasDemand) && (
          <div className={family.hasDemand ? styles.previewDemand : styles.previewDemandBlocked}>
            {family.goods && <img className={styles.cardGoodsIcon} src={resourceIcon(family.goods)} />}
            {!family.hasDemand && <img className={styles.cardGoodsIcon} src={ICONS.warning} />}
            <span>{demandText(family, goodsName, t)}</span>
          </div>
        )}

        <div className={styles.previewPicture}>
          <ZoomableImage src={picture} resetKey={`${family.key}:${level}`} onError={() => setFailed(true)} t={t} />
          <Tooltip tooltip={t("PreviousBuilding", "Previous building")}>
            <div className={styles.previewNavLeft} onClick={() => step(-1)}>
              <img src={ICONS.arrowLeft} />
            </div>
          </Tooltip>
          <Tooltip tooltip={t("NextBuilding", "Next building")}>
            <div className={styles.previewNavRight} onClick={() => step(1)}>
              <img src={ICONS.arrowRight} />
            </div>
          </Tooltip>
          <div className={styles.preloader}>
            {neighbours.map(
              (f) => f && <img key={f.key} src={f.levelPictures[f.shownLevel - 1] || f.levelThumbnails[f.shownLevel - 1] || f.thumbnail} />
            )}
          </div>
        </div>

        <div className={styles.previewLevels}>
          {family.levelThumbnails.map((thumb, i) => {
            const l = i + 1;
            const kind = levelKind(family, l);
            const levelChosen = family.selectedLevels.includes(l);
            const className = kind === 0
              ? styles.previewLevelMissing
              : kind === 1
              ? l === level
                ? styles.previewLevelSharedShown
                : styles.previewLevelShared
              : l === level
              ? levelChosen
                ? styles.previewLevelShownChosen
                : styles.previewLevelShown
              : levelChosen
              ? styles.previewLevelChosen
              : styles.previewLevel;
            return (
              <div key={l} className={className} onClick={() => kind > 0 && setLevel(l)}>
                {thumb && kind > 0 ? <img className={styles.previewLevelImage} src={thumb} /> : <div className={styles.previewLevelImage} />}
                <span>{kind === 1 ? `${t("Level", "Level")} ${l} = L${realLevelBelow(family, l)}` : `${t("Level", "Level")} ${l}`}</span>
              </div>
            );
          })}
        </div>

        <div className={styles.previewActions}>
          <div className={styles.previewChecks}>
            {shared ? (
              <div className={styles.previewSharedNote}>{sharedLevelText(family, level, t, skips)}</div>
            ) : (
              <div className={styles.checkboxField}>
                <Checkbox checked={chosen} onChange={() => toggleFamilyLevel(family.key, level)} />
                <span onClick={() => toggleFamilyLevel(family.key, level)}>
                  {t("GrowsInZone", "This level grows in this zone")}
                </span>
              </div>
            )}
            <Tooltip tooltip={t("AllLevelsHint", "Every level of this building grows in this zone (A). Untick it to take them all away.")}>
              <div className={styles.checkboxField}>
                <Checkbox checked={allChosen} onChange={() => setFamilyAllLevels(family.key, !allChosen)} />
                <span onClick={() => setFamilyAllLevels(family.key, !allChosen)}>{t("AllLevels", "All its levels")}</span>
              </div>
            </Tooltip>
          </div>
          <Tooltip tooltip={t("PlaceLevelHint", "Place it in the city (Escape comes back here)")}>
            <Button
              className={styles.placeButton}
              variant="icon"
              onSelect={() => {
                lastPlaced = { family, level };
                close();
                // A level without its own building places the one that shows it.
                placeFamily(family.key, realLevelBelow(family, level));
              }}
            >
              <img src={ICONS.placeBuilding} />
            </Button>
          </Tooltip>
        </div>
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Zoomable picture of the large preview: mouse wheel zooms where the pointer is,
// drag moves the zoomed picture, double click zooms in / back out. Keys + - 0.
// ---------------------------------------------------------------------------

const MAX_ZOOM = 4;

// When the last drag of a zoomed picture ended: a drag released outside the
// preview must not count as a click on the backdrop (which closes it).
let lastDragEnd = 0;

const ZoomableImage = (props: { src: string; resetKey: string; onError: () => void; t: T }) => {
  const { src, resetKey, onError, t } = props;
  const boxRef = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ z: 1, x: 0, y: 0 });
  const [dragging, setDragging] = useState(false);

  // Each building and level starts whole.
  useEffect(() => setView({ z: 1, x: 0, y: 0 }), [resetKey]);

  // Keeps the zoomed picture over the frame (no empty space dragged in).
  const clamp = (v: { z: number; x: number; y: number }) => {
    const rect = boxRef.current?.getBoundingClientRect();
    if (!rect) return v;
    const mx = ((v.z - 1) * rect.width) / 2;
    const my = ((v.z - 1) * rect.height) / 2;
    return { z: v.z, x: Math.max(-mx, Math.min(mx, v.x)), y: Math.max(-my, Math.min(my, v.y)) };
  };

  // Zooms keeping the point under (clientX, clientY) in place; the centre when none.
  const zoomAt = (factor: number, clientX?: number, clientY?: number) =>
    setView((old) => {
      const rect = boxRef.current?.getBoundingClientRect();
      const z = Math.max(1, Math.min(MAX_ZOOM, old.z * factor));
      if (!rect || z === old.z) return old;
      if (z === 1) return { z: 1, x: 0, y: 0 };
      const px = clientX === undefined ? 0 : clientX - (rect.left + rect.width / 2);
      const py = clientY === undefined ? 0 : clientY - (rect.top + rect.height / 2);
      const qx = (px - old.x) / old.z;
      const qy = (py - old.y) / old.z;
      return clamp({ z, x: px - z * qx, y: py - z * qy });
    });

  const reset = () => setView({ z: 1, x: 0, y: 0 });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "+" || e.key === "=") zoomAt(1.25);
      else if (e.key === "-") zoomAt(0.8);
      else if (e.key === "0") reset();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const onWheel = (e: React.WheelEvent) => {
    e.stopPropagation();
    zoomAt(e.deltaY < 0 ? 1.25 : 0.8, e.clientX, e.clientY);
  };

  const onMouseDown = (e: React.MouseEvent) => {
    if (view.z <= 1 || e.button !== 0) return;
    const start = { mx: e.clientX, my: e.clientY, x: view.x, y: view.y, z: view.z };
    setDragging(true);
    const onMove = (ev: MouseEvent) =>
      setView(clamp({ z: start.z, x: start.x + ev.clientX - start.mx, y: start.y + ev.clientY - start.my }));
    const onUp = () => {
      lastDragEnd = Date.now();
      setDragging(false);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const onDoubleClick = (e: React.MouseEvent) => (view.z > 1 ? reset() : zoomAt(2.5, e.clientX, e.clientY));

  return (
    <>
      <div
        ref={boxRef}
        className={view.z > 1 ? (dragging ? styles.zoomBoxDragging : styles.zoomBoxZoomed) : styles.zoomBox}
        onWheel={onWheel}
        onMouseDown={onMouseDown}
        onDoubleClick={onDoubleClick}
      >
        <img
          className={styles.previewImage}
          src={src}
          onError={onError}
          style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.z})` }}
        />
      </div>
      <div className={styles.zoomControls}>
        <Tooltip tooltip={t("ZoomOut", "Zoom out (-)")}>
          <div className={view.z > 1 ? styles.zoomButton : styles.zoomButtonDisabled} onClick={() => zoomAt(0.8)}>
            {"−"}
          </div>
        </Tooltip>
        <Tooltip tooltip={t("ZoomReset", "Whole building (0). Mouse wheel zooms where the pointer is, drag to move, double click to zoom in or out.")}>
          <div className={styles.zoomLevel} onClick={reset}>{`${Math.round(view.z * 100)}%`}</div>
        </Tooltip>
        <Tooltip tooltip={t("ZoomIn", "Zoom in (+)")}>
          <div className={view.z < MAX_ZOOM ? styles.zoomButton : styles.zoomButtonDisabled} onClick={() => zoomAt(1.25)}>
            {"+"}
          </div>
        </Tooltip>
      </div>
    </>
  );
};

// ---------------------------------------------------------------------------
// Scroll bar (rows)
// ---------------------------------------------------------------------------

const ScrollBar = (props: { scroll: number; maxScroll: number; rows: number; onChange: (row: number) => void }) => {
  const { scroll, maxScroll, rows, onChange } = props;
  const trackRef = useRef<HTMLDivElement>(null);

  if (maxScroll <= 0) {
    return <div className={styles.scrollTrackEmpty} />;
  }

  const total = maxScroll + rows;
  const thumbSize = Math.max(8, (rows / total) * 100);
  const thumbTop = (scroll / maxScroll) * (100 - thumbSize);

  const rowAt = (clientY: number) => {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect || rect.height <= 0) return scroll;
    const ratio = (clientY - rect.top) / rect.height;
    return Math.max(0, Math.min(maxScroll, Math.round(ratio * maxScroll)));
  };

  const onMouseDown = (e: React.MouseEvent) => {
    onChange(rowAt(e.clientY));
    const onMove = (ev: MouseEvent) => onChange(rowAt(ev.clientY));
    const onUp = () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  return (
    <div className={styles.scrollTrack} ref={trackRef} onMouseDown={onMouseDown}>
      <div className={styles.scrollThumb} style={{ top: `${thumbTop}%`, height: `${thumbSize}%` }} />
    </div>
  );
};
