# ADR 0002: Use WebGL for the P0 renderer

Status: Accepted  
Date: 2026-09-19

## Decision

Forge P0 uses Three.js `WebGLRenderer`. WebGPU is not a runtime option or a
fallback backend.

## Context

The first map-o-matic A/B attempt did not produce equivalent, trustworthy
paths: WebGL and WebGPU both had blank-frame failures and the WebGPU path had
single-digit frame rates. Continuing to tune that harness would measure the
migration defects rather than the backends.

The replacement Forge scaffold loads the same UYA level 3 tfrag glTF directly,
renders it correctly with `WebGLRenderer`, and reports the NVIDIA RTX 5090
through ANGLE rather than a software adapter. The project owner selected WebGL
for P0 after reviewing those results.

## Consequences

- M0-008 and later P0 scene work target one renderer and material path.
- WebGPU-specific code is not carried in production or maintained speculatively.
- A future backend review requires a concrete WebGL limitation and a new,
  visually equivalent benchmark; it is not part of P0.
- The quantitative WebGL/WebGPU comparison in the original M0-007 acceptance
  text is superseded by this explicit product decision.
