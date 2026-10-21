// Main panel: floating, draggable window (FindIt/Platter look) using the
// game's own design tokens. Left column = saved palettes submenu, right
// column = building browser or district assignment tab.
import { useRef, useState } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button } from "cs2/ui";
import {
  panelVisible$,
  palettes$,
  selectedPalette$,
  setPanelVisible,
  setSelectedPalette,
  createPalette,
  deletePalette,
  duplicatePalette,
  renamePalette,
  PaletteEntry,
  ZoneCategory,
  DensityType,
} from "../bindings";
import { PaletteEditor } from "./PaletteEditor";
import styles from "../styles.module.scss";

// Palette types the user can create. A palette is exactly one area type;
// density only matters for residential (its demand is split low/med/high).
const CATEGORY_LABELS: Record<number, string> = {
  [ZoneCategory.Residential]: "Residential",
  [ZoneCategory.Commercial]: "Commercial",
  [ZoneCategory.Industrial]: "Industrial",
  [ZoneCategory.Office]: "Office",
};
const RESIDENTIAL_DENSITIES: { density: DensityType; label: string }[] = [
  { density: DensityType.Low, label: "Low" },
  { density: DensityType.Medium, label: "Medium" },
  { density: DensityType.High, label: "High" },
];

export const CustomZonesPanel = () => {
  const visible = useValue(panelVisible$);
  const palettes = useValue(palettes$);
  const selectedId = useValue(selectedPalette$);
  const { translate } = useLocalization();
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState("");
  // New-palette flow: null = idle; otherwise the chosen category awaiting a
  // density pick (residential) or immediate create (others).
  const [creatingCategory, setCreatingCategory] = useState<number | null>(null);

  const beginCreate = (category: number) => {
    if (category === ZoneCategory.Residential) {
      setCreatingCategory(category); // wait for density choice
    } else {
      createPalette("", category, DensityType.Any);
      setCreatingCategory(null);
    }
  };

  const finishResidentialCreate = (density: DensityType) => {
    createPalette("", ZoneCategory.Residential, density);
    setCreatingCategory(null);
  };

  // ----- Window dragging (cohtml supports standard mouse events) -----
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const [size, setSize] = useState({ width: 860, height: 660 });
  const dragOrigin = useRef<{ mx: number; my: number; x: number; y: number } | null>(null);
  const resizeOrigin = useRef<{
    mx: number;
    my: number;
    width: number;
    height: number;
  } | null>(null);
  const windowRef = useRef<HTMLDivElement>(null);

  const onHeaderMouseDown = (e: React.MouseEvent) => {
    const rect = windowRef.current?.getBoundingClientRect();
    if (!rect) return;
    dragOrigin.current = { mx: e.clientX, my: e.clientY, x: rect.left, y: rect.top };

    const onMove = (ev: MouseEvent) => {
      if (!dragOrigin.current) return;
      setPos({
        x: dragOrigin.current.x + (ev.clientX - dragOrigin.current.mx),
        y: dragOrigin.current.y + (ev.clientY - dragOrigin.current.my),
      });
    };
    const onUp = () => {
      dragOrigin.current = null;
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const onResizeMouseDown = (e: React.MouseEvent) => {
    e.stopPropagation();
    resizeOrigin.current = {
      mx: e.clientX,
      my: e.clientY,
      width: size.width,
      height: size.height,
    };

    const onMove = (ev: MouseEvent) => {
      if (!resizeOrigin.current) return;

      setSize({
        width: Math.max(720, resizeOrigin.current.width + ev.clientX - resizeOrigin.current.mx),
        height: Math.max(520, resizeOrigin.current.height + ev.clientY - resizeOrigin.current.my),
      });
    };

    const onUp = () => {
      resizeOrigin.current = null;
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };

    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  if (!visible) return null;

  const commitRename = () => {
    if (renamingId && renameText.trim().length > 0) {
      renamePalette(renamingId, renameText.trim());
    }
    setRenamingId(null);
  };

  return (
    <div
      ref={windowRef}
      className={styles.window}
      style={{
        ...(pos ? { left: pos.x + "px", top: pos.y + "px" } : {}),
        width: size.width + "rem",
        height: size.height + "rem",
      }}
    >
      {/* ---------- Draggable header ---------- */}
      <div className={styles.header} onMouseDown={onHeaderMouseDown}>
        <img className={styles.headerIcon} src="coui://uil/Standard/ColorPalette.svg" />
        <span className={styles.headerTitle}>
          {translate("CustomZones.Title", "Custom Zones")}
        </span>
        <Button
          variant="icon"
          className={styles.closeButton}
          onSelect={() => setPanelVisible(false)}
        >
          <img src="coui://uil/Standard/XClose.svg" />
        </Button>
      </div>

      <div className={styles.body}>
        {/* ---------- Submenu: saved palettes ---------- */}
        <div className={styles.sidebar}>
          {creatingCategory === null ? (
            <Button
              className={styles.newPaletteButton}
              variant="flat"
              onSelect={() => setCreatingCategory(-1)}
            >
              <img src="coui://uil/Standard/Plus.svg" />
              <span>{translate("CustomZones.NewPalette", "New palette")}</span>
            </Button>
          ) : creatingCategory === -1 ? (
            // Step 1: pick the area type.
            <div className={styles.createForm}>
              <div className={styles.createLabel}>
                {translate("CustomZones.PickType", "Palette type")}
              </div>
              {[
                ZoneCategory.Residential,
                ZoneCategory.Commercial,
                ZoneCategory.Industrial,
                ZoneCategory.Office,
              ].map((cat) => (
                <Button
                  key={cat}
                  variant="flat"
                  className={styles.createOption}
                  onSelect={() => beginCreate(cat)}
                >
                  {CATEGORY_LABELS[cat]}
                </Button>
              ))}
              <Button
                variant="flat"
                className={styles.createCancel}
                onSelect={() => setCreatingCategory(null)}
              >
                {translate("CustomZones.Cancel", "Cancel")}
              </Button>
            </div>
          ) : (
            // Step 2 (residential only): pick the density bucket.
            <div className={styles.createForm}>
              <div className={styles.createLabel}>
                {translate("CustomZones.PickDensity", "Residential density")}
              </div>
              {RESIDENTIAL_DENSITIES.map((d) => (
                <Button
                  key={d.density}
                  variant="flat"
                  className={styles.createOption}
                  onSelect={() => finishResidentialCreate(d.density)}
                >
                  {d.label}
                </Button>
              ))}
              <Button
                variant="flat"
                className={styles.createCancel}
                onSelect={() => setCreatingCategory(null)}
              >
                {translate("CustomZones.Cancel", "Cancel")}
              </Button>
            </div>
          )}

          <div className={styles.paletteList}>
            {palettes.map((p: PaletteEntry) => (
              <div
                key={p.id}
                className={
                  p.id === selectedId
                    ? styles.paletteItemSelected
                    : styles.paletteItem
                }
              >
                {renamingId === p.id ? (
                  <input
                    className={styles.renameInput}
                    value={renameText}
                    autoFocus
                    onChange={(e) => setRenameText(e.target.value)}
                    onBlur={commitRename}
                    onKeyDown={(e) => e.key === "Enter" && commitRename()}
                  />
                ) : (
                  <>
                    {/* Selection via a vanilla Button: its onSelect fires
                        reliably in cohtml (raw div onClick does not). */}
                    <Button
                      variant="flat"
                      className={styles.paletteSelect}
                      onSelect={() => setSelectedPalette(p.id)}
                    >
                      <div className={styles.paletteName}>{p.name}</div>
                      <div className={styles.paletteMeta}>
                        {CATEGORY_LABELS[p.category] ?? "?"}
                        {p.category === ZoneCategory.Residential
                          ? " · " +
                            (RESIDENTIAL_DENSITIES.find(
                              (d) => d.density === p.density
                            )?.label ?? "")
                          : ""}
                        {" — "}
                        {p.buildingCount}{" "}
                        {translate("CustomZones.Buildings", "buildings")}
                      </div>
                    </Button>
                    <div className={styles.paletteActions}>
                      <Button
                        variant="icon"
                        onSelect={() => {
                          setRenamingId(p.id);
                          setRenameText(p.name);
                        }}
                      >
                        <img src="coui://uil/Standard/Pencil.svg" />
                      </Button>
                      <Button
                        variant="icon"
                        onSelect={() => duplicatePalette(p.id)}
                      >
                        <img src="coui://uil/Standard/RectangleCopy.svg" />
                      </Button>
                      <Button
                        variant="icon"
                        onSelect={() => deletePalette(p.id)}
                      >
                        <img src="coui://uil/Standard/Trash.svg" />
                      </Button>
                    </div>
                  </>
                )}
              </div>
            ))}
          </div>
        </div>

        {/* ---------- Main area ---------- */}
        <div className={styles.main}>
          <div className={styles.hintBar}>
            {translate(
              "CustomZones.PaintHint",
              "Each palette is a paintable zone: find it in the Palettes tab of the zoning toolbar and paint with the vanilla zoning tool."
            )}
          </div>

          {selectedId ? (
            <PaletteEditor />
          ) : (
            <div className={styles.emptyState}>
              {translate(
                "CustomZones.NoPaletteSelected",
                "Create or select a palette on the left to start choosing buildings."
              )}
            </div>
          )}
        </div>
      </div>

      <div className={styles.resizeHandle} onMouseDown={onResizeMouseDown} />
    </div>
  );
};
