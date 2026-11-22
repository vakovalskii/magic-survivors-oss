using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class VatBaker
{
    const int Fps = 30;
    static readonly string[,] States = {
        { "Idle", "Idle" }, { "Walk", "Walking_D_Skeletons" }, { "Run", "Running_A" },
        { "Attack", "1H_Melee_Attack_Chop" }, { "Hit", "Hit_A" }, { "Death", "Death_C_Skeletons" } };
    static readonly string[,] Models = {
        { "Skeleton_Warrior", "Skeleton_Axe" }, { "Skeleton_Mage", "Skeleton_Staff" },
        { "Skeleton_Rogue", "Skeleton_Blade" }, { "Skeleton_Minion", "" } };

    [MenuItem("Build/Bake VAT (skeletons)")]
    public static void BakeMenu() => Bake();

    public static void Bake()
    {
        Directory.CreateDirectory("Assets/Resources/Vat");
        for (int i = 0; i < Models.GetLength(0); i++) BakeModel(Models[i, 0], Models[i, 1]);
        AssetDatabase.SaveAssets();
        Debug.Log("vat: bake done");
    }

    static void BakeModel(string name, string weapon)
    {
        var prefab = Resources.Load<GameObject>("Models/Characters/" + name);
        var ctrl = Resources.Load<RuntimeAnimatorController>("Anim/" + name);
        var file = string.IsNullOrEmpty(weapon) ? name + "_VAT" : name + "_" + weapon + "_VAT";
        if (prefab == null || ctrl == null) { Debug.Log($"vat: skip {name} prefab={prefab != null} ctrl={ctrl != null}"); return; }
        var go = Object.Instantiate(prefab);
        var anim = go.GetComponentInChildren<Animator>();
        if (anim == null) anim = go.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var mat = Resources.Load<Material>("Mats/Skeleton");
        if (!string.IsNullOrEmpty(weapon)) World.Attach(go, weapon, mat);
        var combined = World.Combine(go, "vat_" + file, mat);
        var smr = combined.Length > 0 ? combined[0] as SkinnedMeshRenderer : null;
        if (smr == null) { Debug.Log($"vat: skip {name} (no combined mesh)"); Object.DestroyImmediate(go); return; }
        if (smr == null) { Debug.Log($"vat: skip {name} (no skinned mesh)"); Object.DestroyImmediate(go); return; }

        var clips = new Dictionary<string, AnimationClip>();
        foreach (var c in ctrl.animationClips)
        {
            var raw = c.name;
            var shortName = raw.Substring(raw.LastIndexOfAny(new[] { '|', '_' }) + 1);
            if (!clips.ContainsKey(shortName)) clips[shortName] = c;
            if (!clips.ContainsKey(raw)) clips[raw] = c;
        }
        Debug.Log($"vat: {name} clips={string.Join(',', System.Array.ConvertAll(ctrl.animationClips, c => c.name))}");

        int verts = smr.sharedMesh.vertexCount;
        var rows = new List<Color[]>();
        var meta = new StringBuilder();
        int total = 0;
        for (int si = 0; si < States.GetLength(0); si++)
        {
            var st = States[si, 0];
            if (!clips.TryGetValue(States[si, 1], out var clip)) { Debug.Log($"vat: {name} no clip {st}"); continue; }
            int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * Fps));
            var mesh = new Mesh();
            var block = new Color[verts * frames];
            float dev = 0f;
            for (int f = 0; f < frames; f++)
            {
                anim.Play(st, 0, (float)f / (frames - 1));
                anim.Update(0f);
                smr.BakeMesh(mesh);
                var vs = mesh.vertices;
                for (int v = 0; v < verts && v < vs.Length; v++)
                {
                    var p = new Color(vs[v].x, vs[v].y, vs[v].z, 1f);
                    block[f * verts + v] = p;
                    if (f == frames / 2)
                    {
                        var a = block[v];
                        float dx = a.r - p.r, dy = a.g - p.g, dz = a.b - p.b;
                        dev = Mathf.Max(dev, Mathf.Sqrt(dx * dx + dy * dy + dz * dz));
                    }
                }
            }
            rows.Add(block);
            meta.AppendLine($"{st},{total},{frames}");
            Debug.Log($"vat: {name} {st} frames={frames} offset={total} motion={dev:0.000}");
            total += frames;
        }
        var tex = new Texture2D(verts, total, TextureFormat.RGBAHalf, false) { filterMode = FilterMode.Point, name = name + "_VAT" };
        var all = new Color[verts * total];
        int at = 0;
        foreach (var r in rows) { r.CopyTo(all, at); at += r.Length; }
        tex.SetPixels(all);
        tex.Apply();
        AssetDatabase.CreateAsset(tex, $"Assets/Resources/Vat/{file}.asset");
        var text = new TextAsset(meta.ToString());
        AssetDatabase.CreateAsset(text, $"Assets/Resources/Vat/{file}.txt");
        Debug.Log($"vat: {file} verts={verts} total_frames={total}");
        Object.DestroyImmediate(go);
    }
}

public static class VatSetup
{
    public static void MakeMaterial()
    {
        var sh = Shader.Find("Magic/VatLit");
        if (sh == null) { Debug.Log("vat: no VatLit shader"); return; }
        var dir = "Assets/Resources/Mats";
        System.IO.Directory.CreateDirectory(dir);
        var path = dir + "/VatLit.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(sh);
            AssetDatabase.CreateAsset(m, path);
        }
        else m.shader = sh;
        AssetDatabase.SaveAssets();
        Debug.Log("vat: material ok " + path);
    }
}
