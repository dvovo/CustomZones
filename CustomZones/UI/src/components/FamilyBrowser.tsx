// Building browser of the zone editor. One card per building (one style at one
// lot size); its levels L1-L5 are a strip under the picture: hover a level to
// see it, click it to let that level be born in the zone. The magnifier opens a
// large preview where every level can be looked at and placed in the city. Virtual scrolling:
// C# only sends the rows on screen; this component measures how many fit.
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Dropdown, DropdownToggle, Tooltip } from "cs2/ui";
import {
  bigInstalled$,
  families$,
  FamilyEntry,
  familyCount$,
  filterLevel$,
  filterLotWidth$,
  filterPack$,
  filterSearch$,
  filterSelectedOnly$,
  filterGrowable$,
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
  ZoneEntry,
} from "../bindings";
import { Checkbox, DropdownItem, gameDropdownTheme } from "./vanilla";
import styles from "../styles.module.scss";

// Card footprint in rem, including the gap (must match styles.module.scss).
const CARD_WIDTH = 120;
const CARD_HEIGHT = 152;

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

  // ----- How many cards fit: measured from the grid area -----
  const gridRef = useRef<HTMLDivElement>(null);
  // Invisible 100rem-wide element: its width in px is the game's real rem scale
  // (the root font size does not tell it in the game's UI).
  const remProbeRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const measure = () => {
      const grid = gridRef.current;
      const probe = remProbeRef.current;
      if (!grid || !probe) return;
      const pxPerRem = probe.getBoundingClientRect().width / 100;
      if (!(pxPerRem > 0)) return;
      const rect = grid.getBoundingClientRect();
      const fitColumns = Math.max(2, Math.floor(rect.width / pxPerRem / CARD_WIDTH));

      // The game's panel does not hand its spare height to our sections, so the
      // grid takes everything from its top down to the panel's bottom edge.
      const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
      const available = (viewportHeight - rect.top) / pxPerRem - PANEL_BOTTOM - BOTTOM_PADDING;
      // At least two rows: the grid scrolls, it is never a single strip.
      const fitRows = Math.max(2, Math.floor(available / CARD_HEIGHT));

      if (fitColumns !== columns) setGridColumns(fitColumns);
      if (fitRows !== rows) setGridRows(fitRows);
    };

    measure();
    // The panel settles over a couple of frames (zone settings, fonts): measure again.
    const late = setTimeout(measure, 150);
    window.addEventListener("resize", measure);
    return () => {
      clearTimeout(late);
      window.removeEventListener("resize", measure);
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
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitleRow}>
          <span className={styles.sectionTitle}>{t("Buildings", "Buildings")}</span>
          <BrowserHelp t={t} />
        </div>
        {!bigInstalled && (
          <Tooltip
            tooltip={t(
              "BigHint",
              "Install Building Icon Generator to see real pictures of every building."
            )}
          >
            <img className={styles.inlineIconDim} src={ICONS.place} />
          </Tooltip>
        )}
      </div>

      <Filters t={t} />

      <div className={styles.resultsRow}>
        <span className={styles.resultsCount}>{`${count} ${t("BuildingsShown", "buildings")}`}</span>
        <Button className={styles.headerButton} variant="flat" onSelect={() => setAllVisible(true)}>
          <img className={styles.buttonIcon} src={ICONS.check} />
          <span>{t("SelectAll", "Add all shown")}</span>
        </Button>
        <Button className={styles.headerButton} variant="flat" onSelect={() => setAllVisible(false)}>
          <img className={styles.buttonIcon} src={ICONS.clear} />
          <span>{t("DeselectAll", "Remove all shown")}</span>
        </Button>
      </div>

      <div className={styles.gridArea} style={{ height: `${rows * CARD_HEIGHT}rem` }}>
        <div className={styles.grid} ref={gridRef} onWheel={onWheel}>
          {families.slice(0, columns * rows).map((family) => (
            <FamilyCard key={family.key} family={family} t={t} />
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

const Filters = (props: { t: (key: string, fallback: string) => string }) => {
  const { t } = props;
  const lotWidth = useValue(filterLotWidth$);
  const level = useValue(filterLevel$);
  const pack = useValue(filterPack$);
  const search = useValue(filterSearch$);
  const selectedOnly = useValue(filterSelectedOnly$);
  const growable = useValue(filterGrowable$);
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

  const levelLabel = (l: number) => (l === 0 ? t("AnyLevel", "Any level") : `${t("Level", "Level")} ${l}`);

  const packLabel = (p: string) =>
    p === PACK_ALL ? t("AllSources", "All sources") : p === PACK_NONE ? t("BaseGame", "Base game") : p === PACK_MOD ? t("FromMods", "From mods") : p;

  const packOptions = [PACK_ALL, PACK_NONE, PACK_MOD, ...packs];
  const packIcon = (p: string) =>
    p === PACK_NONE ? ICONS.baseGame : p === PACK_MOD ? ICONS.mod : packIcons[packs.indexOf(p)] || undefined;

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

      <div className={styles.filterRow}>
        <Dropdown
          theme={gameDropdownTheme}
          content={WIDTH_OPTIONS.map((w) => (
            <DropdownItem key={w} value={w} selected={w === lotWidth} onChange={() => setFilterLotWidth(w)}>
              {widthLabel(w)}
            </DropdownItem>
          ))}
        >
          <DropdownToggle className={styles.dropdown}>{widthLabel(lotWidth)}</DropdownToggle>
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={LEVEL_OPTIONS.map((l) => (
            <DropdownItem key={l} value={l} selected={l === level} onChange={() => setFilterLevel(l)}>
              {levelLabel(l)}
            </DropdownItem>
          ))}
        >
          <DropdownToggle className={styles.dropdown}>{levelLabel(level)}</DropdownToggle>
        </Dropdown>

        <Dropdown
          theme={gameDropdownTheme}
          content={packOptions.map((p) => (
            <DropdownItem
              key={p || "all"}
              value={p}
              icon={packIcon(p)}
              selected={p === pack}
              onChange={() => setFilterPack(p)}
            >
              {packLabel(p)}
            </DropdownItem>
          ))}
        >
          <DropdownToggle className={styles.dropdown}>{packLabel(pack)}</DropdownToggle>
        </Dropdown>

        <div className={styles.checkboxField}>
          <Checkbox checked={selectedOnly} onChange={(v: boolean) => setFilterSelectedOnly(v)} />
          <span onClick={() => setFilterSelectedOnly(!selectedOnly)}>{t("SelectedOnly", "In this zone")}</span>
        </div>

        <div className={styles.checkboxField}>
          <Checkbox checked={growable} onChange={(v: boolean) => setFilterGrowable(v)} />
          <Tooltip tooltip={t("GrowableHint", "Only buildings the city has demand for right now (the ones that can grow).")}>
            <span onClick={() => setFilterGrowable(!growable)}>{t("Growable", "Can grow now")}</span>
          </Tooltip>
        </div>

        <Tooltip tooltip={t("ResetFilters", "Reset filters")}>
          <Button className={styles.iconButton} variant="icon" onSelect={resetFilters}>
            <img src={ICONS.reset} />
          </Button>
        </Tooltip>
      </div>
    </div>
  );
};

// ---------------------------------------------------------------------------
// Card
// ---------------------------------------------------------------------------

const LEVELS = [1, 2, 3, 4, 5];

const FamilyCard = (props: { family: FamilyEntry; t: (key: string, fallback: string) => string }) => {
  const { family, t } = props;
  // Level under the mouse on the strip (shown instead of the card's own level).
  const [hoverLevel, setHoverLevel] = useState<number | null>(null);
  const [failed, setFailed] = useState(false);

  const level = hoverLevel ?? family.shownLevel;
  const thumbnail = family.levelThumbnails[level - 1] || family.thumbnail;
  useEffect(() => setFailed(false), [thumbnail]);
  const picture = !failed && thumbnail ? thumbnail : family.fallbackThumbnail;

  const levelChosen = family.selectedLevels.includes(level);
  const anyChosen = family.selectedLevels.length > 0;

  return (
    <div
      className={anyChosen ? styles.cardSelected : styles.card}
      onClick={() => toggleFamilyLevel(family.key, level)}
      onMouseLeave={() => setHoverLevel(null)}
    >
      <div className={styles.cardPicture}>
        <img
          className={picture === family.fallbackThumbnail ? styles.cardFallback : styles.cardImage}
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
          className={styles.cardPlace}
          onClick={(e) => {
            e.stopPropagation();
            openPreview({ family, level });
          }}
        >
          <Tooltip tooltip={t("Preview", "View it large")}>
            <img src={ICONS.zoom} />
          </Tooltip>
        </div>
      </div>
      <div className={styles.cardName}>{family.name}</div>
      <CardMeta family={family} t={t} />
      <div className={styles.levelStrip}>
        {LEVELS.map((l) => {
          const exists = !!family.levelThumbnails[l - 1];
          const chosen = family.selectedLevels.includes(l);
          const className = !exists
            ? styles.levelPillMissing
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
              onMouseEnter={() => exists && setHoverLevel(l)}
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
const CardMeta = (props: { family: FamilyEntry; t: T }) => {
  const { family, t } = props;
  const goodsName = useGoodsName(family.goods);

  if (!family.goods) {
    return <div className={styles.cardMeta}>{family.lotSizes.join(" · ")}</div>;
  }

  return (
    <Tooltip tooltip={demandText(family, goodsName, t)}>
      <div className={styles.cardMetaRow}>
        <span className={styles.cardMetaLot}>{`${family.lotSizes.join(" · ")} ·`}</span>
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
          <div className={styles.helpLine}>{t("HelpCard", "Click a card to let the level it shows grow in this zone (a tick appears).")}</div>
          <div className={styles.helpLine}>{t("HelpLevels", "The numbers 1-5 are its levels: click one to add or remove that level. Filled = grows in this zone, outlined = the one on the picture, dim = it does not exist.")}</div>
          <div className={styles.helpLine}>{t("HelpZoom", "The magnifier shows it large, with every level, and lets you place one in the city.")}</div>
          <div className={styles.helpLine}>{t("HelpGoods", "Under the name: its lot size and what it sells, makes or stores.")}</div>
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

export const FamilyPreview = () => {
  const { translate } = useLocalization();
  const t = (key: string, fallback: string) => translate(`CustomZones.${key}`, fallback) ?? fallback;
  const [request, setRequest] = useState<{ family: FamilyEntry; level: number } | null>(null);
  const [level, setLevel] = useState(1);
  const [failed, setFailed] = useState(false);
  const families = useValue(families$);

  useEffect(
    () =>
      onPreviewRequested((r) => {
        setRequest(r);
        if (r) setLevel(r.level);
      }),
    []
  );

  // Escape closes it.
  useEffect(() => {
    if (!request) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setRequest(null);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [request]);

  // Latest data of the building while it is on screen (chosen levels change).
  const family = request ? families.find((f) => f.key === request.family.key) ?? request.family : undefined;
  const large = family ? family.levelPictures[level - 1] || "" : "";
  useEffect(() => setFailed(false), [large, family?.key]);
  const goodsName = useGoodsName(family?.goods ?? "");

  if (!family) return null;

  const thumbnail = family.levelThumbnails[level - 1] || family.thumbnail;
  const picture = !failed && large ? large : thumbnail || family.fallbackThumbnail;
  const chosen = family.selectedLevels.includes(level);
  const close = () => setRequest(null);

  return (
    <div className={styles.previewBackdrop} onClick={close}>
      <div className={styles.preview} onClick={(e) => e.stopPropagation()}>
        <div className={styles.previewHeader}>
          <div className={styles.previewHeading}>
            <div className={styles.previewTitle}>{`${family.name} · ${t("Level", "Level")} ${level}`}</div>
            <div className={styles.previewMeta}>
              {`${t("LotSize", "Lot size")} ${family.lotSizes.join(", ")} · ${t("Levels", "Levels")} ${levelRange(family)}${family.theme ? ` · ${family.theme}` : ""}`}
            </div>
          </div>
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
          <img className={styles.previewImage} src={picture} onError={() => setFailed(true)} />
        </div>

        <div className={styles.previewLevels}>
          {family.levelThumbnails.map((thumb, i) => {
            const l = i + 1;
            const levelChosen = family.selectedLevels.includes(l);
            const className = !thumb
              ? styles.previewLevelMissing
              : l === level
              ? levelChosen
                ? styles.previewLevelShownChosen
                : styles.previewLevelShown
              : levelChosen
              ? styles.previewLevelChosen
              : styles.previewLevel;
            return (
              <div key={l} className={className} onClick={() => thumb && setLevel(l)}>
                {thumb ? <img className={styles.previewLevelImage} src={thumb} /> : <div className={styles.previewLevelImage} />}
                <span>{`${t("Level", "Level")} ${l}`}</span>
              </div>
            );
          })}
        </div>

        <div className={styles.previewActions}>
          <div className={styles.checkboxField}>
            <Checkbox checked={chosen} onChange={() => toggleFamilyLevel(family.key, level)} />
            <span onClick={() => toggleFamilyLevel(family.key, level)}>
              {t("GrowsInZone", "This level grows in this zone")}
            </span>
          </div>
          <Button
            className={styles.primaryButton}
            variant="flat"
            onSelect={() => {
              close();
              placeFamily(family.key, level);
            }}
          >
            <img className={styles.buttonIcon} src={ICONS.place} />
            <span>{t("PlaceLevel", "Place it in the city")}</span>
          </Button>
        </div>
      </div>
    </div>
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
