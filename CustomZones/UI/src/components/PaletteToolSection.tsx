// Adds a native "Custom Zones" row to the zoning tool's options panel (next to
// "Tool Mode" / "Topography"), shown whenever the zoning tool is active, with a
// single button that opens or closes the Custom Zones panel (on the custom zone
// being painted, if any). Creating and editing zones is done in the panel.
// Pattern from Platter: wrap the vanilla MouseToolOptions and prepend our
// section to its rendered children.
import { ModuleRegistryExtend, getModule } from "cs2/modding";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { FocusDisabled } from "cs2/input";
import {
  activeZonePalette$,
  ICONS,
  panelVisible$,
  setPanelVisible,
  setSelectedZone,
  zoneToolActive$,
} from "../bindings";

// Vanilla components, so the row matches the game's own tool options exactly.
const Section: any = getModule(
  "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
  "Section"
);
const ToolButton: any = getModule(
  "game-ui/game/components/tool-options/tool-button/tool-button.tsx",
  "ToolButton"
);

const CustomZonesSection = () => {
  const visible = useValue(panelVisible$);
  const activePalette = useValue(activeZonePalette$);
  const { translate } = useLocalization();

  const togglePanel = () => {
    if (visible) {
      setPanelVisible(false);
      return;
    }

    // Open on the custom zone the player is painting with, if any.
    if (activePalette) {
      setSelectedZone(activePalette);
    }

    setPanelVisible(true);
  };

  return (
    <FocusDisabled>
      <Section title={translate("CustomZones.Title", "Custom Zones")}>
        <ToolButton
          src={ICONS.tab}
          selected={visible}
          onSelect={togglePanel}
          tooltip={translate("CustomZones.OpenPanelTooltip", "Open the Custom Zones panel")}
        />
      </Section>
    </FocusDisabled>
  );
};

// ModuleRegistry.extend callback: wraps MouseToolOptions and prepends our
// section to its children while the zoning tool is active.
export const ExtendMouseToolOptions: ModuleRegistryExtend = (Component: any) => {
  return (props: any) => {
    const toolActive = useValue(zoneToolActive$);
    const result: any = Component(props);

    if (toolActive && result?.props) {
      const children = result.props.children;
      const section = <CustomZonesSection key="CustomZones/Section" />;

      if (Array.isArray(children)) {
        children.unshift(section);
      } else if (children) {
        result.props.children = [section, children];
      } else {
        result.props.children = [section];
      }
    }

    return result;
  };
};
