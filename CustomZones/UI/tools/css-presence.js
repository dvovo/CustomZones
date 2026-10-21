// From FindIt-CSII (MIT). Injects a `hasCSS` export into the emitted module
// so the game's UI mod loader knows it must also load the .css file.
// Without this the stylesheet is emitted but NEVER loaded -> unstyled UI.
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
