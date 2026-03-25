# MogyAntiCheat Config Schema (Current + Planned)

This document defines config keys, types, defaults, and constraints.

## Current Keys

## `MissExpirySeconds`

- Type: `float`
- Default: `20.0`
- Meaning: Maximum age of pending shot entries used for hit matching.
- Constraint: `> 0`

## `Weapons.<weapon>.MaxAccuracy`

- Type: `float`
- Typical range: `0.25 - 0.75`
- Meaning: Max allowed hit ratio before penalty logic starts.

## `Weapons.<weapon>.SampleCount`

- Type: `int`
- Typical range: `10 - 100`
- Meaning: Rolling history size used for accuracy calculations.

## `Weapons.<weapon>.SafeDistance`

- Type: `float`
- Typical range: `10 - 100`
- Meaning: Distance baseline used by weighted scoring.

## `DefaultLanguage`

- Type: `string`
- Default: `"en"`
- Meaning: Default message language (implemented).
- Constraint: should match an available language code (`en`, `hu`, ...).

## `PublicApi`

- Type: `object`
- Purpose: controls extension API behavior and declared version.

Fields:
- `Enabled` (`bool`, default `true`)
- `ApiVersion` (`string`, default `"1.0.0"`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)

## Planned Keys (M2+)

## `PenaltyTiers`

- Type: `array<object>`
- Meaning: Multi-threshold penalty definitions.

Planned tier object fields:
- `MinAccuracy` (`float`)
- `MinWeightedScore` (`float`, optional)
- `DamageMultiplier` (`float`, range `0..1`)
- `Action` (`string`, e.g., `warn`, `nerf`, `hard_nerf`)

## Validation Rules

- Invalid numeric values should be clamped or rejected with clear warning logs.
- Unknown keys should be ignored but reported in debug logs.
- On invalid config, plugin should use safe defaults instead of crashing.
