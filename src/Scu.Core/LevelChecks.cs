using UAssetAPI.ExportTypes;

namespace Scu.Core;

public sealed record LevelCheck(int Level, int Script, int Model, bool Passed);

public static class LevelChecks
{
    /// <summary>composer.validate_level: exactly one LevelExport with a Random5x5_C script actor and a Model.</summary>
    public static LevelCheck Validate(Asset a, bool anyHostClass = false)
    {
        var levels = a.Exports.Select((e, i) => (e, i: i + 1)).Where(x => x.e is LevelExport).ToList();
        Require(levels.Count == 1, "expected exactly one LevelExport");
        var (e, i) = levels[0];
        var level = (LevelExport)e;
        int script = level.LevelScriptActor.Index, model = level.Model.Index;
        var scriptExport = a.Export(script);
        var modelExport = a.Export(model);
        var scriptClass = a.ObjectName(scriptExport.ClassIndex.Index);
        Require(anyHostClass ? HostComposer.Assets.Values.Any(p => scriptClass == p + "_C") : scriptClass == "Random5x5_C",
            "LevelScriptActor wrong class " + scriptExport.ObjectName);
        Require(scriptExport.OuterIndex.Index == i && level.Actors.Any(p => p.Index == script), "LevelScriptActor not owned by level");
        Require(a.ObjectName(modelExport.ClassIndex.Index) == "Model", "Level Model wrong class " + modelExport.ObjectName);
        Require(modelExport.OuterIndex.Index == i, "Level Model not owned by level");
        return new LevelCheck(i, script, model, true);
    }

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
