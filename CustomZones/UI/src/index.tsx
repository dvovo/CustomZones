// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// CustomZones UI entry point.
// Registration pattern follows FindIt's index.tsx (ModRegistrar).
import { ModRegistrar } from "cs2/modding";
import { ExtendMouseToolOptions } from "./components/PaletteToolSection";
import { CustomZonesPanel } from "./components/CustomZonesPanel";
import { extendSelectedInfoSections } from "./components/LevelUpButton";

const register: ModRegistrar = (moduleRegistry) => {
  // "Custom Zones" row in the zoning tool's options (next to "Tool Mode" and
  // "Topography"), with the button that opens the panel.
  moduleRegistry.extend(
    "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
    "MouseToolOptions",
    ExtendMouseToolOptions
  );

  // Main panel rendered at game level (hidden until PanelVisible is true).
  moduleRegistry.append("Game", CustomZonesPanel);

  // Level buttons in the selected building's info panel (actions row).
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

  // No thumbnail pre-loading: asking for every building picture at once would
  // delay the ones on screen, which load fast enough on demand.
};

export default register;
