// "Level up" button in the action buttons row of the selected building's info
// panel (next to focus / relocate / delete). Shown only for zoned buildings that
// can still level up. Works on any zone: on a custom zone it also overrides a
// blocked level-up, and the zone's "keep design" rule still decides what the
// building becomes.
//
// The panel's title bar cannot be extended (the game declares it as a constant),
// so the button joins the panel's own row of action buttons instead.
import { getModule } from "cs2/modding";
import { FocusDisabled } from "cs2/input";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Tooltip } from "cs2/ui";
import { levelUpSelected, selectedCanLevelUp$ } from "../bindings";
// Same green arrow as the game's "levelling up" notification over buildings.
import levelUpIcon from "../images/LevelUp.svg";
import styles from "../styles.module.scss";

const ACTIONS_SECTION_KEY = "Game.UI.InGame.ActionsSection";

// The game's own action-button look (round icon button of that row).
const actionButtonTheme: any = getModule(
  "game-ui/game/components/selected-info-panel/selected-info-sections/shared-sections/actions-section/action-button.module.scss",
  "classes"
);

const LevelUpButton = () => {
  const canLevelUp = useValue(selectedCanLevelUp$);
  const { translate } = useLocalization();

  if (!canLevelUp) return null;

  const label = translate("CustomZones.LevelUpBuilding", "Level up this building") ?? "Level up this building";

  return (
    // Out of the gamepad focus tree: the actions row only hosts the game's own buttons.
    <FocusDisabled>
      <div className={styles.levelUpButtonSlot}>
        <Tooltip tooltip={label}>
          <Button variant="icon" theme={actionButtonTheme} src={levelUpIcon} onSelect={levelUpSelected} />
        </Tooltip>
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
      <LevelUpButton />
    </div>
  );
  return components;
};
