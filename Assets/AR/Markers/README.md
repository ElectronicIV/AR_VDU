# Changing AR Markers

The app recognises these three marker images to display their corresponding 3D models:

| File                   | Library name | Associated Model |
|------------------------|--------------|------------------|
| `marker_turbine.png`   | `Turbine`    | Wind Turbine     |
| `marker_satellite.png` | `Satellite`  | Satellite        |
| `marker_geartrain.png` | `GearTrain`  | Gear Train       |

The markers are listed in `Assets/AR/MarkerLibrary.asset`. The models they trigger are set on the
**XR Origin** object in `Assets/AR/Scenes/ImageTrackingAR.unity`.

### How to Replace a Marker
1. Overwrite the target file in `Assets/AR/Markers/` using the exact same filename. Unity keeps the
   file's identity, so the library still points at it.
2. If the new picture has a **different shape** (aspect ratio), open `MarkerLibrary.asset`, find the
   image, and re-type its width (`0.2`). The Inspector only recomputes the height when you edit the
   width, so an unchanged size would still describe the old shape.
3. Build and deploy.

Use **PNG or JPG**. ARCore reads those files straight from disk, so no special import settings are
needed.

### Image Guidelines for Reliable Tracking
ARCore tracks local detail and contrast, not overall subject matter. Aim for an image quality score of **75 or higher**.
- **Resolution:** Minimum 300 * 300 px (1024 * 1024 px recommended).
- **Best choices:** High-contrast, dense artwork, textured surfaces, busy photos, maps, and detailed patterns.
- **Avoid:**
  - Solid colors, blank backgrounds, or smooth gradients
  - Repeating patterns (checkerboards, tiles, bricks) and mirror symmetry
  - Blurry, out-of-focus, very dark, or washed-out images

The Android build scores every library image and logs a Console warning for any below 75.

### Printing & Display
- **Print width:** Default is **20 cm wide**. This is the **Physical Size** width set for each
  image in `MarkerLibrary.asset`; the height follows the image's aspect ratio. If you print at a
  different width, change it there to match, or models will appear the wrong size.
- **Finish:** Use matte paper. Avoid glossy prints, as glare disrupts tracking.
- **Surface:** Ensure the printed marker lies completely flat; curled prints track poorly.

### How to Add a New Image / Marker
1. Place your new image file in `Assets/AR/Markers/`.
2. Open `Assets/AR/MarkerLibrary.asset`, click **Add Image**, drag your texture into it, give it a
   unique **Name**, tick **Specify Size** and set the width to `0.2`.
3. Open the scene, select **XR Origin**, and on **Tracked Image Model Spawner** add an entry to
   **Bindings**:
   - **Reference Image Name** — exactly the Name from step 2
   - **Display Name** — the on-screen label
   - **Prefab** — the model to show (e.g. from `Assets/AR/Prefabs/`)
   - **Width Relative To Image** — `0.9` matches the existing markers
4. Optionally, add the same prefab to **Placeables** on **Tap To Place Spawner** so it also shows up
   in the tap-to-place rotation.
5. Save the scene, then build and deploy.

### Building without the Editor window
From the project root, with Unity closed:

```bash
unity build . --target Android -o Builds/ARImageTracking.apk \
  --execute-method ARVDU.EditorTools.ARDemoBuild.BuildAndroid
```
