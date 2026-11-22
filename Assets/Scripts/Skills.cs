using System.Collections.Generic;
using UnityEngine;

// Школы магии: каждый уровень заклинания школы даёт +3% урона всем заклинаниям этой школы.
public enum Element { None, Fire, Electro, Meteor, Ice }

// Навыки: заклинания кастуются сами (8 уровней, на 8-м — выбор одной из трёх суперсил),
// пассивки меняют статы мага (5 уровней), слияния открываются, когда у двух заклинаний есть суперсилы.
public abstract class Skill
{
    public string title, icon = "might";
    public Element element;
    public bool spell, fusion, forbidden;
    public int level;
    public int max = 5;
    public Color color = Color.white;
    public int super = -1;
    public string[] superNames, superDescs;

    public abstract string Desc(int lvl);
    public virtual void OnLevel(Game g) { }
    public virtual void OnSuper(Game g) { }
    public virtual void Tick(Game g, float dt) { }
    // кооператив: маг пал или ушёл — свои объекты (шары, аура, солнца) прячутся, поднялся — возвращаются
    public virtual void Show(bool on) { }
    public bool NeedsSuper => superNames != null && level >= max && super < 0;
    public string SuperName => super >= 0 ? superNames[super] : null;

    protected static Vector3 W(Vector2 p, float y = 1f) => World.W(p, y);
}

public class Fireball : Skill
{
    float cd;
    public Fireball()
    {
        title = "Огненный шар"; spell = true; max = 8; icon = "fireball"; element = Element.Fire; color = new Color(1f, 0.55f, 0.15f);
        superNames = new[] { "Феникс", "Инферно", "Шквал" };
        superDescs = new[] { "Шары отскакивают ещё в 3 цели", "Огромный взрыв и горящая земля", "Залп сразу во все 8 сторон" };
    }
    static float Dmg(int l) => 9 + 5 * (l - 1);
    static int Count(int l) => 1 + l / 3;
    static float Splash(int l) => l >= 3 ? 1.2f + 0.1f * l : 0;
    static int Pierce(int l) => l >= 6 ? 1 + (l - 6) : 0;
    public override string Desc(int l) => l == 1 ? "Огненный шар в ближайшего врага"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, шаров {Count(l)}" + (l == 3 ? ", взрыв по площади" : "") + (l == 6 ? ", пробивает насквозь" : "");

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        var target = g.Nearest(g.player.pos, 11f);
        if (target == null) return;
        cd = 0.9f * g.player.cdMul;
        var dir = (target.pos - g.player.pos).normalized;
        int n = Count(level);
        float splash = super == 1 ? 2.6f : Splash(level);
        for (int i = 0; i < n; i++)
        {
            float a = (i - (n - 1) * 0.5f) * 12f;
            Vector2 v = Quaternion.Euler(0, 0, a) * dir * 12f;
            var p = g.Shoot(g.player.pos + v.normalized * 0.6f, v, Dmg(level), Pierce(level), splash, Game.FireColor);
            p.bounce = super == 0 ? 3 : 0;
            p.burn = super == 1;
        }
        if (super == 2)
            for (int i = 0; i < 8; i++)
            {
                Vector2 v = Quaternion.Euler(0, 0, i * 45 + 22.5f) * dir * 11f;
                g.Shoot(g.player.pos, v, Dmg(level) * 0.7f, 1, 0, Game.FireColor);
            }
        g.Cast(color);
    }
}

public class Lightning : Skill
{
    float cd = 1f;
    readonly List<Enemy> near = new();
    readonly Stack<HashSet<Enemy>> donePool = new();
    public Lightning()
    {
        title = "Цепная молния"; spell = true; max = 8; icon = "lightning"; element = Element.Electro; color = new Color(0.6f, 0.75f, 1f);
        superNames = new[] { "Громовержец", "Буря", "Статика" };
        superDescs = new[] { "Ещё 4 удара молнии с неба по площади", "15 прыжков, враги оглушены", "Убитые разряжаются цепью на соседей" };
    }
    public static float Dmg(int l) => 14 + 7 * (l - 1);
    static int Jumps(int l) => 2 + l;
    public override string Desc(int l) => l == 1 ? "Молния прыгает между врагами"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, прыжков {Jumps(l)}";

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        var cur = g.RandomEnemy(g.player.pos, 9f);
        if (cur == null) return;
        cd = 2.2f * g.player.cdMul;
        Chain(g, W(g.player.pos, 1.6f), cur, super == 1 ? 15 : Jumps(level), Dmg(level), super == 1 ? 5f : 3.5f);
        if (super == 0)
            for (int i = 0; i < 4; i++)
            {
                var t = g.RandomEnemy(g.player.pos, 10f);
                if (t != null) g.Strike(t.pos, Dmg(level) * 1.5f, 1.6f, color);
            }
        g.Shake(0.05f);
    }

    public void Chain(Game g, Vector3 from, Enemy cur, int jumps, float dmg, float range)
    {
        Vfx.Flash(from, color, 6, 6, 0.2f);
        // у каждой цепи свой набор: убитый заряженный враг пускает «Статику» (Chain внутри Hit → Kill), и общий набор
        // стирал историю внешней цепи — она снова била уже задетых (ревью Codex 17.11)
        var done = donePool.Count > 0 ? donePool.Pop() : new HashSet<Enemy>();
        for (int j = 0; j < jumps && cur != null; j++)
        {
            var to = W(cur.pos, 0.9f * cur.type.scale);
            Vfx.Fork(from, to, new Color(0.75f, 0.85f, 1f), j == 0 ? 3 : 1);
            Vfx.Burst(Vfx.Spark, to, 10, 6, 0.25f, 0.35f, new Color(0.7f, 0.85f, 1f));
            Vfx.Emit(Vfx.Glow, to, Vector3.zero, 1.6f, 0.2f, new Color(0.5f, 0.7f, 1f));
            done.Add(cur);
            if (super == 1) cur.stun = Mathf.Max(cur.stun, 1f);
            if (super == 2) cur.charged = true;
            g.Hit(cur, dmg, Vector2.zero);
            from = to;
            g.Query(cur.pos, range, near);
            var at = cur.pos;
            cur = null;
            float best = float.MaxValue;
            foreach (var e in near)
            {
                if (!e.alive || done.Contains(e)) continue;
                float d = (e.pos - at).sqrMagnitude;
                if (d < best) { best = d; cur = e; }
            }
        }
        done.Clear();
        donePool.Push(done);
    }
}

public class Orbs : Skill
{
    public readonly List<Transform> orbs = new();
    readonly List<Enemy> near = new();
    float angle, shardCd;
    public Orbs()
    {
        title = "Шаровые молнии"; spell = true; max = 8; icon = "ballorbs"; element = Element.Electro; color = new Color(0.4f, 0.85f, 1f);
        superNames = new[] { "Звёздный венец", "Чёрные дыры", "Осколки" };
        superDescs = new[] { "+4 шара, орбита пульсирует", "Шары затягивают врагов", "Шары стреляют разрядами" };
    }
    static float Dmg(int l) => 8 + 4 * (l - 1);
    static int Count(int l) => 1 + (l + 1) / 2;
    public override string Desc(int l) => l == 1 ? "Шаровые молнии кружат вокруг мага"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, сфер {Count(l)}";

    public override void OnLevel(Game g) => Grow(g, Count(level));
    public override void OnSuper(Game g) { if (super == 0) Grow(g, Count(level) + 4); }
    public override void Show(bool on) { foreach (var o in orbs) o.gameObject.SetActive(on); }

    void Grow(Game g, int n)
    {
        while (orbs.Count < n)
        {
            var root = new GameObject("Orb");
            var go = World.Primitive(PrimitiveType.Quad, OrbMat("magic_01", new Color(0.14f, 0.38f, 0.92f)), 2f);
            go.name = "Shell";
            go.transform.SetParent(root.transform, false);
            var core = World.Primitive(PrimitiveType.Quad, OrbMat("circle_02", new Color(0.5f, 0.9f, 1f)), 0.7f);
            core.name = "Core";
            core.transform.SetParent(root.transform, false);
            var tr = root.AddComponent<TrailRenderer>();
            tr.time = 0.25f;
            tr.widthMultiplier = 0.16f;
            tr.widthCurve = AnimationCurve.Linear(0, 1, 1, 0);
            tr.sharedMaterial = World.Additive;
            tr.startColor = new Color(0.5f, 0.9f, 1f, 0.8f);
            tr.endColor = new Color(0.3f, 0.5f, 1f, 0);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            orbs.Add(root.transform);
        }
    }

    // Шар молнии — два слоя лицом к камере: мягкое ядро и вращающаяся электрическая оболочка вокруг него.
    // Одна текстура читалась как значок, а не как плазма. Источники света на шары убраны.
    static readonly Dictionary<string, Material> orbMats = new();

    static Material OrbMat(string tex, Color color)
    {
        if (orbMats.TryGetValue(tex, out var m) && m != null) return m;
        m = new Material(World.Additive) { mainTexture = Art.Tex(tex) };
        m.SetColor("_Color", color);
        orbMats[tex] = m;
        return m;
    }

    public override void Tick(Game g, float dt)
    {
        angle += 3.2f * dt;
        float r = 1.9f * g.player.area * (super == 0 ? 1.3f + 0.4f * Mathf.Sin(g.time * 2) : 1);
        shardCd -= dt;
        // мелкие разряды между шарами
        if (orbs.Count > 1 && Random.value < 0.08f)
        {
            int a = Random.Range(0, orbs.Count);
            Vfx.Bolt(orbs[a].position, orbs[(a + 1) % orbs.Count].position, new Color(0.5f, 0.85f, 1f), 0.08f);
        }
        bool shoot = super == 2 && shardCd <= 0;
        if (shoot) shardCd = 1f;
        for (int i = 0; i < orbs.Count; i++)
        {
            float a = angle + i * Mathf.PI * 2 / orbs.Count;
            var p = g.player.pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            orbs[i].position = W(p, 1f + 0.15f * Mathf.Sin(g.time * 4 + i));
            var face = World.Billboard;
            orbs[i].rotation = face;
            var shell = orbs[i].GetChild(0);
            var core = orbs[i].GetChild(1);
            shell.rotation = face * Quaternion.Euler(0, 0, g.time * 150f + i * 37f);
            shell.localScale = Vector3.one * 2f * (1f + 0.14f * Mathf.Sin(g.time * 7 + i * 2.1f));
            core.rotation = face * Quaternion.Euler(0, 0, -g.time * 60f);
            core.localScale = Vector3.one * (0.7f + 0.16f * Mathf.Sin(g.time * 11 + i));
            if (Random.value < 0.06f) Vfx.Bolt(orbs[i].position, orbs[i].position + Random.onUnitSphere * 0.8f, new Color(0.45f, 0.8f, 1f), 0.05f);
            if (super == 1)
            {
                g.Query(p, 2.6f, near);
                foreach (var e in near)
                    if (!e.type.boss) e.pos = Vector2.MoveTowards(e.pos, p, 2.5f * dt);
                if (Random.value < 0.3f)
                    Vfx.Emit(Vfx.Ember, W(p) + Random.onUnitSphere * 1.5f, (W(p) - (W(p) + Random.onUnitSphere)) * 2, 0.2f, 0.4f, new Color(0.4f, 0.8f, 1f));
            }
            if (shoot)
            {
                var t = g.Nearest(p, 8f);
                if (t != null) g.Shoot(p, (t.pos - p).normalized * 13f, Dmg(level) * 0.8f, 0, 0, new Color(0.4f, 0.85f, 1f));
            }
            g.Query(p, 0.5f, near);
            foreach (var e in near)
            {
                if (!e.alive || g.time < e.orbReadyAt) continue;
                e.orbReadyAt = g.time + 0.4f;
                g.Hit(e, Dmg(level), (e.pos - g.player.pos).normalized, 0.35f);
                Vfx.Burst(Vfx.Spark, W(e.pos), 6, 4, 0.2f, 0.3f, new Color(0.5f, 0.85f, 1f));
            }
        }
    }
}

public class FireAura : Skill
{
    Transform ring;
    Light glow;
    float tick;
    readonly List<Enemy> near = new();
    public float Radius(Game g) => (1.6f + 0.22f * (level - 1)) * g.player.area * (super == 0 ? 1.8f : 1);
    public FireAura()
    {
        title = "Огненная аура"; spell = true; max = 8; icon = "fireaura"; element = Element.Fire; color = new Color(1f, 0.4f, 0.1f);
        superNames = new[] { "Солнце", "Пепел", "Огненный щит" };
        superDescs = new[] { "Радиус ауры почти вдвое больше", "Сгоревшие враги взрываются", "−35% входящего урона, шипы жгут втрое" };
    }
    static float Dmg(int l) => 5 + 2.5f * (l - 1);
    public override string Desc(int l) => l == 1 ? "Жжёт всех рядом с магом"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)} каждые 0.4 с, радиус больше";

    public override void OnLevel(Game g)
    {
        if (ring != null) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.Destroy(go.GetComponent<Collider>());
        var m = new Material(World.Additive) { mainTexture = Art.Tex("circle_05") };
        m.SetColor("_Color", new Color(1.2f, 0.35f, 0.05f, 0.6f));
        go.GetComponent<Renderer>().sharedMaterial = m;
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ring = go.transform;
        glow = new GameObject("AuraLight").AddComponent<Light>();
        glow.color = color; glow.range = 5; glow.intensity = 2.5f; glow.shadows = LightShadows.None;
    }

    public override void OnSuper(Game g)
    {
        if (super == 2) { g.player.armor += 0.35f; g.player.thorns += 3f; }
    }

    public override void Show(bool on)
    {
        if (ring != null) ring.gameObject.SetActive(on);
        if (glow != null) glow.gameObject.SetActive(on);
    }

    public override void Tick(Game g, float dt)
    {
        float r = Radius(g);
        g.ashRadius = super == 1 ? r : 0;
        ring.SetPositionAndRotation(W(g.player.pos, 0.05f), Quaternion.Euler(90, g.time * 40, 0));
        ring.localScale = Vector3.one * r * 2.2f * (1 + 0.05f * Mathf.Sin(g.time * 7));
        glow.transform.position = W(g.player.pos, 0.6f);
        glow.range = r * 2.5f;
        glow.intensity = 2.2f + 0.6f * Mathf.PerlinNoise(g.time * 6, 0);
        // три вращающихся языка пламени по краю и случайные всполохи внутри
        for (int i = 0; i < 3; i++)
        {
            float sa = g.time * 2.6f + i * Mathf.PI * 2 / 3;
            var sp = g.player.pos + new Vector2(Mathf.Cos(sa), Mathf.Sin(sa)) * r * 0.95f;
            var tang = new Vector3(-Mathf.Sin(sa), 0, Mathf.Cos(sa));
            Vfx.Emit(Vfx.Fire, W(sp, 0.3f), Vector3.up * 1.5f + tang * 2.5f, 0.7f, 0.45f, new Color(1f, 0.55f, 0.15f));
        }
        int flames = super == 0 ? 3 : 2;
        for (int i = 0; i < flames; i++)
        {
            float a = Random.Range(0, Mathf.PI * 2);
            var p = g.player.pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * Random.Range(0.7f, 1f);
            Vfx.Emit(Vfx.Fire, W(p, 0.2f), Vector3.up * Random.Range(1f, 2f), Random.Range(0.4f, 0.8f), 0.5f, new Color(1f, 0.5f, 0.15f));
        }
        if (super == 2 && Random.value < 0.5f)
            Vfx.Emit(Vfx.Ember, W(g.player.pos, 1f) + Random.onUnitSphere * 0.9f, Vector3.up * 0.5f, 0.25f, 0.5f, new Color(1f, 0.6f, 0.2f));
        tick -= dt;
        if (tick > 0) return;
        tick = 0.4f;
        g.Query(g.player.pos, r, near);
        foreach (var e in near)
        {
            if (!e.alive) continue;
            g.Hit(e, Dmg(level), Vector2.zero);
            Vfx.Emit(Vfx.Fire, W(e.pos, 0.6f), Vector3.up, 0.7f, 0.35f, new Color(1f, 0.45f, 0.1f));
        }
    }
}

public class Meteor : Skill
{
    float cd = 2f;
    public Meteor()
    {
        title = "Метеор"; spell = true; max = 8; icon = "meteor"; element = Element.Meteor; color = new Color(1f, 0.3f, 0.15f);
        superNames = new[] { "Армагеддон", "Комета", "Лава" };
        superDescs = new[] { "Метеорный дождь: +5 метеоров", "Гигантский метеор, враги оглушены", "Удар оставляет озеро лавы" };
    }
    public static float Dmg(int l) => 30 + 15 * (l - 1);
    static int Count(int l) => 1 + (l - 1) / 3;
    public static float Radius(int l) => 1.8f + 0.12f * l;
    public override string Desc(int l) => l == 1 ? "С неба падает метеор, бьёт по площади"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, метеоров {Count(l)}";

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 3.5f * g.player.cdMul;
        int n = Count(level) + (super == 0 ? 5 : 0);
        for (int i = 0; i < n; i++)
        {
            var t = g.RandomEnemy(g.player.pos, 10f);
            if (t == null) return;
            float r = Radius(level) * g.player.area * (super == 1 ? 2f : 1);
            g.DropMeteor(t.pos, r, Dmg(level) * (super == 1 ? 2 : 1), 0.75f + i * 0.12f, super == 1, super == 2, false);
        }
    }
}

public class Passive : Skill
{
    readonly string what;
    readonly System.Action<Player> apply;
    public Passive(string title, string what, string icon, System.Action<Player> apply, Color c)
    {
        this.title = title; this.what = what; this.icon = icon; this.apply = apply; color = c;
    }
    public override string Desc(int l) => what;
    public override void OnLevel(Game g) => apply(g.player);
    public void ApplyTo(Player p) => apply(p);

    // развилка на последнем уровне: три пути, как суперсилы у заклинаний
    System.Action<Player>[] paths;
    public Passive Paths(params (string name, string desc, System.Action<Player> apply)[] p)
    {
        superNames = new string[p.Length]; superDescs = new string[p.Length]; paths = new System.Action<Player>[p.Length];
        for (int i = 0; i < p.Length; i++) { superNames[i] = p[i].name; superDescs[i] = p[i].desc; paths[i] = p[i].apply; }
        return this;
    }
    public override void OnSuper(Game g) { if (paths != null && super >= 0) paths[super](g.player); }
}

// ---------- Слияния: новое заклинание из двух с суперсилами ----------

public abstract class Fusion : Skill
{
    public Skill a, b;
    protected Fusion(Skill a, Skill b, string title, string desc, string icon, Color c)
    {
        this.a = a; this.b = b; this.title = title; this.icon = icon; color = c;
        spell = true; fusion = true; max = 1; text = desc;
    }
    readonly string text;
    public override string Desc(int l) => text;
    public bool Ready => level == 0 && a.super >= 0 && b.super >= 0;
}

public class PlasmaStorm : Fusion
{
    float cd;
    public PlasmaStorm(Skill a, Skill b) : base(a, b, "Плазменный шторм", "Плазменные шары бьют молниями всех вокруг", "plasma", new Color(0.6f, 0.9f, 1f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 2.5f * g.player.cdMul;
        for (int i = 0; i < 3; i++)
        {
            Vector2 v = Quaternion.Euler(0, 0, i * 120 + g.time * 50) * Vector2.right * 6f;
            var p = g.Shoot(g.player.pos, v, 25, 8, 0, new Color(0.5f, 0.8f, 1f));
            p.zap = true; p.life = 2.5f;
        }
    }
}

public class Starfall : Fusion
{
    float cd;
    public Starfall(Skill a, Skill b) : base(a, b, "Звездопад", "Непрерывный дождь малых метеоров вокруг", "starfall", new Color(1f, 0.7f, 0.3f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 0.3f;
        var p = g.player.pos + Random.insideUnitCircle * 8f;
        g.DropMeteor(p, 1.3f * g.player.area, 45, 0.5f, false, false, false);
    }
}

public class TeslaRing : Fusion
{
    float tick;
    readonly List<Enemy> near = new();
    public TeslaRing(Skill a, Skill b) : base(a, b, "Тесла-кольцо", "Шаровые молнии связаны дугами, всё между ними горит", "tesla", new Color(0.7f, 0.85f, 1f)) { }
    public override void Tick(Game g, float dt)
    {
        var orbs = ((Orbs)b).orbs;
        tick -= dt;
        if (tick > 0 || orbs.Count < 2) return;
        tick = 0.2f;
        for (int i = 0; i < orbs.Count; i++)
        {
            var p0 = orbs[i].position; var p1 = orbs[(i + 1) % orbs.Count].position;
            Vfx.Bolt(p0, p1, new Color(0.7f, 0.85f, 1f), 0.2f);
            var m = new Vector2((p0.x + p1.x) / 2, (p0.z + p1.z) / 2);
            g.Query(m, Vector3.Distance(p0, p1) / 2, near);
            foreach (var e in near) g.Hit(e, 10, Vector2.zero, 0);
        }
    }
}

public class SolarSystem : Fusion
{
    readonly List<Transform> suns = new();
    readonly List<Enemy> near = new();
    float angle, tick;
    public SolarSystem(Skill a, Skill b) : base(a, b, "Солнечная система", "Три солнца с огненными аурами на дальней орбите", "solar", new Color(1f, 0.8f, 0.3f)) { }
    public override void OnLevel(Game g)
    {
        for (int i = 0; i < 3; i++)
        {
            var go = World.Primitive(PrimitiveType.Sphere, World.Glow, 0.9f);
            g.Tint(go.GetComponent<Renderer>(), new Color(6f, 3f, 0.6f));
            var l = new GameObject("SunLight").AddComponent<Light>();
            l.transform.SetParent(go.transform, false);
            l.color = new Color(1f, 0.7f, 0.3f); l.range = 6; l.intensity = 4; l.shadows = LightShadows.None;
            suns.Add(go.transform);
        }
    }
    public override void Show(bool on) { foreach (var s in suns) s.gameObject.SetActive(on); }
    public override void Tick(Game g, float dt)
    {
        angle += 1.2f * dt;
        tick -= dt;
        for (int i = 0; i < suns.Count; i++)
        {
            float a = angle + i * Mathf.PI * 2 / 3;
            var p = g.player.pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 4.2f;
            suns[i].position = W(p, 1.2f);
            Vfx.Emit(Vfx.Fire, W(p, 1.2f) + Random.insideUnitSphere * 0.5f, Random.insideUnitSphere, 0.9f, 0.4f, new Color(1f, 0.55f, 0.2f));
            if (tick <= 0)
            {
                g.Query(p, 1.8f, near);
                foreach (var e in near) g.Hit(e, 18, (e.pos - p).normalized, 0.3f);
            }
        }
        if (tick <= 0) tick = 0.35f;
    }
}

public class SkyWrath : Fusion
{
    float cd = 2;
    public SkyWrath(Skill a, Skill b) : base(a, b, "Гнев небес", "Каждые 6 с — 8 молний и 3 метеора разом", "skywrath", new Color(0.9f, 0.9f, 1f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 6f * g.player.cdMul;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4;
            g.Strike(g.player.pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 5f, 60, 2f, new Color(0.8f, 0.9f, 1f));
        }
        for (int i = 0; i < 3; i++)
        {
            var t = g.RandomEnemy(g.player.pos, 10);
            if (t != null) g.DropMeteor(t.pos, 2.5f, 120, 0.6f + i * 0.2f, false, false, false);
        }
        g.Shake(0.4f);
    }
}

public class Eruption : Fusion
{
    float cd = 1;
    public Eruption(Skill a, Skill b) : base(a, b, "Извержение", "Вулкан под магом швыряет лавовые бомбы", "eruption", new Color(1f, 0.35f, 0.1f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 4f * g.player.cdMul;
        g.Boom(g.player.pos, 1.5f, new Color(1f, 0.4f, 0.1f), false);
        for (int i = 0; i < 10; i++)
            g.DropMeteor(g.player.pos + Random.insideUnitCircle.normalized * Random.Range(2.5f, 7f), 1.4f, 40, 0.5f + i * 0.06f, false, true, false);
    }
}

// ---------- Школа льда ----------

public class IceLance : Skill
{
    float cd = 0.5f;
    public static readonly Color Ice = new(0.55f, 0.85f, 1f);
    public IceLance()
    {
        title = "Ледяные копья"; spell = true; max = 8; icon = "icelance"; element = Element.Ice; color = Ice;
        superNames = new[] { "Заморозка", "Осколки", "Вечная мерзлота" };
        superDescs = new[] { "Копья вмораживают врагов на 1.2 с", "Копьё раскалывается на 3 осколка", "Бесконечное пробитие, урон ×1.5" };
    }
    static float Dmg(int l) => 10 + 5 * (l - 1);
    static int Count(int l) => 2 + l / 3;
    static int Pierce(int l) => 2 + l / 2;
    public override string Desc(int l) => l == 1 ? "Веер ледяных копий: пробивают и замедляют"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, копий {Count(l)}, пробитие {Pierce(l)}";

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        var t = g.Nearest(g.player.pos, 11f);
        if (t == null) return;
        cd = 1.4f * g.player.cdMul;
        var dir = (t.pos - g.player.pos).normalized;
        int n = Count(level);
        for (int i = 0; i < n; i++)
        {
            Vector2 v = Quaternion.Euler(0, 0, (i - (n - 1) * 0.5f) * 10f) * dir * 14f;
            var p = g.Shoot(g.player.pos + v.normalized * 0.5f, v, Dmg(level) * (super == 2 ? 1.5f : 1), super == 2 ? 99 : Pierce(level), 0, Ice);
            p.ice = true; p.freeze = super == 0; p.split = super == 1;
        }
        g.Cast(color);
    }
}

public class Blizzard : Skill
{
    float cd = 1f;
    BurnPool follow;
    public Blizzard()
    {
        title = "Буран"; spell = true; max = 8; icon = "blizzard"; element = Element.Ice; color = new Color(0.75f, 0.9f, 1f);
        superNames = new[] { "Ледниковый период", "Ледяная гробница", "Метель" };
        superDescs = new[] { "Буран всегда вокруг мага и шире", "Замёрзшие враги раскалываются взрывом", "Три бурана за раз" };
    }
    public static float Dmg(int l) => 6 + 3 * (l - 1);
    static float Radius(int l) => 2.2f + 0.15f * l;
    public override string Desc(int l) => l == 1 ? "Ледяной вихрь замедляет и морозит врагов"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)} за тик, радиус {Radius(l):0.0}";

    public override void Tick(Game g, float dt)
    {
        float r = Radius(level) * g.player.area;
        if (super == 0)
        {
            if (follow == null || follow.t <= 0) follow = g.Frost(g.player.pos, r * 1.6f, Dmg(level), 9999, true, false);
            follow.r = r * 1.6f; follow.dps = Dmg(level);
            return;
        }
        cd -= dt;
        if (cd > 0) return;
        cd = 4f * g.player.cdMul;
        for (int i = 0; i < (super == 2 ? 3 : 1); i++)
        {
            var t = g.RandomEnemy(g.player.pos, 9f);
            if (t != null) g.Frost(t.pos, r, Dmg(level), 3.5f, false, super == 1);
        }
    }
}

// ---------- Школа метеоров: землетрясение ----------

public class Quake : Skill
{
    float cd = 2f;
    readonly List<float> after = new();
    readonly List<Enemy> near = new();
    public Quake()
    {
        title = "Землетрясение"; spell = true; max = 8; icon = "quake"; element = Element.Meteor; color = new Color(1f, 0.6f, 0.25f);
        superNames = new[] { "Разлом", "Афтершок", "Гравитация" };
        superDescs = new[] { "Из земли бьют 4 разлома", "Ещё две волны следом", "Сначала стягивает врагов, урон ×1.5" };
    }
    static float Dmg(int l) => 19 + 9 * (l - 1);
    static float Radius(int l) => 3.2f + 0.3f * l;
    public override string Desc(int l) => l == 1 ? "Ударная волна из-под земли отбрасывает врагов"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, радиус {Radius(l):0.0}";

    void Wave(Game g, float mul)
    {
        float r = Radius(level) * g.player.area;
        var c = g.player.pos;
        if (super == 2)
        {
            g.Query(c, r * 1.6f, near);
            foreach (var e in near) if (!e.type.boss && !e.type.golem) e.pos = Vector2.Lerp(e.pos, c, 0.5f);
            mul *= 1.5f;
        }
        Vfx.Emit(Vfx.Ring, W(c, 0.15f), Vector3.zero, r * 2.6f, 0.5f, color);
        for (int i = 0; i < 24; i++)
        {
            float a = i * Mathf.PI * 2 / 24;
            var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * Random.Range(0.6f, 1f);
            Vfx.Emit(Vfx.Smoke, W(p, 0.2f), Vector3.up * 1.2f, 1.1f, 0.9f, new Color(0.35f, 0.28f, 0.2f, 0.7f));
            Vfx.Emit(Vfx.Bone, W(p, 0.2f), Vector3.up * Random.Range(3f, 6f) + Random.insideUnitSphere, 0.12f, 0.8f, new Color(0.45f, 0.35f, 0.25f));
        }
        Vfx.Decal(c, r * 1.6f, new Color(0.08f, 0.05f, 0.03f, 0.6f), 2f, false);
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.PI * 2 / 12 + Random.Range(-0.2f, 0.2f);
            var sp = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * Random.Range(0.55f, 0.95f);
            Vfx.Spike(W(sp), Random.Range(0.8f, 1.5f), new Color(0.3f, 0.2f, 0.13f), Random.Range(0.6f, 0.9f));
        }
        g.Query(c, r, near);
        foreach (var e in near)
        {
            g.Hit(e, Dmg(level) * mul, (e.pos - c).normalized, 1.2f);
            e.stun = Mathf.Max(e.stun, 0.3f);
        }
        if (super == 0)
            for (int k = 0; k < 4; k++)
            {
                var d = new Vector2(Mathf.Cos(k * Mathf.PI / 2 + g.time), Mathf.Sin(k * Mathf.PI / 2 + g.time));
                for (int j = 1; j <= 4; j++) g.Boom(c + d * (r + j * 1.4f), 0.9f, new Color(1f, 0.5f, 0.15f), j == 4);
            }
        g.Shake(0.3f);
    }

    public override void Tick(Game g, float dt)
    {
        for (int i = after.Count - 1; i >= 0; i--)
        {
            after[i] -= dt;
            if (after[i] <= 0) { after.RemoveAt(i); Wave(g, 0.6f); }
        }
        cd -= dt;
        if (cd > 0) return;
        if (g.Nearest(g.player.pos, Radius(level) + 1) == null) return;
        cd = 4.5f * g.player.cdMul;
        Wave(g, 1);
        if (super == 1) { after.Add(0.45f); after.Add(0.9f); }
    }
}

// ---------- Ледяные слияния ----------

public class FrostFire : Fusion
{
    float cd;
    public FrostFire(Skill a, Skill b) : base(a, b, "Ледяное пламя", "Спираль огненных и ледяных шаров: жгут и вмораживают", "frostfire", new Color(0.7f, 0.6f, 1f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 2f * g.player.cdMul;
        for (int i = 0; i < 8; i++)
        {
            bool ice = i % 2 == 0;
            Vector2 v = Quaternion.Euler(0, 0, i * 45 + g.time * 90) * Vector2.right * 9f;
            var p = g.Shoot(g.player.pos, v, 35, 2, ice ? 0 : 1.4f, ice ? IceLance.Ice : Game.FireColor);
            p.ice = ice; p.freeze = ice; p.burn = !ice;
        }
    }
}

public class AbsoluteZero : Fusion
{
    float cd = 2;
    public AbsoluteZero(Skill a, Skill b) : base(a, b, "Абсолютный ноль", "Ледяная комета: замораживает всех в огромном радиусе", "absolutezero", new Color(0.6f, 0.9f, 1f)) { }
    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        cd = 7f * g.player.cdMul;
        var t = g.RandomEnemy(g.player.pos, 9f);
        if (t != null) g.DropMeteor(t.pos, 4f, 150, 0.9f, true, false, true);
    }
}
