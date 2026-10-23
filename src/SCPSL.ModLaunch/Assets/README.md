# Launcher icon

`modlaunch.ico` embeds the approved teal-power Containment Launch artwork in ten 32-bit RGBA sizes:

- 16, 20, and 24 pixels use the larger power symbol to improve recognition.
- 32, 40, 48, 64, 96, 128, and 256 pixels retain the preferred smaller power symbol.

The transparent PNG source images are [the smaller symbol](../../../docs/icon-concepts/2026-09-29/05-containment-teal-power.png) and [the larger symbol](../../../docs/icon-concepts/2026-09-29/07-containment-large-power.png). The concepts were created with built-in ImageGen; ICO export resamples the approved artwork without regenerating it.

Run `scripts/build-launcher-icon.ps1` on Windows to regenerate the icon. It also exports each PNG frame into the ignored `artifacts/icon-review` directory for inspection. Normal builds consume the checked-in ICO and do not require running the script.

The launcher project embeds the icon through `ApplicationIcon`. Existing shortcut creation already uses icon index 0 from the executable.
