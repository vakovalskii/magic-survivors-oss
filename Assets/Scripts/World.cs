using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 3D-мир: модели KayKit, уровень вокруг мага (земля, декор, свет — по LevelContent из Levels.cs), туман, пост-обработка, смена глав.
public static class World
{
    public static Material Mage, Skeleton, Halloween, Glow, Ground, Additive, Dungeon;
    static readonly Dictionary<string, GameObject> prefabs = new();
    static readonly Dictionary<string, float> heights = new();
    static Light moon;
    static Camera cam;
    public static Quaternion Billboard => cam != null ? cam.transform.rotation : Quaternion.identity;
    static Vignette vignette;
    static Bloom bloom;
    public static void SetBloom(bool on) { if (bloom != null) bloom.active = on; }

    // Низкая графика: без SMAA (на телефоне — совсем без сглаживания, в браузере на ПК — дешёвый FXAA),
    // свечение считается с четверти кадра и короче по цепочке — на мобильных видеокартах это главный расход.
    static bool lowGfx, phoneGfx;
    public static void SetQuality(bool low, bool phone)
    {
        lowGfx = low; phoneGfx = phone;
        if (cam == null || bloom == null) return;
        cam.GetUniversalAdditionalCameraData().antialiasing = !low ? AntialiasingMode.SubpixelMorphologicalAntiAliasing
            : phone ? AntialiasingMode.None : AntialiasingMode.FastApproximateAntialiasing;
        bloom.downscale.Override(low ? BloomDownscaleMode.Quarter : BloomDownscaleMode.Half);
        bloom.maxIterations.Override(low ? (phone ? 3 : 4) : 6);
    }
    static ColorAdjustments grade;
    static Transform ground;
    static readonly List<Transform> props = new();
    static readonly List<Renderer> mist = new();
    static MaterialPropertyBlock mistMpb;
    const float Period = 56f, GroundTile = 6f;

    public struct Stage
    {
        public string title;
        public Color moon, ambient, fog, ground;
        public float moonI;
    }

    // главы текущего уровня (Game.UpdateStage меняет их каждые 5 минут)
    public static Stage[] Stages;
    public static LevelContent Level;

    public static Vector3 W(Vector2 p, float y = 0) => new(p.x, y, p.y);

    // Кэш материалов вместо MaterialPropertyBlock: так SRP Batcher склеивает отрисовку сотен объектов.
    static readonly Dictionary<int, Material> glowMats = new(), skelMats = new();
    static Material flashMat;

    static int ColorKey(Color c, int salt) =>
        (Mathf.RoundToInt(c.r * 20) * 73856093) ^ (Mathf.RoundToInt(c.g * 20) * 19349663) ^ (Mathf.RoundToInt(c.b * 20) * 83492791) ^ salt;

    public static Material GlowMat(Color hdr)
    {
        int k = ColorKey(hdr, 1);
        if (!glowMats.TryGetValue(k, out var m) || m == null)
        {
            m = new Material(Glow);
            m.SetColor("_BaseColor", hdr);
            glowMats[k] = m;
        }
        return m;
    }

    public static Material SkelMat(Color tint, float glow)
    {
        int k = ColorKey(tint, Mathf.RoundToInt(glow * 100) + 7);
        if (!skelMats.TryGetValue(k, out var m) || m == null)
        {
            // оттенок уровня ложится на все цвета мобов: в склепе — бледно-зелёные призраки, в топях — тина
            m = new Material(Skeleton);
            var c = tint * Level.enemyTint;
            m.SetColor("_BaseColor", c);
            m.SetColor("_EmissionColor", c * (glow + Level.enemyGlow));
            skelMats[k] = m;
        }
        return m;
    }

    public static Color VatTint(Color tint, float glow)
    {
        var c = tint * Level.enemyTint;
        return c * (1f + Mathf.Min(0.8f, glow + Level.enemyGlow));
    }

    public static Material FlashMat
    {
        get
        {
            if (flashMat == null)
            {
                flashMat = new Material(Skeleton);
                // вспышка от удара не должна стирать текстуру: HDR нет, поэтому и база, и свечение держатся
                // в пределах единицы, а цвет уходит в тёплый пересвет вместо белого
                flashMat.SetColor("_BaseColor", new Color(0.95f, 0.72f, 0.66f));
                flashMat.SetColor("_EmissionColor", new Color(0.35f, 0.16f, 0.12f));
            }
            return flashMat;
        }
    }

    public static void Init(Camera camera, string level = "graveyard")
    {
        cam = camera;
        Level = LevelContent.Get(level);
        Stages = Level.stages;
        prefabs.Clear();
        props.Clear(); mist.Clear(); solids.Clear(); solidPos = new Vector2[0];
        baked.Clear();
        tinted.Clear();
        glowMats.Clear();
        skelMats.Clear();
        flashMat = null;
        Mage = Resources.Load<Material>("Mats/Mage");
        Skeleton = Resources.Load<Material>("Mats/Skeleton");
        Halloween = Resources.Load<Material>("Mats/Halloween");
        Dungeon = Resources.Load<Material>("Mats/Dungeon");
        Glow = Resources.Load<Material>("Mats/Glow");
        Additive = Resources.Load<Material>("Mats/Additive");
        Ground = new Material(Resources.Load<Material>("Mats/Ground"));

        // Луна, амбиент и туман дают глубину: дальний край поля тонет в темноте
        moon = new GameObject("Moon").AddComponent<Light>();
        moon.type = LightType.Directional;
        moon.shadows = LightShadows.Soft;
        moon.shadowStrength = 0.8f;
        moon.transform.rotation = Level.sun;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = Level.fogStart;
        RenderSettings.fogEndDistance = Level.fogEnd;

        cam.orthographic = false;
        cam.fieldOfView = 40;
        cam.nearClipPlane = 0.5f;
        cam.farClipPlane = 80;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.allowHDR = true;
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

        var vol = new GameObject("Post").AddComponent<Volume>();
        vol.isGlobal = true;
        var prof = ScriptableObject.CreateInstance<VolumeProfile>();
        bloom = prof.Add<Bloom>(true);
        bloom.threshold.Override(0.9f);
        bloom.intensity.Override(1.4f);
        bloom.scatter.Override(0.7f);
        vignette = prof.Add<Vignette>(true);
        vignette.intensity.Override(0.4f);
        vignette.smoothness.Override(0.45f);
        vignette.color.Override(Color.black);
        prof.Add<Tonemapping>(true).mode.Override(TonemappingMode.ACES);
        grade = prof.Add<ColorAdjustments>(true);
        grade.contrast.Override(18);
        grade.saturation.Override(12);
        grade.postExposure.Override(0.45f);
        vol.profile = prof;
        SetQuality(lowGfx, phoneGfx);

        BuildGround();
        BuildProps();
        BuildPools();
        BuildMist();
        SetStage(Stages[0]);
    }

    static GameObject Prefab(string path)
    {
        if (!prefabs.TryGetValue(path, out var p) || p == null)
        {
            p = Resources.Load<GameObject>("Models/" + path);
            if (p == null) Debug.LogError("Нет модели " + path);
            prefabs[path] = p;
        }
        return p;
    }

    // Персонаж KayKit нужного роста с аниматором из Resources/Anim.
    public static GameObject Character(string model, float height, out Animator anim, out Renderer[] rends)
    {
        var go = Object.Instantiate(Prefab("Characters/" + model));
        go.name = model;
        var mat = model.StartsWith("Skeleton") ? Skeleton : Mage;
        rends = go.GetComponentsInChildren<Renderer>();
        foreach (var r in rends)
        {
            r.sharedMaterial = mat;
            if (r is SkinnedMeshRenderer sk) { sk.updateWhenOffscreen = false; sk.skinnedMotionVectors = false; }
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        if (!heights.TryGetValue(model, out var h))
        {
            var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in rends) b.Encapsulate(r.bounds);
            heights[model] = h = Mathf.Max(0.1f, b.size.y);
        }
        go.transform.localScale *= height / h;
        anim = go.GetComponent<Animator>();
        if (anim == null) anim = go.AddComponent<Animator>();
        anim.runtimeAnimatorController = Resources.Load<RuntimeAnimatorController>("Anim/" + model);
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        return go;
    }

    // ---------- Склейка персонажа в один SkinnedMeshRenderer ----------
    // У скелета KayKit 9 скинованных частей + шлем и оружие: 11 draw call на врага. Склеиваем в один меш
    // с общим скелетом (один раз на модель), дальше каждому экземпляру — 1 draw call.

    class Baked { public Mesh mesh; public string[] bones; public string rootBone; }
    static readonly Dictionary<string, Baked> baked = new();

    public static Renderer[] Combine(GameObject go, string key, Material mat, bool vatOn = false)
    {
        var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (smrs.Length == 0) return go.GetComponentsInChildren<Renderer>();
        var all = new Dictionary<string, Transform>();
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) all.TryAdd(t.name, t);
        var holder = new GameObject("Combined");
        holder.transform.SetParent(go.transform, false);
        if (!baked.TryGetValue(key, out var b)) baked[key] = b = Bake(go, smrs, holder.transform);
        var parts = key.Split('|');
        var model = parts[0];
        var weapon = parts.Length > 1 ? parts[1] : "";
        var vat = VatMobs.Get(model, weapon);
        var vmat = vatOn && vat != null && !vatOff && model.StartsWith("Skeleton") && vat.tex.width == b.mesh.vertexCount ? VatMaterial(model, vat) : null;
        if (vmat != null)
        {
            holder.AddComponent<MeshFilter>().sharedMesh = b.mesh;
            var mrv = holder.AddComponent<MeshRenderer>();
            mrv.sharedMaterial = vmat;
            mrv.shadowCastingMode = ShadowCastingMode.Off;
            mrv.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            mrv.lightProbeUsage = LightProbeUsage.Off;
            mrv.reflectionProbeUsage = ReflectionProbeUsage.Off;
            holder.AddComponent<VatActor>().Setup(vat, mrv);
            foreach (var r0 in go.GetComponentsInChildren<Renderer>())
                if (r0 != mrv) r0.enabled = false;
            var an = go.GetComponent<Animator>();
            if (an != null) an.enabled = false;
            return new Renderer[] { mrv };
        }
        var smr = holder.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = b.mesh;
        var bones = new Transform[b.bones.Length];
        for (int i = 0; i < bones.Length; i++) bones[i] = all[b.bones[i]];
        smr.bones = bones;
        smr.rootBone = all[b.rootBone];
        smr.sharedMaterial = mat;
        smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 6);
        smr.updateWhenOffscreen = false;
        smr.skinnedMotionVectors = false;
        smr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        smr.lightProbeUsage = LightProbeUsage.Off;
        smr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
            if (r != smr) r.enabled = false;
        return new Renderer[] { smr };
    }

    static readonly Dictionary<string, Material> vatMats = new();

    public static bool vatOff = (Application.absoluteURL ?? "").Contains("novat=1");

    public static Material VatMaterial(string model, VatMobs.Info info)
    {
        if (vatMats.TryGetValue(model, out var m) && m != null) return m;
        var src = Resources.Load<Material>("Mats/VatLit");
        if (src == null) return null;
        m = new Material(src);
        m.SetTexture("_MainTex", Skeleton != null ? Skeleton.mainTexture : null);
        m.SetTexture("_VatTex", info.tex);
        int total = 0;
        foreach (var kv in info.offset) total = Mathf.Max(total, kv.Value + (info.frames.TryGetValue(kv.Key, out var f) ? f : 0));
        m.SetFloat("_VatFrames", Mathf.Max(1, total));
        m.SetFloat("_VatVerts", Mathf.Max(1, info.tex.width));
        m.SetFloat("_VatFps", 30f);
        vatMats[model] = m;
        return m;
    }

    static Baked Bake(GameObject go, SkinnedMeshRenderer[] smrs, Transform R)
    {
        var boneIdx = new Dictionary<Transform, int>();
        var boneList = new List<Transform>();
        var bind = new List<Matrix4x4>();
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
        var weights = new List<BoneWeight>(); var tris = new List<int>();

        int Bone(Transform t, Matrix4x4 bp)
        {
            if (!boneIdx.TryGetValue(t, out var i)) { i = boneList.Count; boneIdx[t] = i; boneList.Add(t); bind.Add(bp); }
            return i;
        }

        foreach (var s in smrs)
        {
            var m = s.sharedMesh;
            if (m == null) continue;
            var toR = R.worldToLocalMatrix * s.transform.localToWorldMatrix;
            var inv = toR.inverse;
            var bp = m.bindposes;
            var map = new int[s.bones.Length];
            for (int i = 0; i < map.Length; i++) map[i] = Bone(s.bones[i], bp[i] * inv);
            int baseV = verts.Count;
            var mv = m.vertices; var mn = m.normals; var mu = m.uv; var mw = m.boneWeights;
            for (int i = 0; i < mv.Length; i++)
            {
                verts.Add(toR.MultiplyPoint3x4(mv[i]));
                norms.Add(i < mn.Length ? toR.MultiplyVector(mn[i]).normalized : Vector3.up);
                uvs.Add(i < mu.Length ? mu[i] : Vector2.zero);
                var w = mw[i];
                w.boneIndex0 = map[w.boneIndex0]; w.boneIndex1 = map[w.boneIndex1];
                w.boneIndex2 = map[w.boneIndex2]; w.boneIndex3 = map[w.boneIndex3];
                weights.Add(w);
            }
            foreach (var t in m.triangles) tris.Add(baseV + t);
        }

        // Жёсткие части (шлем, оружие) висят на кости: переводим их в пространство бинд-позы этой кости
        foreach (var mr in go.GetComponentsInChildren<MeshRenderer>())
        {
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var b = mr.transform.parent;
            while (b != null && !boneIdx.ContainsKey(b)) b = b.parent;
            if (b == null) continue;
            int bi = boneIdx[b];
            var M = bind[bi].inverse * (b.worldToLocalMatrix * mr.transform.localToWorldMatrix);
            var m = mf.sharedMesh;
            int baseV = verts.Count;
            var mv = m.vertices; var mn = m.normals; var mu = m.uv;
            for (int i = 0; i < mv.Length; i++)
            {
                verts.Add(M.MultiplyPoint3x4(mv[i]));
                norms.Add(i < mn.Length ? M.MultiplyVector(mn[i]).normalized : Vector3.up);
                uvs.Add(i < mu.Length ? mu[i] : Vector2.zero);
                weights.Add(new BoneWeight { boneIndex0 = bi, weight0 = 1 });
            }
            foreach (var t in m.triangles) tris.Add(baseV + t);
        }

        var mesh = new Mesh { name = "Combined_" + go.name };
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.boneWeights = weights.ToArray();
        mesh.bindposes = bind.ToArray();
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        var names = new string[boneList.Count];
        for (int i = 0; i < names.Length; i++) names[i] = boneList[i].name;
        return new Baked { mesh = mesh, bones = names, rootBone = smrs[0].rootBone != null ? smrs[0].rootBone.name : boneList[0].name };
    }

    // weapon — модель в правую руку; "правая+левая" — ещё и во вторую (щит), "+щит" — только в левую
    public static void Attach(GameObject who, string weapon, Material mat)
    {
        if (string.IsNullOrEmpty(weapon)) return;
        var parts = weapon.Split('+');
        for (int i = 0; i < parts.Length && i < 2; i++)
        {
            if (parts[i].Length == 0) continue;
            var slot = Find(who.transform, i == 0 ? "handslot.r" : "handslot.l");
            var w = Prefab("Characters/" + parts[i]);
            if (slot == null || w == null) continue;
            var go = Object.Instantiate(w, slot, false);
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
        }
    }

    static Transform Find(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            var f = Find(c, name);
            if (f != null) return f;
        }
        return null;
    }

    public static GameObject Primitive(PrimitiveType type, Material mat, float scale)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.Destroy(go.GetComponent<Collider>());
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        go.transform.localScale = Vector3.one * scale;
        return go;
    }

    // ---------- Сердечко-хилка: выдавленный контур сердца ----------

    static Mesh heart;

    public static Mesh Heart()
    {
        if (heart != null) return heart;
        int n = 48;
        float d = 0.3f;
        var v = new List<Vector3> { new(0, 0, -d) };
        for (int i = 0; i < n; i++)
        {
            float t = i * Mathf.PI * 2 / n;
            float x = 16 * Mathf.Pow(Mathf.Sin(t), 3);
            float y = 13 * Mathf.Cos(t) - 5 * Mathf.Cos(2 * t) - 2 * Mathf.Cos(3 * t) - Mathf.Cos(4 * t);
            v.Add(new Vector3(x / 17f, y / 17f + 0.1f, -d * 0.4f));
        }
        v.Add(new Vector3(0, 0, d));
        for (int i = 0; i < n; i++) v.Add(new Vector3(v[1 + i].x, v[1 + i].y, d * 0.4f));
        var tri = new List<int>();
        void Two(int a, int b, int c) { tri.AddRange(new[] { a, b, c, a, c, b }); }
        int back = n + 1;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Two(0, 1 + i, 1 + j);
            Two(back, back + 1 + i, back + 1 + j);
            Two(1 + i, 1 + j, back + 1 + i);
            Two(1 + j, back + 1 + j, back + 1 + i);
        }
        heart = new Mesh { name = "Heart" };
        heart.SetVertices(v);
        heart.SetTriangles(tri, 0);
        heart.RecalculateNormals();
        heart.RecalculateBounds();
        return heart;
    }

    public static GameObject Prop(string path, Material mat)
    {
        var pf = Prefab(path);
        var go = Object.Instantiate(pf);
        foreach (var r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
        return go;
    }

    // ---------- Земля: бесшовная процедурная текстура по палитре уровня ----------

    static void BuildGround()
    {
        int n = 256;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 4 };
        var px = new Color[n * n];
        var L = Level;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float a = Tile(x, y, n, 0.035f), b = Tile(x, y, n, 0.12f);
            var c = L.ground switch
            {
                "tiles" => Slab(x, y, n, a, b),
                "swamp" => Swamp(x, y, n, a, b),
                _ => Color.Lerp(L.groundA, L.groundB, Mathf.SmoothStep(0.35f, 0.65f, a)) * (0.8f + 0.35f * b),
            };
            c.a = 1;
            px[y * n + x] = c;
        }
        var rnd = new System.Random(3);
        for (int k = 0; k < 2500; k++)
        {
            int i = rnd.Next(px.Length);
            px[i] *= rnd.NextDouble() < 0.5 ? 0.6f : 1.35f;
            px[i].a = 1;
        }
        t.SetPixels(px);
        t.Apply();
        Ground.SetTexture("_BaseMap", t);
        Ground.SetFloat("_Smoothness", L.groundSmooth);

        var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Object.Destroy(g.GetComponent<Collider>());
        g.name = "Ground";
        g.transform.localScale = new Vector3(12, 1, 12); // 120 x 120
        g.GetComponent<Renderer>().sharedMaterial = Ground;
        Ground.SetTextureScale("_BaseMap", Vector2.one * (120f / GroundTile));
        ground = g.transform;
    }

    // Каменные плиты склепа: 4×4 плиты на тайл (1.5 м), у каждой свой тон, тёмные швы, сколы по краям и мох в швах
    static Color Slab(int x, int y, int n, float a, float b)
    {
        var L = Level;
        int cell = n / 4;
        int cx = x / cell, cy = y / cell;
        // ряды со сдвигом на полплиты, как кладка
        int sx = (x + (cy % 2) * cell / 2) % n;
        cx = sx / cell;
        float u = (sx % cell) / (float)cell, v = (y % cell) / (float)cell;
        float h = Mathf.Abs(Mathf.Sin((cx * 12.9898f + cy * 78.233f) * 43758.5453f) % 1f);
        var c = L.groundA * (0.78f + 0.3f * h) * (0.85f + 0.25f * b);
        float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
        float seam = 0.035f + 0.03f * a;
        if (edge < seam) c = Color.Lerp(L.groundB, L.groundC, Mathf.SmoothStep(0.45f, 0.7f, a));
        else if (edge < seam + 0.03f) c *= 0.8f;
        // трещины и пятна сырости
        if (Mathf.Abs(Tile(x, y, n, 0.09f) - 0.5f) < 0.012f) c = Color.Lerp(c, L.groundB, 0.7f);
        if (a > 0.62f) c = Color.Lerp(c, L.groundC, (a - 0.62f) * 1.6f);
        return c;
    }

    // Болото: грязь (A), чёрная стоячая вода (B) и мох с ряской (C)
    static Color Swamp(int x, int y, int n, float a, float b)
    {
        var L = Level;
        float moss = Tile(x, y, n, 0.07f);
        var c = Color.Lerp(L.groundA, L.groundC, Mathf.SmoothStep(0.4f, 0.6f, moss)) * (0.8f + 0.35f * b);
        // вода в низинах шума, у кромки — мокрая тёмная грязь
        float w = Mathf.SmoothStep(0.5f, 0.45f, a);
        float shore = Mathf.SmoothStep(0.56f, 0.5f, a) * (1 - w);
        c *= 1 - 0.35f * shore;
        c = Color.Lerp(c, L.groundB * (0.9f + 0.2f * b), w);
        // ряска на воде
        if (w > 0.5f && Tile(x, y, n, 0.2f) > 0.62f) c = Color.Lerp(c, L.groundC * 1.2f, 0.6f);
        return c;
    }

    // Бесшовный шум: смесь четырёх сдвинутых копий
    static float Tile(int x, int y, int n, float k)
    {
        float fx = (float)x / n, fy = (float)y / n;
        return Mathf.Lerp(
            Mathf.Lerp(Mathf.PerlinNoise(x * k, y * k), Mathf.PerlinNoise((x - n) * k, y * k), fx),
            Mathf.Lerp(Mathf.PerlinNoise(x * k, (y - n) * k), Mathf.PerlinNoise((x - n) * k, (y - n) * k), fx), fy);
    }

    // ---------- Декор уровня ----------

    static Material PropMat(string path) =>
        path.StartsWith("Props/") ? Halloween : path.StartsWith("Dungeon/") ? Dungeon : null;

    static void BuildProps()
    {
        var root = new GameObject("Props").transform;
        foreach (var d in Level.props)
        for (int i = 0, n = d.at?.Length ?? d.count; i < n; i++)
        {
            var pf = Prefab(d.path);
            if (pf == null) break;
            var go = Object.Instantiate(pf, root);
            var mat = PropMat(d.path);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                if (mat != null) r.sharedMaterial = mat;
                else if (d.tint != Color.white) r.sharedMaterials = System.Array.ConvertAll(r.sharedMaterials, m => Tinted(m, d.tint));
            go.transform.localScale *= d.scale * 0.75f;
            Vector2 p;
            float yaw;
            if (d.at != null) { p = new Vector2(d.at[i].x, d.at[i].y); yaw = d.at[i].z; }
            else
            {
                do p = new Vector2(Random.Range(-Period / 2, Period / 2), Random.Range(-Period / 2, Period / 2));
                while (p.magnitude < d.minR || d.avoid.Contains(p));
                yaw = Random.Range(0, 360f);
            }
            go.transform.SetPositionAndRotation(Vector3.zero, pf.transform.rotation);
            if (!d.walk) AddSolid(go, d.path.Substring(d.path.LastIndexOf('/') + 1), yaw, d.sink);
            go.transform.SetPositionAndRotation(W(p, -d.sink), Quaternion.Euler(0, yaw, 0) * pf.transform.rotation);
            if (d.lightI > 0)
            {
                var l = new GameObject("PropLight").AddComponent<Light>();
                l.transform.SetParent(go.transform, false);
                l.transform.localPosition = Vector3.up * (d.lightY + d.sink) / Mathf.Max(0.01f, go.transform.localScale.y);
                l.type = LightType.Point;
                l.color = d.light;
                l.intensity = d.lightI;
                l.range = d.lightR;
                l.shadows = LightShadows.None;
            }
            props.Add(go.transform);
        }
    }

    static readonly Dictionary<(Material, Color), Material> tinted = new();

    static Material Tinted(Material m, Color tint)
    {
        if (!tinted.TryGetValue((m, tint), out var t) || t == null)
        {
            t = new Material(m);
            t.SetColor("_BaseColor", m.GetColor("_BaseColor") * tint);
            tinted[(m, tint)] = t;
        }
        return t;
    }

    // Стоячая вода: неровные плоские пятна, гладкие — ловят блик луны; кувшинки сверху
    static void BuildPools()
    {
        if (Level.pools == 0) return;
        var mat = new Material(Resources.Load<Material>("Mats/Ground"));
        mat.SetTexture("_BaseMap", null);
        mat.SetColor("_BaseColor", Level.pool);
        mat.SetFloat("_Smoothness", Level.poolSmooth);
        if (Level.poolEmit.maxColorComponent > 0)
        {
            // лава светится сама: эмиссия поверх цвета, bloom подхватывает
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", Level.poolEmit);
        }
        var root = new GameObject("Pools").transform;
        for (int i = 0; i < Level.pools; i++)
        {
            var go = new GameObject("Pool");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = Blob(Random.Range(1.5f, 3.2f), Random.Range(0.6f, 1f));
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            Vector2 p;
            do p = new Vector2(Random.Range(-Period / 2, Period / 2), Random.Range(-Period / 2, Period / 2));
            while (p.magnitude < 4);
            go.transform.SetPositionAndRotation(W(p, 0.02f), Quaternion.Euler(0, Random.Range(0, 360f), 0));
            if (Level.poolDecor != null)
                for (int k = Random.Range(1, 4); k > 0; k--)
                {
                    var lily = Object.Instantiate(Prefab(Level.poolDecor[Random.Range(0, Level.poolDecor.Length)]), go.transform);
                    lily.transform.localScale *= 2.6f;
                    lily.transform.localPosition = new Vector3(Random.Range(-0.8f, 0.8f), 0, Random.Range(-0.6f, 0.6f));
                    lily.transform.localRotation = Quaternion.Euler(0, Random.Range(0, 360f), 0) * lily.transform.localRotation;
                }
            props.Add(go.transform);
        }
    }

    // Плоский многоугольник с шумным радиусом: лужа неправильной формы
    static Mesh Blob(float r, float squash)
    {
        int n = 20;
        var v = new Vector3[n + 1];
        var tri = new int[n * 3];
        float ph = Random.Range(0, 10f);
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2 / n;
            float k = r * (0.75f + 0.45f * Mathf.PerlinNoise(ph + Mathf.Cos(a), ph + Mathf.Sin(a)));
            v[i + 1] = new Vector3(Mathf.Cos(a) * k, 0, Mathf.Sin(a) * k * squash);
            tri[i * 3] = 0; tri[i * 3 + 1] = 1 + (i + 1) % n; tri[i * 3 + 2] = 1 + i;
        }
        var m = new Mesh { name = "Pool" };
        m.vertices = v;
        m.triangles = tri;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // Стелющийся туман: большие полупрозрачные пятна дыма над землёй, переносятся за магом вместе с декором
    static void BuildMist()
    {
        if (Level.mistCount == 0) return;
        mistMpb ??= new MaterialPropertyBlock();
        var mat = new Material(Resources.Load<Material>("Mats/Alpha")) { mainTexture = Art.Tex("smoke_01") };
        mat.SetColor("_Color", Level.mist);
        var root = new GameObject("Mist").transform;
        for (int i = 0; i < Level.mistCount; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(root, false);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            var p = new Vector2(Random.Range(-Period / 2, Period / 2), Random.Range(-Period / 2, Period / 2));
            q.transform.SetPositionAndRotation(W(p, Level.mistY + Random.Range(0f, 0.5f)), Quaternion.Euler(90, Random.Range(0, 360f), 0));
            q.transform.localScale = Vector3.one * Random.Range(7f, 12f);
            props.Add(q.transform);
            mist.Add(r);
        }
    }

    // ---------- Коллизии с декором ----------
    // Каждый твёрдый предмет — капсула на земле (отрезок a–b + радиус) относительно его центра.
    // Ограда вытянута в капсулу по длинной стороне, у деревьев твёрдый только ствол, мелочь (PropDef.walk) проходима.

    struct Solid { public Transform tr; public Vector2 a, b; public float r, reach, h, half; }
    static readonly List<Solid> solids = new();
    static Vector2[] solidPos = new Vector2[0];
    static void AddSolid(GameObject go, string name, float yaw, float sink)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;
        var bb = rs[0].bounds;
        foreach (var r in rs) bb.Encapsulate(r.bounds);
        var rot = Quaternion.Euler(0, yaw, 0);
        Vector2 V(Vector3 v) { var w = rot * v; return new Vector2(w.x, w.z); }
        var sol = new Solid { tr = go.transform, h = bb.size.y - sink, half = Mathf.Max(bb.size.x, bb.size.z) / 2 };
        if (name.StartsWith("tree"))
        {
            sol.r = Mathf.Clamp(Mathf.Min(bb.size.x, bb.size.z) * 0.12f, 0.25f, 0.5f);
        }
        else
        {
            bool alongX = bb.size.x >= bb.size.z;
            float L = Mathf.Max(bb.size.x, bb.size.z), S = Mathf.Min(bb.size.x, bb.size.z);
            sol.r = Mathf.Max(0.2f, S * 0.45f);
            float half = Mathf.Max(0, (L - S) / 2);
            var c = new Vector3(bb.center.x, 0, bb.center.z);
            var ax = alongX ? Vector3.right * half : Vector3.forward * half;
            sol.a = V(c - ax);
            sol.b = V(c + ax);
        }
        sol.reach = Mathf.Max(sol.a.magnitude, sol.b.magnitude) + sol.r;
        solids.Add(sol);
    }

    // твёрдым становится и собранное в коде (стойка и стол таверны, GameTavern.cs) — капсула по габариту.
    // AddSolid ждёт предмет в начале координат, а этот уже стоит на месте: габарит переводим в смещение от центра
    public static void AddBlock(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;
        var bb = rs[0].bounds;
        foreach (var r in rs) bb.Encapsulate(r.bounds);
        var p = go.transform.position;
        bool alongX = bb.size.x >= bb.size.z;
        float L = Mathf.Max(bb.size.x, bb.size.z), S = Mathf.Min(bb.size.x, bb.size.z), half = Mathf.Max(0, (L - S) / 2);
        var c = new Vector2(bb.center.x - p.x, bb.center.z - p.z);
        var ax = alongX ? new Vector2(half, 0) : new Vector2(0, half);
        var sol = new Solid { tr = go.transform, h = bb.size.y, half = L / 2, r = Mathf.Max(0.2f, S * 0.5f), a = c - ax, b = c + ax };
        sol.reach = Mathf.Max(sol.a.magnitude, sol.b.magnitude) + sol.r;
        solids.Add(sol);
    }

    // Выталкивает круг радиуса rad из всех предметов: упёрся — скользит вдоль
    // minH > 0 — в прыжке: низкий декор перелетаем, о высокий (склеп, деревья, фонари) всё равно упираемся
    public static Vector2 Collide(Vector2 p, float rad, float minH = 0)
    {
        for (int i = 0; i < solidPos.Length; i++)
        {
            var c = solidPos[i];
            var s = solids[i];
            if (s.h < minH) continue;
            float reach = s.reach + rad;
            if (Mathf.Abs(p.x - c.x) > reach || Mathf.Abs(p.y - c.y) > reach) continue;
            var a = c + s.a;
            var ab = s.b - s.a;
            float l2 = ab.sqrMagnitude;
            float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0;
            var q = a + ab * t;
            var d = p - q;
            float m = d.magnitude, min = s.r + rad;
            if (m >= min) continue;
            p = m > 1e-4f ? q + d / m * min : q + Vector2.right * min;
        }
        return p;
    }

    // Свободное место для сундука/реликвии: точка выталкивается из твёрдого декора с запасом rad.
    // В отличие от Collide считает по периодической решётке декора: точка может лежать далеко от мага,
    // куда предметы ещё не «переехали» (Follow переносит их только вокруг мага).
    public static Vector2 Free(Vector2 p, float rad)
    {
        for (int pass = 0; pass < 6; pass++)
        {
            bool moved = false;
            for (int i = 0; i < solids.Count; i++)
            {
                var s = solids[i];
                var tp = s.tr.position;
                // ближайшая к точке копия предмета
                var c = new Vector2(p.x + Mathf.Repeat(tp.x - p.x + Period / 2, Period) - Period / 2,
                                    p.y + Mathf.Repeat(tp.z - p.y + Period / 2, Period) - Period / 2);
                float reach = s.reach + rad;
                if (Mathf.Abs(p.x - c.x) > reach || Mathf.Abs(p.y - c.y) > reach) continue;
                var a = c + s.a;
                var ab = s.b - s.a;
                float l2 = ab.sqrMagnitude;
                float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0;
                var q = a + ab * t;
                var d = p - q;
                float m = d.magnitude, min = s.r + rad;
                if (m >= min) continue;
                p = m > 1e-4f ? q + d / m * (min + 0.05f) : q + Vector2.right * (min + 0.05f);
                moved = true;
            }
            if (!moved) break;
        }
        return p;
    }

    // Стоит ли высокий декор (склеп, дерево, фонарь) между камерой и магом — тогда маг подсвечивается силуэтом.
    // Проверяем на экране: проекция коробки декора накрывает грудь мага, и декор ближе к камере. Старая проверка по
    // плоскости брала зону с высоту дерева позади него — силуэт включался вдали от дерева, и маг светился сам об себя
    public static bool Occluded(Vector2 player)
    {
        var cam = Camera.main;
        if (cam == null) return false;
        var pw = new Vector3(player.x, 0.9f, player.y);
        var ps = cam.WorldToScreenPoint(pw);
        if (ps.z <= 0) return false;
        for (int i = 0; i < solidPos.Length; i++)
        {
            var s = solids[i];
            if (s.h < 1.6f) continue;
            var c = solidPos[i];
            // грубый отсев: декор далеко от мага не заслоняет
            if (Mathf.Abs(player.x - c.x) > s.half + 1.5f || player.y - c.y < -s.half || player.y - c.y > s.h + s.half) continue;
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, zNear = float.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                var w = new Vector3(c.x + ((k & 1) == 0 ? -s.half : s.half), (k & 2) == 0 ? 0 : s.h, c.y + ((k & 4) == 0 ? -s.half : s.half));
                var q = cam.WorldToScreenPoint(w);
                x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y); zNear = Mathf.Min(zNear, q.z);
            }
            // крона круглая, а коробка — нет: ужимаем на четверть по краям
            float mx = (x1 - x0) * 0.2f, my = (y1 - y0) * 0.12f;
            if (ps.x > x0 + mx && ps.x < x1 - mx && ps.y > y0 + my && ps.y < y1 - my && ps.z > zNear + s.half * 0.5f) return true;
        }
        return false;
    }

    // Мир «бесконечный»: земля едет за магом по сетке тайла, декор переносится на другую сторону квадрата.
    public static void Follow(Vector2 p)
    {
        ground.position = new Vector3(Mathf.Round(p.x / GroundTile) * GroundTile, 0, Mathf.Round(p.y / GroundTile) * GroundTile);
        float h = Period / 2;
        foreach (var t in props)
        {
            var q = t.position;
            if (q.x - p.x > h) q.x -= Period; else if (p.x - q.x > h) q.x += Period;
            if (q.z - p.y > h) q.z -= Period; else if (p.y - q.z > h) q.z += Period;
            t.position = q;
        }
        // шейдер тумана не знает про fog сцены: гасим дальние пятна сами, иначе они светятся в темноте у края кадра
        foreach (var r in mist)
        {
            var q = r.transform.position;
            float k = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(9, 19, new Vector2(q.x - p.x, q.z - p.y).magnitude));
            mistMpb.SetColor("_Color", Level.mist * new Color(1, 1, 1, k));
            r.SetPropertyBlock(mistMpb);
        }
        if (solidPos.Length != solids.Count) solidPos = new Vector2[solids.Count];
        for (int i = 0; i < solids.Count; i++) { var q = solids[i].tr.position; solidPos[i] = new Vector2(q.x, q.z); }
    }

    static Stage cur;

    public static void SetStage(Stage s)
    {
        cur = s;
        moon.color = s.moon;
        moon.intensity = s.moonI;
        RenderSettings.ambientLight = s.ambient;
        RenderSettings.fogColor = s.fog;
        cam.backgroundColor = s.fog;
        Ground.SetColor("_BaseColor", s.ground);
    }

    public static void BlendStage(Stage to, float k)
    {
        Stage s = new()
        {
            moon = Color.Lerp(cur.moon, to.moon, k), moonI = Mathf.Lerp(cur.moonI, to.moonI, k),
            ambient = Color.Lerp(cur.ambient, to.ambient, k), fog = Color.Lerp(cur.fog, to.fog, k),
            ground = Color.Lerp(cur.ground, to.ground, k),
        };
        var keep = cur;
        SetStage(s);
        cur = k >= 1 ? to : keep;
    }

    // Красная виньетка при уроне и на низком здоровье
    public static void Hurt(float k)
    {
        vignette.color.Override(Color.Lerp(Color.black, new Color(0.7f, 0, 0), k));
        vignette.intensity.Override(Mathf.Lerp(0.4f, 0.55f, k));
    }
}
