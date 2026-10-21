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

  export const Button: React.ComponentType<{
    variant?: string;
    className?: string;
    theme?: Record<string, string>;
    onSelect?: () => void;
    children?: React.ReactNode;
  }>;

  export const Dropdown: React.ComponentType<{
    theme?: Record<string, string>;
    content?: React.ReactNode;
    children?: React.ReactNode;
  }>;

  export const DropdownItem: React.ComponentType<{
    value?: unknown;
    closeOnSelect?: boolean;
    onChange?: () => void;
    children?: React.ReactNode;
  }>;

  export const DropdownToggle: React.ComponentType<{
    children?: React.ReactNode;
  }>;

  export const Tooltip: React.ComponentType<{
    tooltip?: React.ReactNode;
    children?: React.ReactNode;
  }>;
}
