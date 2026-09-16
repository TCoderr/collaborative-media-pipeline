// Quire — GLSL shaders for the spiral gallery tiles.
// Named exports consumed by THREE.ShaderMaterial in src/script.js.
// Kept deliberately minimal: no glow, no glitch, no postprocessing —
// just a per-tile edge vignette and a camera-distance depth fade.

export const vertexShader = /* glsl */ `
  varying vec2 vUv;
  varying vec3 vWorldPosition;

  void main() {
    vUv = uv;

    vec4 worldPosition = modelMatrix * vec4(position, 1.0);
    vWorldPosition = worldPosition.xyz;

    gl_Position = projectionMatrix * viewMatrix * worldPosition;
  }
`;

export const fragmentShader = /* glsl */ `
  precision highp float;

  uniform sampler2D uMap;
  uniform vec3 uCameraPosition;

  varying vec2 vUv;
  varying vec3 vWorldPosition;

  void main() {
    vec4 tex = texture2D(uMap, vUv);

    // Subtle edge vignette per tile — softens the four borders so
    // adjacent tiles melt into one another rather than reading as cards.
    vec2 centered = vUv - 0.5;
    float edge = 1.0 - smoothstep(0.34, 0.86, length(centered));
    edge = mix(0.78, 1.0, edge);

    // Depth fade — tiles further from the camera dim toward the void,
    // which gives the spiral its sense of receding volume.
    float dist = distance(vWorldPosition, uCameraPosition);
    float depth = 1.0 - smoothstep(8.0, 22.0, dist);
    depth = mix(0.5, 1.0, depth);

    // Cinematic touch: bleed the most distant tiles toward luma so the
    // far reaches of the helix read as near-monochrome shadow.
    vec3 color = tex.rgb;
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(vec3(luma), color, mix(0.55, 1.0, depth));

    color = color * edge * depth;

    gl_FragColor = vec4(color, tex.a);
  }
`;
