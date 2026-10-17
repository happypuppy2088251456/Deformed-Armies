using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Explicit editor commands only. No automatic scene changes on import or startup.
public static class DragonAiRepair
{
    public const string ScenePath = "Assets/Client260906.unity";
    public const string DragonPath = "Assets/Malbers Animations/Dragons/4 - Unka the Dragon";
    public static string OutputDirectory => Path.Combine(
        Directory.GetParent(Directory.GetParent(Application.dataPath).FullName).FullName,
        ".asset-work/dragon-ai-fix");

    public static void Inspect()
    {
        Directory.CreateDirectory(OutputDirectory);
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var report = new StringBuilder();
        Physics.SyncTransforms();
        foreach (var root in scene.GetRootGameObjects())
        {
            if (!root.name.Contains("Unka") && root.GetComponent("playerMove") == null) continue;
            report.AppendLine($"ROOT {root.name} position={root.transform.position:F3} scale={root.transform.lossyScale:F3} tag={root.tag}");
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("toe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name == "Head" || t.name == "Pelvis" || t.name.StartsWith("Spine"))
                    report.AppendLine($" BONE {t.name} world={t.position:F3} relative={root.transform.InverseTransformPoint(t.position):F3}");
            }
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                report.AppendLine($" ANIMATOR {animator.name} avatar={AssetDatabase.GetAssetPath(animator.avatar)} controller={AssetDatabase.GetAssetPath(animator.runtimeAnimatorController)} rootMotion={animator.applyRootMotion}");
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = new Mesh();
                renderer.BakeMesh(mesh);
                Bounds world = new Bounds(renderer.transform.TransformPoint(mesh.vertices[0]), Vector3.zero);
                foreach (var v in mesh.vertices) world.Encapsulate(renderer.transform.TransformPoint(v));
                report.AppendLine($" MESH {renderer.name} realBounds center={world.center:F3} size={world.size:F3} min={world.min:F3} max={world.max:F3}; renderBounds={renderer.bounds}");
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            var p = root.transform.position;
            foreach (var hit in Physics.RaycastAll(p + Vector3.up * 100f, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance).Take(8))
                report.AppendLine($" GROUND {hit.collider.name} point={hit.point:F3} normal={hit.normal:F3} layer={hit.collider.gameObject.layer}");
        }
        foreach (string file in new[] { "Unka Idle.FBX", "Unka Walk.FBX", "Unka Trot.FBX" })
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(DragonPath + "/Animations/" + file).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")))
                report.AppendLine($"CLIP {file} {clip.name} length={clip.length:F3} averageSpeed={clip.averageSpeed:F3}");
        File.WriteAllText(Path.Combine(OutputDirectory, "inspection.txt"), report.ToString());
        Debug.Log("Dragon inspection complete: " + OutputDirectory);
    }
}
