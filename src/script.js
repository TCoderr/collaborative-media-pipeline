// ─────────────────────────────────────────────────────────────────────────
// Quire — collaborative media pipeline: page entry point.
//
// Wires Lenis, GSAP/ScrollTrigger and the pointer to the spiral in
// ./gallery/spiral.js with the same tuning, choreography and deferred WebGL
// start the original hero had. What changed is where the frames come from:
// the spiral now renders the version archive in ./data/archive.js, and the
// archive list (Chapter III) is rendered from the same records so the two
// surfaces always agree.
// ─────────────────────────────────────────────────────────────────────────

import Lenis from 'lenis';
import gsap from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';

import { createSpiralGallery } from './gallery/spiral.js';
import { subscribeArchive } from './data/archive.js';
import { renderArchiveList } from './archive/list.js';

const hero = document.querySelector('.hero');
const archiveHost = document.getElementById('archive-list');

const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

let gallery = null;
let records = [];
let rows = [];
let visualRecords = [];
let archiveSignature = "";
let refreshingGallery = Promise.resolve();
let selectedTimer = null;

// ─────────────────────────────────────────────────────────────────────────
// ARCHIVE — one list of records feeds both the spiral and the readable list.
// ─────────────────────────────────────────────────────────────────────────
const frameUrls = (list) => list.filter(record => record.thumbnail).map((record) => record.thumbnail);

function applyArchive(next) {
  records = next;
  if (archiveHost) rows = renderArchiveList(archiveHost, records);
}

/** Document-space top of an element from layout alone, ignoring transforms. */
function layoutTop(el) {
  let top = 0;
  for (let node = el; node; node = node.offsetParent) top += node.offsetTop;
  return top;
}

/** A click on a spiral tile lands on the archive row for that version. */
function revealRecord(index) {
  const row = rows.find(row => row.dataset.recordId === visualRecords[index]?.id);
  if (!row) return;

  // Resolve the document position here rather than handing Lenis the element:
  // Lenis adds an element's viewport offset to its own last-known scroll, which
  // can lag the real one after a native jump (reduced motion, Home/End keys).
  // Layout offsets are used, not the bounding rect, so a row that has not yet
  // played its reveal (still translated 50px down) lands where it will settle.
  const top = layoutTop(row) - Math.round(window.innerHeight * 0.3);
  lenis.scrollTo(Math.max(0, Math.round(top)));

  for (const other of rows) other.classList.remove('is-selected');
  row.classList.add('is-selected');
  clearTimeout(selectedTimer);
  selectedTimer = setTimeout(() => row.classList.remove('is-selected'), 2400);
}

// ─────────────────────────────────────────────────────────────────────────
// WEBGL INIT — deferred so the hero headline can claim LCP before WebGL work.
// ─────────────────────────────────────────────────────────────────────────
function initWebGL() {
  if (!hero || gallery || !frameUrls(records).length) return;
  visualRecords = records.filter(record => record.thumbnail);

  gallery = createSpiralGallery(hero, {
    urls: frameUrls(records),
    onSelect: revealRecord,
  });

  // The helix is decorative for assistive tech; the archive list is the
  // readable equivalent of the same versions.
  hero.querySelector('canvas')?.setAttribute('aria-hidden', 'true');

  gallery.ready.then(() => ScrollTrigger.refresh());
}

function deferWebGL() {
  if ('requestIdleCallback' in window) {
    window.requestIdleCallback(() => initWebGL(), { timeout: 1500 });
  } else {
    // Fallback: two rAFs push past first paint.
    requestAnimationFrame(() => requestAnimationFrame(() => initWebGL()));
  }
}

// ─────────────────────────────────────────────────────────────────────────
// LENIS — the single source of scroll. GSAP/ScrollTrigger read from it.
// Under reduced motion the wheel is left native; Lenis still reports scroll.
// ─────────────────────────────────────────────────────────────────────────
const lenis = new Lenis({
  duration: 1.2,
  smoothWheel: !reduceMotion,
  smoothTouch: false,
  easing: (t) => Math.min(1, 1.001 - Math.pow(2, -10 * t)),
});

lenis.on('scroll', ({ scroll, limit, velocity }) => {
  gallery?.setScroll(scroll / Math.max(limit, 1), velocity);

  // Keep GSAP triggers synced to the interpolated scroll position.
  ScrollTrigger.update();
});

function rafLenis(time) {
  lenis.raf(time);
  requestAnimationFrame(rafLenis);
}
requestAnimationFrame(rafLenis);

// ─────────────────────────────────────────────────────────────────────────
// GSAP SCROLL REVEALS — text below the hero only. Never the hero itself.
// Under reduced motion nothing is hidden and nothing tweens.
// ─────────────────────────────────────────────────────────────────────────
gsap.registerPlugin(ScrollTrigger);

let ctx = null;

function setupReveals() {
  ctx?.revert();
  if (reduceMotion) return;

  ctx = gsap.context(() => {
    gsap.utils.toArray('.reveal-text').forEach((el) => {
      gsap.fromTo(
        el,
        { opacity: 0, y: 50 },
        {
          opacity: 1,
          y: 0,
          duration: 1.4,
          ease: 'power3.out',
          scrollTrigger: {
            trigger: el,
            start: 'top 80%',
            toggleActions: 'play none none none',
            once: true,
          },
        }
      );
    });
  });
}

if (import.meta.hot) {
  import.meta.hot.dispose(() => ctx?.revert());
}

window.addEventListener('load', () => ScrollTrigger.refresh());

// ─────────────────────────────────────────────────────────────────────────
// INTERACTION — mouse parallax (desktop) + resize.
// ─────────────────────────────────────────────────────────────────────────
window.addEventListener('mousemove', (e) => {
  gallery?.setTilt(
    (e.clientX / window.innerWidth) * 2 - 1,
    (e.clientY / window.innerHeight) * 2 - 1
  );
});

window.addEventListener('resize', () => gallery?.resize());

// ── Boot ──────────────────────────────────────────────────────────────────
const archiveStatus = document.createElement('p');
archiveStatus.className = 'field__cat';
archiveStatus.setAttribute('role', 'status');
archiveStatus.setAttribute('aria-live', 'polite');
archiveStatus.textContent = 'Loading archive?';
archiveHost?.before(archiveStatus);
setupReveals();

const unsubscribe = subscribeArchive((next) => {
  archiveStatus.textContent = next.length ? '' : 'No versions have been uploaded yet.';
  const signature = JSON.stringify(next);
  if (signature === archiveSignature) return;
  archiveSignature = signature;
  applyArchive(next);
  setupReveals();
  refreshingGallery = refreshingGallery.then(async () => {
    const nextVisual = records.filter(record => record.thumbnail);
    if (gallery) {
      if (!nextVisual.length) { gallery.dispose(); gallery = null; visualRecords = []; }
      else {
        await gallery.replaceTextures(frameUrls(records));
        visualRecords = nextVisual;
      }
    } else deferWebGL();
    ScrollTrigger.refresh();
  }).catch(() => { archiveStatus.textContent = 'Some previews could not be loaded.'; });
}, (error) => {
  archiveStatus.textContent = error.message;
  archiveSignature = '';
  applyArchive([]);
  refreshingGallery = refreshingGallery.then(() => { gallery?.dispose(); gallery = null; visualRecords = []; });
});
window.addEventListener('pagehide', unsubscribe, { once: true });
if (import.meta.hot) import.meta.hot.dispose(unsubscribe);
