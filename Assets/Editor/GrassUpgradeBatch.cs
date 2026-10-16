using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class GrassUpgradeBatch
{
    const string Folder = "Assets/Arts/Generated/GrassDetail/";
    const string Output = "D:/分组作业/.asset-work/grass-upgrade/";
    public static void Run()
    {
        try
        {
            AssetDatabase.Refresh();
            var importer = (TextureImporter)AssetImporter.GetAtPath(Folder + "Grass_Albedo_Generated.png");
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 8;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Folder + "CountrysideGrassDetail.shader");
            if (shader == null || !shader.isSupported) throw new Exception("Grass shader unavailable.");
            CheckShader(shader);
            var original = AssetDatabase.LoadAssetAtPath<Material>("Assets/Countryside - Open World/Materials/Terrain.mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "Terrain_GrassDetail.mat");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, Folder + "Terrain_GrassDetail.mat");
            }
            material.shader = shader;
            material.SetTexture("_MainTex", original.mainTexture);
            material.SetTextureScale("_MainTex", original.mainTextureScale);
            material.SetTextureOffset("_MainTex", original.mainTextureOffset);
            material.SetColor("_Color", original.color);
            material.SetTexture("_GrassTex", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "Grass_Albedo_Generated.png"));
            material.SetFloat("_GrassTileSize", 1.5f);
            material.SetFloat("_GrassStrength", 0.9f);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            var scene = EditorSceneManager.OpenScene("Assets/Client260906.unity", OpenSceneMode.Single);
            var terrain = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true)
                .Single(r => r.sharedMaterials.Contains(original) && r.name.Equals("terrain", StringComparison.OrdinalIgnoreCase));
            var collider = terrain.GetComponent<MeshCollider>();
            if (collider == null) throw new Exception("Terrain collider not found for positioning the preview.");
            Physics.SyncTransforms();
            var readable = ReadTexture(original.mainTexture);
            Vector3 grassPoint = FindGrass(collider, readable);
            UnityEngine.Object.DestroyImmediate(readable);
            var cameraObject = new GameObject("TemporaryGrassPreview");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 65;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 600;
            camera.allowHDR = false;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.transform.position = grassPoint + Vector3.up * 1.7f;
            camera.transform.rotation = Quaternion.Euler(38, 30, 0);
            Capture(camera, "grass-before.png");
            var materials = terrain.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) if (materials[i] == original) materials[i] = material;
            terrain.sharedMaterials = materials;
            PrefabUtility.RecordPrefabInstancePropertyModifications(terrain);
            Capture(camera, "grass-after.png");
            camera.transform.position = new Vector3(399.6f, 3f, 258.6f);
            camera.transform.rotation = Quaternion.Euler(25, 30, 0);
            Capture(camera, "scene-after.png");
            UnityEngine.Object.DestroyImmediate(cameraObject);
            CheckShader(shader);
            if (Physics.simulationMode != SimulationMode.FixedUpdate) throw new Exception("Unexpected physics setting.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            File.WriteAllText(Output + "verification.txt", "PASS\nShader supported; no shader compiler errors.\nGrass point=" + grassPoint + "\nRenderer=" + terrain.name + "\nMaterial=" + AssetDatabase.GetAssetPath(material) + "\nPhysics=" + Physics.simulationMode + "\nTexture=" + material.GetTexture("_GrassTex").width + " x " + material.GetTexture("_GrassTex").height + "\n");
            Debug.Log("GRASS_UPGRADE_PASS");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(Output + "verification.txt", "FAILED\n" + e);
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
    static void CheckShader(Shader shader)
    {
        var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity.ToString() == "Error").ToArray();
        if (errors.Length > 0) throw new Exception(string.Join("\n", errors.Select(m => m.message)));
    }
    static Texture2D ReadTexture(Texture source)
    {
        var target = RenderTexture.GetTemporary(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Graphics.Blit(source, target);
        RenderTexture.active = target;
        var texture = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
        texture.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        return texture;
    }
    static Vector3 FindGrass(MeshCollider collider, Texture2D atlas)
    {
        for (int radius = 0; radius <= 40; radius += 4)
        for (int angle = 0; angle < 360; angle += 30)
        {
            var origin = new Vector3(399.6f + radius * Mathf.Sin(angle * Mathf.Deg2Rad), collider.bounds.max.y + 10, 258.6f + radius * Mathf.Cos(angle * Mathf.Deg2Rad));
            RaycastHit hit;
            if (!collider.Raycast(new Ray(origin, Vector3.down), out hit, 1000)) continue;
            var c = atlas.GetPixelBilinear(hit.textureCoord.x, hit.textureCoord.y);
            if (hit.normal.y > 0.8f && c.g > c.r * 1.07f && c.g > c.b * 1.4f) return hit.point;
        }
        throw new Exception("No grass preview position found near spawn.");
    }
    static void Capture(Camera camera, string filename)
    {
        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        texture.Apply();
        File.WriteAllBytes(Output + filename, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        camera.targetTexture = null;
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
    }
}
