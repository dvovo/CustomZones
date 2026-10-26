// Level controls in the selected building's info panel, for zoned buildings,
// next to the panel's own action buttons (focus / relocate / delete):
//   - "level up": the next level now, with the green arrow of the game's own
//     "levelling up" notification;
//   - "choose level": opens a small floating panel with the building's other
//     levels, to turn it into one of any level now, up or down (its own design
//     when it has that level).
// Works on any zone: on a custom zone it also overrides a blocked level-up, and
// the zone's chosen buildings decide what the building can become.
//
// The panel's title bar cannot be extended (the game declares it as a constant),
// so the controls join the panel's own row of action buttons instead.
import { useEffect, useRef, useState } from "react";
import { getModule } from "cs2/modding";
import { FocusDisabled } from "cs2/input";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Tooltip } from "cs2/ui";
import { levelUpSelected, selectedCanLevelUp$, selectedLevels$, setSelectedLevel } from "../bindings";
// Same green arrow as the game's "levelling up" notification over buildings.
import levelUpIcon from "../images/LevelUp.svg";
import chooseLevelIcon from "../images/ChooseLevel.svg";
import styles from "../styles.module.scss";

const ACTIONS_SECTION_KEY = "Game.UI.InGame.ActionsSection";

// The game's action-button look (round icon button of that row).
const actionButtonTheme: any = getModule(
  "game-ui/game/components/selected-info-panel/selected-info-sections/shared-sections/actions-section/action-button.module.scss",
  "classes"
);

type T = (key: string, fallback: string) => string;

const LevelControls = () => {
  const canLevelUp = useValue(selectedCanLevelUp$);
  const states = useValue(selectedLevels$);
  const { translate } = useLocalization();
  const t: T = (key, fallback) => translate(`CustomZones.${key}`, fallback) ?? fallback;
  const [open, setOpen] = useState(false);
  const slotRef = useRef<HTMLDivElement>(null);

  const hasLevels = states.length === 5;
  const current = states.indexOf(2) + 1;

  // Closes when another building is selected (its levels change).
  useEffect(() => setOpen(false), [current, states.join(",")]);

  // A click anywhere else closes the floating panel.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (slotRef.current && !slotRef.current.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", onDown, true);
    return () => window.removeEventListener("mousedown", onDown, true);
  }, [open]);

  if (!canLevelUp && !hasLevels) return null;

  return (
    // Out of the gamepad focus tree: the actions row only hosts the game's own buttons.
    <FocusDisabled>
      <div className={styles.levelButtons} ref={slotRef}>
        {canLevelUp && (
          <Tooltip tooltip={t("LevelUpBuilding", "Level up this building")}>
            <Button variant="icon" theme={actionButtonTheme} src={levelUpIcon} onSelect={levelUpSelected} />
          </Tooltip>
        )}
        {hasLevels && (
          <Tooltip tooltip={t("ChooseLevel", "Choose its level")}>
            <Button
              variant="icon"
              theme={actionButtonTheme}
              src={chooseLevelIcon}
              selected={open}
              onSelect={() => setOpen(!open)}
            />
          </Tooltip>
        )}
        {open && hasLevels && (
          <div className={styles.levelPopup}>
            <div className={styles.levelPopupTitle}>{`${t("ChooseLevelTitle", "Turn it into level")}`}</div>
            <div className={styles.levelPopupOptions}>
              {states.map((state, i) => {
                const level = i + 1;
                if (state === 2) return null;
                const tooltip =
                  state === 1
                    ? t("LevelSet", "Turn it into a building of this level now (its own design when it has this level).")
                    : t("LevelNone", "No building of this zone, lot size and goods fits at this level here (it would lose homes or be too tall for this spot).");
                return (
                  <Tooltip key={level} tooltip={tooltip}>
                    <div
                      className={state === 1 ? styles.levelSelectorOption : styles.levelSelectorMissing}
                      onClick={() => {
                        if (state !== 1) return;
                        setSelectedLevel(level);
                        setOpen(false);
                      }}
                    >
                      {`${level}`}
                    </div>
                  </Tooltip>
                );
              })}
            </div>
            <div className={styles.levelPopupCurrent}>{`${t("LevelCurrentShort", "Now")}: ${current}`}</div>
          </div>
        )}
      </div>
    </FocusDisabled>
  );
};

/** Extends the map of info-panel section components: wraps the actions row. */
export const extendSelectedInfoSections = (components: any) => {
  const Original = components?.[ACTIONS_SECTION_KEY];
  if (!Original) return components;

  components[ACTIONS_SECTION_KEY] = (props: any) => (
    <div className={styles.actionsRow}>
      <div className={styles.actionsRowOriginal}>
        <Original {...props} />
      </div>
      <LevelControls />
    </div>
  );
  return components;
};
