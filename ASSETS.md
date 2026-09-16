# QUIRE media assets

The frontend includes twelve project-owned abstract placeholder frames in
`public/images/placeholder-01.jpg` through `placeholder-12.jpg`. They contain no
third-party photography or identifiable people. They are retained as frontend
assets and test upload fixtures, with no runtime fallback to these files.

The live spiral displays only authorised thumbnails generated from uploaded version
records. Unvalidated sources and failed or quarantined media have no preview URL.
Uploaded content requires appropriate rights from the collaborator supplying it.

## Bundled media

These twelve JPEGs are numbered QUIRE placeholder artwork: simple gradients,
grain, borders and typography. Each visibly says `PLACEHOLDER FRAME — REPLACE`.
They are synthetic fixtures, not photographs, client work or evidence of a live
review state. Labels embedded in the artwork are illustrative; only the backend
record determines processing, approval and delivery state. The files contain no
EXIF metadata. No imported source-repository imagery is included.

When changing a fixture, use original artwork or an asset with documented
redistribution permission, record its source and license here, remove private
metadata, and rerun upload/browser tests. Real workspace media must be uploaded
through the API rather than added as a frontend fallback.

## Fonts and procedural visuals

The page loads Fraunces, Manrope and Space Mono through Google Fonts. Font binaries
are not vendored. Each family is distributed under the SIL Open Font License 1.1:

- [Fraunces license and copyright](https://github.com/google/fonts/blob/main/ofl/fraunces/OFL.txt)
- [Manrope license and copyright](https://github.com/google/fonts/blob/main/ofl/manrope/OFL.txt)
- [Space Mono license and copyright](https://github.com/google/fonts/blob/main/ofl/spacemono/OFL.txt)

If fonts are self-hosted later, retain the corresponding copyright and license
files alongside the binaries. The CSS grain is an inline SVG turbulence filter;
the spiral geometry and GLSL effects are implemented in source. No separate stock
textures or icon packs are bundled.
