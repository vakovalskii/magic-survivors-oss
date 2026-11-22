using System.Collections.Generic;
using UnityEngine;

public static class VatMobs
{
    public class Info
    {
        public Texture2D tex;
        public int verts;
        public Dictionary<string, int> offset = new();
        public Dictionary<string, int> frames = new();
    }

    static readonly Dictionary<string, Info> cache = new();

    public static Info Get(string model, string weapon)
    {
        var file = string.IsNullOrEmpty(weapon) ? model + "_VAT" : model + "_" + weapon + "_VAT";
        if (cache.TryGetValue(file, out var cached)) return cached;
        var tex = Resources.Load<Texture2D>("Vat/" + file);
        var meta = Resources.Load<TextAsset>("Vat/" + file);
        if (tex == null || meta == null) { cache[file] = null; return null; }
        var info = new Info { tex = tex };
        foreach (var line in meta.text.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length != 3 || !int.TryParse(parts[1], out var off) || !int.TryParse(parts[2], out var fr)) continue;
            info.offset[parts[0]] = off;
            info.frames[parts[0]] = fr;
        }
        cache[file] = info;
        return info;
    }
}

public class VatActor : MonoBehaviour
{
    static readonly string[] Looping = { "Idle", "Walk", "Run" };

    public Renderer rend;
    VatMobs.Info info;
    MaterialPropertyBlock block;
    int offset, frames;
    Color color = Color.white;

    public void Setup(VatMobs.Info i, Renderer r)
    {
        info = i; rend = r;
        block = new MaterialPropertyBlock();
        State("Idle");
    }

    float start, speed = 1f, loop = 1f;

    public void State(string state)
    {
        if (info == null) return;
        if (info.offset.TryGetValue(state, out var o))
        {
            if (o == offset && frames > 0) { Push(); return; }
            offset = o;
            frames = info.frames.TryGetValue(state, out var f) ? Mathf.Max(1, f) : 32;
            start = Time.time;
            loop = System.Array.IndexOf(Looping, state) >= 0 ? 1f : 0f;
        }
        Push();
    }

    public void Speed(float v) { if (Mathf.Approximately(v, speed)) return; speed = Mathf.Max(0.05f, v); Push(); }

    public void Tint(Color c) { color = c; Push(); }

    void Push()
    {
        if (rend == null || block == null) return;
        block.SetColor("_Color", color);
        block.SetFloat("_VatStateOffset", offset);
        block.SetFloat("_VatStateFrames", frames);
        block.SetFloat("_VatStateStart", start);
        block.SetFloat("_VatSpeed", speed);
        block.SetFloat("_VatLoop", loop);
        rend.SetPropertyBlock(block);
    }
}
