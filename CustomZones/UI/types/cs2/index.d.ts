// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

declare module "cs2/api" {
  export interface Binding<T> {
    readonly __value?: T;
  }

  export function bindValue<T>(
    modId: string,
    name: string,
    fallback: T
  ): Binding<T>;

  export function useValue<T>(binding: Binding<T>): T;

  export function trigger(
    modId: string,
    name: string,
    ...args: unknown[]
  ): void;
}

declare module "cs2/input" {
  import * as React from "react";

  export const FocusDisabled: React.ComponentType<{
    children?: React.ReactNode;
  }>;
}

declare module "cs2/l10n" {
  export function useLocalization(): {
    translate(key: string, fallback?: string): string;
  };
}

declare module "cs2/modding" {
  import { ComponentType } from "react";

  export type ModuleRegistryExtend = <T extends ComponentType<any>>(
    curr: T
  ) => (props: any) => JSX.Element;

  export type AppendHookTargets =
    | "Menu"
    | "Editor"
    | "Game"
    | "GameTopLeft"
    | "GameTopRight"
    | "GameBottomRight"
    | "UniversalModMenu";

  export interface ModuleRegistry {
    get(modulePath: string, exportName: string): any;
    extend(
      modulePath: string,
      exportName: string,
      extendCb?: ModuleRegistryExtend
    ): void;
    append(
      target: AppendHookTargets,
      component: ComponentType<{}> | (() => JSX.Element),
      index?: number
    ): void;
  }

  export type ModRegistrar = (moduleRegistry: ModuleRegistry) => void;

  export const getModule: (modulePath: string, exportName: string) => any;
}

declare module "cs2/ui" {
  import * as React from "react";

  // Loose typings for the game's UI components (see the official modding
  // types for the full props). Extra props are allowed on purpose.
  type Props = { className?: string; children?: React.ReactNode; [key: string]: any };

  export const Button: React.ComponentType<
    Props & {
      variant?: "flat" | "primary" | "round" | "menu" | "icon" | "floating" | "text" | "default";
      selected?: boolean;
      disabled?: boolean;
      src?: string;
      tinted?: boolean;
      theme?: Record<string, string>;
      tooltipLabel?: React.ReactNode;
      onSelect?: () => void;
    }
  >;

  export const Panel: React.ComponentType<
    Props & {
      header?: React.ReactNode;
      footer?: React.ReactNode;
      contentClassName?: string;
      theme?: Record<string, string>;
      onClose?: () => void;
    }
  >;

  export const Scrollable: React.ComponentType<
    Props & { vertical?: boolean; horizontal?: boolean; trackVisibility?: "always" | "scrollable" | "reserve" }
  >;

  export const Icon: React.ComponentType<Props & { src: string; tinted?: boolean | string }>;

  export const Dropdown: React.ComponentType<
    Props & {
      theme?: Record<string, string>;
      content?: React.ReactNode;
      alignment?: "left" | "right";
      onToggle?: (visible: boolean) => void;
    }
  >;

  export const DropdownToggle: React.ComponentType<
    Props & { theme?: Record<string, string>; tooltip?: React.ReactNode; disabled?: boolean }
  >;

  export const Tooltip: React.ComponentType<
    Props & {
      tooltip?: React.ReactNode;
      direction?: "up" | "down" | "left" | "right";
      alignment?: "start" | "center" | "end";
      disabled?: boolean;
    }
  >;
}
