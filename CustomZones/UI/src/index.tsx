// CustomZones UI entry point.
// Registration pattern follows FindIt's index.tsx (ModRegistrar).
import { ModRegistrar } from "cs2/modding";
import { ExtendMouseToolOptions } from "./components/PaletteToolSection";
import { CustomZonesPanel } from "./components/CustomZonesPanel";
import { extendSelectedInfoSections } from "./components/LevelUpButton";

const register: ModRegistrar = (moduleRegistry) => {
  // Inject our palette section at the top of the zone tool's left options
  // panel (inside the panel background, next to "Herramientas" / "Topografía").
  // It self-hides unless the palette painter is the active tool, and is the
  // ONLY way to open the palette panel.
  moduleRegistry.extend(
    "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
    "MouseToolOptions",
    ExtendMouseToolOptions
  );

  // Main panel rendered at game level (hidden until PanelVisible is true).
  moduleRegistry.append("Game", CustomZonesPanel);

  // "Level up" button in the selected building's info panel (actions row).
  // Guarded: a change in the game's UI must never take the rest of the mod down.
  try {
    moduleRegistry.extend(
      "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx",
      "selectedInfoSectionComponents",
      extendSelectedInfoSections as any
    );
  } catch (e) {
    console.error("CustomZones: could not add the level-up button", e);
  }

  // NOTE: no thumbnail pre-cacher. Warming all ~9000 thumbnail://ThumbnailCamera
  // URLs at once FLOODS the game's ThumbnailCamera queue, so the actually-visible
  // grid cards get starved and stay on the zone placeholder. FindIt's warmer is
  // likewise disabled — the renders resolve fast enough on demand.
};

export default register;
