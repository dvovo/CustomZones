// Adds a native "Custom Zones" row to the zoning tool's options panel (next to
// "Tool Mode" / "Topography"), shown whenever the zoning tool is active:
//   [+]    create a new custom zone
//   [pen]  edit custom zones (opens the one selected in the toolbar, if any)
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
  requestNewZone,
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

  const newZone = () => {
    setPanelVisible(true);
    requestNewZone();
  };

  const editZones = () => {
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
          src={ICONS.plus}
          onSelect={newZone}
          tooltip={translate("CustomZones.NewZoneTooltip", "Create a new custom zone")}
        />
        <ToolButton
          src={ICONS.pen}
          selected={visible}
          onSelect={editZones}
          tooltip={translate("CustomZones.EditZonesTooltip", "Edit custom zones")}
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
