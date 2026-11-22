using System.Collections.Generic;
using UnityEngine;

// Простые классы без MonoBehaviour: весь апдейт идёт из Game.Update, так сотни врагов не тормозят.
// Логика живёт на плоскости (Vector2 x,y), мир рисуется в 3D на (x, 0, y).

public class Player
{
    public GameObject go;
    public Transform tr;
    public Animator anim;
    public Light light;
    public Vector2 pos;
    public Vector2 face = Vector2.up;
    public float hp = 100, maxHp = 100;
    public float speed = 4.2f;
    public float magnet = 2.4f;
    public float regen;
    public float vamp;
    public float dmgMul = 1, cdMul = 1, area = 1;
    public float crit = 0.05f, critMul = 2, armor, thorns, xpMul = 1;
    public float killHeal, necro, findMul = 1, goldMul = 1;
    public bool cloak;
    public int revives, extraChoice;
    public float hurt;
    public float drainPool, drainAt;   // запас «украденного» здоровья (вампиризм, лечение за убийства) и когда он считался
    public bool moving;
    public Vector2 vel, lastPos;   // скорость бега — для упреждения стрелков
    public const float Radius = 0.4f;
    public Player Clone() => (Player)MemberwiseClone();
}

public class EnemyType
{
    public string name, model, weapon;
    public float hp, speed, dmg, scale = 1, xp = 1;
    public bool boss, elite, fallen; // fallen — павший маг другого игрока (GameFallen.cs)
    public bool walker, golem, archer; // особые (GameThreats.cs): без замедления и заморозки / без отбрасывания и оглушения / стрелок
    public bool bomber, summoner;      // бомбер взрывается рядом с магом; призыватель держится поодаль и поднимает скелетов
}

public class Enemy
{
    public GameObject go;
    public Transform tr;
    public Animator anim;
    public Renderer[] rends;
    public EnemyType type;
    public Vector2 pos, sep;
    public float hp, maxHp, dmg, radius, flash, orbReadyAt, spawnLock, dying, attackCd, stun, shownUntil, slow, frozen;
    public float atkAnim;
    public bool charged, rage;
    public bool raised;    // поднят призывателем — таких держим немного, иначе к 8-й минуте врагов вдвое больше и телефоны тонут
    public float shotCd;   // стрелок: до выстрела
    public int merged;     // сколько мобов сверх потолка влилось в этого (GameThreats.cs)
    // полёт трупа после смерти и направление последнего удара
    public Vector3 fly, spinAxis, scale0;   // scale0 — размер без «сплющивания» от удара
    public float animSpeed = 1;             // темп шага этого вида; замедление и оглушение множат его
    public Vector2 lastHit;
    public Material mat;
    public int tier;
    public Color tint;
    public bool alive;
    // кольцо волны: враг держит слот на сжимающейся окружности
    public bool ring;
    public Vector2 ringDir;
    // кооператив: номер в снимке хозяина; у гостя — цель и скорость по снимкам, флаги состояния
    public int id, netSeen;
    public Vector2 netPos, netVel;
    public bool netAtk;
    // у хозяина — что про этого врага уже знают гости: откуда и с какой скоростью они его ведут, здоровье, флаги
    public bool sentKnown;
    public Vector2 sentPos, sentVel, tickPos;
    public float sentAt;
    public byte sentHp, sentFlags;
}

public class Gem
{
    public GameObject go;
    public Transform tr;
    public Renderer rend;
    public Vector2 pos;
    public float value, phase;
    public int tier;
    public bool heal, pulled, alive;
    public bool sentKnown;   // у хозяина: гости про этот кристалл уже знают
    public int id, netSeen;
    public Vector2 netPos;
}

public class Proj
{
    public GameObject go;
    public Transform tr;
    public Vector2 pos, vel;
    public float dmg, life, radius, splash, zapCd;
    public int pierce, bounce;
    public bool burn, zap, ice, freeze, split, big; // big — шар рисуется по своему радиусу (Чёрное солнце)
    public Element element;
    public Renderer rend;
    public MeshFilter mf;
    public Mesh baseMesh;
    public Light light;
    public Material mat;
    public bool alive;
    public Mage owner;
    public readonly HashSet<Enemy> hit = new();
}

public class DamageNumber
{
    public Vector3 pos;
    public string text;
    public float t;
    public Color color;
    public bool crit;
}

public class Chest
{
    public GameObject go;
    public Transform tr;
    public Light light;
    public LineRenderer beam;
    public Vector2 pos;
    public int rarity; // 0 обычный, 1 редкий, 2 легендарный
    public bool alive;
    public int id, netSeen;
}

// Горящая земля: лава метеора, след инферно, бомбы извержения
public class BurnPool
{
    public Vector2 pos;
    public float r, dps, t, tick;
    public Renderer decal, swirl, swirl2;
    public bool ice, follow, tomb;
    public Element element;
    public Mage owner;
}

public class Artifact
{
    public string title, desc, icon;
    public Color color;
    public System.Action<Game> apply;
    public bool unique;
}
