# Containment Launch revisions

These three transparent PNG variants were edited with the built-in `image_gen` tool using `01-containment-launch.png` as the source image. All edits requested preservation of the outer containment seal. The teal power candidate includes a second edit to improve spacing from the surrounding arrows.

| Teal play | Teal power | Ivory launch arrow |
| --- | --- | --- |
| ![Teal play](04-containment-teal-play.png) | ![Teal power](05-containment-teal-power.png) | ![Ivory launch arrow](06-containment-ivory-launch.png) |

Recommended: teal power. It combines the containment motif with a facility startup control. Teal play offers the smallest change; the ivory launch arrow is a monochrome alternative.

## Final icon-scale selection

The approved smaller power symbol is retained in the 32, 40, 48, 64, 96, 128, and 256 pixel frames. Native-size comparison showed that the larger symbol is clearer at 16, 20, and 24 pixels, so those frames use the original larger power candidate, now saved as [07-containment-large-power.png](07-containment-large-power.png). See the [final scale preview](icon-scale-preview.png) and [embedded Windows icon](../../../src/SCPSL.ModLaunch/Assets/modlaunch.ico).

## Exact edit prompts

### Teal Play

```text
Use case: precise-object-edit.
Asset type: Windows desktop application icon for SCPSL-ModLaunch.
Input image: The supplied Containment Launch image is the EDIT TARGET.
Preserve the exact existing near-black circular badge, ivory circular ring, three inward ivory containment arrows, their positions, proportions, original subtle bevel and shading, crop and margins. Preserve the image's true transparent background outside the circular badge. Do not redraw, restyle, recolor or change the outer containment seal.
Only edit the orange symbol in the center as described below. Keep the central mark bold, simple, cleanly separated from the three surrounding arrows and readable at 32 pixels. No lettering, labels, watermarks, extra symbols, glow, presentation board, scenery, or added background. Return one isolated transparent PNG icon.
Requested change: Recolor ONLY the existing central orange play triangle to cool teal #54D8C7, preserving its exact triangular shape, size, position, softly rounded corners and subtle shading. Remove every orange hue from the mark. This is the minimal-change color option.
```

### Teal Power

```text
Use case: precise-object-edit.
Asset type: Windows desktop application icon for SCPSL-ModLaunch.
Input image: The supplied Containment Launch image is the EDIT TARGET.
Preserve the exact existing near-black circular badge, ivory circular ring, three inward ivory containment arrows, their positions, proportions, original subtle bevel and shading, crop and margins. Preserve the image's true transparent background outside the circular badge. Do not redraw, restyle, recolor or change the outer containment seal.
Only edit the orange symbol in the center as described below. Keep the central mark bold, simple, cleanly separated from the three surrounding arrows and readable at 32 pixels. No lettering, labels, watermarks, extra symbols, glow, presentation board, scenery, or added background. Return one isolated transparent PNG icon.
Requested change: Completely remove the orange play triangle and replace it with a single cool-teal #54D8C7 standard power-on symbol: a broad nearly circular open ring with the gap centered at twelve o'clock and one vertical stem descending through the gap toward the center. Use thick, crisp strokes with very subtly softened edges, no enclosing button. Match the original triangle's overall visual scale and centered placement. Keep a clear gap from the outer ivory arrows. No orange and no triangle remain. Match the original restrained shallow bevel.
```

### Ivory Launch Arrow

```text
Use case: precise-object-edit.
Asset type: Windows desktop application icon for SCPSL-ModLaunch.
Input image: The supplied Containment Launch image is the EDIT TARGET.
Preserve the exact existing near-black circular badge, ivory circular ring, three inward ivory containment arrows, their positions, proportions, original subtle bevel and shading, crop and margins. Preserve the image's true transparent background outside the circular badge. Do not redraw, restyle, recolor or change the outer containment seal.
Only edit the orange symbol in the center as described below. Keep the central mark bold, simple, cleanly separated from the three surrounding arrows and readable at 32 pixels. No lettering, labels, watermarks, extra symbols, glow, presentation board, scenery, or added background. Return one isolated transparent PNG icon.
Requested change: Completely remove the orange play triangle and replace it with one bold warm-ivory #F2EEE4 upward-right launch arrow. The arrow has a broad straight diagonal shaft extending from lower-left to upper-right and a large clearly defined arrowhead at its upper-right end. It is a complete arrow with a substantial visible shaft, not a standalone triangle or a chevron. Center it in the inner space at the original triangle's approximate scale, with clear separation from all three outer containment arrows. Use the same ivory and restrained shallow bevel as the outer seal. No orange, no teal, no square, no extra trail and no play triangle remain. This is a restrained monochrome option.
```

### Teal power spacing refinement

The edit target for this step was the generated teal power draft from the preceding prompt. The final saved file is `05-containment-teal-power.png`.

```text
Use case: precise-object-edit.
Edit target: the supplied Containment Launch icon with a teal power symbol.
Requested change: Make ONLY the central teal power symbol smaller. Scale the whole teal power glyph uniformly to approximately 76 percent of its current width and height, keeping its visual center fixed at the same point. The current teal glyph comes too close to the ivory arrows; the smaller version must have a clearly visible broad charcoal gap from all three inward arrow tips, especially the top arrow. Keep the glyph's standard power shape, ring/stem proportions, teal color and restrained bevel.
Preserve every other part exactly: the charcoal circular badge, ivory segmented containment ring, all three ivory arrows, shading, overall silhouette, framing, margins and true transparent background. Do not move or redraw the outer arrows or ring. No orange, no additional marks, no text, no glow, no new background. One finished transparent PNG application icon.
```
