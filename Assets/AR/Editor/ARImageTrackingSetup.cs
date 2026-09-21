using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

namespace ARVDU.EditorTools
{
    /// <summary>
    /// One-shot builder for the marker-tracking demo: materials, meshes, the three model prefabs,
    /// the reference image library and the AR scene that ties them together.
    /// <para>
    /// It is idempotent -- running it again overwrites the generated assets in place, so the
    /// models can be tweaked here and rebuilt without cleaning anything up by hand.
    /// </para>
    /// </summary>
    public static class ARImageTrackingSetup
    {
        const string k_Root = "Assets/AR";
        const string k_MaterialsDir = k_Root + "/Materials";
        const string k_ModelsDir = k_Root + "/Models";
        const string k_PrefabsDir = k_Root + "/Prefabs";
        const string k_MarkersDir = k_Root + "/Markers";
        const string k_ScenesDir = k_Root + "/Scenes";

        const string k_LibraryPath = k_Root + "/MarkerLibrary.asset";
        const string k_ScenePath = k_ScenesDir + "/ImageTrackingAR.unity";

        /// <summary>Printed width of each marker, in metres. A4 landscape fits 20 cm comfortably.</summary>
        const float k_MarkerWidthMetres = 0.2f;

        /// <summary>Width of a tap-placed model, in metres. Small enough to sit on a desk.</summary>
        const float k_PlacedWidthMetres = 0.25f;

        static readonly Color k_PlaneFill = new(0.28f, 0.72f, 1f, 0.26f);
        static readonly Color k_PlaneOutline = new(0.45f, 0.85f, 1f, 0.85f);

        /// <summary>Reference image name -> marker texture and HUD display name, in library order.</summary>
        static readonly (string imageName, string texturePath, string displayName)[] k_Markers =
        {
            ("Turbine", k_MarkersDir + "/marker_turbine.png", "Wind Turbine"),
            ("Satellite", k_MarkersDir + "/marker_satellite.png", "Satellite"),
            ("GearTrain", k_MarkersDir + "/marker_geartrain.png", "Gear Train"),
        };

        [MenuItem("Tools/AR/Rebuild AR Demo")]
        public static void BuildAll()
        {
            foreach (var dir in new[] { k_MaterialsDir, k_ModelsDir, k_PrefabsDir, k_ScenesDir })
                Directory.CreateDirectory(dir);

            ConfigurePortraitOrientation();
            ConfigureMarkerImports();
            var materials = BuildMaterials();
            var meshes = BuildMeshes();

            var prefabs = new Dictionary<string, GameObject>
            {
                ["Turbine"] = BuildTurbinePrefab(materials, meshes),
                ["Satellite"] = BuildSatellitePrefab(materials, meshes),
                ["GearTrain"] = BuildGearTrainPrefab(materials, meshes),
            };

            // Built before BuildScene, like the model prefabs: BuildScene opens a fresh scene in
            // Single mode, which would destroy any half-built GameObject still lying around.
            var planePrefab = BuildPlanePrefab(
                MakeUnlitTransparentMaterial("M_PlaneOverlay", k_PlaneFill),
                MakeUnlitTransparentMaterial("M_PlaneOutline", k_PlaneOutline));

            var library = BuildReferenceImageLibrary();
            BuildScene(library, prefabs, planePrefab);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AR setup] Done. Scene: {k_ScenePath}");
        }

        // ---------------------------------------------------------------- markers

        /// <summary>
        /// The reference image library reads raw pixels when it builds the provider-side image
        /// database, so every marker texture has to be readable and uncompressed.
        /// </summary>
        static void ConfigureMarkerImports()
        {
            foreach (var (_, texturePath, _) in k_Markers)
            {
                if (AssetImporter.GetAtPath(texturePath) is not TextureImporter importer)
                {
                    Debug.LogError($"[AR setup] Missing marker texture at {texturePath}.");
                    continue;
                }

                importer.textureType = TextureImporterType.Default;
                importer.isReadable = true;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = 2048;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.sRGBTexture = true;
                importer.SaveAndReimport();
            }
        }

        static XRReferenceImageLibrary BuildReferenceImageLibrary()
        {
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(k_LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
                AssetDatabase.CreateAsset(library, k_LibraryPath);
            }

            while (library.count > 0)
                library.RemoveAt(library.count - 1);

            var index = 0;
            foreach (var (imageName, texturePath, _) in k_Markers)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                {
                    // Skip rather than add a broken entry, so one bad path doesn't silently
                    // shift every later image onto the wrong library slot.
                    Debug.LogError(
                        $"[AR setup] No texture at {texturePath}. Update the k_Markers table to " +
                        "match the files in Assets/AR/Markers, then rebuild. See the README there.");
                    continue;
                }

                library.Add();
                library.SetName(index, imageName);
                library.SetTexture(index, texture, keepTexture: true);

                // Telling the provider the real-world size lets it report a correct scale from the
                // first frame and makes tracking converge faster. Height follows the image's own
                // aspect ratio so a non-square picture isn't declared square and mis-scaled --
                // k_MarkerWidthMetres is the width you print, whatever the shape.
                library.SetSpecifySize(index, true);
                library.SetSize(index, new Vector2(
                    k_MarkerWidthMetres,
                    k_MarkerWidthMetres * texture.height / texture.width));

                index++;
            }

            EditorUtility.SetDirty(library);
            return library;
        }

        // ---------------------------------------------------------------- materials

        static Material MakeMaterial(
            string name, Color color, float metallic, float smoothness, bool doubleSided = false)
        {
            var path = $"{k_MaterialsDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Cull", doubleSided ? 0f : 2f);
            material.doubleSidedGI = doubleSided;

            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// A URP Unlit material set up for alpha blending.
        /// <para>
        /// URP's Unlit shader is authored opaque, and "transparent" is material data rather than
        /// a separate shader -- so tinting the colour's alpha achieves nothing on its own. Every
        /// piece the inspector would set has to be written by hand: the _Surface/_Blend enums,
        /// all four blend factors the SubShader reads through [_SrcBlend][_DstBlend],
        /// [_SrcBlendAlpha][_DstBlendAlpha], ZWrite, the keyword the transparent variant is
        /// compiled behind, the RenderType override tag, the DepthOnly pass (which would
        /// otherwise stamp the plane into the depth prepass) and the render queue.
        /// </para>
        /// </summary>
        static Material MakeUnlitTransparentMaterial(string name, Color color)
        {
            var path = $"{k_MaterialsDir}/{name}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;

            material.SetFloat("_Surface", 1f);   // SurfaceType.Transparent
            material.SetFloat("_Blend", 0f);     // BlendMode.Alpha
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_QueueOffset", 0f);

            // Planes get looked at from underneath as often as from above.
            material.SetFloat("_Cull", (float)CullMode.Off);

            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);

            // No depth write, so opaque models still occlude the overlay correctly even though
            // the plane draws after them.
            material.SetFloat("_ZWrite", 0f);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            material.SetOverrideTag("RenderType", "Transparent");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.renderQueue = (int)RenderQueue.Transparent;

            material.SetColor("_BaseColor", color);

            EditorUtility.SetDirty(material);
            return material;
        }

        static Dictionary<string, Material> BuildMaterials() => new()
        {
            ["Shell"] = MakeMaterial("M_Shell", new Color(0.92f, 0.93f, 0.95f), 0.05f, 0.55f),
            ["Dark"] = MakeMaterial("M_Dark", new Color(0.16f, 0.17f, 0.20f), 0.20f, 0.35f),
            ["Accent"] = MakeMaterial("M_Accent", new Color(0.95f, 0.45f, 0.10f), 0.10f, 0.50f),
            ["Steel"] = MakeMaterial("M_Steel", new Color(0.62f, 0.65f, 0.70f), 0.90f, 0.55f),
            ["Brass"] = MakeMaterial("M_Brass", new Color(0.83f, 0.62f, 0.22f), 0.90f, 0.62f),
            ["Gold"] = MakeMaterial("M_GoldFoil", new Color(0.95f, 0.72f, 0.24f), 0.85f, 0.70f),
            ["SolarPanel"] = MakeMaterial("M_SolarPanel", new Color(0.09f, 0.13f, 0.35f), 0.35f, 0.88f),
            ["Dish"] = MakeMaterial("M_Dish", new Color(0.88f, 0.89f, 0.92f), 0.30f, 0.65f,
                doubleSided: true),
        };

        // ---------------------------------------------------------------- meshes

        static Mesh SaveMesh(Mesh mesh, string name)
        {
            var path = $"{k_ModelsDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

            if (existing == null)
            {
                mesh.name = name;
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            // Overwrite in place so prefabs already referencing this mesh keep their reference.
            existing.Clear();
            existing.indexFormat = mesh.indexFormat;
            existing.vertices = mesh.vertices;
            existing.uv = mesh.uv;
            existing.triangles = mesh.triangles;
            existing.normals = mesh.normals;
            existing.tangents = mesh.tangents;
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }

        static Dictionary<string, Mesh> BuildMeshes() => new()
        {
            ["Blade"] = SaveMesh(
                ProceduralMeshes.CreateTurbineBlade(
                    length: 0.44f, rootChord: 0.075f, tipChord: 0.028f),
                "TurbineBlade"),
            ["Dish"] = SaveMesh(
                ProceduralMeshes.CreateParabolicDish(radius: 0.17f, depth: 0.07f),
                "ParabolicDish"),
            ["Gear24"] = SaveMesh(
                ProceduralMeshes.CreateGear(24, 0.24f, 0.05f, 0.055f, 0.035f), "Gear_24T"),
            ["Gear16"] = SaveMesh(
                ProceduralMeshes.CreateGear(16, 0.16f, 0.05f, 0.055f, 0.030f), "Gear_16T"),
            ["Gear12"] = SaveMesh(
                ProceduralMeshes.CreateGear(12, 0.12f, 0.05f, 0.055f, 0.026f), "Gear_12T"),
        };

        // ---------------------------------------------------------------- model helpers

        static GameObject Primitive(
            PrimitiveType type, Transform parent, string name, Material material,
            Vector3 position, Vector3 scale, Vector3? euler = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            // Colliders would let the models block AR raycasts for no benefit; these are decoration.
            Object.DestroyImmediate(go.GetComponent<Collider>());

            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler ?? Vector3.zero;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static GameObject MeshPart(
            Mesh mesh, Transform parent, string name, Material material,
            Vector3 position, Vector3 scale, Vector3? euler = null)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler ?? Vector3.zero;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static GameObject SavePrefab(GameObject root, string name)
        {
            var path = $"{k_PrefabsDir}/{name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------- the three models

        /// <summary>
        /// A three-bladed horizontal-axis wind turbine. Authored roughly one unit wide so the
        /// spawner can scale it straight to the printed marker's width.
        /// </summary>
        static GameObject BuildTurbinePrefab(
            Dictionary<string, Material> materials, Dictionary<string, Mesh> meshes)
        {
            var root = new GameObject("Model_WindTurbine");
            var t = root.transform;

            Primitive(PrimitiveType.Cylinder, t, "Foundation", materials["Dark"],
                new Vector3(0f, 0.012f, 0f), new Vector3(0.34f, 0.012f, 0.34f));
            Primitive(PrimitiveType.Cylinder, t, "Tower", materials["Shell"],
                new Vector3(0f, 0.44f, 0f), new Vector3(0.062f, 0.44f, 0.062f));
            Primitive(PrimitiveType.Cylinder, t, "TowerCollar", materials["Accent"],
                new Vector3(0f, 0.30f, 0f), new Vector3(0.072f, 0.012f, 0.072f));

            var nacelle = Primitive(PrimitiveType.Cube, t, "Nacelle", materials["Shell"],
                new Vector3(0f, 0.90f, 0.02f), new Vector3(0.10f, 0.10f, 0.26f));
            Primitive(PrimitiveType.Sphere, nacelle.transform, "NacelleNose", materials["Shell"],
                new Vector3(0f, 0f, -0.5f), new Vector3(1f, 1f, 0.45f));

            // The rotor is its own transform so only the blades spin, not the nacelle.
            var rotor = new GameObject("Rotor");
            rotor.transform.SetParent(t, false);
            rotor.transform.localPosition = new Vector3(0f, 0.90f, -0.13f);

            var spin = rotor.AddComponent<SpinAnimator>();
            var spinObject = new SerializedObject(spin);
            spinObject.FindProperty("m_Axis").vector3Value = Vector3.forward;
            spinObject.FindProperty("m_DegreesPerSecond").floatValue = -70f;
            spinObject.ApplyModifiedPropertiesWithoutUndo();

            Primitive(PrimitiveType.Sphere, rotor.transform, "Hub", materials["Accent"],
                Vector3.zero, new Vector3(0.09f, 0.09f, 0.11f));

            for (var i = 0; i < 3; i++)
            {
                MeshPart(meshes["Blade"], rotor.transform, $"Blade_{i + 1}", materials["Shell"],
                    Vector3.zero, Vector3.one, new Vector3(0f, 0f, i * 120f));
            }

            return SavePrefab(root, "Model_WindTurbine");
        }

        /// <summary>A communications satellite: bus, two solar wings, a parabolic dish.</summary>
        static GameObject BuildSatellitePrefab(
            Dictionary<string, Material> materials, Dictionary<string, Mesh> meshes)
        {
            var root = new GameObject("Model_Satellite");

            // Everything hangs off a spinning pivot lifted clear of the picture, so the satellite
            // reads as floating above the marker rather than resting on it.
            var pivot = new GameObject("Pivot");
            pivot.transform.SetParent(root.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 0.45f, 0f);

            var spin = pivot.AddComponent<SpinAnimator>();
            var spinObject = new SerializedObject(spin);
            spinObject.FindProperty("m_Axis").vector3Value = Vector3.up;
            spinObject.FindProperty("m_DegreesPerSecond").floatValue = 22f;
            spinObject.FindProperty("m_BobAmplitude").floatValue = 0.035f;
            spinObject.FindProperty("m_BobCyclesPerSecond").floatValue = 0.25f;
            spinObject.ApplyModifiedPropertiesWithoutUndo();

            var p = pivot.transform;

            Primitive(PrimitiveType.Cube, p, "Bus", materials["Gold"],
                Vector3.zero, new Vector3(0.24f, 0.26f, 0.30f));
            Primitive(PrimitiveType.Cube, p, "BusBand", materials["Dark"],
                new Vector3(0f, 0.04f, 0f), new Vector3(0.25f, 0.04f, 0.31f));

            // Both wings stay in one plane, the way a real solar array is deployed -- tilting
            // them opposite ways made one read as a panel and the other as a bare edge.
            foreach (var side in new[] { -1f, 1f })
            {
                var label = side < 0f ? "L" : "R";

                Primitive(PrimitiveType.Cylinder, p, $"Boom_{label}", materials["Steel"],
                    new Vector3(0.15f * side, 0f, 0f), new Vector3(0.012f, 0.03f, 0.012f),
                    new Vector3(0f, 0f, 90f));

                Primitive(PrimitiveType.Cube, p, $"SolarWing_{label}", materials["SolarPanel"],
                    new Vector3(0.35f * side, 0f, 0f), new Vector3(0.34f, 0.010f, 0.20f));

                // Spar is a sibling, not a child: as a child it inherited the panel's very
                // non-uniform scale and collapsed into a sliver.
                Primitive(PrimitiveType.Cube, p, $"WingSpar_{label}", materials["Steel"],
                    new Vector3(0.35f * side, 0.004f, 0f), new Vector3(0.345f, 0.014f, 0.022f));
            }

            // Dish slung under the bus on a short mast, tilted off-axis (real comsat dishes never
            // point straight down) so its concave face is actually visible from a 3/4 view instead
            // of foreshortening into a sliver.
            Primitive(PrimitiveType.Cylinder, p, "DishMast", materials["Steel"],
                new Vector3(0f, -0.15f, 0f), new Vector3(0.018f, 0.03f, 0.018f));
            MeshPart(meshes["Dish"], p, "Dish", materials["Dish"],
                new Vector3(0f, -0.19f, 0.02f), Vector3.one, new Vector3(150f, 0f, 20f));
            Primitive(PrimitiveType.Cylinder, p, "FeedHorn", materials["Dark"],
                new Vector3(0f, -0.30f, 0.11f), new Vector3(0.022f, 0.022f, 0.022f),
                new Vector3(-30f, 0f, 0f));
            Primitive(PrimitiveType.Cylinder, p, "Antenna", materials["Steel"],
                new Vector3(0.06f, 0.20f, -0.08f), new Vector3(0.008f, 0.10f, 0.008f));

            return SavePrefab(root, "Model_Satellite");
        }

        /// <summary>
        /// Three meshing spur gears on a plate. Speeds are set from the tooth counts, so the
        /// train turns at real gear ratios and neighbouring gears counter-rotate.
        /// </summary>
        static GameObject BuildGearTrainPrefab(
            Dictionary<string, Material> materials, Dictionary<string, Mesh> meshes)
        {
            var root = new GameObject("Model_GearTrain");
            var t = root.transform;

            Primitive(PrimitiveType.Cube, t, "BasePlate", materials["Dark"],
                new Vector3(0f, 0.012f, 0.01f), new Vector3(1.10f, 0.024f, 0.68f));

            foreach (var corner in new[]
                     {
                         new Vector3(-0.50f, 0.026f, -0.27f), new Vector3(0.50f, 0.026f, -0.27f),
                         new Vector3(-0.50f, 0.026f, 0.29f), new Vector3(0.50f, 0.026f, 0.29f),
                     })
            {
                Primitive(PrimitiveType.Cylinder, t, "Bolt", materials["Steel"],
                    corner, new Vector3(0.028f, 0.006f, 0.028f));
            }

            const float driverDegreesPerSecond = 34f;
            const float gearY = 0.06f;

            // Centre distance between meshing gears is the sum of their pitch radii: the 16T and
            // 12T both mesh with the 24T driver, and the layout is centred on the plate.
            var layout = new[]
            {
                (mesh: "Gear24", material: "Steel", teeth: 24,
                    position: new Vector3(-0.064f, gearY, -0.03f), direction: 1f),
                (mesh: "Gear16", material: "Brass", teeth: 16,
                    position: new Vector3(0.336f, gearY, -0.03f), direction: -1f),
                (mesh: "Gear12", material: "Accent", teeth: 12,
                    position: new Vector3(-0.376f, gearY, 0.15f), direction: -1f),
            };

            foreach (var gear in layout)
            {
                var pivot = new GameObject($"Gear_{gear.teeth}T");
                pivot.transform.SetParent(t, false);
                pivot.transform.localPosition = gear.position;

                var spin = pivot.AddComponent<SpinAnimator>();
                var spinObject = new SerializedObject(spin);
                spinObject.FindProperty("m_Axis").vector3Value = Vector3.up;
                spinObject.FindProperty("m_DegreesPerSecond").floatValue =
                    gear.direction * driverDegreesPerSecond * 24f / gear.teeth;
                spinObject.ApplyModifiedPropertiesWithoutUndo();

                MeshPart(meshes[gear.mesh], pivot.transform, "Body", materials[gear.material],
                    Vector3.zero, Vector3.one);

                Primitive(PrimitiveType.Cylinder, t, $"Shaft_{gear.teeth}T", materials["Steel"],
                    gear.position + new Vector3(0f, 0.01f, 0f),
                    new Vector3(0.022f, 0.055f, 0.022f));
            }

            return SavePrefab(root, "Model_GearTrain");
        }

        // ---------------------------------------------------------------- plane overlay

        /// <summary>
        /// The prefab <see cref="ARPlaneManager"/> instantiates for each detected surface.
        /// <para>
        /// This mirrors what AR Foundation's own "GameObject > XR > AR Default Plane" menu item
        /// builds, because that code lives in an <c>internal static</c> class and can only be
        /// copied, not called. Two deliberate differences: both materials are generated here, as
        /// AF's plane material is bound to a simulation-only shader and its line material is the
        /// built-in <c>Default-Line.mat</c>, which is not a URP material and renders wrong here.
        /// </para>
        /// </summary>
        static GameObject BuildPlanePrefab(Material fill, Material outline)
        {
            var root = new GameObject("AR_PlaneVisualizer");

            // ARPlane explicitly and first: ARPlaneMeshVisualizer requires it, and relying on
            // [RequireComponent] auto-add ordering here is needlessly fragile.
            root.AddComponent<ARPlane>();
            root.AddComponent<MeshFilter>();

            var meshRenderer = root.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = fill;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var lineRenderer = root.AddComponent<LineRenderer>();
            lineRenderer.sharedMaterials = new[] { outline };
            lineRenderer.loop = true;
            lineRenderer.widthCurve = new AnimationCurve(new Keyframe(0f, 0.005f));
            lineRenderer.numCornerVertices = 4;
            lineRenderer.numCapVertices = 4;
            lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;

            // The visualizer writes boundary points in plane-local space.
            lineRenderer.useWorldSpace = false;

            root.AddComponent<MeshCollider>();
            root.AddComponent<ARPlaneMeshVisualizer>();

            return SavePrefab(root, "AR_PlaneVisualizer");
        }

        // ---------------------------------------------------------------- scene

        static void BuildScene(
            XRReferenceImageLibrary library, Dictionary<string, GameObject> prefabs,
            GameObject planePrefab)
        {
            // Single mode, not additive: if this exact scene is already open (e.g. the user is
            // looking at it, or a previous run of this tool left it open), building additively
            // over it would collide when saving -- "Overwriting the same path as another open
            // scene is not allowed" -- and closing it first runs into "can't close the last loaded
            // scene" whenever it is the only one open. Single mode replaces whatever is currently
            // open in one step, sidestepping both restrictions, and leaves the rebuilt scene as the
            // one visibly open afterwards, which is what re-running this tool should do anyway.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var session = new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));
            SceneManager.MoveGameObjectToScene(session, scene);

            var originGo = new GameObject("XR Origin", typeof(XROrigin));
            SceneManager.MoveGameObjectToScene(originGo, scene);
            var origin = originGo.GetComponent<XROrigin>();

            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originGo.transform, false);

            var cameraGo = new GameObject(
                "Main Camera",
                typeof(Camera), typeof(AudioListener),
                typeof(ARCameraManager), typeof(ARCameraBackground), typeof(TrackedPoseDriver));
            cameraGo.transform.SetParent(offset.transform, false);
            cameraGo.tag = "MainCamera";

            var camera = cameraGo.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Color;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            // Bindings cover both a phone's AR input device and an HMD, matching what AR
            // Foundation's own "XR Origin (Mobile AR)" menu item creates.
            var positionAction = new InputAction(
                "Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rotationAction = new InputAction(
                "Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");

            var poseDriver = cameraGo.GetComponent<TrackedPoseDriver>();
            poseDriver.positionInput = new InputActionProperty(positionAction);
            poseDriver.rotationInput = new InputActionProperty(rotationAction);

            origin.CameraFloorOffsetObject = offset;
            origin.Camera = camera;

            // ARTrackedImageManager requires the XROrigin, so it lives on the origin itself.
            var imageManager = originGo.AddComponent<ARTrackedImageManager>();
            imageManager.referenceLibrary = library;
            imageManager.requestedMaxNumberOfMovingImages = k_Markers.Length;
            imageManager.trackedImagePrefab = null;

            var spawner = originGo.AddComponent<TrackedImageModelSpawner>();
            var spawnerObject = new SerializedObject(spawner);
            var bindingList = spawnerObject.FindProperty("m_Bindings");
            bindingList.arraySize = k_Markers.Length;

            for (var i = 0; i < k_Markers.Length; i++)
            {
                var (imageName, _, displayName) = k_Markers[i];
                var element = bindingList.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("referenceImageName").stringValue = imageName;
                element.FindPropertyRelative("displayName").stringValue = displayName;
                element.FindPropertyRelative("prefab").objectReferenceValue = prefabs[imageName];
                element.FindPropertyRelative("widthRelativeToImage").floatValue = 0.9f;
                element.FindPropertyRelative("offsetRelativeToImage").vector3Value = Vector3.zero;
            }

            spawnerObject.ApplyModifiedPropertiesWithoutUndo();

            // Plane detection, raycasting and anchoring: the tap-to-place half of the demo.
            // All three carry [RequireComponent(typeof(XROrigin))], so the origin is their only
            // legal home -- same as ARTrackedImageManager above.
            var planeManager = originGo.AddComponent<ARPlaneManager>();
            planeManager.planePrefab = planePrefab;

            // Horizontal only: these models are authored standing upright, so one placed on a
            // wall with yaw-only rotation would hang off it awkwardly. Enabling Vertical later
            // also means orienting to plane.normal instead -- see TapToPlaceSpawner.ResolveYaw.
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

            originGo.AddComponent<ARRaycastManager>();
            originGo.AddComponent<ARAnchorManager>();

            var placer = BuildPlacer(originGo, camera, prefabs);
            var planeToggle = originGo.AddComponent<PlaneVisibilityToggle>();

            BuildEventSystem(scene);
            BuildHud(scene, spawner, placer, planeToggle);

            var lightGo = new GameObject("Directional Light", typeof(Light));
            SceneManager.MoveGameObjectToScene(lightGo, scene);
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            if (!EditorSceneManager.SaveScene(scene, k_ScenePath))
                throw new System.IO.IOException($"Failed to save {k_ScenePath} -- see the Console for the underlying error.");

            // Left open rather than closed: with Single mode above, this is now the Editor's only
            // loaded scene, and closing the last loaded scene isn't supported anyway -- and
            // leaving the freshly rebuilt scene visible is the more useful outcome here.

            AddSceneToBuildSettings(k_ScenePath);
        }

        /// <summary>
        /// Wires the tap-placement spawner. Its pool is built from the same marker table the
        /// image-tracking bindings use, so the random models and their HUD names can never drift
        /// apart from the tracked ones.
        /// </summary>
        static TapToPlaceSpawner BuildPlacer(
            GameObject originGo, Camera camera, Dictionary<string, GameObject> prefabs)
        {
            var placer = originGo.AddComponent<TapToPlaceSpawner>();
            var placerObject = new SerializedObject(placer);

            var list = placerObject.FindProperty("m_Placeables");
            list.arraySize = k_Markers.Length;

            for (var i = 0; i < k_Markers.Length; i++)
            {
                var (imageName, _, displayName) = k_Markers[i];
                var element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("displayName").stringValue = displayName;
                element.FindPropertyRelative("prefab").objectReferenceValue = prefabs[imageName];

                // "Face the camera" means +Z points at it, but the turbine's nacelle nose is
                // built along -Z, so without this it would greet you with its tail.
                element.FindPropertyRelative("yawOffsetDegrees").floatValue =
                    imageName == "Turbine" ? 180f : 0f;
            }

            placerObject.FindProperty("m_WidthMetres").floatValue = k_PlacedWidthMetres;
            placerObject.FindProperty("m_FaceCameraOnPlace").boolValue = true;
            placerObject.FindProperty("m_AvoidImmediateRepeat").boolValue = true;
            placerObject.FindProperty("m_Camera").objectReferenceValue = camera;
            placerObject.ApplyModifiedPropertiesWithoutUndo();

            return placer;
        }

        /// <summary>
        /// The EventSystem the HUD buttons need in order to receive clicks at all.
        /// </summary>
        static void BuildEventSystem(Scene scene)
        {
            // EventSystem first: BaseInputModule is [RequireComponent(typeof(EventSystem))].
            var go = new GameObject("EventSystem", typeof(EventSystem));
            SceneManager.MoveGameObjectToScene(go, scene);

            // InputSystemUIInputModule, never the legacy StandaloneInputModule: this project runs
            // activeInputHandler 1 (Input System only), where every UnityEngine.Input call throws
            // and StandaloneInputModule is built entirely on them.
            //
            // actionsAsset is deliberately left null -- the module assigns its own embedded
            // default actions at runtime when it has none, and anything assigned from here would
            // be an in-memory ScriptableObject that doesn't survive the scene save.
            go.AddComponent<InputSystemUIInputModule>();
        }

        // ---------------------------------------------------------------- HUD

        const string k_InfoText = "lab work 1 - Augmented Reality Engeenering\nLouis Persin";

        static void BuildHud(
            Scene scene, TrackedImageModelSpawner spawner,
            TapToPlaceSpawner placer, PlaneVisibilityToggle planeToggle)
        {
            // GraphicRaycaster is what turns the canvas into something the EventSystem can hit --
            // without it the buttons are inert.
            var canvasGo = new GameObject(
                "HUD Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            SceneManager.MoveGameObjectToScene(canvasGo, scene);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Reference resolution is portrait so text keeps its intended size and position with
            // the player locked to portrait (see ConfigurePortraitOrientation).
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            var objectNameLabel = CreateHudLabel(
                canvasGo.transform, "Object Name Label",
                anchorMin: new Vector2(0.5f, 0f), anchorMax: new Vector2(0.5f, 0f),
                pivot: new Vector2(0.5f, 0f), anchoredPosition: new Vector2(0f, 90f),
                size: new Vector2(920f, 130f), fontSize: 64f,
                alignment: TextAlignmentOptions.Center);
            objectNameLabel.text = string.Empty;
            // Hidden (panel and all) until TrackedImageModelSpawner reports a tracked model.
            var objectNamePanel = objectNameLabel.transform.parent.gameObject;
            objectNamePanel.SetActive(false);

            var infoLabel = CreateHudLabel(
                canvasGo.transform, "Info Label",
                // Top-left and dropped down a bit -- top-center/right is where a phone's
                // hole-punch or notch camera usually sits, so the label is kept clear of it.
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(0f, 1f),
                pivot: new Vector2(0f, 1f), anchoredPosition: new Vector2(24f, -110f),
                size: new Vector2(620f, 140f), fontSize: 32f,
                alignment: TextAlignmentOptions.TopLeft);
            infoLabel.text = k_InfoText;

            // Buttons sit just above the name label, which occupies y 90-220 at this reference
            // resolution. Split either side of centre so neither drifts under a thumb.
            var planesButton = CreateHudButton(
                canvasGo.transform, "Planes Button",
                pivot: new Vector2(1f, 0f), anchoredPosition: new Vector2(-15f, 250f),
                size: new Vector2(450f, 120f), text: "Hide Planes", out var planesLabel);

            var clearButton = CreateHudButton(
                canvasGo.transform, "Clear Button",
                pivot: new Vector2(0f, 0f), anchoredPosition: new Vector2(15f, 250f),
                size: new Vector2(450f, 120f), text: "Clear", out _);

            BindButton(planesButton, planeToggle.Toggle);
            BindButton(clearButton, placer.ClearAll);

            // Back-fill: these components were created before the buttons existed, and the
            // buttons' onClick needed the components. One of the two has to go second.
            var toggleObject = new SerializedObject(planeToggle);
            toggleObject.FindProperty("m_PlanesVisible").boolValue = true;
            toggleObject.FindProperty("m_ButtonLabel").objectReferenceValue = planesLabel;
            toggleObject.ApplyModifiedPropertiesWithoutUndo();

            var placerObject = new SerializedObject(placer);
            var blockers = placerObject.FindProperty("m_BlockingRects");
            blockers.arraySize = 2;
            blockers.GetArrayElementAtIndex(0).objectReferenceValue =
                planesButton.GetComponent<RectTransform>();
            blockers.GetArrayElementAtIndex(1).objectReferenceValue =
                clearButton.GetComponent<RectTransform>();
            placerObject.ApplyModifiedPropertiesWithoutUndo();

            var hud = canvasGo.AddComponent<ObjectNameHud>();
            var hudObject = new SerializedObject(hud);
            hudObject.FindProperty("m_Spawner").objectReferenceValue = spawner;
            hudObject.FindProperty("m_PlacementSpawner").objectReferenceValue = placer;
            hudObject.FindProperty("m_Label").objectReferenceValue = objectNameLabel;
            hudObject.FindProperty("m_Panel").objectReferenceValue = objectNamePanel;
            hudObject.FindProperty("m_PlacementLabelSeconds").floatValue = 2f;
            hudObject.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// A tappable chip, built on <see cref="CreateHudLabel"/> so the two share their panel +
        /// child-text structure.
        /// </summary>
        static Button CreateHudButton(
            Transform parent, string name, Vector2 pivot, Vector2 anchoredPosition,
            Vector2 size, string text, out TMP_Text label)
        {
            label = CreateHudLabel(
                parent, name,
                anchorMin: new Vector2(0.5f, 0f), anchorMax: new Vector2(0.5f, 0f),
                pivot: pivot, anchoredPosition: anchoredPosition,
                size: size, fontSize: 44f, alignment: TextAlignmentOptions.Center);
            label.text = text;

            var panelGo = label.transform.parent.gameObject;
            var image = panelGo.GetComponent<Image>();

            // A mid-tone chip rather than the labels' near-black: Selectable's colour transition
            // MULTIPLIES this by the pressed colour, and a near-black background multiplies to
            // black, so the button would give no visible press feedback at all.
            image.color = new Color(0.10f, 0.42f, 0.70f, 0.88f);
            image.raycastTarget = true;

            var button = panelGo.AddComponent<Button>();

            // AddComponent doesn't assign targetGraphic, and without it the tint does nothing.
            button.targetGraphic = image;

            var colors = button.colors;
            colors.pressedColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;

            return button;
        }

        /// <summary>
        /// Adds a <em>persistent</em> click listener -- the kind the inspector shows and the scene
        /// file stores. A runtime <c>onClick.AddListener</c> lives in a delegate list that is
        /// never serialized, so it would silently do nothing in a build.
        /// </summary>
        static void BindButton(Button button, UnityAction call)
        {
            // The call must be a method group on a UnityEngine.Object, never a lambda: a
            // persistent call is stored as an object reference plus a method name, and a lambda's
            // target is a compiler-generated closure that serializes as null.
            UnityEventTools.AddPersistentListener(button.onClick, call);
            EditorUtility.SetDirty(button);
        }

        /// <summary>
        /// A screen-space label with a translucent backing panel so it stays legible over an
        /// arbitrary camera feed. Anchor/pivot/position follow uGUI's usual anchored-position
        /// convention: e.g. anchor+pivot both (1,1) with a negative offset pins to the top-right.
        /// </summary>
        static TMP_Text CreateHudLabel(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition,
            Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            // The background and the text are separate GameObjects, not two Graphic components
            // (Image + TextMeshProUGUI) stacked on one -- doing that throws a NullReferenceException
            // from inside uGUI's canvas-rebuild plumbing when the object is built and wired up in
            // the same tick, as this editor-script scene generation does.
            var panelGo = new GameObject(name, typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(parent, false);

            var rect = panelGo.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var panelImage = panelGo.GetComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.4f);

            // Labels are decoration. Keeping them out of the UI raycast means the new
            // GraphicRaycaster only ever reports the actual buttons. CreateHudButton turns this
            // back on for the chips it builds.
            panelImage.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(panelGo.transform, false);

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(24f, 12f);
            textRect.offsetMax = new Vector2(-24f, -12f);

            var text = textGo.AddComponent<TextMeshProUGUI>();
            // Assign the font explicitly rather than relying on TMP_Settings' implicit default --
            // that resolution can be a frame late (or simply unset) when this runs from an
            // in-memory editor script rather than the normal Editor GUI flow.
            text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.raycastTarget = false;
            return text;
        }

        // ---------------------------------------------------------------- player settings

        /// <summary>Locks the player to portrait -- this is a handheld, point-the-phone-at-a-
        /// picture experience, not something meant to be held sideways.</summary>
        static void ConfigurePortraitOrientation()
        {
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
        }


        static void AddSceneToBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == path))
                return;

            // First in the list, so an Android build launches straight into the AR scene.
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
