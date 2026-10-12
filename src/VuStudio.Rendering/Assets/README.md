# VU Studio assets

Static assets used by `VuStudio.Rendering` to draw dial faces. The manifests describe what is bundled: `Fonts/fonts.json` and `Icons/icons.json`.

## Fonts

14 font families (21 files) from the Google Fonts repository (`ofl/` directory, https://github.com/google/fonts, commit `bd8f81ddb5c74d5c8897b36ad88b440266245103`). The files are unmodified copies. Each family folder holds its font files and its own `OFL.txt`.

| id | Family | Folder | License |
|---|---|---|---|
| inter | Inter | `Fonts/Inter` | OFL-1.1 |
| barlow-condensed | Barlow Condensed | `Fonts/BarlowCondensed` | OFL-1.1 |
| b612 | B612 | `Fonts/B612` | OFL-1.1 |
| b612-mono | B612 Mono | `Fonts/B612Mono` | OFL-1.1 |
| orbitron | Orbitron | `Fonts/Orbitron` | OFL-1.1 |
| share-tech-mono | Share Tech Mono | `Fonts/ShareTechMono` | OFL-1.1 |
| libre-baskerville | Libre Baskerville | `Fonts/LibreBaskerville` | OFL-1.1 |
| oswald | Oswald | `Fonts/Oswald` | OFL-1.1 |
| bebas-neue | Bebas Neue | `Fonts/BebasNeue` | OFL-1.1 |
| silkscreen | Silkscreen | `Fonts/Silkscreen` | OFL-1.1 |
| vt323 | VT323 | `Fonts/VT323` | OFL-1.1 |
| jetbrains-mono | JetBrains Mono | `Fonts/JetBrainsMono` | OFL-1.1 |
| space-grotesk | Space Grotesk | `Fonts/SpaceGrotesk` | OFL-1.1 |
| teko | Teko | `Fonts/Teko` | OFL-1.1 |

## Icons

202 icons from Material Design Icons, `@mdi/js` version 7.4.47 (npm; https://github.com/Templarian/MaterialDesign-JS). `Icons/icons.json` holds the 24x24 SVG path data for each icon, keyed by its MDI name and grouped by category.

License: Apache-2.0, as declared in the package metadata. The license file shipped with the package (`Icons/LICENSE-MDI.txt`) is titled "Pictogrammers Free License". It says some icons are Apache 2.0 and the others are either under their respective licenses or Apache 2.0. Brand logos in this set (steam, microsoft-windows, linux, docker, spotify, raspberry-pi, home-assistant, bitcoin, ethereum) may carry their own terms, so check them before release.

## Files

- `Fonts/fonts.json`: font manifest (family, category, files, weight or variable axes)
- `Icons/icons.json`: icon manifest (id, category, path)
- `Icons/LICENSE-MDI.txt`: license file shipped with `@mdi/js` 7.4.47
