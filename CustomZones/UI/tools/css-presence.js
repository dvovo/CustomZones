// Adapted from FindIt-CSII (MIT License).
// Changes: Copyright (c) 2026 dvovo. https://mods.paradoxplaza.com/authors/dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
//
// Adds a `hasCSS` export to the emitted module, so the game's UI mod loader
// also loads the .css file (otherwise the stylesheet is emitted but not loaded).
const { RawSource } = require("webpack").sources;

exports.CSSPresencePlugin = class CSSPresencePlugin {
  apply(compiler) {
    compiler.hooks.compilation.tap("CSSPresencePlugin", (compilation) => {
      compilation.hooks.processAssets.tap(
        {
          name: "CSSPresencePlugin",
          stage: compilation.PROCESS_ASSETS_STAGE_ADDITIONS,
        },
        () => {
          const cssFiles = Object.keys(compilation.assets).filter((asset) =>
            asset.endsWith(".css")
          );
          const hasCSS = cssFiles.length > 0;

          for (const chunk of compilation.chunks) {
            for (const file of chunk.files) {
              if (file.endsWith(".mjs") || file.endsWith(".js")) {
                const asset = compilation.getAsset(file);
                const source = asset.source.source();
                const updatedSource = source.replace(
                  "export {",
                  `const hasCSS = ${hasCSS}; export { hasCSS, `
                );

                const newSourceAndMap = {
                  sources: [file],
                  mappings: "",
                  file,
                  sourceRoot: "",
                  sourcesContent: [updatedSource],
                };

                compilation.updateAsset(
                  file,
                  new RawSource(updatedSource, newSourceAndMap)
                );
              }
            }
          }
        }
      );
    });
  }
};
