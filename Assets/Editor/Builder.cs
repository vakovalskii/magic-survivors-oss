using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Сборки для раздачи: Mac, Windows, Android APK, браузер. Всё складывается в Builds/<платформа>.
public static class Builder
{
    static readonly string[] Scenes = { "Assets/Scenes/Game.unity" };

    static string Run(BuildTarget target, BuildTargetGroup group, string path)
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
        var r = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = Scenes, locationPathName = path, target = target, targetGroup = group });
        var s = r.summary;
        return $"{target}: {s.result}, {s.totalSize / 1048576f:0.0} MB, {s.totalTime.TotalSeconds:0}s, errors {s.totalErrors}";
    }

    // Запуск из CLI: eval отваливается по таймауту, а сборка идёт дальше; результат — в Builds/status.txt
    public static string Queue(string what)
    {
        System.IO.Directory.CreateDirectory("Builds");
        System.IO.File.WriteAllText("Builds/status.txt", "running " + what);
        {
            var log = "";
            foreach (var w in what.Split(','))
            {
                try
                {
                    log += w switch { "mac" => Mac(), "win" => Windows(), "android" => Android(), "web" => Web(), _ => "?" + w } + "\n";
                }
                catch (System.Exception e) { log += w + ": " + e.Message + "\n"; }
                System.IO.File.WriteAllText("Builds/status.txt", log + "...");
            }
            System.IO.File.WriteAllText("Builds/status.txt", log + "done");
        }
        return "built";
    }

    // для -batchmode -executeMethod: Builder.Cli -builds web,mac
    public static void Cli()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-builds");
        Queue(i >= 0 && i + 1 < args.Length ? args[i + 1] : "web");
        EditorApplication.Exit(System.IO.File.ReadAllText("Builds/status.txt").Contains("Succeeded") ? 0 : 1);
    }

    [MenuItem("Build/Mac")]
    public static string Mac() => Run(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone, "Builds/Mac/MagicSurvivors.app");

    [MenuItem("Build/Windows")]
    public static string Windows() => Run(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, "Builds/Windows/MagicSurvivors.exe");

    [MenuItem("Build/Android")]
    public static string Android()
    {
        EditorUserBuildSettings.buildAppBundle = false;
        return Run(BuildTarget.Android, BuildTargetGroup.Android, "Builds/Android/MagicSurvivors.apk");
    }

    [MenuItem("Build/Web")]
    public static string Web()
    {
        VatBaker.Bake();
        // gzip, а не brotli: brotli браузер распакует только по https с правильными заголовками; с fallback игра стартует даже без настройки nginx
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.template = "PROJECT:Full";
        // Анимацию и скиннинг сотен мобов в браузере считает одно ядро процессора (видеокарта почти простаивает),
        // поэтому код собираем на скорость, а не на время сборки (по умолчанию BuildTimes), и с SIMD (wasm 2023:
        // Chrome 91+, Firefox 89+, Safari 16.4+)
        UnityEditor.WebGL.UserBuildSettings.codeOptimization = UnityEditor.WebGL.WasmCodeOptimization.RuntimeSpeed;
        PlayerSettings.WebGL.wasm2023 = true;
        return Run(BuildTarget.WebGL, BuildTargetGroup.WebGL, "Builds/Web");
    }
}
