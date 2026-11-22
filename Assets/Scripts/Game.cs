using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Ядро игры: маг в центре ночного кладбища, волны скелетов, кристаллы опыта, сундуки с артефактами,
// выбор магии, суперсилы и слияния, HUD. Логика на плоскости (Vector2), картинка в 3D через World.
public partial class Game : MonoBehaviour
{
    public static Game I;
    static bool quitHooked;
    public const float RunLength = 15 * 60f;
    public static readonly Color FireColor = new(1f, 0.5f, 0.15f);

    public enum State { Play, LevelUp, Super, Chest, Stats, Dead, Won, Pause, Menu, Town }
    public State state = State.Play;

    public Player player;
    public Camera cam;
    public float time;
    public int kills, level = 1;
    public float xp, xpNext = 5;
    public float ashRadius;
    public Element curElement;

    public static readonly Color[] SchoolColors = { new(0.85f, 0.8f, 0.6f), new(1f, 0.5f, 0.15f), new(0.45f, 0.8f, 1f), new(1f, 0.6f, 0.25f), new(0.65f, 0.9f, 1f) };
    public static readonly string[] SchoolNames = { "", "Огонь", "Электро", "Метеоры", "Лёд" };
    public static readonly string[] SchoolIcons = { "", "school_fire", "school_electro", "school_meteor", "school_ice" };

    // Уровень школы — сумма уровней её заклинаний; каждый уровень даёт +3% урона заклинаниям школы
    public int SchoolLevel(Element el)
    {
        int n = 0;
        foreach (var s in skills) if (s.spell && !s.fusion && s.element == el) n += s.level;
        return n;
    }

    float SchoolMul(Element el) => el == Element.None ? 1 : 1 + 0.03f * SchoolLevel(el);

    public readonly List<Enemy> enemies = new();
    readonly List<Enemy> dying = new();
    readonly Dictionary<string, Stack<Enemy>> enemyPool = new();
    readonly Dictionary<string, Vector3> baseScale = new();
    readonly List<Gem> gems = new();
    readonly Stack<Gem> gemPool = new(), healPool = new();
    readonly List<Proj> projs = new();
    readonly Stack<Proj> projPool = new();
    readonly List<DamageNumber> numbers = new();
    readonly List<Chest> chests = new();
    readonly List<BurnPool> pools = new();
    readonly Stack<Transform> rockPool = new();
    readonly List<(Vector2 pos, float r, float dmg, float t, float t0, bool stun, bool lava, bool ice, Element el, Transform rock, Mage owner)> meteors = new();

    public List<Skill> skills = new();
    readonly List<Skill> choices = new();
    readonly List<Artifact> artifactPool = Artifacts.All();
    public List<Artifact> artifacts = new();
    readonly List<Artifact> chestChoices = new();
    int chestRarity;
    Skill superSkill;
    int pendingLevels;
    State resumeState = State.Play;

    EnemyType[] types;
    EnemyType elite, boss1, boss2;
    Enemy bossShown;
    float spawnAcc, nextElite = 110, surgeUntil, nextChest = 35;
    int bossStage, waveIdx, stage;
    // свободный забег: после победы можно играть дальше, пока не убьют. Победа, золото и рекорд победы уже записаны.
    bool freeRun;
    // режим «Выживание» (GameMenu.selMode): без победы по времени, боссы и кольца по кругу, как в свободном забеге
    bool survival;
    bool NoTimeLimit => freeRun || survival;
    float nextFreeBoss, nextFreeRing;
    int freeBosses, killsAtWin;

    // Большие волны: кольцо врагов сжимается вокруг мага
    static readonly float[] WaveTimes = { 180, 300, 540, 720 };
    static readonly int[] WaveCounts = { 36, 55, 75, 95 };
    bool ringOn;
    Vector2 ringCenter;
    float ringR, ringHold;
    LineRenderer ringLine, pillar;
    float pillarT;

    // Тиры врагов: цвет и рост силы
    static readonly Color[] TierColors =
    {
        new(1f, 1f, 1f), new(0.55f, 1f, 0.55f), new(0.5f, 0.75f, 1.2f),
        new(0.9f, 0.5f, 1.2f), new(1.3f, 0.4f, 0.35f), new(1.4f, 1.1f, 0.3f),
    };
    static readonly Color[] RarityColors = { new(0.85f, 0.75f, 0.55f), new(0.4f, 0.7f, 1f), new(1f, 0.75f, 0.2f) };
    static readonly string[] RarityNames = { "Сундук", "Редкий сундук", "Легендарный сундук" };

    string banner, bannerSub;
    float bannerT, screenFlash;
    float shake, hurtFx, dustT;
    Vector3 camVel;
    MaterialPropertyBlock mpb;

    bool stickOn;
    Vector2 stickOrigin, stickPos;
    readonly List<Enemy> tmp = new(), tmp2 = new();

    void Awake()
    {
        // GUILayout нигде не используется: без этого Unity каждый кадр гоняет OnGUI ещё и ради события Layout
        // (по телеметрии интерфейс на телефонах — 5,7 мс на кадр, втрое дороже всей симуляции)
        useGUILayout = false;
        LiteUi = Application.isMobilePlatform || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-liteui") >= 0;
        I = this;
        if (!quitHooked) { quitHooked = true; Application.quitting += () => Debug.Log("quit: " + System.Environment.StackTrace); }
        Application.targetFrameRate = 60;
        cam = Camera.main;
        mpb = new MaterialPropertyBlock();
        // уровень задаёт мир, мобов и боссов (Levels.cs)
        // без «сразу в забег» игра открывается в городе (GameTown.cs); классическое меню — по кнопке из города
        townScene = !skipMenu && TownWanted();
        var lc = LevelContent.Get(townScene ? "town" : Levels[selLevel].id);
        // кооператив: декор у обоих одинаковый (от него зависят столкновения) — общий сид от хозяина
        coopRun = Coop.starting && skipMenu && Coop.InRoom;
        if (coopRun) Random.InitState(Coop.seed);
        if (townScene) Random.InitState(20261112);   // лес вокруг площади у всех один
        World.Init(cam, lc.id);
        if (coopRun || townScene) Random.InitState(System.Environment.TickCount);
        Vfx.Init();
        types = lc.enemies; elite = lc.elite; boss1 = lc.boss1; boss2 = lc.boss2;

        player = new Player();
        // герой задаёт модель, статы и черту (Heroes.cs); одежда — только у мага, она сшита под его модель
        // выбранный герой помнится между запусками (в городе его меняют кнопкой «Герой»)
        if (!heroLoaded)
        {
            heroLoaded = true;
            selMode = Mathf.Clamp(PlayerPrefs.GetInt("sel.mode", 0), 0, 1);
            selHero = Mathf.Clamp(PlayerPrefs.GetInt("sel.hero", selHero), 0, Heroes.Length - 1);
        }
        // некупленный (или кошелёк ещё не загружен) — в этот раз маг, но сохранённый выбор не трогаем:
        // город вернёт героя, когда кошелёк подтвердит покупку
        // некупленный (или кошелёк ещё не загружен) — в этот раз маг. Сохранённый выбор («sel.hero») пишут только явные
        // действия игрока: карточка героя в меню и экран «Герой» в городе; город вернёт героя, когда покупка подтвердится
        if (Heroes[selHero].soon || !Owns(Heroes[selHero].item)) selHero = 0;
        var hc = HeroContent.Get(Heroes[selHero].id);
        builtHero = Heroes[selHero].id;
        player.go = hc.Spawn(out player.anim);
        if (hc.id == "mage") { Cosmetics.Apply(player.go, Wearing("hat"), Wearing("robe"), Wearing("staff"), Wearing("glow")); builtLook = LookKey(); if (gearOk) ApplyMyWeapon(); }
        // силуэт сквозь склеп и деревья: лишний материал рисует меш ещё раз шейдером XRay (только где мага заслонили)
        xrayMat = new Material(Resources.Load<Shader>("Shaders/XRay"));
        playerRends = PlayerRends();
        player.tr = player.go.transform;
        player.light = new GameObject("MageLight").AddComponent<Light>();
        player.light.type = LightType.Point;
        player.light.color = new Color(1f, 0.78f, 0.55f);
        player.light.range = 9;
        player.light.intensity = 7f;
        player.light.shadows = LightShadows.None;
        hc.ApplyTrait(this);
        player.anim.Play("Idle");

        ringLine = MakeLine("WaveRing", 72, 0.25f, new Color(4f, 0.4f, 0.3f));
        ringLine.loop = true;
        pillar = MakeLine("LevelPillar", 2, 1f, new Color(2f, 3f, 4f));

        skills = NewSkills();
        LevelSkill(hc.StartSkill(skills));
        ApplyMeta();
        if (gearOk) RefreshGearStats(true);
        me = cur = new Mage { hero = builtHero, nick = Nick };
        Save(me);
        Hello();
        // после «Начать заново» сразу в бой, иначе — стартовый экран выбора уровня и героя
        if (skipMenu) { skipMenu = false; StartRun(); }
        else if (townScene) TownStart();
        else state = State.Menu;
    }

    // заклинания, пассивки и слияния одного мага; у друга в кооперативе — свой такой же набор
    // Стенд: ?orbs=N выдаёт шаровые молнии нужного уровня сразу на старте, иначе бот их видит
    // только если карта случайно выпала. Работает лишь на локальном адресе.
    void GiveOnStand(Orbs orbs, Fireball fireball)
    {
        if (!(Application.absoluteURL ?? "").Contains("127.0.0.1")) return;
        var of = System.Text.RegularExpressions.Regex.Match(Application.absoluteURL, "[?&]orbs=(\\d)");
        if (of.Success)
        {
            orbs.level = Mathf.Clamp(int.Parse(of.Groups[1].Value), 1, 8);
            orbs.OnLevel(this);
            orbs.Show(true);
        }
        var fire = System.Text.RegularExpressions.Regex.Match(Application.absoluteURL, "[?&]fire=(\\d)");
        if (fire.Success)
        {
            fireball.level = Mathf.Clamp(int.Parse(fire.Groups[1].Value), 1, 8);
            fireball.OnLevel(this);
        }
    }

    List<Skill> NewSkills()
    {
        var skills = new List<Skill>();
        var fb = new Fireball(); var li = new Lightning(); var orbs = new Orbs(); var aura = new FireAura(); var met = new Meteor();
        var lance = new IceLance(); var bliz = new Blizzard(); var quake = new Quake();
        skills.AddRange(new Skill[] { fb, li, orbs, aura, met, lance, bliz, quake });
        GiveOnStand(orbs, fb);
        var gold = new Color(1f, 0.85f, 0.3f);
        // пассивки: 5 уровней, на последнем — развилка из трёх путей
        skills.Add(new Passive("Сила", "+15% урона всем заклинаниям", "might", p => p.dmgMul += 0.15f, gold).Paths(
            ("Берсерк", "+40% урона, но -25 к максимуму здоровья", p => { p.dmgMul += 0.4f; p.maxHp -= 25; p.hp = Mathf.Min(p.hp, p.maxHp); }),
            ("Точность", "+12% шанс крита, крит сильнее на 0.5", p => { p.crit += 0.12f; p.critMul += 0.5f; }),
            ("Стойкость", "+15% урона и +40 здоровья", p => { p.dmgMul += 0.15f; p.maxHp += 40; p.hp += 40; })));
        skills.Add(new Passive("Спешка", "Заклинания на 8% быстрее", "haste", p => p.cdMul *= 0.92f, gold).Paths(
            ("Буря", "Заклинания на 20% быстрее", p => p.cdMul *= 0.8f),
            ("Размах бури", "+15% площади и чары на 8% быстрее", p => { p.area += 0.15f; p.cdMul *= 0.92f; }),
            ("Попутный ветер", "+20% к бегу и чары на 8% быстрее", p => { p.speed *= 1.2f; p.cdMul *= 0.92f; })));
        skills.Add(new Passive("Лёгкие сапоги", "+10% к скорости бега", "boots", p => p.speed *= 1.1f, gold).Paths(
            ("Призрак", "+25% к бегу", p => p.speed *= 1.25f),
            ("Собиратель", "Радиус сбора опыта +60%", p => p.magnet *= 1.6f),
            ("Уклонение", "Входящий урон -15%", p => p.armor += 0.15f)));
        skills.Add(new Passive("Живучесть", "+25 к здоровью и лечение", "vitality", p => { p.maxHp += 25; p.hp += 25; }, new Color(1f, 0.4f, 0.45f)).Paths(
            ("Колосс", "+100 к здоровью", p => { p.maxHp += 100; p.hp += 100; }),
            ("Второе дыхание", "+2 здоровья в секунду", p => p.regen += 2),
            ("Шипы", "Скелеты получают 200% урона, который наносят", p => p.thorns += 2)));
        skills.Add(new Passive("Магнит", "Опыт притягивается на 35% дальше", "magnet", p => p.magnet *= 1.35f, gold).Paths(
            ("Мудрец", "+30% опыта", p => p.xpMul += 0.3f),
            ("Золотоискатель", "+40% золота в конце забега", p => p.goldMul += 0.4f),
            ("Чёрная дыра", "Радиус сбора опыта ×2", p => p.magnet *= 2f)));
        skills.Add(new Passive("Регенерация", "+0.6 здоровья в секунду", "regen", p => p.regen += 0.6f, new Color(0.5f, 1f, 0.5f)).Paths(
            ("Вечность", "+2.5 здоровья в секунду", p => p.regen += 2.5f),
            ("Кровопийца", "Вампиризм +4%", p => p.vamp += 0.04f),
            ("Каменная кожа", "Входящий урон -15%", p => p.armor += 0.15f)));
        skills.Add(new Passive("Размах", "+12% к площади заклинаний", "area", p => p.area += 0.12f, gold).Paths(
            ("Шторм", "+30% к площади", p => p.area += 0.3f),
            ("Фокус", "+25% урона", p => p.dmgMul += 0.25f),
            ("Сеть", "+15% площади и чары на 10% быстрее", p => { p.area += 0.15f; p.cdMul *= 0.9f; })));
        skills.Add(new Passive("Вампиризм", "Лечит на 2% от нанесённого урона", "vampire", p => p.vamp += 0.02f, new Color(1f, 0.25f, 0.3f)).Paths(
            ("Упырь", "Вампиризм +5%", p => p.vamp += 0.05f),
            ("Пир", "Каждое убийство лечит на 3", p => p.killHeal += 3),
            ("Кровавый доспех", "+50 здоровья и вампиризм +1%", p => { p.maxHp += 50; p.hp += 50; p.vamp += 0.01f; })));
        skills.Add(new Passive("Меткость", "+6% шанс критического удара", "precision", p => p.crit += 0.06f, gold).Paths(
            ("Снайпер", "+20% шанс крита", p => p.crit += 0.2f),
            ("Казнь", "Крит сильнее на 1.0", p => p.critMul += 1f),
            ("Удача", "+12% шанс крита и +20% золота", p => { p.crit += 0.12f; p.goldMul += 0.2f; })));
        skills.Add(new PlasmaStorm(fb, li));
        skills.Add(new Starfall(fb, met));
        skills.Add(new TeslaRing(li, orbs));
        skills.Add(new SolarSystem(aura, orbs));
        skills.Add(new SkyWrath(met, li));
        skills.Add(new Eruption(aura, met));
        skills.Add(new FrostFire(fb, lance));
        skills.Add(new AbsoluteZero(bliz, met));
        // запретная школа — в конце списка: номера остальных заклинаний (сеть, сохранения) не сдвигаются
        skills.Add(new SoulReap()); skills.Add(new BonePrison()); skills.Add(new BlackSun());
        return skills;
    }

    LineRenderer MakeLine(string name, int n, float w, Color hdr)
    {
        var lr = new GameObject(name).AddComponent<LineRenderer>();
        lr.positionCount = n;
        lr.widthMultiplier = w;
        lr.sharedMaterial = new Material(World.Additive) { mainTexture = Art.White };
        lr.sharedMaterial.SetColor("_Color", hdr);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.gameObject.SetActive(false);
        return lr;
    }

    Material xrayMat;
    Renderer[] playerRends;
    bool xrayOn;

    // включаем силуэт только когда маг за высоким декором, иначе контур просвечивал бы сквозь его же шляпу
    void SetXRay(bool on)
    {
        if (on == xrayOn || xrayMat == null) return;
        xrayOn = on;
        foreach (var r in playerRends)
        {
            if (r == null) continue;
            var m = new List<Material>(r.sharedMaterials);
            if (on) m.Add(xrayMat); else m.Remove(xrayMat);
            r.sharedMaterials = m.ToArray();
        }
    }

    void LevelSkill(Skill s)
    {
        s.level++;
        s.OnLevel(this);
    }

    // Замер: сколько миллисекунд кадра уходит на логику с анимацией (Update → LateUpdate) и на интерфейс (OnGUI Repaint).
    // Уходит в срезы телеметрии (sim, gui — десятые доли мс) и в лог автопилота.
    double perfT0, perfSim, perfGui, perfOvl, perfHud;   // ovl — поверх мира (цифры, полоски, маркеры), hud — верхняя панель
    int perfFrames;
    void LateUpdate() { perfSim += Time.realtimeSinceStartupAsDouble - perfT0; perfFrames++; }

    void Update()
    {
        perfT0 = Time.realtimeSinceStartupAsDouble;
        LabArgs();
        UiHookTick();   // ?ui=<экран> — проверка вёрстки (GameUiHook.cs)
        // шаг не больше 1/20 с: на слабом ноуте (20–30 FPS) мир не должен идти медленнее мага и часов
        float dt = LabOn ? 1 / 30f : Mathf.Min(Time.deltaTime, bot ? 1 / 12f : 1 / 20f);
        // стенд: экран выбора магии открывается редко, поэтому его частоту кадров считаем отдельно
        if (state == State.LevelUp && (Application.absoluteURL ?? "").Contains("127.0.0.1"))
        {
            lvlT += Time.unscaledDeltaTime; lvlF++;
            if (lvlT > 1f) { Debug.Log($"levelup {lvlF / lvlT:0} fps"); lvlT = 0; lvlF = 0; }
        }
        // хитстоп: на долю секунды мир почти замирает — удар ощущается тяжелее
        if (hitstop > 0) { hitstop -= Time.unscaledDeltaTime; dt *= 0.08f; }
        if (bot || auto) BotTick();
        CoopTick();
        PrefsTick();   // наряд и настройки — на сервер (GamePrefs.cs)
        PhoneInputTick();
        FrameRateTick();
        if (state == State.Menu) WorldTick();   // скидка дня нужна и магазину из меню
        PresenceTick(Time.unscaledDeltaTime);
        MusicTick(Time.unscaledDeltaTime);
        SfxTick(Time.unscaledDeltaTime);
        Cosmetics.Tick(Time.deltaTime);
        var kb = Keyboard.current;
        // пока игрок печатает в чат, клавиши игры молчат
        if (Typing) kb = null;
        if (kb != null && (coopRun || SoloChat) && state == State.Play && kb.tKey.wasPressedThisFrame) RunChatOpen();
        if (kb != null && (kb.tabKey.wasPressedThisFrame || kb.iKey.wasPressedThisFrame)) ToggleStats();
        if (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.pKey.wasPressedThisFrame)) TogglePause();
        // вдвоём выбор карты мир не останавливает: карты открываются по кнопке и закрываются «Позже» (GameCoop.cs)
        if (kb != null && coopRun)
        {
            if (state == State.Play && (kb.eKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) OpenChoice();
            else if (Choosing && kb.escapeKey.wasPressedThisFrame) HoldChoice();
        }
        bool run = WorldRuns;
        if (run) { RunSnapTick(); FpsTick(); }
        if (run) { if (Guest) GuestStep(dt); else Step(dt); if (Choosing) Keys(); }
        else if (state == State.Town) TownStep(dt);
        else Keys();
        LabTick();
        float sdt = run || state == State.Town ? dt : 0;
        Vfx.Update(sdt);
        GemHalos();
        UpdateRune(dt);
        UpdateDying(sdt);
        UpdatePillar(dt);
        UpdateCamera(dt);
        bannerT -= dt;
        screenFlash = Mathf.Max(0, screenFlash - dt * 2.5f);
        // в толпе за 150 мобов искр меньше: и глазам, и кадру легче
        Vfx.Amount = optCalm ? 0.35f : enemies.Count > 150 ? 0.6f : 1f;
        Vfx.Dim = optCalm ? 0.55f : 1f;
        if (optCalm) screenFlash = 0;
        hurtFx = Mathf.Max(0, hurtFx - dt * 2);
        World.Hurt(Mathf.Max(hurtFx, player.hp < player.maxHp * 0.3f ? 0.5f + 0.2f * Mathf.Sin(time * 6) : 0));
        foreach (var n in numbers) n.t += dt;
        if (state == State.Play) HealNumbers();
        numbers.RemoveAll(n => n.t > 0.8f);
    }

    void ToggleStats()
    {
        if (state == State.Play) { resumeState = State.Play; state = State.Stats; }
        else if (state == State.Stats) state = resumeState;
    }

    void Step(float dt)
    {
        time += dt;
        TelemetryTick(dt);
        AdaptTick();
        TrackVel(dt);
        DirectorTick(dt);
        if (time - dt < 1.2f && time >= 1.2f && !freeRun) RunGoalHint();
        if (!me.down) MoveMe(dt);
        MatesTick(dt);
        Ambient(dt, player.moving);

        Spawn(dt);
        UpdateStage();
        BuildGrid();
        UpdateEnemies(dt);
        FallenTick(dt);
        TickMages(dt);
        UpdateMeteors(dt);
        UpdateProjs(dt);
        UpdateBolts(dt);
        UpdateSlams(dt);
        UpdatePools(dt);
        UpdateGems(dt);
        UpdateChests(dt);
        UpdateLoot(dt);
        foreach (var at in magnetDrops) SpawnRelic(1, at);
        magnetDrops.Clear();
        Sweep();
        World.Follow(ViewPos);
        SetXRay(World.Occluded(player.pos));
        WatchFeather();

        if (CoopDeaths(dt)) { }
        else if (player.hp <= 0)
        {
            if (player.revives > 0)
            {
                player.revives--;
                player.hp = player.maxHp * 0.5f;
                Boom(player.pos, 4, new Color(1f, 0.6f, 0.2f), false);
                Query(player.pos, 5, tmp);
                foreach (var e in tmp) Hit(e, 200, (e.pos - player.pos).normalized, 2f);
                Banner("Перо феникса!", "Маг восстал из пепла");
                screenFlash = 1;
                if (me.down) SetDown(me, false);
            }
            else
            {
                player.hp = 0;
                state = State.Dead;
                if (freeRun) FreeEnd();
                else
                {
                    SaveRecords(false);
                    AwardGold(false);
                    FallenUpload();
                }
                if (coopRun && !Guest) SendEnd(false);
                if (bot) botLog.AppendLine($"DEAD t={time / 60:0.0} lv={level} kills={kills} spells={SpellSummary()}");
                player.anim.CrossFade("Death", 0.1f);
            }
        }
        if (state != State.Play) return;
        if (!NoTimeLimit && time >= RunLength && bossStage >= 3 && !AnyBoss())
        {
            state = State.Won;
            SaveRecords(true);
            AwardGold(true);
            if (coopRun) SendEnd(true);
            if (bot) botLog.AppendLine($"WON t={time / 60:0.0} lv={level} kills={kills} hp={player.hp:0}");
        }
        else if (grimoirePending) OpenGrimoire();
        else if (pendingLevels > 0 && !coopRun) OpenLevelUp();
    }

    // «Играть дальше» на экране победы: тот же забег без конца по времени
    void FreeContinue()
    {
        freeRun = true;
        killsAtWin = kills;
        nextFreeBoss = time + 90;
        nextFreeRing = time + 45;
        tSent = false;
        state = State.Play;
        WorldBanner("СВОБОДНЫЙ ЗАБЕГ", "Сколько продержишься?");
    }

    // гибель в свободном забеге: победа и золото уже засчитаны, обновляем только личные рекорды и телеметрию
    void FreeEnd()
    {
        if (bot) return;
        PlayerPrefs.SetFloat("rec.time", Mathf.Max(PlayerPrefs.GetFloat("rec.time"), time));
        PlayerPrefs.SetInt("rec.level", Mathf.Max(PlayerPrefs.GetInt("rec.level"), level));
        PlayerPrefs.SetInt("rec.kills", Mathf.Max(PlayerPrefs.GetInt("rec.kills"), kills));
        PlayerPrefs.SetInt("rec.total", PlayerPrefs.GetInt("rec.total") + kills - killsAtWin);
        PlayerPrefs.Save();
        SendTelemetry("free");
    }

    // свой маг: ввод, прыжок, столкновения с декором, анимация
    void MoveMe(float dt)
    {
        var move = ReadMove();
        player.pos += move * player.speed * (time < windsUntil ? 1.6f : 1f) * dt;
        jumpCd = Mathf.Max(0, jumpCd - dt);
        if (jumpT > 0)
        {
            // подлёт: рывок вперёд по дуге, декор и толпа под ногами
            player.pos += jumpDir * player.speed * 1.5f * dt;
            player.pos = World.Collide(player.pos, 0.65f, 1.6f);
            jumpT -= dt;
            if (jumpT <= 0) Land();
        }
        // шляпа шире тела: о декор маг упирается полями шляпы, а не телом
        else player.pos = World.Collide(player.pos, 0.65f);
        Tether(dt);
        bool moving = move.sqrMagnitude > 0.01f;
        if (moving) player.face = move.normalized;
        if (moving != player.moving)
        {
            player.anim.CrossFade(moving ? "Run" : "Idle", 0.12f);
            player.moving = moving;
        }
        if (!Guest)
        {
            float heal = Mathf.Min(player.maxHp - player.hp, player.regen * (player.hurt > 0 ? 0.35f : 1f) * dt);
            if (heal > 0) { player.hp += heal; labRegen += heal; }
        }
        player.hurt = Mathf.Max(0, player.hurt - dt);
        player.tr.position = World.W(player.pos, JumpH);
        MageBody(dt, moving);
        player.light.transform.position = World.W(player.pos, 2.6f);
    }

    // Живое тело мага поверх клипов: в беге корпус клонится вперёд, в прыжке — ныряет и вытягивается,
    // на приземлении — приседает и пружинит обратно. Поворот считается отдельно от наклона, иначе наклон копился бы.
    Quaternion mageYaw = Quaternion.identity;
    Vector3 mageScale0;
    float mageLean, squashT;

    void MageBody(float dt, bool moving)
    {
        if (mageScale0 == Vector3.zero) { mageScale0 = player.tr.localScale; mageYaw = player.tr.rotation; }
        mageYaw = Quaternion.Slerp(mageYaw, Quaternion.LookRotation(World.W(player.face)), dt * 12);
        float phase = jumpT > 0 ? 1 - jumpT / JumpTime : 0;   // 0 → 1 за прыжок
        float target = jumpT > 0 ? Mathf.Lerp(26, -12, phase) : moving ? 9 : 0;
        mageLean = Mathf.Lerp(mageLean, target, 1 - Mathf.Exp(-dt * 14));
        player.tr.rotation = mageYaw * Quaternion.Euler(mageLean, 0, 0);
        squashT = Mathf.Max(0, squashT - dt * 4.5f);
        float stretch = jumpT > 0 ? 0.12f * Mathf.Sin(phase * Mathf.PI) : 0;
        // приземление: присел (squashT 1 → 0.5), затем слегка вытянулся и вернулся
        float sq = squashT > 0 ? Mathf.Sin(squashT * Mathf.PI) * (squashT > 0.5f ? 0.24f : -0.06f) : 0;
        float y = 1 + stretch - sq, xz = 1 - stretch * 0.5f + sq * 0.6f;
        player.tr.localScale = new Vector3(mageScale0.x * xz, mageScale0.y * y, mageScale0.z * xz);
    }

    // Светлячки в воздухе и пыль из-под ног
    void Ambient(float dt, bool moving)
    {
        if (Random.value < 0.35f)
        {
            var p = World.W(player.pos + new Vector2(Random.Range(-13f, 13f), Random.Range(-8f, 12f)), Random.Range(0.3f, 2.5f));
            var c = stage == 1 ? new Color(1f, 0.4f, 0.3f) : stage == 2 ? new Color(0.5f, 1f, 0.5f) : new Color(0.7f, 0.9f, 0.6f);
            Vfx.Emit(Vfx.Ember, p, new Vector3(Random.Range(-0.3f, 0.3f), 0.25f, Random.Range(-0.3f, 0.3f)), 0.13f, 3f, c);
        }
        dustT -= dt;
        if (moving && dustT <= 0)
        {
            dustT = 0.12f;
            Vfx.Emit(Vfx.Smoke, World.W(player.pos - player.face * 0.3f, 0.1f), Vector3.up * 0.3f, 0.5f, 0.6f, new Color(0.35f, 0.3f, 0.25f, 0.4f));
        }
    }

    float hitstop, runeT;
    Transform rune;
    Material runeMat;

    void HitStop(float t) { if (!bot && !optCalm) hitstop = Mathf.Max(hitstop, t); }

    // Руна под ногами мага: вспыхивает на каждом касте и медленно гаснет
    void UpdateRune(float dt)
    {
        if (rune == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(go.GetComponent<Collider>());
            runeMat = new Material(Resources.Load<Material>("Mats/Additive")) { mainTexture = Art.Tex("magic_05") };
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = runeMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rune = go.transform;
        }
        runeT = Mathf.Max(0, runeT - dt * 2.2f);
        rune.gameObject.SetActive(runeT > 0.01f);
        if (runeT <= 0.01f) return;
        rune.SetPositionAndRotation(World.W(player.pos, 0.06f), Quaternion.Euler(90, time * 70, 0));
        rune.localScale = Vector3.one * (2.4f + (1 - runeT) * 0.8f);
        runeMat.SetColor("_Color", runeColor * (runeT * 2.5f));
    }
    Color runeColor;

    public void Cast(Color c)
    {
        if (cur == me) { runeT = 1; runeColor = c; }
        Vfx.Emit(Vfx.Glow, World.W(player.pos + player.face * 0.4f, 1.4f), Vector3.zero, 1.1f, 0.15f, c);
        if (!player.moving) player.anim.CrossFade("Cast", 0.05f);
    }

    // dur — сколько висит: подсказку цели забега читают дольше обычной таблички
    void Banner(string title, string sub = null, float dur = 3.5f)
    {
        banner = title;
        bannerSub = sub;
        bannerT = bannerDur = dur;
    }
    float bannerDur = 3.5f;

    // ---------- Ввод ----------

    Vector2 ReadMove()
    {
        if (LabOn) return LabMove();
        if (bot || auto) return BotMove();
        if (Typing) { stickOn = false; return Vector2.zero; }
        var v = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1;
        }

        // карты открыты поверх идущего боя (кооператив): касания — это выбор карты, а не джойстик
        if (state != State.Play && state != State.Town) { stickOn = false; return Vector2.ClampMagnitude(v, 1); }

        bool down = false;
        Vector2 sp = default;
        // касание кнопки прыжка не трогает джойстик; первый палец вне кнопки — джойстик
        var ts = Touchscreen.current;
        if (ts != null)
            foreach (var t in ts.touches)
            {
                if (!t.press.isPressed) continue;
                var p = t.position.ReadValue();
                if (InJumpBtn(p)) { if (t.press.wasPressedThisFrame) TryJump(move: v); continue; }
                if (!down) { down = true; sp = p; }
            }
        if (!down && Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            var p = Mouse.current.position.ReadValue();
            if (InJumpBtn(p)) { if (Mouse.current.leftButton.wasPressedThisFrame) TryJump(move: v); }
            else { down = true; sp = p; }
        }
        if (kb != null && kb.spaceKey.wasPressedThisFrame) TryJump(move: v);
        // кнопка статов в правом верхнем углу не должна дёргать джойстик
        if (down && !stickOn && sp.y > Screen.height * 0.88f && sp.x > Screen.width * 0.75f) down = false;
        // в городе кнопки, чат и окно портала — не джойстик
        if (down && !stickOn && state == State.Town && TownBlocked(sp)) down = false;
        // быстрые фразы похода и «Поле боя» — тоже не джойстик
        if (down && !stickOn && QuickBlocked(sp)) down = false;

        if (down)
        {
            if (!stickOn) { stickOn = true; stickOrigin = sp; }
            float R = Mathf.Min(Screen.width, Screen.height) * 0.12f;
            var d = sp - stickOrigin;
            if (d.magnitude > R) { stickOrigin = sp - d.normalized * R; d = d.normalized * R; }
            stickPos = sp;
            v += d / R;
        }
        else stickOn = false;

        return Vector2.ClampMagnitude(v, 1);
    }

    void Keys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        int k = kb.digit1Key.wasPressedThisFrame ? 0 : kb.digit2Key.wasPressedThisFrame ? 1 : kb.digit3Key.wasPressedThisFrame ? 2 : kb.digit4Key.wasPressedThisFrame ? 3 : -1;
        if (k < 0) return;
        if (state == State.LevelUp) Pick(k);
        else if (state == State.Super) PickSuper(k);
        else if (state == State.Chest) PickArtifact(k);
    }

    // ---------- Сложность: главы, волны, боссы ----------

    void UpdateStage()
    {
        int s = Mathf.Min(World.Stages.Length - 1, (int)(time / 300));
        if (s != stage)
        {
            stage = s;
            Banner(World.Stages[s].title, "Монстры стали сильнее");
        }
        float k = Mathf.Clamp01((time - stage * 300) / 4f);
        if (stage > 0) World.BlendStage(World.Stages[stage], k);
    }

    // поздний нажим уровня (LevelContent.late*); лаборатория может подставить свои числа: lhp, ldmg, lrate, lfrom
    float LateX => Mathf.Max(0, Mathf.Min(time, RunLength) / 60f - (LabOn && lab.lfrom >= 0 ? lab.lfrom : World.Level.lateFrom));
    float LateHp => LabOn && lab.lhp >= 0 ? lab.lhp : World.Level.lateHp;
    float LateDmg => LabOn && lab.ldmg >= 0 ? lab.ldmg : World.Level.lateDmg;
    float LateRate => LabOn && lab.lrate >= 0 ? lab.lrate : World.Level.lateRate;

    int TierNow()
    {
        int t = Mathf.Min(5, (int)(time / 150));
        if (Random.value < 0.15f) t++;
        return Mathf.Min(5, t);
    }

    void Spawn(float dt)
    {
        FlushSummons();   // скелеты призывателей (GameThreats.cs)
        float m = time / 60f;
        if (time < RunLength || NoTimeLimit)
        {
            float rate = (1.5f + m * 0.85f) * (time < surgeUntil ? 1.7f : 1f) * CurDiff.rate * (1 + LateRate * LateX);
            spawnAcc += rate * dt;
            int unlocked = m < 1 ? 1 : m < 2.5f ? 2 : m < 4 ? 3 : 4;
            while (spawnAcc >= 1)
            {
                spawnAcc -= 1;
                int i = Random.value < 0.6f ? unlocked - 1 : Random.Range(0, unlocked);
                var st = RollSpecial(m) ?? types[i];
                // сверх потолка (он ниже при слабом FPS) моб не рождается, а вливается в живого — кадр не тонет, давление то же
                if (enemies.Count >= EnemyCap) { MergeSpawn(st, TierNow()); continue; }
                SpawnEnemy(st, RingPoint(Random.value * Mathf.PI * 2), TierNow(), false);
            }
        }

        if (waveIdx < WaveTimes.Length && time >= WaveTimes[waveIdx])
        {
            StartRing(WaveCounts[waveIdx]);
            waveIdx++;
        }
        UpdateRing(dt);

        if (time >= nextElite)
        {
            nextElite += CurDiff.elite;
            SpawnEnemy(elite, RingPoint(Random.value * Mathf.PI * 2), TierNow(), false);
        }
        if (time >= nextChest)
        {
            nextChest += Random.Range(50f, 70f) / player.findMul;
            float r = Random.value;
            SpawnChest(World.Free(Anchor() + Random.insideUnitCircle.normalized * Random.Range(13f, 19f), 1.3f), r < 0.06f ? 2 : r < 0.32f ? 1 : 0);
        }
        if (bossStage == 0 && time >= 450) { bossStage = 1; SpawnBoss(boss1); }
        if (bossStage == 1 && time >= 660) { bossStage = 2; SpawnBoss(boss1); }
        if (bossStage == 2 && time >= 14 * 60) { bossStage = 3; SpawnBoss(boss2); }
        if (NoTimeLimit && time > RunLength)
        {
            // дальше по кругу: кольцо каждые две минуты, босс каждые две с половиной (через раз — старший)
            if (time >= nextFreeRing && !ringOn) { nextFreeRing = time + 120; StartRing(26); }
            if (time >= nextFreeBoss) { nextFreeBoss = time + 150; SpawnBoss(freeBosses++ % 2 == 0 ? boss1 : boss2); }
        }
    }

    void SpawnBoss(EnemyType t)
    {
        bossShown = SpawnEnemy(t, Anchor() + Random.insideUnitCircle.normalized * 9, TierNow(), true);
        WorldBanner(t.name, "Босс пробудился · уходи из красных кругов");
        Shake(0.5f);
    }

    void StartRing(int n)
    {
        ringOn = true;
        ringCenter = Anchor();
        ringR = 13f;
        ringHold = 2f;
        surgeUntil = time + 25;
        WorldBanner("ВОЛНА!", "Кольцо сжимается — прорывайся");
        Shake(0.3f);
        var t = types[Mathf.Min(types.Length - 1, waveIdx)];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2 / n;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var e = SpawnEnemy(t, ringCenter + dir * ringR, Mathf.Min(5, TierNow() + (waveIdx > 0 ? 1 : 0)), true);
            e.ring = true;
            e.ringDir = dir;
        }
        ringLine.gameObject.SetActive(true);
    }

    void UpdateRing(float dt)
    {
        if (!ringOn) return;
        ringHold -= dt;
        if (ringHold <= 0) ringR = Mathf.Max(0, ringR - 1.1f * dt);
        DrawRing();
        if (ringR < 1.5f)
        {
            ringOn = false;
            ringLine.gameObject.SetActive(false);
            foreach (var e in enemies) e.ring = false;
        }
    }

    void DrawRing()
    {
        for (int i = 0; i < ringLine.positionCount; i++)
        {
            float a = i * Mathf.PI * 2 / ringLine.positionCount;
            ringLine.SetPosition(i, World.W(ringCenter + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (ringR + 0.6f), 0.08f));
        }
    }

    // на разном расстоянии (16–23), а не ровным кольцом 17: толпа подходит волнами, а не одним комком (фидбек 16.11)
    Vector2 RingPoint(float a) => Anchor() + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(16f, 23f);

    // здоровье врага этого вида и тира на текущей минуте забега
    float EnemyHp(EnemyType t, int tier)
    {
        float mins = time / 60f;
        float late = 1 + LateHp * LateX * LateX;
        return t.hp * (1 + mins * 0.36f + mins * mins * 0.045f) * (1 + (t.boss ? 0 : tier) * 0.85f) * CurDiff.hp * (t.boss ? Mathf.Sqrt(late * press) : late * press);
    }

    Enemy SpawnEnemy(EnemyType t, Vector2 pos, int tier, bool rise)
    {
        // пул по модели и оружию: у особых врагов то же тело, но другое оружие
        var key = PoolKey(t);
        if (!enemyPool.TryGetValue(key, out var pool)) enemyPool[key] = pool = new Stack<Enemy>();
        var e = pool.Count > 0 ? pool.Pop() : null;
        if (e == null)
        {
            e = new Enemy();
            e.go = World.Character(t.model, 1f, out e.anim, out _);
            World.Attach(e.go, t.weapon, World.Skeleton);
            e.rends = World.Combine(e.go, t.model + "|" + t.weapon, World.Skeleton, true);
            // после склейки исходные рендеры выключены, и аниматор с отсечением считает скелет невидимым — кости замирают
            e.anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            e.anim.keepAnimatorStateOnDisable = true;
            e.tr = e.go.transform;
            if (!baseScale.ContainsKey(key)) baseScale[key] = e.tr.localScale;
        }
        e.go.SetActive(true);
        e.type = t;
        e.tier = t.boss ? 0 : tier;
        float tierScale = 1 + 0.06f * e.tier;
        e.tr.localScale = e.scale0 = baseScale[key] * t.scale * tierScale;
        e.pos = pos;
        float mins = time / 60f;
        // поздняя игра: квадратичный рост, иначе к 10-й минуте билд со слияниями выкашивает всё мгновенно
        e.maxHp = e.hp = EnemyHp(t, e.tier);
        // базовый рост урона — только до 15:00; дальше (выживание, «Играть дальше») растёт одна добавка +10%/мин.
        // Раньше шли оба роста сразу, и к 34-й минуте удар босса был ~2800 при здоровье ~600 (фидбек 16.11)
        e.dmg = t.dmg * (1 + Mathf.Min(time, RunLength) / 60f * 0.18f) * (1 + e.tier * 0.35f) * CurDiff.dmg * (1 + LateDmg * LateX);
        if (NoTimeLimit && time > RunLength) e.dmg *= 1 + (time - RunLength) / 60f * 0.1f;   // конец должен наступить, но не за секунду
        e.radius = 0.32f * t.scale * tierScale;
        e.flash = 0; e.orbReadyAt = 0; e.attackCd = 0; e.stun = 0; e.shownUntil = 0; e.slow = 0; e.frozen = 0;
        e.charged = false; e.raised = false;
        e.merged = 0;
        e.shotCd = Random.Range(1.2f, 2.6f);
        e.rage = false;
        e.ring = false;
        e.alive = true;
        e.sentKnown = false;
        nextEnemyId = (nextEnemyId + 1) & 0xFFFF;
        e.id = nextEnemyId;
        e.tint = TierColors[e.tier];
        if (t.elite) e.tint = new Color(1.2f, 0.45f, 0.4f);
        if (t.fallen) e.tint = new Color(0.45f, 1.5f, 1.2f);
        if (t.boss) e.tint = t == boss2 ? new Color(0.7f, 0.9f, 1.3f) : new Color(1.1f, 0.95f, 0.8f);
        if (t.walker) e.tint = WalkerTint;
        if (t.golem) e.tint = GolemTint;
        if (t.archer) e.tint = ArcherTint;
        if (t.bomber) e.tint = BomberTint;
        if (t.summoner) e.tint = SummonerTint;
        if (t.walker || t.golem || t.archer || t.bomber || t.summoner) SpecialIntro(t);
        Visual(e);
        e.tr.SetPositionAndRotation(World.W(pos), Quaternion.LookRotation(World.W(player.pos - pos)));
        e.anim.speed = e.animSpeed = Mathf.Clamp(t.speed / (t.speed > 2.5f ? 3f : 1.7f), 0.7f, 1.5f);
        if (rise)
        {
            e.anim.enabled = true;
            e.anim.Play("Spawn", 0, 0);
            VatState(e.rends, "Idle");
            e.spawnLock = 1.1f;
            Vfx.Burst(Vfx.Smoke, World.W(pos, 0.2f), 4, 1.2f, 1.2f, 0.9f, new Color(0.25f, 0.2f, 0.15f, 0.7f), 0.8f, 0.4f);
            Vfx.Burst(Vfx.Bone, World.W(pos, 0.1f), 5, 3, 0.08f, 0.6f, new Color(0.35f, 0.28f, 0.2f), 1f);
        }
        else
        {
            e.anim.enabled = true;
            e.anim.Play(t.speed > 2.5f ? "Run" : "Walk", 0, Random.value);
            VatState(e.rends, t.speed > 2.5f ? "Run" : "Walk");
            e.spawnLock = 0;
        }
        enemies.Add(e);
        return e;
    }

    void Visual(Enemy e)
    {
        var tint = e.rage ? RageTint : e.tint;
        if (e.charged) tint = Color.Lerp(tint, new Color(0.6f, 0.8f, 1.5f), 0.5f);
        if (e.frozen > 0) tint = new Color(0.4f, 0.62f, 1.05f);
        else if (e.slow > 0) tint = Color.Lerp(tint, new Color(0.6f, 0.85f, 1.3f), 0.5f);
        var va = e.rends != null && e.rends.Length > 0 ? e.rends[0].GetComponent<VatActor>() : null;
        if (va != null) { va.Tint(e.flash > 0.02f ? Color.Lerp(tint, Color.white, 0.55f) : World.VatTint(tint, e.tier >= 4 ? 0.15f : 0f)); return; }
        var m = e.flash > 0.02f ? World.FlashMat : World.SkelMat(tint, e.frozen > 0 ? 0.12f : e.tier >= 4 ? 0.15f : 0);
        if (m == e.mat) return;
        e.mat = m;
        foreach (var r in e.rends) r.sharedMaterial = m;
    }

    public void Tint(Renderer r, Color c) => r.sharedMaterial = World.GlowMat(c);

    static void VatSpeed(Renderer[] rends, float speed)
    {
        var a = rends != null && rends.Length > 0 ? rends[0].GetComponent<VatActor>() : null;
        if (a != null) a.Speed(speed);
    }

    static void VatState(Renderer[] rends, string state)
    {
        var a = rends != null && rends.Length > 0 ? rends[0].GetComponent<VatActor>() : null;
        if (a != null) a.State(state);
    }

    // Чёрное солнце аддитивным быть не может: аддитив только складывает свет, чёрного в нём нет.
    // Ему нужен обычный прозрачный материал с тёмным ядром.
    static Material blackSun;
    public void MakeBlackSun(Proj p)
    {
        if (blackSun == null)
        {
            blackSun = new Material(Resources.Load<Material>("Mats/Alpha")) { mainTexture = Art.Tex("circle_02") };
            blackSun.SetColor("_Color", new Color(0.04f, 0.02f, 0.07f, 0.94f));
        }
        p.rend.sharedMaterial = blackSun;
    }

    bool AnyBoss()
    {
        foreach (var e in enemies) if (e.alive && e.type.boss) return true;
        return false;
    }

    readonly Dictionary<Mage, float> biteUsed = new();   // урон касаниями за этот кадр по каждому магу

    void UpdateEnemies(float dt)
    {
        biteUsed.Clear();
        foreach (var e in enemies)
        {
            if (!e.alive) continue;
            // Расталкивание соседей, чтобы толпа не схлопывалась в точку
            e.sep = Vector2.zero;
            int cx = Mathf.FloorToInt(e.pos.x / Cell), cy = Mathf.FloorToInt(e.pos.y / Cell);
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (!grid.TryGetValue(Key(x, y), out var l)) continue;
                foreach (var n in l)
                {
                    if (n == e) continue;
                    var d = e.pos - n.pos;
                    float min = e.radius + n.radius, dd = d.sqrMagnitude;
                    if (dd < min * min && dd > 1e-6f)
                    {
                        float len = Mathf.Sqrt(dd);
                        e.sep += d / len * (min - len) * (n.type.boss && !e.type.boss ? 1f : 0.5f);
                    }
                }
            }
        }

        foreach (var e in enemies)
        {
            if (!e.alive) continue;
            // ходока не держат ни буран, ни лёд, ни оглушение; голем не оглушается
            if (e.type.walker) { e.slow = 0; e.frozen = 0; e.stun = 0; }
            else if (e.type.golem) e.stun = 0;
            var to = TargetOf(e.pos) - e.pos;
            float dist = to.magnitude;
            if (dist > 30 && !e.type.boss && !e.ring) { e.pos = RingPoint(Random.value * Mathf.PI * 2); continue; }

            Vector2 dir;
            float speed = e.type.speed * (e.rage ? 1.3f : 1f) * CurDiff.speed;
            if (e.ring && ringOn)
            {
                var slot = ringCenter + e.ringDir * ringR;
                var d = slot - e.pos;
                dir = d.sqrMagnitude > 0.01f ? d.normalized : Vector2.zero;
                speed = Mathf.Min(d.magnitude / Mathf.Max(dt, 1e-3f), 3f);
            }
            else dir = dist > 0.001f ? to / dist : Vector2.zero;
            if (e.type.archer && !e.ring) dir = ArcherMove(e, dir, dist, dt);
            else if (e.type.summoner && !e.ring && !Guest) dir = SummonerMove(e, dir, dist, dt);
            else if (e.type.bomber && !Guest && BomberTick(e, dist)) continue;
            else if (e.type.boss && !e.type.fallen) BossTick(e, dt);

            if (e.slow > 0) { e.slow -= dt; speed *= 0.5f; if (e.slow <= 0) Visual(e); }
            if (e.frozen > 0)
            {
                e.frozen -= dt;
                if (e.frozen <= 0) { e.anim.speed = 1; Visual(e); }
            }
            else if (e.stun > 0)
            {
                e.stun -= dt;
                if (Random.value < 0.1f) Vfx.Emit(Vfx.Spark, World.W(e.pos, 1.2f * e.type.scale), Random.onUnitSphere * 2, 0.15f, 0.2f, new Color(0.7f, 0.85f, 1f));
            }
            else if (e.spawnLock > 0) e.spawnLock -= dt;
            else e.pos += dir * speed * dt + Vector2.ClampMagnitude(e.sep, 0.12f);
            // скелеты упираются в надгробия и ограды и путаются в них; боссы проламываются
            if (!e.type.boss) e.pos = World.Collide(e.pos, e.radius * 0.8f);

            if (e.spawnLock <= 0 && e.stun <= 0 && e.frozen <= 0 && time >= shieldUntil && time >= graceUntil)
            {
                bool bit = false;
                // кусают любого мага, кто вплотную; в прыжке маг неуязвим
                var bitten = All;
                for (int k = 0; k < bitten.Count && e.alive; k++)
                {
                    var m = bitten[k];
                    if (!Alive(m) || (m == me ? jumpT > 0 : m.air > 0.05f)) continue;
                    float rr = e.radius + Player.Radius;
                    if ((m.player.pos - e.pos).sqrMagnitude >= rr * rr) continue;
                    bit = true;
                    Use(m);
                    if (player.cloak && e.slow <= 0 && !e.type.boss) Slow(e, 1.5f);
                    // потолки: моб — не больше 25% здоровья мага в секунду, все касания вместе — 50%/с.
                    // С полного здоровья толпа убивает не быстрее чем за 2 с — есть время прыгнуть или вырваться
                    float taken = Mathf.Min(e.dmg, player.maxHp * 0.25f) * dt * Mathf.Max(0.2f, 1 - player.armor);
                    float used = biteUsed.GetValueOrDefault(m);
                    taken = Mathf.Min(taken, Mathf.Max(0, player.maxHp * 0.5f * dt - used));
                    biteUsed[m] = used + taken;
                    player.hp -= AbsorbShield(taken);
                    player.hurt = 0.15f;
                    if (m == me)
                    {
                        labTaken += taken;
                        lastHitBy = e.rage ? "ярость: " + e.type.name : e.type.name;
                        hurtFx = Mathf.Max(hurtFx, 0.6f);
                    }
                    if (player.thorns > 0)
                    {
                        e.hp -= taken * player.thorns;
                        if (e.hp <= 0) Kill(e);
                    }
                    Use(me);
                }
                if (bit)
                {
                    e.attackCd -= dt;
                    if (e.attackCd <= 0) { e.attackCd = 1.3f; e.atkAnim = 0.8f; e.anim.CrossFade("Attack", 0.05f); }
                }
            }

            // шаг в такт: замедленный идёт вяло, оглушённый почти замер; подъём из земли играет в своём темпе
            if (e.frozen <= 0)
            {
                float a = e.spawnLock > 0 ? 1 : e.animSpeed * (e.stun > 0 ? 0.15f : e.slow > 0 ? 0.55f : e.rage ? 1.25f : 1f);
                if (e.anim.speed != a) e.anim.speed = a;
                VatSpeed(e.rends, a);
            }
            if (e.atkAnim > 0) e.atkAnim -= dt;
            if (e.spawnLock > 0) VatState(e.rends, "Idle");
            else VatState(e.rends, e.atkAnim > 0 ? "Attack" : e.type.speed > 2.5f ? "Run" : "Walk");
            float punch = 0;
            if (e.flash > 0)
            {
                e.flash -= dt;
                Visual(e);
                // отдача от удара: на миг сплющивается; в конце вспышки размер возвращается точно
                punch = Mathf.Clamp01(e.flash / 0.12f);
                e.tr.localScale = e.flash > 0 ? Vector3.Scale(e.scale0, new Vector3(1 + 0.12f * punch, 1 - 0.14f * punch, 1 + 0.12f * punch)) : e.scale0;
            }
            e.tr.position = World.W(e.pos);
            // кости считаем только у тех, кто в кадре или чуть за краем — остальные стоят «на паузе»
            var vp = cam.WorldToViewportPoint(e.tr.position);
            bool vis = vp.z > 0 && vp.x > -0.15f && vp.x < 1.15f && vp.y > -0.25f && vp.y < 1.2f;
            if (e.anim.enabled != vis) e.anim.enabled = vis;
            var look = World.W(to);
            // поворот — только вокруг вертикали (наклон отдачи накладывается поверх и не копится)
            var yaw = Quaternion.Euler(0, e.tr.eulerAngles.y, 0);
            if (look.sqrMagnitude > 0.01f && e.spawnLock <= 0 && e.stun <= 0 && e.frozen <= 0)
                yaw = Quaternion.Slerp(yaw, Quaternion.LookRotation(look), dt * 8);
            if (punch > 0 && e.lastHit.sqrMagnitude > 0.01f && !e.type.boss)
                yaw = Quaternion.AngleAxis(22 * punch, Vector3.Cross(Vector3.up, World.W(e.lastHit).normalized)) * yaw;
            e.tr.rotation = yaw;
        }
    }

    // Вампиризм, лечение за убийство и жатва душ лечат из общего запаса, который копится со временем:
    // 2.5 + 2% максимума здоровья в секунду, не больше полутора секунд впрок. Без потолка поздний урон в тысячи
    // в секунду лечил на сотни — можно было встать посреди толпы и ничего не делать.
    // С 16.11 потолок растёт с вампиризмом (+20% вампиризма — ещё +4% максимума в секунду): с прежним потолком
    // игроки не замечали вампиризм вовсе («он судя по всему не работает»). Лечение видно зелёными «+N» над магом.
    public void Drain(float heal)
    {
        var p = player;
        float rate = 2.5f + p.maxHp * (0.02f + p.vamp * 0.2f);
        p.drainPool = Mathf.Min(rate * 1.5f, p.drainPool + Mathf.Max(0, time - p.drainAt) * rate);
        p.drainAt = time;
        float h = Mathf.Min(heal, p.drainPool);
        if (h <= 0) return;
        p.drainPool -= h;
        float real = Mathf.Min(h, p.maxHp - p.hp);
        if (p == me?.player) { labDrain += real; healShown += real; }
        p.hp = Mathf.Min(p.maxHp, p.hp + h);
    }

    // сколько вылечил вампиризм с прошлой зелёной цифры: копим и показываем раз в 0,7 с, а не на каждое попадание
    float healShown, healShowAt;

    void HealNumbers()
    {
        if (time < healShowAt) return;
        healShowAt = time + 0.7f;
        if (healShown >= 1 && optNumbers && numbers.Count < 60)
            numbers.Add(new DamageNumber { pos = World.W(player.pos, 2.4f), text = "+" + Mathf.RoundToInt(healShown), color = new Color(0.45f, 1f, 0.5f) });
        healShown = 0;
    }

    public void Hit(Enemy e, float dmg, Vector2 knockDir, float knock = 0.15f)
    {
        if (!e.alive) return;
        bool crit = Random.value < player.crit;
        float d = dmg * (player.dmgMul + NecroBonus) * SchoolMul(curElement) * HeroDmgMul(curElement) * (crit ? player.critMul : 1) * (time < elixirUntil ? 2 : 1);
        // у гостя урон только для глаз: настоящий считает хозяин
        if (Guest) { GhostHit(e, d, crit); return; }
        e.hp -= d;
        TrackDamage(curElement, d);
        e.flash = 0.12f;
        e.lastHit = knockDir;
        e.shownUntil = time + 2.5f;
        if (crit && (e.type.elite || e.type.boss || e.rage)) HitStop(0.05f);
        Visual(e);
        if (!e.type.boss && !e.type.golem) e.pos += knockDir * knock / Mathf.Max(1, e.type.scale * 0.6f);
        if (player.vamp > 0)
        {
            Drain(d * player.vamp);
            if (Random.value < 0.15f)
                Vfx.Emit(Vfx.Ember, World.W(e.pos, 1f), (World.W(player.pos, 1.2f) - World.W(e.pos, 1f)) * 2.5f, 0.35f, 0.4f, new Color(1f, 0.15f, 0.2f));
        }
        if (crit) Vfx.Burst(Vfx.Spark, World.W(e.pos, 1f), 8, 7, 0.3f, 0.3f, new Color(1f, 0.9f, 0.4f));
        if (numbers.Count < 60 && optNumbers && cur == me)
            numbers.Add(new DamageNumber
            {
                pos = World.W(e.pos, 1.2f * e.type.scale),
                text = Mathf.CeilToInt(d) + (crit ? "!" : ""),
                color = crit ? new Color(1f, 0.85f, 0.2f) : e.tier >= 2 ? e.tint : Color.white,
                crit = crit,
            });
        if (crit) HeroOnCrit(e);
        if (e.hp <= 0) Kill(e);
    }

    void GhostHit(Enemy e, float d, bool crit)
    {
        e.flash = 0.12f;
        e.shownUntil = time + 2.5f;
        Visual(e);
        if (crit) Vfx.Burst(Vfx.Spark, World.W(e.pos, 1f), 8, 7, 0.3f, 0.3f, new Color(1f, 0.9f, 0.4f));
        if (numbers.Count < 60 && optNumbers && cur == me)
            numbers.Add(new DamageNumber
            {
                pos = World.W(e.pos, 1.2f * e.type.scale),
                text = Mathf.CeilToInt(d) + (crit ? "!" : ""),
                color = crit ? new Color(1f, 0.85f, 0.2f) : e.tier >= 2 ? e.tint : Color.white,
                crit = crit,
            });
    }

    void KillFx(Enemy e)
    {
        var p = World.W(e.pos, 0.8f);
        Vfx.Burst(Vfx.Bone, p, e.type.boss ? 40 : 10, 4, 0.1f * e.type.scale, 0.8f, new Color(0.9f, 0.88f, 0.8f), 0.8f, 0.3f);
        Vfx.Burst(Vfx.Smoke, p, 3, 1, 0.8f * e.type.scale, 0.8f, new Color(0.3f, 0.3f, 0.35f, 0.5f));
        var soul = e.tier > 0 ? e.tint : new Color(0.5f, 0.8f, 1f);
        Vfx.Emit(Vfx.Ember, p, Vector3.up * 2.5f, 0.6f, 0.9f, soul);
        Vfx.Emit(Vfx.Glow, p, Vector3.zero, 1.2f * e.type.scale, 0.25f, soul);
        if (e.type.boss) Boom(e.pos, 3, new Color(0.6f, 0.8f, 1f), false);
    }

    void Kill(Enemy e)
    {
        if (!e.alive) return;
        e.alive = false;
        kills++;
        NoteKill(e);
        HeroOnKill(e);
        if (player.killHeal > 0) Drain(player.killHeal);
        if (e.type.bomber && !Guest) BomberBoom(e.pos, false);   // убитый бомбер рвётся среди своих (GameThreats.cs)
        var p = World.W(e.pos, 0.8f);
        KillFx(e);
        if (e.type.fallen) FallenSlain(e);

        // Статика: заряженный враг разряжается цепью
        if (e.charged && skills[1] is Lightning li)
        {
            var t = Nearest(e.pos, 4f);
            if (t != null) li.Chain(this, World.W(e.pos, 1f), t, 3, Lightning.Dmg(li.level) * 0.6f, 3.5f);
        }
        // Ледяная гробница: враг, погибший в буране, раскалывается
        foreach (var b in pools)
        {
            if (!b.tomb || (e.pos - b.pos).sqrMagnitude > b.r * b.r) continue;
            Vfx.Burst(Vfx.Spark, p, 14, 6, 0.3f, 0.4f, IceLance.Ice);
            Vfx.Burst(Vfx.Bone, p, 10, 4, 0.12f, 0.6f, new Color(0.7f, 0.9f, 1f));
            Vfx.Emit(Vfx.Ring, World.W(e.pos, 0.15f), Vector3.zero, 3.5f, 0.3f, IceLance.Ice);
            var near = NearList(e.pos, 1.7f);
            foreach (var o in near) if (o != e) { Hit(o, Blizzard.Dmg(skills[6].level) * 3, (o.pos - e.pos).normalized, 0.4f); Freeze(o, 0.8f); }
            FreeList(near);
            break;
        }
        // Пепел: сгоревший в ауре взрывается
        if (ashRadius > 0 && (e.pos - player.pos).sqrMagnitude < ashRadius * ashRadius)
        {
            Vfx.Burst(Vfx.Fire, p, 10, 3, 0.8f, 0.4f, FireColor);
            var near = NearList(e.pos, 1.6f);
            foreach (var o in near) if (o != e) Hit(o, 15 + e.maxHp * 0.15f, (o.pos - e.pos).normalized, 0.3f);
            FreeList(near);
        }

        if (e.type.boss)
        {
            // рейд-босс: гигантский кристалл на пару уровней и россыпь вокруг
            DropGem(e.pos, Mathf.Max(e.type.xp, xpNext), false);
            for (int i = 0; i < 16; i++) DropGem(e.pos + Random.insideUnitCircle.normalized * Random.Range(1.2f, 3f), e.type.xp / 16, false);
            SpawnChest(World.Free(e.pos, 1.3f), 2);
            Shake(0.6f);
            WorldBanner($"{e.type.name} повержен!", "Легендарный сундук!");
        }
        else
        {
            DropGem(e.pos, e.rage ? Mathf.Max(8, xpNext * 0.05f) : e.type.xp * (1 + e.tier * 0.5f) * (1 + e.merged), false);
            if (e.rage) { Vfx.Burst(Vfx.Fire, p, 14, 4, 0.6f, 0.5f, RageColor); Vfx.Emit(Vfx.Ring, World.W(e.pos, 0.15f), Vector3.zero, 3, 0.35f, RageColor); }
            if (e.type.elite)
            {
                SpawnChest(World.Free(e.pos, 1.3f), Random.value < 0.3f ? 1 : 0);
                // магнит — после шага: Kill бывает внутри обхода реликвий (череп гнева), новая реликвия сломала бы обход
                if (Random.value < 0.3f) magnetDrops.Add(World.Free(e.pos + Random.insideUnitCircle.normalized * 1.5f, 0.9f));
            }
            else if (Random.value < 0.012f) DropGem(e.pos + Vector2.right * 0.4f, 0, true);
        }
    }

    // Kill бьёт соседей (гробница, пепел) и сам зовётся изнутри обходов tmp2 (сплеш, метеор, лужи): соседи, умирая,
    // переписывали tmp2 посреди внешнего foreach — «Collection was modified», и весь шаг кадра обрывался (лаборатория 17.11).
    // Поэтому у каждой смерти свой список из пула
    readonly Stack<List<Enemy>> killNearPool = new();
    List<Enemy> NearList(Vector2 c, float r) { var l = killNearPool.Count > 0 ? killNearPool.Pop() : new List<Enemy>(); Query(c, r, l); return l; }
    void FreeList(List<Enemy> l) { l.Clear(); killNearPool.Push(l); }

    void UpdateDying(float dt)
    {
        for (int i = dying.Count - 1; i >= 0; i--)
        {
            var e = dying[i];
            e.dying -= dt;
            if (e.fly != Vector3.zero)
            {
                // баллистика трупа: летит, кувыркается, падает на землю
                var p = e.tr.position + e.fly * dt;
                e.fly.y -= 20 * dt;
                e.tr.Rotate(e.spinAxis, 520 * dt, Space.World);
                if (p.y <= 0) { p.y = 0; e.fly = Vector3.zero; Vfx.Burst(Vfx.Smoke, p, 2, 1, 0.6f, 0.5f, new Color(0.2f, 0.18f, 0.16f, 0.5f)); }
                e.tr.position = p;
            }
            else if (e.dying < 0.6f) e.tr.position += Vector3.down * dt * 1.2f;
            if (e.dying > 0) continue;
            e.go.SetActive(false);
            enemyPool[PoolKey(e.type)].Push(e);
            dying.RemoveAt(i);
        }
    }

    // ---------- Удары по площади ----------

    public void Boom(Vector2 at, float r, Color c, bool scorch)
    {
        var p = World.W(at, 0.4f);
        Vfx.Burst(Vfx.Fire, p, 26, 4.5f * r / 2, 1.3f * r / 2, 0.55f, c, 0.7f, 0.3f);
        Vfx.Burst(Vfx.Spark, p, 30, 10, 0.3f, 0.6f, Color.Lerp(c, Color.white, 0.4f), 0.8f);
        Vfx.Burst(Vfx.Smoke, p + Vector3.up * 0.5f, 8, 1.5f, 1.6f * r / 2, 1.4f, new Color(0.12f, 0.1f, 0.1f, 0.7f), 0.9f, 0.5f);
        Vfx.Emit(Vfx.Ring, World.W(at, 0.15f), Vector3.zero, r * 3, 0.45f, c);
        Vfx.Emit(Vfx.Glow, p, Vector3.zero, r * 3, 0.25f, c);
        Vfx.Flash(p + Vector3.up, c, 10, r * 4, 0.35f);
        if (scorch) Vfx.Decal(at, r * 2, new Color(0.08f, 0.05f, 0.04f, 0.85f), 4, false);
        Shake(0.25f);
    }

    // Молния с неба: столб света, ударная волна, урон по площади
    public void Strike(Vector2 at, float dmg, float r, Color c)
    {
        var ground = World.W(at, 0.1f);
        Vfx.Bolt(ground + new Vector3(Random.Range(-1f, 1f), 14, Random.Range(-1f, 1f)), ground, c, 0.25f);
        Vfx.Burst(Vfx.Spark, ground, 18, 8, 0.3f, 0.4f, c, 0.9f);
        Vfx.Emit(Vfx.Ring, World.W(at, 0.15f), Vector3.zero, r * 2.5f, 0.35f, c);
        Vfx.Emit(Vfx.Glow, ground + Vector3.up * 0.5f, Vector3.zero, r * 2.5f, 0.2f, c);
        Vfx.Flash(ground + Vector3.up * 2, c, 8, r * 4, 0.25f);
        Vfx.Decal(at, r * 1.5f, new Color(0.05f, 0.05f, 0.08f, 0.7f), 2.5f, false);
        Query(at, r, tmp2);
        foreach (var e in tmp2) Hit(e, dmg, (e.pos - at).normalized, 0.3f);
    }

    public void DropMeteor(Vector2 at, float r, float dmg, float fall, bool stun, bool lava, bool ice)
    {
        Vfx.Decal(at, r * 0.5f, ice ? new Color(0.3f, 0.6f, 1.4f, 0.9f) : new Color(1f, 0.15f, 0.05f, 0.9f), fall, true, r * 2.2f);
        Transform rock;
        if (rockPool.Count > 0) { rock = rockPool.Pop(); rock.gameObject.SetActive(true); }
        else
        {
            var go = World.Primitive(PrimitiveType.Sphere, World.Glow, 1f);
            Tint(go.GetComponent<Renderer>(), new Color(5f, 1.6f, 0.4f));
            var l = new GameObject("MeteorLight").AddComponent<Light>();
            l.transform.SetParent(go.transform, false);
            l.color = new Color(1f, 0.45f, 0.15f); l.range = 6; l.intensity = 4; l.shadows = LightShadows.None;
            rock = go.transform;
        }
        rock.localScale = Vector3.one * (0.4f + r * 0.18f);
        Tint(rock.GetComponent<Renderer>(), ice ? new Color(1.5f, 3.5f, 6f) : new Color(5f, 1.6f, 0.4f));
        meteors.Add((at, r, dmg, fall, fall, stun, lava, ice, curElement, rock, cur));
    }

    void UpdateMeteors(float dt)
    {
        for (int i = meteors.Count - 1; i >= 0; i--)
        {
            var m = meteors[i];
            m.t -= dt;
            float k = 1 - Mathf.Clamp01(m.t / m.t0);
            var target = World.W(m.pos);
            var p = Vector3.Lerp(target + new Vector3(-4, 16, -3), target, k * k);
            m.rock.position = p;
            Vfx.Emit(Vfx.Fire, p, Random.insideUnitSphere, Random.Range(0.8f, 1.4f) * (0.6f + m.r * 0.2f), 0.4f, m.ice ? new Color(0.4f, 0.75f, 1f) : new Color(1f, 0.45f, 0.1f));
            Vfx.Emit(Vfx.Smoke, p, Vector3.up * 0.3f, 0.9f, 0.8f, new Color(0.1f, 0.08f, 0.07f, 0.6f));
            meteors[i] = m;
            if (m.t > 0) continue;
            m.rock.gameObject.SetActive(false);
            rockPool.Push(m.rock);
            meteors.RemoveAt(i);
            Use(m.owner ?? me);
            Boom(m.pos, m.r, m.ice ? new Color(0.45f, 0.8f, 1f) : new Color(1f, 0.45f, 0.12f), !m.ice);
            Query(m.pos, m.r, tmp2);
            curElement = m.el;
            foreach (var e in tmp2)
            {
                Hit(e, m.dmg, (e.pos - m.pos).normalized, 0.8f);
                if (m.ice) Freeze(e, 2f);
                else if (m.stun) e.stun = Mathf.Max(e.stun, 1.5f);
            }
            curElement = Element.None;
            if (m.lava) Burn(m.pos, m.r * 0.9f, m.dmg * 0.25f, 4f);
            Use(me);
        }
    }

    public void Burn(Vector2 at, float r, float dps, float life)
    {
        var d = Vfx.Decal(at, r * 2.2f, new Color(1.6f, 0.45f, 0.08f, 0.7f), life, true);
        pools.Add(new BurnPool { pos = at, r = r, dps = dps, t = life, decal = d, element = Element.Fire, owner = cur });
    }

    // Ледяная зона бурана: снег, замедление, урон тиками
    public BurnPool Frost(Vector2 at, float r, float dps, float life, bool follow, bool tomb)
    {
        var d = Vfx.Decal(at, r * 2.2f, new Color(0.5f, 0.8f, 1.4f, 0.55f), Mathf.Min(life, 9999), true);
        var b = new BurnPool { pos = at, r = r, dps = dps, t = life, decal = d, ice = true, follow = follow, tomb = tomb, element = Element.Ice, owner = cur };
        // два встречных вихря на земле и венец ледяных шипов по краю
        b.swirl = Vfx.Decal(at, r * 2.3f, new Color(0.45f, 0.75f, 1.3f, 0.7f), Mathf.Min(life, 9999), true, -1, "twirl_02");
        b.swirl2 = Vfx.Decal(at, r * 1.5f, new Color(0.7f, 0.9f, 1.4f, 0.5f), Mathf.Min(life, 9999), true, -1, "twirl_01");
        int n = Mathf.Clamp(Mathf.RoundToInt(r * 4), 8, 16);
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2 / n + Random.Range(-0.15f, 0.15f);
            var sp = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * Random.Range(0.85f, 1.05f);
            Vfx.Spike(World.W(sp), Random.Range(0.7f, 1.3f), IceLance.Ice, follow ? 0.9f : Mathf.Min(life, 1.6f), true);
        }
        Vfx.Emit(Vfx.Ring, World.W(at, 0.1f), Vector3.zero, r * 2.6f, 0.5f, new Color(0.6f, 0.85f, 1.2f));
        Vfx.Burst(Vfx.Spark, World.W(at, 0.6f), 24, 7, 0.25f, 0.45f, new Color(0.8f, 0.95f, 1.2f), 0.6f, 1f);
        Vfx.Flash(World.W(at, 1.5f), new Color(0.5f, 0.75f, 1f), 6, r * 2.5f, 0.5f);
        pools.Add(b);
        return b;
    }

    public void Freeze(Enemy e, float t)
    {
        // у гостя заморозку и замедление присылает хозяин
        if (Guest || e.type.walker) return;
        if (e.type.boss) t *= 0.3f;
        e.frozen = Mathf.Max(e.frozen, t);
        e.anim.speed = 0;
        Visual(e);
        if (Random.value < 0.5f) Vfx.Burst(Vfx.Spark, World.W(e.pos, 1f), 6, 3, 0.2f, 0.3f, IceLance.Ice);
    }

    public void Slow(Enemy e, float t)
    {
        if (Guest || e.type.walker) return;
        bool was = e.slow > 0;
        e.slow = Mathf.Max(e.slow, t);
        if (!was) Visual(e);
    }

    void UpdatePools(float dt)
    {
        for (int i = pools.Count - 1; i >= 0; i--)
        {
            var b = pools[i];
            Use(b.owner ?? me);
            b.t -= dt;
            // маг ушёл из похода — его буран, который ходит за ним, гаснет (иначе бил бы на месте до конца забега)
            if (b.follow && b.owner != null && b.owner.gone)
            {
                foreach (var d in new[] { b.decal, b.swirl, b.swirl2 }) if (d != null) d.transform.localScale = Vector3.zero;
                pools.RemoveAt(i);
                continue;
            }
            if (b.follow)
            {
                b.pos = player.pos;
                b.t = 9999;
                if (b.decal != null) { b.decal.transform.position = World.W(b.pos, 0.04f); b.decal.transform.localScale = Vector3.one * b.r * 2.2f; }
                if (b.swirl != null) { b.swirl.transform.position = World.W(b.pos, 0.05f); b.swirl.transform.localScale = Vector3.one * b.r * 2.3f; }
                if (b.swirl2 != null) { b.swirl2.transform.position = World.W(b.pos, 0.06f); b.swirl2.transform.localScale = Vector3.one * b.r * 1.5f; }
            }
            if (b.t <= 0) { pools.RemoveAt(i); continue; }
            if (b.ice)
            {
                // вихри на земле крутятся навстречу друг другу
                if (b.swirl != null) b.swirl.transform.rotation = Quaternion.Euler(90, -time * 140, 0);
                if (b.swirl2 != null) b.swirl2.transform.rotation = Quaternion.Euler(90, time * 230, 0);
                // снежная воронка: хлопья идут по спирали внутрь и поднимаются столбом в центре
                float spin = time * 3f;
                for (int k = 0; k < 4; k++)
                {
                    float a = spin + k * Mathf.PI * 0.5f + Random.Range(-0.4f, 0.4f), rr = b.r * Random.Range(0.4f, 1f);
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var sp = b.pos + dir * rr;
                    var vel = new Vector3(-dir.y * 7f - dir.x * 2.5f, Random.Range(0.4f, 1.6f), dir.x * 7f - dir.y * 2.5f);
                    Vfx.Emit(Vfx.Spark, World.W(sp, Random.Range(0.2f, 1.2f)), vel, 0.09f, 0.45f, new Color(0.85f, 0.95f, 1.2f));
                }
                for (int k = 0; k < 2; k++)
                {
                    var p = b.pos + Random.insideUnitCircle * b.r;
                    Vfx.Emit(Vfx.Ember, World.W(p, Random.Range(2f, 4f)), new Vector3(Random.Range(-2f, 2f), -3f, Random.Range(-2f, 2f)), Random.Range(0.1f, 0.2f), 1f, new Color(0.85f, 0.95f, 1f));
                }
                if (Random.value < 0.35f)
                {
                    var c0 = b.pos + Random.insideUnitCircle * b.r * 0.3f;
                    Vfx.Emit(Vfx.Smoke, World.W(c0, 0.3f), new Vector3(0, Random.Range(1.5f, 2.5f), 0), b.r * 0.6f, 1.1f, new Color(0.7f, 0.85f, 1f, 0.16f));
                }
            }
            else if (Random.value < 0.6f)
            {
                var p = b.pos + Random.insideUnitCircle * b.r;
                Vfx.Emit(Vfx.Fire, World.W(p, 0.1f), Vector3.up * Random.Range(0.8f, 1.6f), Random.Range(0.4f, 0.8f), 0.5f, new Color(1f, 0.4f, 0.1f));
            }
            b.tick -= dt;
            if (b.tick > 0 || (b.owner != null && b.owner.down)) continue;
            b.tick = 0.5f;
            if (b.ice)
            {
                // каждый тик — морозная волна и пара ледяных игл из-под земли
                Vfx.Emit(Vfx.Ring, World.W(b.pos, 0.08f), Vector3.zero, b.r * 2.2f, 0.4f, new Color(0.4f, 0.65f, 1f, 0.6f));
                for (int k = 0; k < 3; k++)
                    Vfx.Spike(World.W(b.pos + Random.insideUnitCircle * b.r * 0.9f), Random.Range(0.5f, 0.9f), IceLance.Ice, 0.6f, true);
            }
            Query(b.pos, b.r, tmp2);
            curElement = b.element;
            foreach (var e in tmp2)
            {
                Hit(e, b.dps * 0.5f, Vector2.zero, 0);
                if (b.ice)
                {
                    Slow(e, 1f);
                    if (Random.value < 0.3f) Vfx.Burst(Vfx.Spark, World.W(e.pos, 0.9f), 4, 3, 0.15f, 0.3f, IceLance.Ice);
                }
            }
            curElement = Element.None;
        }
        Use(me);
    }

    // ---------- Сундуки ----------

    Chest SpawnChest(Vector2 at, int rarity)
    {
        if (chests.Count >= 4) return null;
        nextChestId = (nextChestId + 1) & 0xFF;
        var c = new Chest { pos = at, rarity = rarity, alive = true, id = nextChestId };
        c.go = World.Prop(rarity == 2 ? "Dungeon/chest_gold" : "Dungeon/chest", World.Dungeon);
        c.tr = c.go.transform;
        c.tr.localScale *= rarity == 2 ? 1.4f : 1.1f;
        c.tr.SetPositionAndRotation(World.W(at), Quaternion.Euler(0, Random.Range(-30f, 30f) + 180, 0) * c.tr.rotation);
        var col = RarityColors[rarity];
        c.light = new GameObject("ChestLight").AddComponent<Light>();
        c.light.transform.position = World.W(at, 1.2f);
        c.light.color = col; c.light.range = 5; c.light.intensity = 4; c.light.shadows = LightShadows.None;
        c.beam = MakeLine("ChestBeam", 2, 0.5f, new Color(col.r * 2, col.g * 2, col.b * 2));
        c.beam.gameObject.SetActive(true);
        c.beam.SetPosition(0, World.W(at, 0));
        c.beam.SetPosition(1, World.W(at, 9));
        c.beam.startColor = new Color(1, 1, 1, 0.9f);
        c.beam.endColor = new Color(1, 1, 1, 0);
        chests.Add(c);
        Vfx.Burst(Vfx.Ember, World.W(at, 0.5f), 20, 3, 0.25f, 1f, col, 1f, 0.5f);
        return c;
    }

    void UpdateChests(float dt)
    {
        for (int i = chests.Count - 1; i >= 0; i--)
        {
            var c = chests[i];
            var col = RarityColors[c.rarity];
            c.beam.widthMultiplier = 0.4f + 0.15f * Mathf.Sin(time * 4);
            c.light.intensity = 3.5f + Mathf.Sin(time * 5);
            if (Random.value < 0.3f)
                Vfx.Emit(Vfx.Ember, World.W(c.pos + Random.insideUnitCircle * 0.6f, 0.2f), Vector3.up * Random.Range(1f, 2.5f), 0.18f, 1.2f, col);
            if (Guest) continue;
            var m = Toucher(c.pos, 1.4f);
            if (m == null) continue;
            if (m == me) { if (coopRun) { ChestFx(c); pendingChests.Add(c.rarity); } else OpenChest(c); }
            else
            {
                // сундук открыл друг: выбирает он у себя
                ChestFx(c);
                var w = RunMsg(0x16); w.Write((byte)3); w.Write((byte)m.slot); w.Write((byte)c.rarity); Flush();
            }
            chests.RemoveAt(i);
        }
    }

    void OpenChest(Chest c)
    {
        ChestFx(c);
        ChestChoices(c.rarity);
    }

    void ChestFx(Chest c)
    {
        var col = RarityColors[c.rarity];
        var p = World.W(c.pos, 0.6f);
        Vfx.Burst(Vfx.Spark, p, 40, 9, 0.3f, 0.7f, col, 1f);
        Vfx.Burst(Vfx.Ember, p, 40, 5, 0.3f, 1.4f, col, 1f, 0.4f);
        Vfx.Emit(Vfx.Ring, World.W(c.pos, 0.15f), Vector3.zero, 6, 0.5f, col);
        Vfx.Emit(Vfx.Glow, p, Vector3.zero, 5, 0.4f, col);
        Vfx.Flash(p + Vector3.up, col, 10, 8, 0.5f);
        Destroy(c.go); Destroy(c.light.gameObject); Destroy(c.beam.gameObject);
        Shake(0.2f);
    }

    void ChestChoices(int rarity)
    {
        chestRarity = rarity;
        chestChoices.Clear();
        var pool = artifactPool.FindAll(a => !(a.unique && artifacts.Contains(a)));
        for (int i = 0; i < rarity + 1 && pool.Count > 0; i++)
        {
            int k = Random.Range(0, pool.Count);
            chestChoices.Add(pool[k]);
            pool.RemoveAt(k);
        }
        if (chestChoices.Count > 0) { state = State.Chest; menuT = Time.unscaledTime; }
    }

    float graceUntil;

    // после выбора карты — подарок: волна отбрасывает толпу и секунда без урона, чтобы не умирать сразу из меню
    void PickWave(Color c)
    {
        const float r = 6f;
        Query(player.pos, r, tmp);
        foreach (var e in tmp)
        {
            if (e.type.boss || e.type.golem) continue;
            var d = e.pos - player.pos;
            float k = 1 - d.magnitude / r;
            e.pos += (d.sqrMagnitude > 0.001f ? d.normalized : Random.insideUnitCircle.normalized) * (1.5f + 2.5f * k) / Mathf.Max(1, e.type.scale * 0.5f);
            Slow(e, 1f);
        }
        graceUntil = time + 1f;
        Vfx.Emit(Vfx.Ring, World.W(player.pos, 0.1f), Vector3.zero, r * 2.2f, 0.45f, c);
        Vfx.Burst(Vfx.Ember, World.W(player.pos, 0.5f), 20, 6, 0.18f, 0.5f, c, 0.5f, 1f);
        CoopSendWave(c);
    }

    void PickArtifact(int i)
    {
        if (i >= chestChoices.Count) return;
        var a = chestChoices[i];
        TrackPick("chest", a.title, chestChoices.ConvertAll(c => c.title));
        CoopAct(4, artifactPool.IndexOf(a));
        a.apply(this);
        artifacts.Add(a);
        state = State.Play;
        PickWave(a.color);
        stickOn = false;
        Banner(a.title, a.desc);
        if (chestRarity == 2) pendingLevels++;
        NextChoice();
    }

    // ---------- Пространственная сетка ----------

    const float Cell = 1.5f;
    readonly Dictionary<int, List<Enemy>> grid = new();
    readonly Stack<List<Enemy>> listPool = new();
    static int Key(int x, int y) => (x & 0xFFFF) | (y << 16);

    void BuildGrid()
    {
        foreach (var l in grid.Values) { l.Clear(); listPool.Push(l); }
        grid.Clear();
        foreach (var e in enemies)
        {
            if (!e.alive) continue;
            int k = Key(Mathf.FloorToInt(e.pos.x / Cell), Mathf.FloorToInt(e.pos.y / Cell));
            if (!grid.TryGetValue(k, out var l))
            {
                l = listPool.Count > 0 ? listPool.Pop() : new List<Enemy>();
                grid[k] = l;
            }
            l.Add(e);
        }
    }

    public void Query(Vector2 c, float r, List<Enemy> res)
    {
        res.Clear();
        float reach = r + 2f;
        int x0 = Mathf.FloorToInt((c.x - reach) / Cell), x1 = Mathf.FloorToInt((c.x + reach) / Cell);
        int y0 = Mathf.FloorToInt((c.y - reach) / Cell), y1 = Mathf.FloorToInt((c.y + reach) / Cell);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue(Key(x, y), out var l)) continue;
            foreach (var e in l)
            {
                float rr = r + e.radius;
                if (e.alive && (e.pos - c).sqrMagnitude <= rr * rr) res.Add(e);
            }
        }
    }

    public Enemy Nearest(Vector2 p, float maxDist, HashSet<Enemy> skip = null)
    {
        Enemy best = null;
        float bd = maxDist * maxDist;
        foreach (var e in enemies)
        {
            if (!e.alive || e.spawnLock > 0 || (skip != null && skip.Contains(e))) continue;
            float d = (e.pos - p).sqrMagnitude;
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    public Enemy RandomEnemy(Vector2 p, float r)
    {
        Query(p, r, tmp);
        return tmp.Count == 0 ? null : tmp[Random.Range(0, tmp.Count)];
    }

    // ---------- Снаряды ----------

    public Proj Shoot(Vector2 from, Vector2 vel, float dmg, int pierce, float splash, Color c)
    {
        var p = projPool.Count > 0 ? projPool.Pop() : null;
        if (p == null)
        {
            p = new Proj();
            p.mat = new Material(World.Additive) { mainTexture = Art.Tex("fire_01") };
            p.go = World.Primitive(PrimitiveType.Sphere, p.mat, 0.38f);
            p.rend = p.go.GetComponent<Renderer>();
            p.mf = p.go.GetComponent<MeshFilter>();
            p.baseMesh = p.mf.sharedMesh;
            p.light = new GameObject("ProjLight").AddComponent<Light>();
            p.light.transform.SetParent(p.go.transform, false);
            p.light.range = 4; p.light.intensity = 3; p.light.shadows = LightShadows.None;
            p.light.enabled = false;
            p.tr = p.go.transform;
        }
        p.go.SetActive(true);
        p.mf.sharedMesh = p.baseMesh;
        // в густом залпе снаряды тусклее: при высокой скорости чар свечение сливалось в сплошное пятно (фидбек 16.11)
        // HDR нет: ярче единицы буфер уже не покажет, а пересвет съедает цвет школы и текстуру — держим в пределах
        float glow = 0.95f * (1 - 0.6f * Crowd);
        // снаряд рисуется аддитивным спрайтом с текстурой школы: подмена материала давала ровный белый шар
        p.mat.SetColor("_Color", new Color(c.r * glow, c.g * glow, c.b * glow));
        p.mat.SetTexture("_MainTex", Art.Tex(curElement == Element.Ice ? "circle_05" : curElement == Element.Fire ? "fire_01" : curElement == Element.Meteor ? "magic_03" : "magic_01"));
        p.rend.sharedMaterial = p.mat;
        p.light.color = c;
        p.pos = from; p.vel = vel; p.dmg = dmg; p.pierce = pierce; p.splash = splash;
        p.life = 1.1f; p.radius = 0.35f; p.alive = true;
        p.bounce = 0; p.burn = false; p.zap = false; p.zapCd = 0; p.ice = false; p.freeze = false; p.split = false; p.big = false;
        p.element = curElement;
        p.owner = cur;
        p.hit.Clear();
        projs.Add(p);
        return p;
    }

    // густота залпа: 0 — до 25 снарядов, 1 — от 100. Снаряды мельче, тусклее и реже искрят — врагов снова видно
    float Crowd => Mathf.Clamp01((projs.Count - 25) / 75f);

    void UpdateProjs(float dt)
    {
        int count = projs.Count;
        float crowd = Crowd, size = 1 - 0.4f * crowd, fx = 1 - 0.75f * crowd;
        for (int pi = 0; pi < count; pi++)
        {
            var p = projs[pi];
            if (!p.alive) continue;
            Use(p.owner ?? me);
            p.pos += p.vel * dt;
            p.life -= dt;
            var w = World.W(p.pos, 1f);
            p.tr.position = w;
            var c = p.light.color;
            curElement = p.element;
            // у каждой школы свой снаряд: ледяная игла, электрический шар, огненный шар со спиралью искр
            var fwd = World.W(p.vel);
            if (p.ice)
            {
                // гранёная сосулька летит строго остриём вперёд
                if (p.mf.sharedMesh == p.baseMesh)
                {
                    p.mf.sharedMesh = Vfx.Icicles[pi % Vfx.Icicles.Length];
                    p.mat.SetColor("_Color", new Color(0.55f, 0.85f, 1f));
                    p.rend.sharedMaterial = p.mat;
                }
                p.tr.rotation = Quaternion.LookRotation(fwd) * Quaternion.Euler(90, 0, 0);
                p.tr.localScale = new Vector3(0.24f, 1.5f, 0.24f) * size;
                if (Random.value < 0.5f * fx) Vfx.Emit(Vfx.Smoke, w - fwd.normalized * 0.4f, -fwd * 0.03f, 0.3f, 0.25f, new Color(0.45f, 0.65f, 0.9f, 0.14f));
                if (Random.value < 0.6f * fx) Vfx.Emit(Vfx.Spark, w + Random.insideUnitSphere * 0.15f, -fwd * 0.15f, 0.12f, 0.25f, new Color(0.85f, 0.95f, 1f));
            }
            else if (p.element == Element.Electro)
            {
                p.tr.localScale = Vector3.one * (0.24f + 0.05f * Mathf.Sin(time * 50 + pi)) * size;
                if (Random.value < 0.3f * fx) Vfx.Bolt(w, w + Random.onUnitSphere * Random.Range(0.4f, 0.8f), c, 0.06f);
                if (Random.value < 0.5f * fx) Vfx.Emit(Vfx.Spark, w, Random.onUnitSphere * 2, 0.12f, 0.15f, Color.Lerp(c, Color.white, 0.4f));
            }
            else
            {
                p.tr.localScale = Vector3.one * (p.big ? p.radius * 2f : (0.3f + Mathf.Min(0.35f, p.dmg / 140f)) * size) * (1 + 0.16f * Mathf.Sin(time * 38 + pi));
                if (Random.value < fx) Vfx.Emit(Vfx.Fire, w, -fwd * 0.05f + Random.insideUnitSphere * 0.3f, Random.Range(0.45f, 0.75f) * size, 0.3f, c);
                var side = Vector3.Cross(fwd.normalized, Vector3.up);
                float a = time * 26 + pi;
                var off = side * Mathf.Sin(a) * 0.32f + Vector3.up * Mathf.Cos(a) * 0.32f;
                if (Random.value < fx)
                {
                    Vfx.Emit(Vfx.Ember, w + off, -fwd * 0.1f, 0.13f, 0.4f, Color.Lerp(c, Color.white, 0.25f));
                    Vfx.Emit(Vfx.Ember, w - off, -fwd * 0.1f, 0.13f, 0.4f, Color.Lerp(c, Color.white, 0.25f));
                }
            }
            if (p.zap)
            {
                p.zapCd -= dt;
                if (p.zapCd <= 0)
                {
                    p.zapCd = 0.25f;
                    var t = Nearest(p.pos, 3.5f);
                    if (t != null) { Vfx.Bolt(w, World.W(t.pos, 1f), new Color(0.6f, 0.85f, 1f), 0.15f); Hit(t, 12, Vector2.zero, 0); }
                }
            }
            if (p.life <= 0) { p.alive = false; continue; }
            Query(p.pos, p.radius, tmp);
            foreach (var e in tmp)
            {
                if (p.hit.Contains(e)) continue;
                p.hit.Add(e);
                Hit(e, p.dmg, p.vel.normalized, 0.3f);
                if (p.ice)
                {
                    // ледяная игла раскалывается на осколки
                    Vfx.Burst(Vfx.Bone, w, 7, 4, 0.1f, 0.5f, new Color(0.75f, 0.92f, 1f), 0.6f);
                    Vfx.Emit(Vfx.Ring, World.W(p.pos, 0.15f), Vector3.zero, 1.6f, 0.25f, c);
                }
                else Vfx.Burst(Vfx.Fire, w, 8, 2.5f, 0.6f, 0.35f, c);
                Vfx.Burst(Vfx.Spark, w, 10, 7, 0.22f, 0.35f, Color.Lerp(c, Color.white, 0.3f));
                Vfx.Flash(w, c, 5, 4, 0.15f);
                if (p.splash > 0)
                {
                    Query(p.pos, p.splash, tmp2);
                    foreach (var o in tmp2) if (o != e) Hit(o, p.dmg * 0.5f, (o.pos - p.pos).normalized, 0.3f);
                    Vfx.Emit(Vfx.Ring, World.W(p.pos, 0.15f), Vector3.zero, p.splash * 2.5f, 0.3f, c);
                    if (p.splash > 2) Boom(p.pos, p.splash * 0.6f, c, true);
                }
                if (p.burn) Burn(p.pos, 1.2f, p.dmg * 0.4f, 2.5f);
                if (p.ice)
                {
                    Slow(e, 1.5f);
                    if (p.freeze) Freeze(e, 1.2f);
                    if (p.split)
                    {
                        p.split = false;
                        for (int k = 0; k < 3; k++)
                        {
                            var sh = Shoot(p.pos, (Vector2)(Quaternion.Euler(0, 0, Random.Range(0, 360f)) * Vector2.right) * 11f, p.dmg * 0.5f, 1, 0, IceLance.Ice);
                            sh.ice = true; sh.element = Element.Ice; sh.life = 0.5f; sh.hit.Add(e);
                        }
                    }
                }
                if (--p.pierce < 0)
                {
                    // Феникс: рикошет в ближайшую новую цель
                    var next = p.bounce > 0 ? Nearest(p.pos, 7f, p.hit) : null;
                    if (next != null)
                    {
                        p.bounce--;
                        p.pierce = 0;
                        p.vel = (next.pos - p.pos).normalized * p.vel.magnitude;
                        p.life = 0.8f;
                    }
                    else { p.alive = false; break; }
                }
            }
        }
        Use(me);
    }

    // ---------- Опыт и сердечки ----------

    void DropGem(Vector2 pos, float value, bool heal)
    {
        if (!heal && gems.Count > 220)
        {
            gems[Random.Range(0, gems.Count)].value += value;
            return;
        }
        MakeGem(pos, value, heal);
    }

    Gem MakeGem(Vector2 pos, float value, bool heal)
    {
        var pool = heal ? healPool : gemPool;
        var g = pool.Count > 0 ? pool.Pop() : null;
        if (g == null)
        {
            g = new Gem { heal = heal };
            if (heal)
            {
                g.go = new GameObject("Heart");
                g.go.AddComponent<MeshFilter>().sharedMesh = World.Heart();
                var mr = g.go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = World.Glow;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                g.go.transform.localScale = Vector3.one * 0.5f;
                var l = new GameObject("HeartLight").AddComponent<Light>();
                l.transform.SetParent(g.go.transform, false);
                l.color = new Color(1f, 0.25f, 0.3f); l.range = 3; l.intensity = 3; l.shadows = LightShadows.None;
            }
            else g.go = World.Primitive(PrimitiveType.Cube, World.Glow, 0.2f);
            g.tr = g.go.transform;
            g.rend = g.go.GetComponent<Renderer>();
        }
        g.go.SetActive(true);
        g.pos = pos; g.value = value; g.pulled = false; g.alive = true; g.phase = Random.value * 6;
        g.tier = heal ? 0 : GemTier(value);
        Tint(g.rend, heal ? new Color(4f, 0.35f, 0.45f) : GemColors[g.tier]);
        if (!heal) g.tr.localScale = Vector3.one * GemSizes[g.tier];
        nextGemId = (nextGemId + 1) & 0xFFFF;
        g.id = nextGemId;
        g.netPos = pos;
        g.sentKnown = false;
        gems.Add(g);
        return g;
    }

    void UpdateGems(float dt)
    {
        foreach (var g in gems)
        {
            if (!g.alive) continue;
            // кристалл летит к ближайшему живому магу, в чей магнит попал
            var m = Nearest(g.pos);
            var mp = m.player;
            var d = mp.pos - g.pos;
            if (!g.pulled && d.sqrMagnitude < mp.magnet * mp.magnet && !m.down) g.pulled = true;
            if (g.pulled) g.pos += d.normalized * Mathf.Min(d.magnitude, 14 * dt);
            GemPose(g);
            if (d.sqrMagnitude >= 0.3f || m.down) continue;
            g.alive = false;
            if (g.heal)
            {
                mp.hp = Mathf.Min(mp.maxHp, mp.hp + 30);
                Vfx.Burst(Vfx.Ember, World.W(mp.pos, 1f), 16, 2.5f, 0.25f, 0.8f, new Color(1f, 0.3f, 0.35f), 1f, 0.4f);
                Vfx.Emit(Vfx.Ring, World.W(mp.pos, 0.15f), Vector3.zero, 3, 0.4f, new Color(1f, 0.3f, 0.35f));
                if (m == me) numbers.Add(new DamageNumber { pos = World.W(mp.pos, 2.2f), text = "+30", color = new Color(0.5f, 1f, 0.5f), crit = true });
            }
            else
            {
                Vfx.Emit(Vfx.Glow, World.W(mp.pos, 1f), Vector3.zero, 0.8f, 0.2f, new Color(0.4f, 0.7f, 1f));
                // опыт общий: кто подобрал, тот и множит своим бонусом
                AddXp(g.value * mp.xpMul);
            }
        }
    }

    // мягкое свечение вокруг каждого кристалла и сердца: видно в траве, в толпе и на низкой графике
    void GemHalos()
    {
        Vfx.HaloBegin();
        float t = Time.unscaledTime;
        foreach (var g in gems)
        {
            if (!g.alive) continue;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 3.2f + g.phase);
            if (g.heal) { Vfx.Halo(World.W(g.pos, 0.6f), 2.0f + 0.3f * pulse, new Color(1f, 0.2f, 0.25f, 0.5f)); continue; }
            var c = GemColors[g.tier];
            float k = 1f / Mathf.Max(c.r, c.g, c.b);
            Vfx.Halo(World.W(g.pos, 0.35f), GemSizes[g.tier] * 4.5f + 0.75f + 0.2f * pulse, new Color(c.r * k, c.g * k, c.b * k, 0.42f + 0.2f * pulse));
        }
        Vfx.HaloEnd();
    }

    void GemPose(Gem g)
    {
        if (g.heal)
        {
            g.tr.SetPositionAndRotation(World.W(g.pos, 0.6f + 0.15f * Mathf.Sin(time * 3 + g.phase)), Quaternion.Euler(0, time * 90, 0));
            float pulse = 1 + 0.12f * Mathf.Max(0, Mathf.Sin(time * 8));
            g.tr.localScale = Vector3.one * 0.5f * pulse;
        }
        else
            g.tr.SetPositionAndRotation(World.W(g.pos, 0.35f + 0.1f * Mathf.Sin(time * 4 + g.phase)), Quaternion.Euler(45, time * 120 + g.phase * 50, 45));
    }

    void AddXp(float v)
    {
        xp += v;
        while (xp >= xpNext)
        {
            xp -= xpNext;
            level++;
            pendingLevels++;
            if (coopRun) coopLevels++;
            // кривая круче: к 15-й минуте уровень 40–45, а не 60+
            xpNext = Mathf.Round(3 + level * 2f + level * level * 0.32f);
        }
    }

    // ---------- Выбор улучшений, суперсил ----------

    public const int MaxSpells = 6, MaxPassives = 6;
    int SpellsTaken { get { int n = 0; foreach (var s in skills) if (s.spell && !s.fusion && s.level > 0) n++; return n; } }
    int PassivesTaken { get { int n = 0; foreach (var s in skills) if (!s.spell && s.level > 0 && s.max <= 20) n++; return n; } }

    void OpenLevelUp()
    {
        pendingLevels--;
        choices.Clear();
        int n = 3 + player.extraChoice;
        // готовое слияние всегда в выборе
        foreach (var s in skills)
            if (s is Fusion f && f.Ready && HeroContent.Get(builtHero).Allows(f) && choices.Count < 1) choices.Add(s);
        // предел: шесть заклинаний и шесть пассивок; когда слоты заняты, новые не выпадают — только прокачка взятого.
        // Заклинания — из набора героя (Heroes.cs); запретные у остальных — после гримуара (level > 0)
        var hero = HeroContent.Get(builtHero);
        int spellsTaken = SpellsTaken, passivesTaken = PassivesTaken;
        var pool = skills.FindAll(s => !s.fusion && s.level < s.max && !choices.Contains(s)
            && (s.level > 0 || (hero.Allows(s) && (!s.forbidden || hero.spells != null && System.Array.IndexOf(hero.spells, s.GetType().Name) >= 0)
                && (s.spell ? spellsTaken < MaxSpells : passivesTaken < MaxPassives))));
        // новые заклинания чуть реже, чтобы успеть докачать до суперсилы
        while (choices.Count < n && pool.Count > 0)
        {
            int k = Random.Range(0, pool.Count);
            if (pool[k].level == 0 && pool[k].spell && Random.value < 0.35f && pool.Count > n) continue;
            choices.Add(pool[k]);
            pool.RemoveAt(k);
        }
        // всё прокачано — уровни не пропадают: бесконечные карты по чуть-чуть статов
        if (choices.Count < n)
        {
            var more = Endless().FindAll(x => !choices.Contains(x));
            while (choices.Count < n && more.Count > 0) { int k = Random.Range(0, more.Count); choices.Add(more[k]); more.RemoveAt(k); }
        }
        if (choices.Count == 0) { player.hp = player.maxHp; return; }
        state = State.LevelUp;
        menuT = Time.unscaledTime;
        if ((Application.absoluteURL ?? "").Contains("127.0.0.1")) Debug.Log($"levelup open t={Time.unscaledTime:0.00}");
        LevelFx(new Color(0.6f, 0.9f, 1f));
    }

    void LevelFx(Color c)
    {
        Vfx.Burst(Vfx.Ember, World.W(player.pos, 0.5f), 30, 3, 0.25f, 1f, c, 1f, 0.5f);
        Vfx.Emit(Vfx.Ring, World.W(player.pos, 0.15f), Vector3.zero, 5, 0.5f, c);
        pillar.gameObject.SetActive(true);
        pillar.SetPosition(0, World.W(player.pos, 0));
        pillar.SetPosition(1, World.W(player.pos, 12));
        pillar.sharedMaterial.SetColor("_Color", c * 3);
        pillarT = 0.7f;
    }

    void UpdatePillar(float dt)
    {
        if (pillarT <= 0) return;
        pillarT -= dt;
        pillar.widthMultiplier = Mathf.Max(0, pillarT) * 2f;
        if (pillarT <= 0) pillar.gameObject.SetActive(false);
    }

    void Pick(int i)
    {
        if (i >= choices.Count) return;
        var s = choices[i];
        TrackPick("lvl", s.title, choices.ConvertAll(c => c.title));
        int si = skills.IndexOf(s);
        if (si >= 0) CoopAct(1, si); else CoopAct(2, Endless().IndexOf(s));
        LevelSkill(s);
        stickOn = false;
        if (s.fusion)
        {
            Banner("СЛИЯНИЕ: " + s.title, s.Desc(1));
            screenFlash = 1;
            Boom(player.pos, 3, s.color, false);
        }
        grimoireOpen = false;
        if (s.NeedsSuper) { superSkill = s; state = State.Super; menuT = Time.unscaledTime; }
        else { state = State.Play; PickWave(s.color); NextChoice(); }
    }

    void PickSuper(int i)
    {
        if (superSkill == null || i > 2) return;
        superSkill.super = i;
        CoopAct(3, skills.IndexOf(superSkill), i);
        TrackPick("super", superSkill.title + ": " + superSkill.superNames[i], new List<string>(superSkill.superNames));
        superSkill.OnSuper(this);
        Banner("СУПЕРСИЛА: " + superSkill.superNames[i], superSkill.superDescs[i]);
        screenFlash = 1;
        LevelFx(superSkill.color);
        Boom(player.pos, 3.5f, superSkill.color, false);
        PickWave(superSkill.color);
        superSkill = null;
        state = State.Play;
        NextChoice();
    }

    public void Shake(float s)
    {
        // тряска от магии друга — только если он рядом
        if (cur != me && me != null) s *= Mathf.Clamp01(1 - (player.pos - me.player.pos).magnitude / 16f) * 0.7f;
        shake = Mathf.Max(shake, optShake && !optCalm ? s : 0);
        // GameSfx ловит взрыв по росту тряски; без тряски зовём звук напрямую
        if ((!optShake || optCalm) && s >= 0.2f) Sfx("boom", Mathf.Clamp(s, 0.6f, 1.3f));
    }

    // ---------- Уборка пулов, камера ----------

    void Sweep()
    {
        for (int i = enemies.Count - 1; i >= 0; i--)
        {
            var e = enemies[i];
            if (e.alive) continue;
            e.anim.speed = 1;
            e.anim.enabled = true;
            e.anim.CrossFade("Death", 0.05f);
            VatState(e.rends, "Death");
            e.dying = 1.8f;
            if (!e.type.boss && Random.value < 0.6f)
            {
                var kd = e.lastHit.sqrMagnitude > 0.01f ? e.lastHit.normalized : (e.pos - TargetOf(e.pos)).normalized;
                e.fly = World.W(kd) * Random.Range(3f, 6f) + Vector3.up * Random.Range(3.5f, 6f);
                e.spinAxis = Vector3.Cross(Vector3.up, World.W(kd)).normalized;
            }
            else e.fly = Vector3.zero;
            if (e.type.elite || e.rage) HitStop(0.06f);
            if (e.type.boss) HitStop(0.3f);
            e.flash = 0;
            e.charged = false;
            Visual(e);
            dying.Add(e);
            enemies.RemoveAt(i);
        }
        for (int i = projs.Count - 1; i >= 0; i--)
            if (!projs[i].alive) { projs[i].go.SetActive(false); projPool.Push(projs[i]); projs.RemoveAt(i); }
        for (int i = gems.Count - 1; i >= 0; i--)
            if (!gems[i].alive) { gems[i].go.SetActive(false); (gems[i].heal ? healPool : gemPool).Push(gems[i]); gems.RemoveAt(i); }
    }

    void UpdateCamera(float dt)
    {
        UpdateRenderScale();
        shake = Mathf.Max(0, shake - dt);
        // в вертикальном экране камера отъезжает и шире смотрит, чтобы по бокам было видно скелетов
        float k = Mathf.InverseLerp(1.2f, 0.56f, cam.aspect);
        cam.fieldOfView = Mathf.Lerp(40, 50, k);
        var target = World.W(ViewPos) + Vector3.Lerp(new Vector3(0, 15f, -10.5f), new Vector3(0, 17.5f, -9.3f), k);
        var pos = Vector3.SmoothDamp(cam.transform.position, target, ref camVel, 0.12f);
        if ((cam.transform.position - target).sqrMagnitude > 100) pos = target;
        var off = shake > 0 ? Random.insideUnitSphere * shake * 0.5f : Vector3.zero;
        cam.transform.SetPositionAndRotation(pos + off, Quaternion.Euler(Mathf.Lerp(55, 62, k), 0, 0));
    }

    // ---------- Интерфейс: стили и текст — GameText.cs, экраны — GameUI.cs ----------

    bool ToGui(Vector3 world, float u, out Vector2 g)
    {
        var sp = cam.WorldToScreenPoint(world);
        g = new Vector2(sp.x / u, (Screen.height - sp.y) / u);
        return sp.z > 0;
    }

    // Интерфейс рисуется в условных пикселях: альбом — 1080 по высоте, вертикальный телефон — 760 по ширине,
    // иначе на телефоне всё мельчит в полтора раза
    static bool Portrait => Screen.height > Screen.width;
    // телефон в альбомной: ниже 560 CSS-пикселей. При 1080 условных по высоте текст там почти не читался —
    // берём 820 (крупнее на треть), экраны под эту высоту раскладываются плотнее (PhoneLand)
    static bool PhoneLand => !Portrait && CssHeight > 0 && CssHeight < 560;
    static float UiScale => Portrait ? Screen.width / 760f : Mathf.Min(Screen.width, Screen.height) / (PhoneLand ? 820f : 1080f);
#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float BrowserCssHeight();
#else
    static float BrowserCssHeight() => 0;
#endif
    static float cssH; static int cssFrame = -1;
    static float CssHeight { get { if (cssFrame != Time.frameCount) { cssFrame = Time.frameCount; cssH = BrowserCssHeight(); } return cssH; } }

    bool peek;
    readonly List<Vector2> magnetDrops = new();

    void OnGUI()
    {
        // Кнопки без GUILayout: событие Layout не нужно, а это половина вызовов OnGUI
        if (Event.current.type == EventType.Layout) return;
        bool paint = Event.current.type == EventType.Repaint;
        double t0 = Time.realtimeSinceStartupAsDouble;
        DrawGui(paint);
        if (paint) perfGui += Time.realtimeSinceStartupAsDouble - t0;
    }

    void DrawGui(bool paint)
    {
        Styles();
        PortraitType();
        float u = UiScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(u, u, 1));
        float W = Screen.width / u, H = Screen.height / u;

        if (state == State.Menu || state == State.Town)
        {
            NewsInput(W, H);
            // без аккаунта — только экран входа (вход обязателен, GameGate.cs)
            if (DrawGate(W, H)) { DrawNews(W, H); return; }
        }
        if (state == State.Menu) { DrawMenu(W, H); DrawNews(W, H); return; }
        if (state == State.Town)
        {
            if (paint) TownOverlay(u, W, H);
            DrawTown(W, H);
            DrawStick(u);
            DrawNews(W, H);
            return;
        }
        double tq = Time.realtimeSinceStartupAsDouble;
        if (paint) WorldOverlay(u, W, H);
        double tw = Time.realtimeSinceStartupAsDouble;
        TopBar(W, H);
        if (paint) { perfOvl += tw - tq; perfHud += Time.realtimeSinceStartupAsDouble - tw; }
        CoopHud(W, H);
        DrawJumpButton(u);
        if (screenFlash > 0) Rect(new Rect(0, 0, W, H), new Color(1, 1, 1, screenFlash * 0.6f));
        // «выбираю способности и ниче не вижу» (фидбек): кнопка «Поле боя» прячет карты, мир виден (на паузе в одиночной игре)
        bool picking = state == State.LevelUp || state == State.Super || state == State.Chest;
        if (!picking) peek = false;
        if (picking && peek)
        {
            float pw = Mathf.Min(560, W - 48);
            if (Btn(new Rect((W - pw) / 2, H - 150, pw, 96), "Вернуться к выбору", Gold) || GUI.Button(new Rect(0, 0, W, H - 170), GUIContent.none, GUIStyle.none))
            { peek = false; menuT = Time.unscaledTime - 0.3f; }
            return;
        }
        if (state == State.LevelUp) DrawLevelUp(W, H);
        if (state == State.Super) DrawSuper(W, H);
        if (state == State.Chest) DrawChest(W, H);
        // на телефоне шапка выбора во всю ширину — кнопки «Поле боя» и «Позже» уходят вниз под карты
        bool lowBtns = Portrait;   // в портрете шапка выбора во всю ширину — кнопки внизу, карты оставляют им место
        if (picking && Btn(lowBtns ? new Rect(24, H - 88, 230, 64) : new Rect(24, 130, 230, 64), "Поле боя", Bone)) peek = true;
        if (state == State.Stats) DrawStats(W, H);
        if (state == State.Dead || state == State.Won) DrawEnd(W, H);
        if (state == State.Pause) DrawPause(W, H);
        if (coopRun && Choosing && Btn(lowBtns ? new Rect(W - 264, H - 88, 240, 64) : new Rect(W - 264, 130, 240, 64), "Позже", Bone)) HoldChoice();
    }

    // Всё, что привязано к 3D: цифры урона, полоски здоровья, маркеры сундуков. Рисуется только на Repaint.
    void WorldOverlay(float u, float W, float H)
    {
        // Цифры урона. На телефоне — только 24 свежих: каждая цифра — два прохода текста, а при сотне врагов их 60,
        // и оверлей на Adreno занимал 2,8 мс кадра (телеметрия 17.11). Старые всё равно уже почти прозрачные
        for (int ni = LiteUi ? Mathf.Max(0, numbers.Count - 24) : 0; ni < numbers.Count; ni++)
        {
            var n = numbers[ni];
            if (!ToGui(n.pos + Vector3.up * n.t * 1.5f, u, out var g)) continue;
            var c = n.color; c.a = 1 - n.t / 0.8f;
            num.normal.textColor = c;
            num.fontSize = n.crit ? 46 : 30;
            // криты с обводкой, обычные цифры — с тенью: так дешевле при сотне цифр на экране
            if (n.crit) Outline(new Rect(g.x - 90, g.y - 30, 180, 60), n.text, num, 3);
            else Text(new Rect(g.x - 80, g.y - 25, 160, 50), n.text, num, 2);
        }

        // Полоски здоровья врагов: у сильных всегда, у остальных после попадания
        foreach (var e in enemies)
        {
            if (!e.alive || e.type.boss) continue;
            bool always = e.type.elite || e.rage;
            if (!always && time > e.shownUntil) continue;
            if (!ToGui(World.W(e.pos, 1.15f * e.type.scale * (1 + 0.06f * e.tier) + 0.2f), u, out var g)) continue;
            float w = e.type.elite ? 100 : always ? 64 : 46;
            Rect(new Rect(g.x - w / 2, g.y, w, 8), new Color(0, 0, 0, 0.75f));
            var c = e.rage ? RageColor : e.tier > 0 || e.type.elite ? e.tint : new Color(0.9f, 0.3f, 0.3f); c.a = 1;
            Rect(new Rect(g.x - w / 2 + 1, g.y + 1, (w - 2) * Mathf.Clamp01(e.hp / e.maxHp), 6), c);
        }

        // Здоровье мага
        if (ToGui(World.W(player.pos, -0.2f), u, out var hp))
        {
            Rect(new Rect(hp.x - 70, hp.y, 140, 16), new Color(0, 0, 0, 0.75f));
            Rect(new Rect(hp.x - 68, hp.y + 2, 136 * Mathf.Clamp01(player.hp / player.maxHp), 12), new Color(0.95f, 0.2f, 0.25f));
        }

        FallenOverlay(u);
        ChestMarkers(u, W, H);
        CoopOverlay(u, W, H);
    }

    // Маркер сундука: над ним, если виден, или у края экрана со стрелкой-расстоянием
    void ChestMarkers(float u, float W, float H)
    {
        // босс за краем экрана — красная стрелка с расстоянием: иначе он «постоял и исчез»
        if (bossShown != null && bossShown.alive)
        {
            bool front = ToGui(World.W(bossShown.pos, 1.8f), u, out var g);
            if (!(front && g.x > 70 && g.x < W - 70 && g.y > 140 && g.y < H - 70))
            {
                var ctr = new Vector2(W / 2, H / 2);
                var dir = g - ctr;
                if (!front) dir = -dir;
                if (dir.sqrMagnitude < 1) dir = Vector2.up;
                dir.Normalize();
                float sx = (W / 2 - 70) / Mathf.Max(0.001f, Mathf.Abs(dir.x)), sy = (H / 2 - 140) / Mathf.Max(0.001f, Mathf.Abs(dir.y));
                var e = ctr + dir * Mathf.Min(sx, sy);
                var col = new Color(1f, 0.3f, 0.25f);
                float pulse = 1 + 0.12f * Mathf.Sin(time * 7);
                IconTex(new Rect(e.x - 44 * pulse, e.y - 44 * pulse, 88 * pulse, 88 * pulse), "star_06", col);
                Outline(new Rect(e.x - 44, e.y - 44, 88, 88), "Б", Col(heavy, Color.white, TextAnchor.MiddleCenter), 2);
                Outline(new Rect(e.x - 70, e.y + 40, 140, 40), $"{(bossShown.pos - player.pos).magnitude:0} м", Col(caps, col, TextAnchor.MiddleCenter), 2);
            }
        }
        foreach (var c in chests)
        {
            var col = RarityColors[c.rarity];
            bool front = ToGui(World.W(c.pos, 1.8f), u, out var g);
            float margin = 70;
            bool inside = front && g.x > margin && g.x < W - margin && g.y > 140 && g.y < H - margin;
            float dist = (c.pos - player.pos).magnitude;
            if (inside)
            {
                float bob = Mathf.Sin(time * 5) * 10;
                IconTex(new Rect(g.x - 30, g.y - 70 + bob, 60, 60), "star_06", col);
                Outline(new Rect(g.x - 30, g.y - 72 + bob, 60, 60), "!", Col(heavy, Color.white, TextAnchor.MiddleCenter), 2);
                continue;
            }
            var ctr = new Vector2(W / 2, H / 2);
            var dir = (g - ctr);
            if (!front) dir = -dir;
            if (dir.sqrMagnitude < 1) dir = Vector2.up;
            dir.Normalize();
            float sx = (W / 2 - margin) / Mathf.Max(0.001f, Mathf.Abs(dir.x));
            float sy = (H / 2 - margin - 70) / Mathf.Max(0.001f, Mathf.Abs(dir.y));
            var e = ctr + dir * Mathf.Min(sx, sy);
            float pulse = 1 + 0.1f * Mathf.Sin(time * 6);
            IconTex(new Rect(e.x - 40 * pulse, e.y - 40 * pulse, 80 * pulse, 80 * pulse), "star_06", col);
            var tip = e + dir * 52;
            GUI.color = col;
            GUI.DrawTexture(new Rect(tip.x - 12, tip.y - 12, 24, 24), Art.Tex("circle_05"));
            GUI.color = Color.white;
            Outline(new Rect(e.x - 60, e.y + 36, 120, 40), $"{dist:0} м", Col(caps, Color.Lerp(col, Color.white, 0.5f), TextAnchor.MiddleCenter), 2);
        }
    }

}
