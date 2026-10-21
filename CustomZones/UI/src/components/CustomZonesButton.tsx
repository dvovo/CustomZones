// Toolbar button (top-left of the game UI), Platter toolButton style.
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button, Tooltip } from "cs2/ui";
import { panelVisible$, setPanelVisible } from "../bindings";
import styles from "../styles.module.scss";

export const CustomZonesButton = () => {
  const visible = useValue(panelVisible$);
  const { translate } = useLocalization();

  return (
    <Tooltip tooltip={translate("CustomZones.Title", "Custom Zones")}>
      <Button
        className={visible ? styles.toolbarButtonActive : styles.toolbarButton}
        variant="floating"
        onSelect={() => setPanelVisible(!visible)}
      >
        <img
          src="coui://uil/Standard/ColorPalette.svg"
          className={styles.toolbarButtonIcon}
        />
      </Button>
    </Tooltip>
  );
};
