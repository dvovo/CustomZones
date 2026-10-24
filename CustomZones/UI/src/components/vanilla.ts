// The game's own UI pieces that the modding API does not export directly.
// Taken from the game's module registry, so they look and sound exactly like
// the rest of the game.
import { getModule } from "cs2/modding";

/** Item of a vanilla Dropdown (props: value, selected, onChange, closeOnSelect, icon). */
export const DropdownItem: any = getModule(
  "game-ui/common/input/dropdown/items/dropdown-item.tsx",
  "DropdownItem"
);

/** Vanilla checkbox (props: checked, onChange(checked), disabled, className). */
export const Checkbox: any = getModule(
  "game-ui/common/input/toggle/checkbox/checkbox.tsx",
  "Checkbox"
);

/** Theme used by the in-game dropdowns. */
export const gameDropdownTheme: any = getModule(
  "game-ui/game/themes/game-dropdown.module.scss",
  "classes"
);
