# CustomZones — Mod para Cities: Skylines 2

Mod que permite crear **"paletas" de edificios** (listas blancas seleccionadas manualmente) y asignarlas a áreas de zonificación, de modo que en esa área **solo crezcan los edificios que tú has elegido** y ningún otro.

---

## ¿Qué hace?

1. **Crea paletas**: una paleta es una plantilla con nombre que contiene los edificios spawnables que quieres permitir. Las paletas se guardan globalmente (disponibles en todas tus partidas) y aparecen en un submenú propio.
2. **Selecciona edificios con filtros**: el panel de edición incluye un navegador de todos los edificios zonificables del juego (y de assets de mods/regiones instalados) con filtros de:
   - **Tipo de zona**: Residencial, Mixta, Comercial, Industrial, Oficina.
   - **Densidad**: Low, Row (adosados), Medium, High, Signature (singulares).
   - **Tamaño de parcela**: ancho y fondo (1–9, "10+").
   - **Nivel** del edificio (1–5).
   - **Tema/región** (European, North American, packs de regiones…).
   - **Búsqueda por texto** y vista de "solo seleccionados".
3. **Asigna la paleta a un área**: las áreas se gestionan mediante **distritos** del juego. En la pestaña *Distritos* eliges qué paleta aplica a cada distrito. La asignación se guarda dentro del savegame.
4. **Aplica la restricción**: cuando el juego hace crecer un edificio dentro de un distrito con paleta, el mod lo valida; si el edificio no está en la lista blanca, se elimina inmediatamente y el juego vuelve a intentar el spawn hasta que aparece uno permitido.

## Cómo funciona por dentro (arquitectura v2: zonas pintables)

**Cada paleta es un tipo de zona real del juego.** Es el mismo mecanismo nativo que usan los region packs (EU/NA/UK/FR/DE/JP): el `ZoneSpawnSystem` vanilla solo considera los prefabs de edificio cuya zona coincide con la pintada en la celda (agrupación por `BuildingSpawnGroupData`). Al convertir la paleta en zona, **el juego solo recorre los edificios de la paleta de forma nativa**, sin filtrado posterior ni intentos desperdiciados.

`PaletteZoneSystem` (calcado del `P_PrefabsCreateSystem` de Platter, que demuestra que cada paso funciona):

1. Crea **una pestaña "Paletas"** en la barra de zonificación: un `UIAssetCategoryPrefab` enganchado al mismo `m_Menu` que la categoría vanilla "ZonesOffice", con icono `ColorPalette` de UIL.
2. Por cada paleta, **clona un `ZonePrefab`** (`PrefabBase.Clone`) tomando como base la zona del primer edificio permitido (así hereda `ZoneProperties` coherentes: economía, tipo de área, oficina...). Le asigna un **color estable derivado del hash** de la paleta para reconocer las áreas pintadas, y un `UIObject` con `m_Group` = nuestra pestaña.
3. Por cada edificio permitido, **clona el `BuildingPrefab`** y apunta su `SpawnableBuilding.m_ZoneType` a la zona-paleta. Tras la inicialización del `PrefabSystem`, el job de spawn vanilla lo agrupa bajo el tipo de zona de la paleta.
4. Todo se crea en `OnGamePreload` —**antes de deserializar el savegame**— con nombres deterministas derivados de `palettes.json`, así los edificios construidos con clones cargan bien en sesiones futuras (mismo punto de instalación que Platter).

`SpawnFilterSystem` queda reducido a una **red de seguridad mínima**: como los prefabs no se pueden des-registrar en runtime, si quitas un edificio de una paleta en mitad de la partida su clon sigue en el pool; este sistema elimina esos spawns al nacer (caso raro, coste despreciable).

**Limitaciones documentadas**: si borras una paleta, su zona sigue existiendo durante la sesión pero deja de generar nada (sus clones quedan vetados); las paletas que mezclan tipos de área (R+C+I) heredan la economía del primer edificio, así que lo ideal es una paleta por tipo de zona; y si desinstalas el mod, los edificios de paleta de tus partidas guardadas desaparecerán al cargar (el juego poda prefabs ausentes).

## Miniaturas e iconos (corregido)

Las miniaturas usan la **API del juego** `ImageSystem.GetThumbnail(prefab)` con fallback `GetIconOrGroupIcon(zona)` vía `onError` en la UI — exactamente como Find It. **Asset Icon Library no expone ninguna URL `coui://ail/`**: funciona sustituyendo las miniaturas del propio juego, así que con AIL instalado las imágenes mejoran solas sin código. Los iconos de los filtros de categoría se resuelven en runtime desde zonas reales (binding `CategoryIcons`), eliminando los 404 de nombres de archivo que varían entre versiones. Iconos UIL verificados contra el repositorio: `ColorPalette`, `RectangleCopy`, `BuildingZoneSignature`, `LowLevel`, `Row`, `MediumLevel`, `HighLevel`, `StarAll`, `Plus`, `Pencil`, `Trash`, `Reset`, `XClose`, `Checkmark`, `ArrowUp`, `ArrowDown`.

## Inventario de iconos e imágenes

**El mod no incluye ningún icono ni imagen propios**: todo lo visual procede del juego base o de las dos librerías de iconos. Desglose completo:

| Uso | Recurso | Origen |
|---|---|---|
| Botón principal del mod | `coui://uil/Colored/Zones.svg` | Unified Icon Library |
| Crear paleta (+) | `coui://uil/Standard/Plus.svg` | Unified Icon Library |
| Renombrar paleta | `coui://uil/Standard/Pencil.svg` | Unified Icon Library |
| Duplicar paleta | `coui://uil/Standard/Copy.svg` | Unified Icon Library |
| Eliminar paleta | `coui://uil/Standard/Trash.svg` | Unified Icon Library |
| Cerrar panel | `coui://uil/Standard/XClose.svg` | Unified Icon Library |
| Reset de filtros | `coui://uil/Standard/Reset.svg` | Unified Icon Library |
| Marca de seleccionado | `coui://uil/Colored/Checkmark.svg` | Unified Icon Library |
| Filtro "cualquiera" (zona/densidad) | `coui://uil/Standard/StarAll.svg` | Unified Icon Library |
| Densidades Low/Row/Medium/High/Signature | `coui://uil/Standard/{LowLevel,Row,MediumLevel,HighLevel,Star}.svg` | Unified Icon Library |
| Filtros de tipo de zona (R/Mixta/C/I/Oficina) | `Media/Game/Icons/Zone*.svg` | **Juego base** (atlas de UI vanilla) |
| Miniaturas de edificios | `coui://ail/{PrefabName}.png` | Asset Icon Library |
| Fallback de miniaturas | `ImageSystem.GetIconOrGroupIcon` → icono de la zona | Juego base |

El único recurso gráfico **propio** que tendrás que crear es ajeno a la interfaz: la **miniatura del mod para PDX Mods** (`Properties/Thumbnail.png`, referenciada al publicar en `PublishConfiguration.xml`) y las capturas de pantalla de la página del mod. Opcionalmente, si quieres una identidad visual propia, puedes sustituir el icono del botón principal por un SVG tuyo en `UI/src/` (webpack lo embebe); ahora mismo reutiliza el de UIL para no añadir assets.

## Estructura del proyecto

```
CustomZones/
├── CustomZones.sln
└── CustomZones/
    ├── Mod.cs                      # IMod: registro de sistemas y dependencias
    ├── Setting.cs                  # Opciones del mod
    ├── Components/Components.cs    # PaletteDistrict (ISerializable), PaletteChecked
    ├── Domain/
    │   ├── DomainModel.cs          # Enums, PaletteDefinition, BrowserFilters
    │   └── UIBinding/UIEntries.cs  # DTOs para la UI (IJsonWritable)
    ├── Systems/
    │   ├── BuildingIndexSystem.cs  # Índice de prefabs spawnables
    │   ├── SpawnFilterSystem.cs    # Enforcement de la lista blanca
    │   └── PaletteUISystem.cs      # Bindings y triggers de la UI
    ├── Storage/PaletteStorage.cs   # CRUD de paletas + JSON
    ├── Utilities/                  # ExtendedUISystemBase, GenericUIWriter/Reader, LocaleHelper
    ├── Locale/                     # en-US.json, es-ES.json
    ├── Properties/PublishConfiguration.xml
    └── UI/                         # React/TS (webpack → CSII_USERDATAPATH/Mods/CustomZones)
```

## Compilación

### Requisitos

1. **Cities: Skylines II** instalado con el **Modding Toolchain** activado (Opciones → Modding en el juego, o instalar el toolchain desde el launcher). Esto define las variables de entorno:
   - `CSII_TOOLPATH` → carpeta del toolchain (contiene `Mod.props` / `Mod.targets` y las DLLs del juego).
   - `CSII_USERDATAPATH` → carpeta de datos de usuario del juego.
2. **.NET SDK** (el proyecto es `net48`, lo compila el toolchain con MSBuild).
3. **Node.js + npm** para la UI.

### Pasos

```bash
# 1. UI: instalar dependencias (una sola vez)
cd CustomZones/UI
npm install

# 2. Compilar el mod (el target BuildUI ejecuta npm run build automáticamente)
cd ..
dotnet build
```

El build copia la DLL y la UI compilada a `%CSII_USERDATAPATH%/Mods/CustomZones`, lista para probar en el juego.

> **Nota sobre los types de `cs2/*`**: la UI importa módulos del juego (`cs2/api`, `cs2/modding`, `cs2/ui`...). Las definiciones de tipos (`.d.ts`) las genera el scaffolding oficial. Si TypeScript se queja de imports no resueltos, ejecuta `npx create-csii-ui-mod` en una carpeta temporal y copia su carpeta `types/` a `CustomZones/UI/`.

## Dependencias recomendadas (en el juego)

- **Asset Icon Library** (JadHajjar) — genera las miniaturas de los edificios (`coui://ail/...`). Sin él, el mod usa los iconos genéricos del juego.
- **Unified Icon Library** (algernon) — iconos de la interfaz (`coui://uil/...`). Muy recomendado para que el panel se vea correctamente.

Ambas se declaran como dependencias en `PublishConfiguration.xml` al publicar en PDX Mods.

## Advertencias

- **Este proyecto no se ha podido compilar ni probar en el entorno donde se generó** (requiere las DLLs del juego y el toolchain). El código sigue fielmente los patrones de Platter y Find It, pero es esperable algún ajuste menor según la versión del juego: firmas de API como `NameSystem.GetRenderedLabelName`, componentes `Dropdown`/`DropdownItem` de `cs2/ui`, u opciones de `Colossal.Json`. Los errores de compilación, si aparecen, serán puntuales y fáciles de corregir con el toolchain delante.
- Cambiar `ModId` en `PublishConfiguration.xml` (ahora 0) al publicar.

## Posibles mejoras futuras

- Herramienta de área propia (polígonos independientes) en lugar de reutilizar distritos.
- Mitigación del "churn" de spawns rechazados (p. ej. cooldown por celda).
- Importar/exportar paletas para compartirlas.

## Créditos

- **Platter** (lucarager, MIT) — patrón de sistemas de filtrado de spawn, ValueBindingHelper, GenericUIWriter/Reader.
- **Find It** (JadHajjar, MIT) — indexado de prefabs, filtros, estructura de UI y build.
- **Unified Icon Library** (algernon) y **Asset Icon Library** (JadHajjar) — iconografía.
