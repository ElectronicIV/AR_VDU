# Changing AR Markers

The app recognises these three marker images to display their corresponding 3D models:
| File                   | Associated Model |
|------------------------|------------------|
| `marker_turbine.png`   | Wind Turbine     |
| `marker_satellite.png` | Satellite        |
| `marker_geartrain.png` | Gear Train       |

### How to Replace a Marker
1. Overwrite the target file in `Assets/AR/Markers/` using the exact same filename (`marker_turbine.png`, `marker_satellite.png`, or `marker_geartrain.png`).
2. In Unity, select **Tools -> AR -> Rebuild AR Demo**.
3. Build and deploy.

### Image Guidelines for Reliable Tracking
ARCore tracks local detail and contrast, not overall subject matter. Aim for an image quality score of **75 or higher**.
- **Resolution:** Minimum 300 * 300 px (1024 * 1024 px recommended).
- **Best choices:** High-contrast, dense artwork, textured surfaces, busy photos, maps, and detailed patterns.
- **Avoid:**
- Solid colors, blank backgrounds, or smooth gradients
- Repeating patterns (checkerboards, tiles, bricks) and mirror symmetry
- Blurry, out-of-focus, very dark, or washed-out images

### Printing & Display
- **Print width:** Default is **20 cm wide** (height scales automatically).
- **Finish:** Use matte paper. Avoid glossy prints, as glare disrupts tracking.
- **Surface:** Ensure the printed marker lies completely flat; curled prints track poorly.

### How to Add a New Image / Marker

To introduce a new marker beyond replacing the existing three:

1. Place your new image file in `Assets/AR/Markers/`.
2. Open `Assets/AR/Editor/ARImageTrackingSetup.cs` and add a new row to the `k_Markers` list with the image name, file path, and display name.
3. Add a matching entry to the `prefabs` dictionary in `BuildAll()` using the same image name key to assign its 3D model.
4. Run **Tools -> AR -> Rebuild AR Demo** in Unity.
