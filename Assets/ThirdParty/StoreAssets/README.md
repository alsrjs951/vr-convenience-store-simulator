# Store dressing assets (P3-K1)

Free assets for the store's looks, downloaded 2026-11-08 with the user's approval. Only login-free sources whose license
was checked on the source's own page. Converted for URP by `scripts/windows/import_store_assets.py` and placed by
`Assets/Editor/P3StoreDressing.cs` (run from `P2StoreBuilder.Build`). Notes in Korean: `docs/p2/매장에셋_사운드_2026-11-08.md`.

## Sources and licenses

| Folder | Asset | Source | License |
|---|---|---|---|
| `AmbientCG/Metal027` | Metal027, 1K JPG (normal map used) | https://ambientcg.com/view?id=Metal027 | CC0 1.0 |
| `PolyHaven/steel_frame_shelves_01` | Steel Frame Shelves 01, FBX + 2k JPG | https://polyhaven.com/a/steel_frame_shelves_01 | CC0 1.0 |
| `PolyHaven/cardboard_box_01` | Cardboard Box 01, 2k JPG (one side cut out) | https://polyhaven.com/a/cardboard_box_01 | CC0 1.0 |
| `PolyHaven/industrial_pastic_container` | Industrial Plastic Container, FBX + 2k JPG | https://polyhaven.com/a/industrial_pastic_container | CC0 1.0 |
| `PolyHaven/hand_truck` | Hand Truck, FBX + 2k JPG | https://polyhaven.com/a/hand_truck | CC0 1.0 |
| `Khronos/CommercialRefrigerator` | Commercial Refrigerator, GLB | https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/CommercialRefrigerator | **CC BY 4.0** |

CC0 assets need no attribution (Poly Haven: https://polyhaven.com/license, ambientCG: https://docs.ambientcg.com/license/).

**The refrigerator needs attribution** wherever the game is shown or shared (credits, presentation):

> Commercial Refrigerator © 2025 Darmstadt Graphics Group GmbH (model/textures: Eric Chadwick), based on "Commercial
> Fridge" (https://sketchfab.com/3d-models/commercial-fridge-2174e1e4f1f24f1a95aa110ee060f473) © 2025 Sean Thomas,
> CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/), via KhronosGroup/glTF-Sample-Assets.

Its original README is kept as `Khronos/CommercialRefrigerator/SOURCE_README.md`. The "Icey Drinks" sign in its
texture is the model's own fictional brand.

Removed again on 2026-11-08 (second pass, "동아편의점" look): Tiles043, PaintedPlaster017, OfficeCeiling004 and Mounted
Fluorescent Lights were downloaded and used for the first pass, then replaced by the store's own generated floor, wall
and LED panel textures (`Assets/Art/Generated`, `scripts/windows/make_store_textures.py`), so they are no longer here.

## Changes from the originals

- `*_Mask.png` is new in every folder: R = metallic, G = ambient occlusion, B = 0, A = smoothness (1 - roughness),
  made from Poly Haven's ARM map, the glTF metallic-roughness and occlusion maps, or ambientCG's separate maps. Masks of
  2k sources are 1024 px.
- Poly Haven: the FBX is renamed from `<id>_2k.fbx` to `<id>.fbx`; textures are the glTF set's JPGs renamed to
  `_Color` and `_NormalGL` (and `_Emission`). Their EXR versions were not taken.
- `cardboard_box_01`: only one side of the box (with the handling marks) is used, cut out of the 2k atlas after turning
  it -45 + 180 degrees; the normal map's X/Y vectors are turned by the same angle. The model itself is not used.
- `CommercialRefrigerator`: converted from GLB to OBJ with node transforms applied (no glTF importer package in the
  project). The champagne bottles (113k vertices, with a label) are left out. The door glass uses a plain transparent
  URP material instead of KHR_materials_transmission.
- ambientCG: only Color, NormalGL and the generated mask are kept; displacement, `.blend`, `.usdc`, `.mtlx` and
  `.tres` files are not.
