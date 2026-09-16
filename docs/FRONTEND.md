# Frontend

One page, one design system, one spiral.

```
index.html    src/script.js           the page: Lenis, GSAP, pointer, archive wiring
              src/gallery/spiral.js   the helix
              src/shaders.js          the tile shaders
              src/data/archive.js     the records the helix and the list render
              src/archive/list.js     the readable list of the same records
```

Vite 8, vanilla JavaScript modules, Three.js, GSAP with ScrollTrigger, and
Lenis. Plain CSS, no framework, no preprocessor, no postprocessing library: the
tile shaders in `src/shaders.js` are hand-written GLSL.

---

## The spiral

`createSpiralGallery(host, { urls, config, onSelect })` builds a helix of curved
image tiles inside `host` and returns the handles a page drives it with:

| Handle | Does |
| --- | --- |
| `setScroll(progress, velocity)` | Glides the camera down the helix; the velocity feeds the spin |
| `setTilt(x, y)` | Pointer parallax, normalised to `[-1, 1]`; ignored below the mobile breakpoint |
| `resize()` | Re-reads the host box and the mobile breakpoint (768 px) |
| `replaceTextures(urls)` | Rebuilds the tiles from a new image set, disposing the old GPU buffers first |
| `dispose()` | Stops the loop and releases geometry, materials, textures and the canvas |
| `ready` | Resolves once the textures are loaded and the tiles exist |

The page passes the thumbnails of the archive records from `src/data/archive.js`
and an `onSelect` handler: a raycaster maps a click on a tile back to the record
it shows, and the page scrolls to that record's row in the archive list. A
texture that fails to load resolves to a 1×1 dark texture rather than
rejecting: the spiral always renders. The texture loader sends credentials so
that authorised thumbnails use the current session; same-origin requests are
unaffected.

### Geometry

Each tile is a slice of a cylinder wall, not a flat plane. The builder walks
`tileSegments + 1` slices along an arc, pushes a top and a bottom vertex at
each, and stitches neighbouring slices into two triangles. Tile `i` sits at
`rotation.y = i · angleStep` and `position.y = startY − i · spiralGap`, and its
radius lerps from `startRadius` to `endRadius` along the helix, which is the
cone taper. Each tile's arc is `angleStep + tileOverlap`, so neighbours overlap
by a hair and no seam shows. The tiles live in one group, and the group is what
rotates and tilts.

### Motion

- **Idle drift** advances the group's rotation by `baseRotationSpeed` every frame.
- **Scroll** sets the camera's target height from scroll progress. The camera
  eases toward it by `cameraSmoothing` and looks at a point 40% of the way down,
  so scrolling descends into the helix rather than past it.
- **Scroll velocity** adds angular momentum, `velocity × scrollRotationMultiplier
  × scrollMultiplier`, which decays by `rotationDecay` each frame: a flick surges,
  then settles back to the drift.
- **Pointer** tilts the group toward the cursor, desktop only. Crossing into the
  mobile breakpoint on resize zeroes the tilt.
- **Reduced motion** (`prefers-reduced-motion: reduce`) stops the idle drift
  inside the spiral. The page additionally leaves the wheel native in Lenis,
  skips the GSAP text reveals and disables the CSS cue animations. The spiral
  still renders and still answers scroll.

The WebGL setup is deferred behind `requestIdleCallback`, with a double
`requestAnimationFrame` fallback, so the serif headline can be the LCP
candidate before any GPU work begins. Lenis is the single scroll source and
ScrollTrigger reads its interpolated position, so reveals stay in step with the
smoothed scroll rather than the raw one.

### Shaders

The fragment shader does two things and nothing else: a per-tile edge vignette,
`smoothstep(0.34, 0.86, …)` softened to `mix(0.78, 1.0, edge)`, and a
camera-distance depth fade over `smoothstep(8.0, 22.0, dist)` that dims far
tiles to `mix(0.5, 1.0, depth)` and desaturates them by `mix(0.55, 1.0, depth)`,
so the far reaches of the helix fall toward monochrome shadow.

---

## Tuning

Every knob lives in `CONFIG` in `src/gallery/spiral.js`. A page can override any
of them through the `config` option; the page currently uses the defaults.

| Key | Default | What it does |
| --- | --- | --- |
| `totalImages` | `10` | Legacy count from the original page; unused now that the URL list comes from the archive records. Tiles cycle round-robin through whatever list they are given |
| `tilesPerRevolution` | `15` | Tiles in one full turn. Sets the arc width of each tile |
| `revolutions` | `5` | Number of turns. `tilesPerRevolution × revolutions` tiles in total |
| `startRadius` | `5` | Helix radius at the top |
| `endRadius` | `3.5` | Helix radius at the bottom. `end < start` gives the cone |
| `tileHeightRatio` | `1.1` | Tile height as a multiple of its chord width. Above `1` is portrait |
| `tileSegments` | `24` | Slices per tile arc. Higher is a smoother curve and more vertices |
| `spiralGap` | `0.35` | Vertical spacing between stacked tiles |
| `tileOverlap` | `0.005` | Extra arc, in radians, so neighbours overlap and no seam shows |
| `cameraZ` | `12` | Camera distance from the axis, plus `3` on mobile to fit more in frame |
| `cameraSmoothing` | `0.075` | Lerp factor for camera height and tilt. Lower is heavier |
| `baseRotationSpeed` | `0.001` | Idle spin in radians per frame |
| `scrollRotationMultiplier` | `0.0035` | How strongly scroll velocity feeds the spin |
| `rotationDecay` | `0.9` | Per-frame decay of scroll-driven spin back toward idle |
| `scrollMultiplier` | `1.25` | Extra gain on the velocity-to-spin coupling |
| `cameraYMultiplier` | `0.2` | How far scroll travels the camera down the helix |
| `parallaxStrength` | `0.1` | Maximum tilt from the pointer, in radians |
| `spiralOffsetY` | `-2.0` | Vertical offset of the whole group |

Which to reach for:

- **Tighter or looser spiral:** `spiralGap` for vertical density,
  `tilesPerRevolution` for tiles per turn, `revolutions` for length.
- **More or less cone:** widen the gap between `startRadius` and `endRadius`.
  Equal values give a straight cylinder.
- **Portrait or square tiles:** `tileHeightRatio`, `1.0` for square.
- **Faster idle drift:** `baseRotationSpeed`.
- **Punchier scroll spin:** raise `scrollRotationMultiplier` or
  `scrollMultiplier`. Lower `rotationDecay` to settle faster, raise it to coast.
- **Heavier or snappier motion:** `cameraSmoothing`, lower for more inertia.
- **Deeper scroll dive:** `cameraYMultiplier`.
- **Stronger pointer lean:** `parallaxStrength`.
- **Frame more of the spiral:** raise `cameraZ`.
- **Smoother tile curve:** raise `tileSegments`, at the cost of vertices.

### Archive integration

`src/data/archive.js` fetches authorised version records from `/api/v1/archive`.
`src/script.js` supplies ready thumbnails to the existing spiral and all version
states to the list. SignalR events and periodic reconciliation refresh the HTTP
projection. The thumbnail URL must point to the same-origin, authorised version
content endpoint. No bundled placeholder becomes a runtime archive record.

The styles, shaders, geometry and motion parameters remain the approved design.
The viewer displays controlled sign-in and availability messages using existing
text styles. Backend authentication and authoring actions remain API operations.
