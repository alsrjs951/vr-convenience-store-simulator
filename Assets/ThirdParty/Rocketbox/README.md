# Microsoft Rocketbox avatars (customer characters)

- Source: https://github.com/microsoft/Microsoft-Rocketbox (branch `master`, archived read-only by Microsoft on 2026-10-02). Downloaded 2026-11-07.
- License: MIT, see `LICENSE.md` in this folder (copied unchanged from the repository).
- Used for the customer characters of the VR convenience store (design doc D4, D12).

## What is here

| Folder | Files | From the repository |
|---|---|---|
| `Avatars/Male_Adult_08` | `Male_Adult_08.fbx`, `Textures/m014_*` | `Assets/Avatars/Adults/Male_Adult_08` |
| `Avatars/Male_Adult_16` | `Male_Adult_16.fbx`, `Textures/m019_*` | `Assets/Avatars/Adults/Male_Adult_16` |
| `Avatars/Female_Adult_01` | `Female_Adult_01.fbx`, `Textures/f001_*` | `Assets/Avatars/Adults/Female_Adult_01` |
| `Avatars/Female_Adult_17` | `Female_Adult_17.fbx`, `Textures/f006_*` | `Assets/Avatars/Adults/Female_Adult_17` |
| `Animations` | `{m,f}_idle_neutral_01`, `{m,f}_idle_look_around_01`, `{m,f}_wave_01` | `Assets/Animations/all_animations_max_motextr_static/*.max.fbx` |
| `Animations` | `{m,f}_walk_neutral_01` | `Assets/Animations/all_animations_max_motextr_xy/*.max.fbx` |

## Changes from the originals

- Animation files renamed from `*.max.fbx` to `*.fbx`. Contents unchanged.
- Textures converted from 2048x2048 TGA to 1024x1024 PNG (Lanczos). Only color, normal and the hair (opacity) maps were taken; specular maps and the `_facial.fbx` blendshape models were not.
- Import settings, URP materials, animator controllers and prefabs are made by `Assets/Editor/P2CustomerSetup.cs` (menu `Tools > P2 > Setup Customer Characters`), which `P2StoreBuilder.Build` also runs.
- The player's hands (2026-11-11) are cut out of `Female_Adult_01`: the skin from 11 cm behind the wrist to the fingertips with its finger joints, scaled 1.06, using the same body color and normal maps. `Assets/Editor/PlayerHandSetup.cs` makes them (`Assets/Prefabs/Hands`, `Assets/Art/Generated/Hands`); the sleeve cuff over the cut is generated, not Rocketbox's. Notes in Korean: `docs/p2/손모델_2026-11-11.md`.
