// Injects a native-looking "Custom Zones" section into the zone tool's
// left options panel (alongside "Herramientas" / "Topografía"), at the TOP and
// inside the panel background. Pattern proven by Platter: wrap the vanilla
// MouseToolOptions, then unshift our section into its rendered children.
import { ModuleRegistryExtend, getModule } from "cs2/modding";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { FocusDisabled } from "cs2/input";
import { painterToolActive$, panelVisible$, setPanelVisible } from "../bindings";

// Vanilla components, so the row matches the game's own tool options exactly.
const Section: any = getModule(
  "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
  "Section"
);
const ToolButton: any = getModule(
  "game-ui/game/components/tool-options/tool-button/tool-button.tsx",
  "ToolButton"
);

const PaletteSection = () => {
  const visible = useValue(panelVisible$);
  const { translate } = useLocalization();
  const label = translate("CustomZones.Title", "Custom Zones");

  return (
    <FocusDisabled>
      <Section title={label}>
        <ToolButton
          src="coui://uil/Standard/ColorPalette.svg"
          selected={visible}
          onSelect={() => setPanelVisible(!visible)}
          tooltip={label}
        />
      </Section>
    </FocusDisabled>
  );
};

// ModuleRegistry.extend callback: wraps MouseToolOptions and prepends our
// section to its children when the palette painter is the active tool.
export const ExtendMouseToolOptions: ModuleRegistryExtend = (Component: any) => {
  return (props: any) => {
    const toolActive = useValue(painterToolActive$);
    const result: any = Component(props);

    if (toolActive && result?.props) {
      const children = result.props.children;
      const section = <PaletteSection key="CustomZones/Section" />;

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
