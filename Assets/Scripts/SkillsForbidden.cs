using System.Collections.Generic;
using UnityEngine;

// Запретная школа: заклинаний нет в обычных картах уровня. Первое берётся только из гримуара упокоенного
// павшего мага (GameFallen.cs); взятое дальше качается обычными картами. Школы у них нет — бонус школы не идёт.
public abstract class Forbidden : Skill
{
    public static readonly Color Soul = new(0.35f, 1f, 0.8f);
    protected Forbidden() { spell = true; forbidden = true; max = 5; color = Soul; }
}

// Жатва душ: раз в несколько секунд рвёт души у всех вокруг — урон, рывок к магу и лечение за каждого задетого
public class SoulReap : Forbidden
{
    float cd = 2f;
    readonly List<Enemy> near = new();
    public SoulReap() { title = "Жатва душ"; icon = "a_necroskull"; }
    static float Dmg(int l) => 14 + 9 * (l - 1);
    static float Radius(int l) => 4.2f + 0.45f * (l - 1);
    static float Heal(int l) => 0.4f + 0.15f * (l - 1);
    public override string Desc(int l) => l == 1 ? "Вырывает души у всех вокруг: урон, рывок к магу, лечение за каждого"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, радиус {Radius(l - 1):0.0} → {Radius(l):0.0}, лечение за врага {Heal(l):0.00}";

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        float r = Radius(level) * g.player.area;
        g.Query(g.player.pos, r, near);
        if (near.Count == 0) { cd = 0.3f; return; }
        cd = 4.2f * g.player.cdMul;
        var c = W(g.player.pos, 1.2f);
        Vfx.Emit(Vfx.Ring, W(g.player.pos, 0.15f), Vector3.zero, r * 2.2f, 0.45f, Soul);
        Vfx.Flash(c, Soul, 6, r * 2, 0.3f);
        int n = 0;
        foreach (var e in near)
        {
            if (!e.alive) continue;
            var to = g.player.pos - e.pos;
            var at = W(e.pos, 0.9f * e.type.scale);
            // душа летит от врага к магу
            Vfx.Emit(Vfx.Glow, at, (c - at) * 2.2f, 0.7f, 0.4f, Soul);
            g.Hit(e, Dmg(level), to.normalized, e.type.boss ? 0 : 0.55f);
            n++;
        }
        g.Drain(Mathf.Min(n, 30) * Heal(level));
        g.Cast(color);
        g.Shake(0.08f);
    }
}

// Костяная тюрьма: вокруг случайной толпы вырастают кости — урон и оглушение
public class BonePrison : Forbidden
{
    float cd = 3f;
    readonly List<Enemy> near = new();
    public BonePrison() { title = "Костяная тюрьма"; icon = "a_lichcrown"; color = new Color(0.85f, 0.95f, 0.8f); }
    static float Dmg(int l) => 22 + 14 * (l - 1);
    static float Stun(int l) => 1.4f + 0.35f * (l - 1);
    static float Radius(int l) => 2.4f + 0.25f * (l - 1);
    public override string Desc(int l) => l == 1 ? "Кости запирают толпу врагов: урон и оглушение"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, оглушение {Stun(l - 1):0.0} → {Stun(l):0.0} с";

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        var t = g.RandomEnemy(g.player.pos, 9f);
        if (t == null) { cd = 0.3f; return; }
        cd = 5f * g.player.cdMul;
        float r = Radius(level) * g.player.area;
        var at = t.pos;
        Vfx.Emit(Vfx.Ring, W(at, 0.15f), Vector3.zero, r * 2.4f, 0.5f, color);
        for (int i = 0; i < 10; i++)
        {
            float a = i * Mathf.PI * 2 / 10;
            var p = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            Vfx.Burst(Vfx.Bone, W(p, 0.2f), 4, 4, 0.14f, 0.8f, color, 1.4f, 0.15f);
        }
        Vfx.Flash(W(at, 1.5f), color, 5, r * 3, 0.3f);
        g.Query(at, r, near);
        foreach (var e in near)
        {
            if (!e.alive) continue;
            if (!e.type.boss) e.stun = Mathf.Max(e.stun, Stun(level));
            g.Hit(e, Dmg(level), Vector2.zero, 0);
        }
        g.Cast(color);
        g.Shake(0.12f);
    }
}

// Чёрное солнце: медленный тёмный шар, который проходит сквозь всех на своём пути
public class BlackSun : Forbidden
{
    float cd = 2.5f;
    public BlackSun() { title = "Чёрное солнце"; icon = "a_bloodstone"; color = new Color(0.75f, 0.4f, 1f); }
    static float Dmg(int l) => 26 + 16 * (l - 1);
    static float Size(int l) => 0.9f + 0.15f * (l - 1);
    public override string Desc(int l) => l == 1 ? "Медленный тёмный шар проходит сквозь всех на пути"
        : $"Урон {Dmg(l - 1)} → {Dmg(l)}, шар крупнее" + (l == 4 ? ", два шара" : "");

    public override void Tick(Game g, float dt)
    {
        cd -= dt;
        if (cd > 0) return;
        var t = g.Nearest(g.player.pos, 12f);
        if (t == null) { cd = 0.3f; return; }
        cd = 4.6f * g.player.cdMul;
        var dir = (t.pos - g.player.pos).normalized;
        int n = level >= 4 ? 2 : 1;
        for (int i = 0; i < n; i++)
        {
            Vector2 v = Quaternion.Euler(0, 0, n == 1 ? 0 : (i == 0 ? -18 : 18)) * dir * 4.2f;
            var p = g.Shoot(g.player.pos + v.normalized * 0.7f, v, Dmg(level), 999, 0, color);
            p.radius = Size(level) * g.player.area;
            p.life = 3.2f;
            p.big = true;
            g.MakeBlackSun(p);
        }
        g.Cast(color);
    }
}
