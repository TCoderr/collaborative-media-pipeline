// ─────────────────────────────────────────────────────────────────────────
// Quire — spiral gallery
//
// Extracted verbatim from the original hero so two surfaces can share one
// implementation: the marketing hero (fixed images from /images) and the
// archive (a project's real asset thumbnails). The geometry, shaders, motion
// and defaults are unchanged — the only difference is that the texture list
// is now an argument rather than a hard-coded path pattern.
// ─────────────────────────────────────────────────────────────────────────

import * as THREE from 'three';

import { vertexShader, fragmentShader } from '../shaders.js';

// ── Tuning ──────────────────────────────────────────────────────────────
// Every knob that shapes the spiral lives here. docs/FRONTEND.md explains
// what each one does.
export const CONFIG = {
  totalImages: 10,
  tilesPerRevolution: 15,
  revolutions: 5,
  startRadius: 5,
  endRadius: 3.5,
  tileHeightRatio: 1.1,
  tileSegments: 24,
  spiralGap: 0.35,
  tileOverlap: 0.005,
  cameraZ: 12,
  cameraSmoothing: 0.075,
  baseRotationSpeed: 0.001,
  scrollRotationMultiplier: 0.0035,
  rotationDecay: 0.9,
  scrollMultiplier: 1.25,
  cameraYMultiplier: 0.2,
  parallaxStrength: 0.1,
  spiralOffsetY: -2.0,
};

const MOBILE_BREAKPOINT = 768;
const computeIsMobile = () => window.innerWidth < MOBILE_BREAKPOINT;

// ─────────────────────────────────────────────────────────────────────────
// GEOMETRY — a curved BufferGeometry tile (a slice of a cylinder wall).
// Walk (segments + 1) slices along an arc; push a top + bottom vertex at each;
// stitch adjacent slices into two triangles apiece.
// ─────────────────────────────────────────────────────────────────────────
function createCurvedTileGeometry(radius, arcAngle, tileHeight, segments) {
  const positions = [];
  const uvs = [];
  const indices = [];

  const half = tileHeight / 2;
  const startAngle = -arcAngle / 2; // centre the arc on theta = 0

  for (let i = 0; i <= segments; i++) {
    const t = i / segments;
    const theta = startAngle + t * arcAngle;
    const x = Math.sin(theta) * radius;
    const z = Math.cos(theta) * radius;

    positions.push(x, half, z); // top vertex
    uvs.push(t, 1.0);

    positions.push(x, -half, z); // bottom vertex
    uvs.push(t, 0.0);
  }

  for (let i = 0; i < segments; i++) {
    const top = i * 2;
    const bottom = i * 2 + 1;
    const topNext = (i + 1) * 2;
    const bottomNext = (i + 1) * 2 + 1;

    indices.push(top, bottom, topNext);
    indices.push(bottom, bottomNext, topNext);
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geometry.setAttribute('uv', new THREE.Float32BufferAttribute(uvs, 2));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();

  return geometry;
}

// ─────────────────────────────────────────────────────────────────────────
// TEXTURES — never reject: a failed image resolves with a 1×1 dark-grey
// DataTexture so the spiral always renders.
// ─────────────────────────────────────────────────────────────────────────
function makeFallbackTexture() {
  const data = new Uint8Array([22, 22, 24, 255]);
  const texture = new THREE.DataTexture(data, 1, 1, THREE.RGBAFormat);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.needsUpdate = true;
  return texture;
}

function loadTextures(rendererInstance, urls) {
  const loader = new THREE.TextureLoader();

  // The archive serves media from the API behind a session cookie, so the
  // loader has to send credentials. Same-origin requests are unaffected.
  loader.setCrossOrigin('use-credentials');

  const maxAnisotropy = rendererInstance.capabilities.getMaxAnisotropy();

  return Promise.all(
    urls.map(
      (url) =>
        new Promise((resolve) => {
          loader.load(
            url,
            (texture) => {
              texture.colorSpace = THREE.SRGBColorSpace;
              texture.anisotropy = maxAnisotropy;
              texture.minFilter = THREE.LinearMipmapLinearFilter;
              texture.magFilter = THREE.LinearFilter;
              texture.needsUpdate = true;
              resolve(texture);
            },
            undefined,
            () => resolve(makeFallbackTexture())
          );
        })
    )
  );
}

/**
 * Creates a spiral gallery inside `host`.
 *
 * @param {HTMLElement} host        Element the canvas is appended to; its box drives sizing.
 * @param {object}      options
 * @param {string[]}    options.urls        Texture URLs, cycled round-robin across tiles.
 * @param {object}      [options.config]    Overrides merged over CONFIG.
 * @param {Function}    [options.onSelect]  Called with a tile index when a tile is clicked.
 * @returns {{ setScroll: Function, setTilt: Function, resize: Function,
 *            replaceTextures: Function, dispose: Function, ready: Promise }}
 */
export function createSpiralGallery(host, { urls, config = {}, onSelect = null } = {}) {
  const settings = { ...CONFIG, ...config };

  const state = {
    isMobile: computeIsMobile(),
    scrollProgress: 0,
    spinVelocity: 0,
    targetCameraY: 0,
    currentCameraY: 0,
    targetTiltX: 0,
    targetTiltZ: 0,
    currentTiltX: 0,
    currentTiltZ: 0,
    disposed: false,
    // Honour the OS reduced-motion setting: the spiral still renders, it just
    // stops drifting on its own.
    reducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  };

  const scene = new THREE.Scene();

  const width = host.clientWidth || window.innerWidth;
  const height = host.clientHeight || window.innerHeight;

  const camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 100);
  camera.position.set(0, 0, settings.cameraZ + (state.isMobile ? 3 : 0));

  const renderer = new THREE.WebGLRenderer({
    antialias: true,
    alpha: true,
    powerPreference: 'high-performance',
  });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setClearColor(0x000000, 0);

  host.appendChild(renderer.domElement);
  renderer.domElement.classList.add('hero__canvas');

  const spiral = new THREE.Group();
  spiral.position.y = settings.spiralOffsetY;
  scene.add(spiral);

  const disposables = { geometries: [], materials: [], textures: [] };

  function sizeRenderer() {
    const w = host.clientWidth;
    const h = host.clientHeight;
    if (w === 0 || h === 0) return;

    camera.aspect = w / h;
    camera.updateProjectionMatrix();

    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(w, h, false); // false → CSS keeps the canvas at 100%
  }

  sizeRenderer();

  // ── BUILD — assemble the helix of curved tiles into the `spiral` group. ──
  function buildSpiral(textures) {
    const textureCount = textures.length || 1;
    const totalTiles = settings.tilesPerRevolution * settings.revolutions; // 75
    const angleStep = (Math.PI * 2) / settings.tilesPerRevolution;
    const arcAngle = angleStep + settings.tileOverlap;
    const chord = 2 * settings.startRadius * Math.sin(angleStep / 2);
    const tileHeight = chord * settings.tileHeightRatio;

    // Total vertical span, centred around y = 0.
    const startY = ((totalTiles - 1) * settings.spiralGap) / 2;

    for (let i = 0; i < totalTiles; i++) {
      const progress = totalTiles > 1 ? i / (totalTiles - 1) : 0;
      // Radius lerps startRadius → endRadius along the helix (cone-like taper).
      const radius = THREE.MathUtils.lerp(settings.startRadius, settings.endRadius, progress);

      const geometry = createCurvedTileGeometry(radius, arcAngle, tileHeight, settings.tileSegments);

      const material = new THREE.ShaderMaterial({
        vertexShader,
        fragmentShader,
        uniforms: {
          uMap: { value: textures[i % textureCount] },
          uCameraPosition: { value: camera.position },
        },
        side: THREE.DoubleSide,
        transparent: true,
      });

      const tile = new THREE.Mesh(geometry, material);
      tile.position.y = startY - i * settings.spiralGap;
      tile.rotation.y = i * angleStep;

      // Which source item this tile shows, so a click can resolve to an asset.
      tile.userData.sourceIndex = i % textureCount;

      spiral.add(tile);
      disposables.geometries.push(geometry);
      disposables.materials.push(material);
    }

    disposables.textures.push(...textures);
  }

  // ── RENDER LOOP ─────────────────────────────────────────────────────────
  let frame = null;

  function tick() {
    if (state.disposed) return;

    // Constant drift + decaying scroll-driven spin.
    if (!state.reducedMotion) {
      spiral.rotation.y += settings.baseRotationSpeed + state.spinVelocity;
    } else {
      spiral.rotation.y += state.spinVelocity;
    }
    state.spinVelocity *= settings.rotationDecay;

    // Mouse-parallax tilt (desktop only), eased toward its target.
    if (!state.isMobile) {
      state.currentTiltX += (state.targetTiltX - state.currentTiltX) * settings.cameraSmoothing;
      state.currentTiltZ += (state.targetTiltZ - state.currentTiltZ) * settings.cameraSmoothing;
      spiral.rotation.x = state.currentTiltX;
      spiral.rotation.z = state.currentTiltZ;
    }

    // Scroll glides the camera down the helix.
    state.targetCameraY = -state.scrollProgress * settings.cameraYMultiplier * 10;
    state.currentCameraY += (state.targetCameraY - state.currentCameraY) * settings.cameraSmoothing;

    camera.position.y = state.currentCameraY;
    camera.lookAt(0, state.currentCameraY * 0.4, 0);

    renderer.render(scene, camera);
    frame = requestAnimationFrame(tick);
  }

  frame = requestAnimationFrame(tick);

  const ready = loadTextures(renderer, urls).then((textures) => {
    if (state.disposed) return;
    buildSpiral(textures);
    renderer.domElement.classList.add('is-ready'); // CSS fades the canvas up
  });

  // ── Tile picking (archive version selection). ────
  const raycaster = onSelect ? new THREE.Raycaster() : null;
  const pointer = new THREE.Vector2();

  function handleClick(event) {
    const rect = renderer.domElement.getBoundingClientRect();
    pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;

    raycaster.setFromCamera(pointer, camera);
    const hit = raycaster.intersectObjects(spiral.children, false)[0];
    if (hit) onSelect(hit.object.userData.sourceIndex);
  }

  if (onSelect) {
    renderer.domElement.addEventListener('click', handleClick);
    renderer.domElement.style.cursor = 'pointer';
  }

  return {
    /** Scroll progress 0–1, plus the velocity that feeds the spin. */
    setScroll(progress, velocity = 0) {
      state.scrollProgress = Math.min(Math.max(progress, 0), 1);
      state.spinVelocity += velocity * settings.scrollRotationMultiplier * settings.scrollMultiplier;
    },

    /** Normalised pointer position in [-1, 1]. */
    setTilt(x, y) {
      if (state.isMobile) return;
      state.targetTiltX = y * settings.parallaxStrength;
      state.targetTiltZ = x * settings.parallaxStrength * -0.5;
    },

    resize() {
      state.isMobile = computeIsMobile();
      sizeRenderer();
      camera.position.z = settings.cameraZ + (state.isMobile ? 3 : 0);

      if (state.isMobile) {
        state.targetTiltX = 0;
        state.targetTiltZ = 0;
      }
    },

    /** Rebuilds the helix with a new image set — the archive receiving version updates. */
    async replaceTextures(nextUrls) {
      const textures = await loadTextures(renderer, nextUrls);
      if (state.disposed) return;

      // Tear down the old tiles before adding new ones, or every project switch
      // would leak a full set of GPU buffers.
      for (const tile of [...spiral.children]) spiral.remove(tile);
      disposeResources();

      buildSpiral(textures);
      renderer.domElement.classList.add('is-ready');
    },

    dispose() {
      state.disposed = true;
      if (frame !== null) cancelAnimationFrame(frame);
      if (onSelect) renderer.domElement.removeEventListener('click', handleClick);

      disposeResources();
      renderer.dispose();
      renderer.domElement.remove();
    },

    ready,
    get tileCount() {
      return spiral.children.length;
    },
  };

  function disposeResources() {
    for (const geometry of disposables.geometries) geometry.dispose();
    for (const material of disposables.materials) material.dispose();
    for (const texture of disposables.textures) texture.dispose();
    disposables.geometries.length = 0;
    disposables.materials.length = 0;
    disposables.textures.length = 0;
  }
}
