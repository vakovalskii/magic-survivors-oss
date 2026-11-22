using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// Ярость, кристаллы опыта пяти размеров, реликвии на поле с временными эффектами, пауза и настройки.
public partial class Game
{
    // ---------- Кристаллы опыта ----------

    static readonly Color[] GemColors =
    {
        new(0.25f, 0.7f, 2.6f), new(0.4f, 2.5f, 0.8f), new(1.6f, 0.5f, 2.8f), new(4f, 1.4f, 0.3f), new(5f, 3.8f, 1.2f),
    };
    static readonly float[] GemSizes = { 0.13f, 0.17f, 0.22f, 0.3f, 0.55f };
    static int GemTier(float v) => v < 2 ? 0 : v < 6 ? 1 : v < 20 ? 2 : v < 80 ? 3 : 4;

    float NecroBonus => player.necro > 0 ? Mathf.Min(0.3f, kills / 100 * 0.01f) : 0;

    // ---------- Ярость ----------

    static readonly Color RageColor = new(1f, 0.35f, 0.12f), RageTint = new(1.7f, 0.4f, 0.15f);
    const int RageCount = 20;
    float nextRage = 150, rageUntil;

    void StartRage()
    {
        nextRage = time + Random.Range(130f, 170f);
        rageUntil = time + 30;
        tmp.Clear();
        foreach (var e in enemies)
            if (e.alive && !e.type.boss && !e.ring && !e.rage) tmp.Add(e);
        tmp.Sort((a, b) => (a.pos - player.pos).sqrMagnitude.CompareTo((b.pos - player.pos).sqrMagnitude));
        int n = 0;
        for (; n < RageCount && n < tmp.Count; n++) MakeRage(tmp[n]);
        var t = types[Mathf.Min(types.Length - 1, (int)(time / 120))];
        for (; n < RageCount; n++) MakeRage(SpawnEnemy(t, RingPoint(Random.value * Mathf.PI * 2), TierNow(), false));
        WorldBanner("ЯРОСТЬ!", $"{RageCount} скелетов в ярости — с них много опыта");
        Shake(0.35f);
        hurtFx = Mathf.Max(hurtFx, 0.5f);
    }

    void MakeRage(Enemy e)
    {
        e.rage = true;
        e.hp *= 3; e.maxHp *= 3; e.dmg *= 1.3f;
        e.tr.localScale *= 1.2f; e.radius *= 1.2f;
        Visual(e);
        Vfx.Burst(Vfx.Fire, World.W(e.pos, 0.8f), 10, 3, 0.6f, 0.5f, RageColor);
    }

    void EndRage()
    {
        foreach (var e in enemies)
        {
            if (!e.alive || !e.rage) continue;
            e.rage = false;
            e.hp /= 3; e.maxHp /= 3; e.dmg /= 1.3f;
            e.tr.localScale /= 1.2f; e.radius /= 1.2f;
            Visual(e);
        }
    }

    // ---------- Реликвии на поле ----------

    class Relic
    {
        public GameObject go;
        public Transform tr;
        public SpriteRenderer sr;
        public Light light;
        public Vector2 pos;
        public int kind, id;
        public float t, fx;
        public bool alive;
    }

    static readonly string[] RelicIcons = { "r_skull", "r_star", "r_clock", "r_elixir", "r_shield", "r_wings" };
    static readonly string[] RelicNames = { "Череп гнева", "Звезда притяжения", "Ледяные часы", "Эликсир силы", "Святой щит", "Крылья ветра" };
    static readonly string[] RelicDescs =
    {
        "Взрыв испепеляет скелетов вокруг", "Весь опыт на поле летит к магу", "Все враги заморожены на 4 с",
        "Урон ×2 на 12 с", "Неуязвимость на 8 с", "Скорость ×1.6 на 10 с",
    };
    static readonly Color[] RelicColors =
    {
        new(1f, 0.3f, 0.2f), new(0.4f, 0.7f, 1f), new(0.6f, 0.9f, 1f), new(1f, 0.35f, 0.4f), new(1f, 0.85f, 0.4f), new(0.85f, 1f, 0.9f),
    };
    static readonly float[] RelicWeights = { 0.8f, 1.6f, 1f, 1f, 0.8f, 1f };   // магнит чаще: «не хватает магнита» — он есть, но редко попадался

    readonly List<Relic> relics = new();
    float nextRelic = 40, elixirUntil, shieldUntil, windsUntil, clockUntil;

    // kind и место задаёт хозяин кооператива; без них — случайная реликвия рядом с магом
    Relic SpawnRelic(int kind = -1, Vector2? at = null)
    {
        if (kind < 0)
        {
            float sum = 0, r;
            foreach (var w in RelicWeights) sum += w;
            r = Random.value * sum;
            kind = 0;
            for (; kind < RelicWeights.Length - 1; kind++) { r -= RelicWeights[kind]; if (r < 0) break; }
        }

        Relic rel = null;
        foreach (var x in relics) if (!x.alive) { rel = x; break; }
        if (rel == null)
        {
            rel = new Relic { go = new GameObject("Relic") };
            rel.tr = rel.go.transform;
            rel.sr = rel.go.AddComponent<SpriteRenderer>();
            rel.light = new GameObject("RelicLight").AddComponent<Light>();
            rel.light.transform.SetParent(rel.tr, false);
            rel.light.range = 4; rel.light.intensity = 4; rel.light.shadows = LightShadows.None;
            relics.Add(rel);
        }
        rel.kind = kind;
        rel.sr.sprite = Resources.Load<Sprite>("Relics/" + RelicIcons[kind]) ?? Art.Fx("star_06");
        rel.light.color = RelicColors[kind];
        rel.pos = at ?? World.Free(Anchor() + Random.insideUnitCircle.normalized * Random.Range(9f, 15f), 0.9f);
        rel.t = 60; rel.fx = 0;
        rel.alive = true;
        nextRelicId = (nextRelicId + 1) & 0xFF;
        rel.id = nextRelicId;
        rel.go.SetActive(true);
        return rel;
    }

    void PickRelic(Relic r)
    {
        r.alive = false;
        r.go.SetActive(false);
        var c = RelicColors[r.kind];
        Vfx.Emit(Vfx.Ring, World.W(player.pos, 0.15f), Vector3.zero, 5, 0.5f, c);
        Vfx.Burst(Vfx.Spark, World.W(player.pos, 1f), 24, 6, 0.3f, 0.5f, c);
        screenFlash = 0.35f;
        switch (r.kind)
        {
            case 0:
                Boom(player.pos, 6, RageColor, true);
                Shake(0.6f);
                Query(player.pos, 10, tmp2);
                foreach (var e in tmp2)
                {
                    if (e.type.boss) Hit(e, e.maxHp * 0.1f, (e.pos - player.pos).normalized, 0);
                    else { Vfx.Burst(Vfx.Fire, World.W(e.pos, 0.8f), 5, 3, 0.5f, 0.4f, RageColor); Kill(e); }
                }
                break;
            case 1:
                foreach (var g in gems) if (g.alive && !g.heal) g.pulled = true;
                break;
            case 2:
                foreach (var e in enemies) if (e.alive) Freeze(e, 4);
                clockUntil = time + 4;
                break;
            case 3: elixirUntil = time + 12; break;
            case 4: shieldUntil = time + 8; break;
            case 5: windsUntil = time + 10; break;
        }
        WorldBanner(RelicNames[r.kind], RelicDescs[r.kind]);
    }

    void UpdateLoot(float dt)
    {
        // у гостя ярость, реликвии и подбор решает хозяин — здесь только картинка
        if (!Guest)
        {
            if (time >= nextRage) StartRage();
            if (rageUntil > 0 && time >= rageUntil) { rageUntil = 0; EndRage(); }
            if (rageUntil > 0)
            {
                bool any = false;
                foreach (var e in enemies) if (e.alive && e.rage) { any = true; break; }
                if (!any) { rageUntil = 0; WorldBanner("Ярость подавлена", "Все одержимые скелеты повержены"); }
            }
        }
        foreach (var e in enemies)
            if (e.alive && e.rage && Random.value < 0.25f)
                Vfx.Emit(Vfx.Fire, World.W(e.pos + Random.insideUnitCircle * 0.3f, Random.Range(0.3f, 1.4f) * e.type.scale),
                    Vector3.up * 1.5f, 0.35f, 0.4f, RageColor);

        // крупные кристаллы искрят, чтобы их было видно издалека
        foreach (var g in gems)
            if (g.alive && g.tier >= 3 && Random.value < (g.tier == 4 ? 0.5f : 0.1f))
                Vfx.Emit(Vfx.Spark, World.W(g.pos, 0.4f) + Random.insideUnitSphere * 0.3f, Vector3.up, 0.12f, 0.6f, GemColors[g.tier] * 0.3f);

        if (time >= nextRelic && !Guest)
        {
            nextRelic = time + Random.Range(30f, 45f) / player.findMul;
            int alive = 0;
            foreach (var x in relics) if (x.alive) alive++;
            if (alive < 3) SpawnRelic();
        }

        var camRot = cam.transform.rotation;
        foreach (var r in relics)
        {
            if (!r.alive) continue;
            r.t -= dt;
            if (r.t <= 0) { r.alive = false; r.go.SetActive(false); continue; }
            float bob = Mathf.Sin(time * 3 + r.kind) * 0.15f;
            r.tr.SetPositionAndRotation(World.W(r.pos, 1.1f + bob), camRot);
            float blink = r.t < 10 && Mathf.Sin(time * 14) > 0 ? 0.35f : 1f;
            r.tr.localScale = Vector3.one * 2.2f * (1 + 0.06f * Mathf.Sin(time * 5));
            r.sr.color = new Color(1, 1, 1, blink);
            r.fx -= dt;
            if (r.fx <= 0)
            {
                r.fx = 0.9f;
                Vfx.Emit(Vfx.Ring, World.W(r.pos, 0.12f), Vector3.zero, 2.2f, 0.9f, RelicColors[r.kind] * 0.7f);
            }
            if (Random.value < 0.2f)
                Vfx.Emit(Vfx.Ember, World.W(r.pos + Random.insideUnitCircle * 0.5f, 0.2f), Vector3.up * 1.2f, 0.12f, 1f, RelicColors[r.kind]);
            var m = Guest ? null : Toucher(r.pos, 1.1f);
            if (m != null) { Use(m); PickRelic(r); Use(me); }
        }

        // ауры временных эффектов на маге
        var pp = World.W(player.pos, 0.15f);
        if (time < shieldUntil && Random.value < 0.12f) Vfx.Emit(Vfx.Ring, pp, Vector3.zero, 2.4f, 0.35f, RelicColors[4]);
        if (time < elixirUntil && Random.value < 0.4f) Vfx.Emit(Vfx.Ember, World.W(player.pos + Random.insideUnitCircle * 0.4f, 0.5f), Vector3.up * 2, 0.2f, 0.5f, RelicColors[3]);
        if (time < windsUntil && player.moving && Random.value < 0.5f) Vfx.Emit(Vfx.Smoke, pp, Vector3.up * 0.3f, 0.6f, 0.5f, new Color(0.85f, 1f, 0.9f, 0.35f));
    }

    // Активные эффекты: плашка с иконкой, названием, секундами и тающей полоской; ярость — над ними по центру
    void BuffBar(float W)
    {
        float x = 24, y = Portrait ? 166 : 130;
        void Buff(int k, float until, float total)
        {
            if (time >= until) return;
            float left = until - time, f = Mathf.Clamp01(left / total);
            var c = RelicColors[k];
            var r = new Rect(x, y, 300, 64);
            // в последние 2 секунды плашка мигает — эффект вот-вот кончится
            float a = left < 2 && Mathf.Sin(time * 16) > 0 ? 0.55f : 1;
            Plate(r, new Color(0.05f, 0.04f, 0.07f, 0.85f * a), new Color(c.r, c.g, c.b, 0.5f * a), 12);
            IconTex(new Rect(r.x + 8, r.y + 6, 52, 52), RelicIcons[k], c);
            Text(new Rect(r.x + 70, r.y + 4, 170, 32), RelicNames[k], Col(tiny, new Color(Bone.r, Bone.g, Bone.b, a), TextAnchor.MiddleLeft), 2);
            Text(new Rect(r.x + 70, r.y + 4, 220, 32), $"{Mathf.CeilToInt(left)} с", Col(val, new Color(c.r, c.g, c.b, a)), 2);
            RR(new Rect(r.x + 70, r.y + 44, 218, 8), new Color(0, 0, 0, 0.6f), 4);
            RR(new Rect(r.x + 70, r.y + 44, 218 * f, 8), new Color(c.r, c.g, c.b, a), 4);
            y += 74;
        }
        Buff(3, elixirUntil, 12);
        Buff(4, shieldUntil, 8);
        Buff(5, windsUntil, 10);
        Buff(2, clockUntil, 4);
        if (rageUntil > time)
        {
            int s = Mathf.CeilToInt(rageUntil - time), left = 0;
            foreach (var e in enemies) if (e.alive && e.rage) left++;
            ChipAt(W / 2, 124, $"ЯРОСТЬ  0:{s:00}  ·  ОСТАЛОСЬ {left}", RageColor, Mathf.Sin(time * 6) > 0);
        }
    }

    // ---------- Пауза и настройки ----------

    bool optNumbers = true, optShake = true, optBloom = true, optCalm, optLoaded;
    int optFps = 60, pauseTab;
    bool optHigh;
    static UniversalRenderPipelineAsset rp;
    int rpHeight;

    void LoadOpts()
    {
        if (optLoaded) return;
        optLoaded = true;
        optNumbers = PlayerPrefs.GetInt("opt.numbers", 1) == 1;
        optShake = PlayerPrefs.GetInt("opt.shake", 1) == 1;
        optBloom = PlayerPrefs.GetInt("opt.bloom", 1) == 1;
        // телефоны по умолчанию на 30: по телеметрии они и так держат ~39 из 60, а ровные 30 — вдвое меньше нагрева
        optFps = PlayerPrefs.GetInt("opt.fps", Application.isMobilePlatform ? 30 : 60);
        // стенд: ?fps=30 перебивает сохранённую настройку, чтобы мерить потолок без захода в настройки.
        // Такой заход главнее игры: шаблон страницы уже выставил потолок, трогать его отсюда нельзя
        var fpsUrl = System.Text.RegularExpressions.Regex.Match(Application.absoluteURL ?? "", "[?&]fps=(\\d{1,3})");
        if (fpsUrl.Success)
        {
            fpsUrlLock = true;
            optFps = Mathf.Clamp(int.Parse(fpsUrl.Groups[1].Value), 0, 120);
        }
        optCalm = PlayerPrefs.GetInt("opt.calm", 0) == 1;
        AudioListener.volume = testMute || PlayerPrefs.GetInt("opt.mute", 0) == 1 ? 0 : 1;
        // телефоны и браузер по умолчанию на низкой графике
        var scaleUrl = System.Text.RegularExpressions.Regex.Match(Application.absoluteURL ?? "", "[?&]scale=(\\d\\.\\d+)");
        if (scaleUrl.Success) scaleForce = float.Parse(scaleUrl.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        bool weak = Application.isMobilePlatform || Application.platform == RuntimePlatform.WebGLPlayer;
        optHigh = PlayerPrefs.GetInt("opt.high", weak ? 0 : 1) == 1;
        ApplyOpts();
    }

    // Частота кадров по месту: полная — только в бою и на прогулке по городу; в меню, окнах и на паузе — 30,
    // в фоне (вкладка или окно без фокуса) — 15. Логика игры почти ничего не стоит (0,2–2 мс на кадр по телеметрии),
    // нагрузку даёт отрисовка сцены — её и не гоняем впустую за меню.
    int fpsNow;

    float fpsLogT; int fpsLogF;

    float lvlT; int lvlF;

    // На WebGL Application.targetFrameRate игнорируется: Unity рисует по requestAnimationFrame, то есть на частоте
    // экрана — на 120 Гц это 120 кадров, вдвое больше нужного нагрева. Режем кадры в шаблоне страницы
    // (WebGLTemplates/Full/index.html), отсюда только сообщаем желаемую частоту.
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void SetFrameCap(int fps);

    static bool fpsUrlLock;

    void FrameRateTick()
    {
        if (LabOn || bot) return;
        // проверка на локальном стенде: сколько кадров реально, какая цель и vSync (в консоль раз в 3 с)
        if (Application.absoluteURL.Contains("127.0.0.1") && Time.unscaledTime - fpsLogT > 3)
        {
            Debug.Log($"fpscheck: {(Time.frameCount - fpsLogF) / (Time.unscaledTime - fpsLogT):0} fps, target {Application.targetFrameRate}, vsync {QualitySettings.vSyncCount}, state {state}, tab {menuTab}, focused {Application.isFocused}");
            fpsLogT = Time.unscaledTime; fpsLogF = Time.frameCount;
        }
        // мир стоит (одиночный выбор карт, пауза, конец забега) — 30; в кооперативе мир идёт и под картами — полная
        // карты прокачки выезжают анимацией, на 30 кадрах она рвётся — им полная частота
        bool cards = state == State.LevelUp || state == State.Super || state == State.Chest;
        bool idle = !cards && (state == State.Menu || (state == State.Town && menuTab != 0)
            || (!WorldRuns && state != State.Play && state != State.Town));
        // фон — 15, но только когда мир стоит: в бою шаг ограничен 1/20 с, и 15 кадров замедлили бы мир (у хозяина
        // кооператива — для всей комнаты) и сбили бы автоподстройку и потолок мобов (ревью Codex 16.11)
        int want = !idle ? optFps : Application.isFocused ? Mathf.Min(30, optFps) : 15;
        UpdateRenderScale();
        if (want == fpsNow) return;
        fpsNow = want;
        // при vSync (уровень качества High для WebGL) Unity игнорирует targetFrameRate и рисует на частоте экрана —
        // на 120-герцовых экранах это 120 кадров: так и было до 16.11, ограничение «60» не работало никогда
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = want;
#if UNITY_WEBGL && !UNITY_EDITOR
        if (!fpsUrlLock) SetFrameCap(want);
#endif
    }

    void ApplyOpts()
    {
        fpsNow = 0;   // FrameRateTick выставит частоту по месту с новой настройкой
        Application.targetFrameRate = optFps;
        World.SetBloom(optBloom && !optCalm);
        PlayerPrefs.SetInt("opt.numbers", optNumbers ? 1 : 0);
        PlayerPrefs.SetInt("opt.shake", optShake ? 1 : 0);
        PlayerPrefs.SetInt("opt.bloom", optBloom ? 1 : 0);
        PlayerPrefs.SetInt("opt.fps", optFps);
        PlayerPrefs.SetInt("opt.calm", optCalm ? 1 : 0);
        PlayerPrefs.SetInt("opt.high", optHigh ? 1 : 0);
        ApplyQuality();
    }

    // Графика правится на копии URP-ассета, чтобы в редакторе не перезаписывать файл проекта
    void ApplyQuality()
    {
        if (rp == null)
        {
            rp = Instantiate((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline);
            GraphicsSettings.defaultRenderPipeline = rp;
            QualitySettings.renderPipeline = rp;
        }
        rp.shadowDistance = optHigh ? 24 : 0;
        rp.msaaSampleCount = optHigh ? 2 : 1;
        rp.supportsHDR = false;
        bool phone = Application.isMobilePlatform;
        World.SetQuality(!optHigh, phone);
        // точечный свет заклинаний считается попиксельно на каждый предмет: на низкой — меньше источников разом
        rp.maxAdditionalLightsCount = optHigh ? 4 : phone ? 2 : 3;
        // в браузере кости считает процессор: у угловатых моделей KayKit двух (на телефоне — одной) костей на вершину хватает
        QualitySettings.skinWeights = optHigh ? SkinWeights.FourBones : phone ? SkinWeights.OneBone : SkinWeights.TwoBones;
        rpHeight = 0;
        UpdateRenderScale();
    }

    // На Retina и 4K рендер в родном разрешении съедает всё: держим короткую сторону кадра ~800 (высокая)
    // или ~560 пикселей; adaptK — поправка автоподстройки (AdaptTick). Поверх полноэкранной карточки мир
    // виден на 10% сквозь затемнение — там ужимаем кадр до 60% (множитель 0.6), интерфейс рисуется мимо
    // этого буфера и остаётся резким.
    bool rpPanel;

    static float scaleForce;

    public static void ForceScale(float v) { scaleForce = v; }

    void UpdateRenderScale()
    {
        bool panel = state == State.LevelUp || state == State.Super || state == State.Chest
            || state == State.Pause || state == State.Stats || state == State.Menu;
        if (rp == null || (Screen.width * 10000 + Screen.height == rpHeight && panel == rpPanel)) return;
        rpHeight = Screen.width * 10000 + Screen.height;
        rpPanel = panel;
        int target = optHigh ? 800 : Application.isMobilePlatform ? 560 : 560;
        float scale = Mathf.Clamp(target * adaptK / Mathf.Max(1, Mathf.Min(Screen.width, Screen.height)), 0.35f, 1f);
        if (scaleForce > 0) scale = Mathf.Clamp(scaleForce, 0.35f, 1f);
        rp.renderScale = panel ? Mathf.Max(0.35f, scale * 0.6f) : scale;
    }

    // Автоподстройка разрешения: если в бою кадров заметно меньше цели, кадр мельчает шагами до 60%.
    // Шаг, который не прибавил кадров (упёрлись в процессор или в 30 Гц энергосбережения), откатывается,
    // и подстройка выключается до следующего запуска — картинку зря не портим.
    static float adaptK = 1;
    static bool adaptLocked;
    float adT, adProbeFps;
    int adFrames;
    bool adProbe, adSkip;

    void AdaptTick()
    {
        if (adaptLocked || bot || rp == null) return;
        float u = Time.unscaledDeltaTime;
        if (state != State.Play || u > 0.5f) { adSkip = true; return; } // меню, свёрнутая вкладка — окно не в счёт
        adT += u; adFrames++;
        if (adT < 3) return;
        float fps = adFrames / adT;
        adT = 0; adFrames = 0;
        if (adSkip) { adSkip = false; return; }
        const float Step = 0.12f;
        if (adProbe)
        {
            adProbe = false;
            if (fps < adProbeFps * 1.08f) { adaptK += Step; adaptLocked = true; rpHeight = 0; UpdateRenderScale(); return; }
        }
        if (fps < Mathf.Min(optFps, 60) * 0.72f && adaptK > 0.6f + Step * 0.5f)
        {
            adProbe = true; adProbeFps = fps;
            adaptK -= Step;
            rpHeight = 0; UpdateRenderScale();
        }
    }

    void TogglePause()
    {
        if (state == State.Play) { state = State.Pause; pauseTab = 0; menuT = Time.unscaledTime; }
        else if (state == State.Pause) { if (pauseTab != 0) pauseTab = 0; else state = State.Play; }
    }

    bool Btn(Rect r, string label, Color c)
    {
        bool hov = r.Contains(Event.current.mousePosition);
        Plate(r, new Color(0.07f, 0.05f, 0.06f, 0.94f), hov ? Gold : new Color(c.r, c.g, c.b, 0.45f), 12);
        if (hov) RR(new Rect(r.x + 10, r.yMax - 4, r.width - 20, 2), Gold, 1);
        Outline(r, label, Shrink(Col(titleS, hov ? Color.white : new Color(0.95f, 0.9f, 0.8f)), label, r.width - 20), 2);   // надпись кнопки — ярко: серое на сером не читалось
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }

    // Кнопка паузы в HUD: две вертикальные черты
    void PauseButton(Rect r)
    {
        bool hov = r.Contains(Event.current.mousePosition);
        Plate(r, new Color(0.06f, 0.05f, 0.09f, 0.82f), hov ? Gold : new Color(1, 1, 1, 0.15f), 14);
        var c = hov ? Gold : Bone;
        RR(new Rect(r.center.x - 13, r.center.y - 17, 9, 34), c, 3);
        RR(new Rect(r.center.x + 4, r.center.y - 17, 9, 34), c, 3);
        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) TogglePause();
    }

    // Звук одной кнопкой на верхней панели: глушит всё разом, громкости музыки и звуков в настройках не трогает
    static bool Muted
    {
        get => AudioListener.volume < 0.01f;
        set { AudioListener.volume = value ? 0 : 1; PlayerPrefs.SetInt("opt.mute", value ? 1 : 0); }
    }

    void MuteButton(Rect r)
    {
        bool hov = r.Contains(Event.current.mousePosition), off = Muted;
        Plate(r, new Color(0.06f, 0.05f, 0.09f, 0.82f), hov ? Gold : new Color(1, 1, 1, 0.15f), 14);
        var c = hov ? Gold : off ? new Color(Bone.r, Bone.g, Bone.b, 0.45f) : Bone;
        float cx = r.center.x - 8, cy = r.center.y;
        // динамик: коробочка и раструб; волны, пока звук есть, и косая черта, когда выключен
        RR(new Rect(cx - 12, cy - 7, 10, 14), c, 2);
        RR(new Rect(cx - 3, cy - 15, 9, 30), c, 4);
        if (!off)
        {
            RR(new Rect(cx + 11, cy - 8, 4, 16), c, 2);
            RR(new Rect(cx + 20, cy - 14, 4, 28), c, 2);
        }
        else
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45, GUIUtility.GUIToScreenPoint(new Vector2(cx + 4, cy)));
            RR(new Rect(cx - 16, cy - 2.5f, 40, 5), new Color(1f, 0.45f, 0.4f), 2);
            GUI.matrix = m;
        }
        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) Muted = !off;
    }

    void DrawPause(float W, float H)
    {
        MenuBackdrop(W, H, new Color(0.45f, 0.4f, 0.35f));
        float bw = Mathf.Min(560, W - 80), x = (W - bw) / 2, y = H * 0.32f, bh = 96, gap = 22;
        if (pauseTab == 0)
        {
            if (DrawNote(W, H)) return;   // окно отзыва поверх: кнопки паузы под ним не должны нажиматься (ревью Codex)
            MenuHeader(W, H * 0.12f, $"{(int)time / 60:00}:{(int)time % 60:00}  ·  УРОВЕНЬ {level}", "Пауза", Bone);
            if (Btn(new Rect(x, y, bw, bh), "Продолжить", Bone)) state = State.Play;
            // гость не перезапускает общий забег — это делает хозяин
            if (Btn(new Rect(x, y + (bh + gap), bw, bh), Guest ? "—" : "Начать заново", Bone) && !Guest) Restart(false);
            if (Btn(new Rect(x, y + 2 * (bh + gap), bw, bh), "Настройки", Bone)) pauseTab = 1;
            // «выйти» в браузере и на телефоне некуда — возвращаемся к выбору уровня и героя
            // выход посреди забега засчитывает его: золото и рекорды остаются (Restart) — сумму пишем прямо на кнопке
            string quit = Town.home ? "В город" : "В меню";
            if (!freeRun && runGold < 0 && time >= 5 && !bot) quit = $"Завершить и {(Town.home ? "в город" : "в меню")}  ·  +{RunGold(false)} золота";
            if (Btn(new Rect(x, y + 3 * (bh + gap), bw, bh), quit, new Color(0.8f, 0.3f, 0.25f))) Restart(true);
            if (!bot && Btn(new Rect(x, y + 4 * (bh + gap), bw, 70), "Отзыв о забеге", Gold)) NoteOpen();
            return;
        }

        MenuHeader(W, H * 0.12f, "ПАУЗА", "Настройки", Bone);
        bh = 64; gap = 8; y = Mathf.Max(H * 0.24f, H * 0.12f + 166); // ниже заголовка и его линии при любой высоте экрана
        // низкий широкий экран (телефон в альбомной): девять строк в столбик не влезают — два столбца
        int total = Application.platform == RuntimePlatform.WebGLPlayer ? 9 : 8;
        bool two = H < 980 && W > H;
        int per = two ? (total + 1) / 2 : total;
        float bw2 = two ? Mathf.Min(560, (W - 100) / 2) : bw, x2 = two ? (W - bw2 * 2 - 24) / 2 : x;
        void Toggle(int i, string name, string value, System.Action flip)
        {
            var r = new Rect(x2 + i / per * (bw2 + 24), y + i % per * (bh + gap), bw2, bh);
            bool hov = r.Contains(Event.current.mousePosition);
            Plate(r, new Color(0.07f, 0.05f, 0.06f, 0.94f), hov ? Gold : new Color(1, 1, 1, 0.12f), 12);
            Text(new Rect(r.x + 30, r.y, r.width - 60, r.height), name, Col(small, Bone, TextAnchor.MiddleLeft), 2);
            Text(new Rect(r.x + 30, r.y, r.width - 60, r.height), value, Col(val, hov ? Gold : new Color(0.75f, 0.7f, 0.6f)), 2);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { flip(); ApplyOpts(); }
        }
        Toggle(0, "Цифры урона", optNumbers ? "ВКЛ" : "ВЫКЛ", () => optNumbers = !optNumbers);
        Toggle(1, "Тряска экрана", optShake ? "ВКЛ" : "ВЫКЛ", () => optShake = !optShake);
        Toggle(2, "Свечение (bloom)", optBloom ? "ВКЛ" : "ВЫКЛ", () => optBloom = !optBloom);
        Toggle(3, "Частота кадров", $"{optFps} FPS", () => optFps = optFps == 60 ? 30 : 60);
        Toggle(4, "Графика", optHigh ? "ВЫСОКАЯ" : "НИЗКАЯ", () => optHigh = !optHigh);
        // музыка по кругу: громко → тихо → выкл
        Toggle(5, "Музыка", MusicVolume > 0.7f ? "ГРОМКО" : MusicVolume > 0.01f ? "ТИХО" : "ВЫКЛ",
            () => MusicVolume = MusicVolume > 0.7f ? 0.35f : MusicVolume > 0.01f ? 0 : 1);
        Toggle(6, "Звуки", SfxVolume > 0.85f ? "ГРОМКО" : SfxVolume > 0.01f ? "ТИХО" : "ВЫКЛ",
            () => SfxVolume = SfxVolume > 0.85f ? 0.4f : SfxVolume > 0.01f ? 0 : 1);
        // жалобы «рябит в глазах»: без вспышек экрана, тряски и свечения, искр втрое меньше и они тусклее
        Toggle(7, "Спокойный режим", optCalm ? "ВКЛ" : "ВЫКЛ", () => optCalm = !optCalm);
        // полный экран в браузере (на телефоне — без адресной строки); Unity включает его на ближайшем касании
        int rows = 8;
        if (Application.platform == RuntimePlatform.WebGLPlayer)
            Toggle(rows++, "Полный экран", Screen.fullScreen ? "ВКЛ" : "ВЫКЛ", () => Screen.fullScreen = !Screen.fullScreen);
        if (Btn(new Rect(x, y + Mathf.Min(rows, per) * (bh + gap) + 20, bw, bh), "Назад", Bone)) pauseTab = 0;
    }
}
