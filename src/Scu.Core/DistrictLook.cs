using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Scu.Core;

public sealed record LookSummary(int Actors, SortedDictionary<string, int> ByStyle, int ChangedDoorLeaves, int ChangedDoorWalls,
    int ChangedFurniture, int RuntimeTargets, double Delay);

/// <summary>
/// Port of scu_map_patch.district_look_data + wall_styles.apply_style(actor_styles=...) + district_style_runtime.apply:
/// imported rooms keep their own stage's door and shelf materials, and a delayed runtime function re-applies them after
/// the host stage's native BeginPlay recolors them.
/// SCU-PATCH app/district_style_runtime.py #1-#2 (optional): the begin event may carry an extra difficulty check in front,
/// so the schedule call is inserted before the final Return instead of requiring exactly three nodes.
/// </summary>
public static class DistrictLook
{
    static readonly Dictionary<string, string> LookStages = new() { ["Stage5"] = "soul", ["Stage6"] = "sanctuary", ["Extra"] = "abyss", ["Cicada"] = "cicada" };
    static readonly Dictionary<string, int> NativeStages = new() { ["cicada"] = 3, ["abyss"] = 4, ["soul"] = 6, ["sanctuary"] = 7 };
    static readonly Regex Pattern = new(@"^SCMix_(Stage5|Stage6|Extra|Cicada)_", RegexOptions.Compiled);

    // ---- material tables (wall_styles) ----
    const string MaterialDir = "/Game/Arcitectuers/Materials/";
    static string Mat(string name) => MaterialDir + name + "." + name;
    static readonly string Frame = "/Game/Arcitectuers/M_Flam1.M_Flam1";
    static readonly string Plaster = Mat("M_Sickui"), Plank = Mat("M_WoodRoof"), Lattice = Mat("M_Kousi"), Bamboo = Mat("M_WallBamboo");
    static readonly string Red = Mat("M_WoodPanel_Red"), Vine = Mat("M_BlodVine1"), VineLattice = Mat("M_BlodVine_Kousi"), White = Mat("M_SikkuiWhite");
    static readonly string Wood = Mat("M_Flam"), Wood2 = Mat("M_Flam2"), Husuma = Mat("M_Husuma"), HusumaVine = Mat("M_Husuma2");
    static readonly string Syouji = Mat("M_Syouji"), SyoujiVine = Mat("M_Syouji2"), VineBase = Mat("M_BlodVineBase");
    const string Handle = "/Game/Props/Material/M_Syokudai.M_Syokudai";
    const string RedWood = "/Game/Props/Material/M_RedWood.M_RedWood";

    static readonly (string Path, string Role)[] Dummy = Array.Empty<(string, string)>();

    static readonly Dictionary<string, (string Path, string Role)[]> WallSlots = new()
    {
        ["JinjyaWall_1"] = new[] { (Frame, "frame"), (Plaster, "panel") },
        ["JinjyaWall_1-1"] = new[] { (Plank, "plank"), (Plaster, "panel"), (Frame, "frame") },
        ["JinjyaWall_1-2"] = new[] { (Plank, "plank"), (Lattice, "lattice"), (Frame, "frame") },
        ["JinjyaWall_2"] = new[] { (Frame, "frame"), (Plank, "plank"), (Plaster, "panel"), (Frame, "frame") },
        ["JinjyaWall_3"] = new[] { (Lattice, "lattice"), (Plaster, "panel"), (Plaster, "panel"), (Frame, "frame") },
        ["JinjyaWall_4"] = new[] { (Plaster, "panel"), (Frame, "frame") },
        ["JinjyaWall_5"] = new[] { (Frame, "frame"), (Plaster, "panel"), (Frame, "frame"), (Bamboo, "bamboo") },
        ["Zashikirou_Wall"] = new[] { (Wood, "panel") },
    };

    static readonly Dictionary<string, (string Path, string Role)[]> DoorMeshSlots = new()
    {
        ["WoodDoor"] = new[] { (Wood, "door_wood"), (Wood, "door_wood") },
        ["WoodDoor_DM"] = new[] { (Wood, "door_wood"), (Wood, "door_wood"), (Wood, "door_wood") },
        ["WoodDoor_DM_Vine"] = new[] { (VineBase, "door_wood"), (VineBase, "door_wood"), (Wood, "door_wood") },
        ["Syouzi"] = new[] { (Wood, "door_wood"), (Wood, "door_wood"), (Syouji, "masked_paper") },
        ["Syouzi_DM"] = new[] { (Wood, "door_wood"), (Wood, "door_wood"), (Syouji, "masked_paper"), (Wood, "door_wood") },
        ["Syouzi_DM_Vine"] = new[] { (VineBase, "door_wood"), (VineBase, "door_wood"), (SyoujiVine, "masked_paper"), (Wood, "door_wood") },
        ["Husuma"] = new[] { (Husuma, "opaque_paper"), (Handle, "preserve") },
        ["Husuma_DM"] = new[] { (Husuma, "opaque_paper"), (Handle, "preserve"), (Frame, "door_wood") },
        ["Husuma_DM_Vine"] = new[] { (HusumaVine, "opaque_paper"), (Handle, "preserve"), (Frame, "door_wood") },
        ["WoodDoor4"] = new[] { (Wood, "door_wood"), (Lattice, "lattice"), (Wood, "door_wood"), (Wood2, "masked_wood") },
        ["WoodDoor4_DM"] = new[] { (Wood, "door_wood"), (Lattice, "lattice"), (Wood, "door_wood"), (Wood2, "masked_wood"), (Wood, "door_wood") },
        ["Zashikirou_Door"] = new[] { (Wood, "door_wood") },
        ["Zashikirou_Door_DM"] = new[] { (Wood, "door_wood"), (Wood, "door_wood") },
        ["Zashikirou_Wall_Door"] = new[] { (Wood, "door_wood") },
    };

    static readonly Dictionary<string, string> DoorClasses = new[] { "BP_DoorActor_Slide1", "BP_DoorActor_Slide2", "BP_DoorActor_Slide3", "BP_DoorActor_Push" }
        .ToDictionary(f => "/Game/Blueprints/" + f + "." + f + "_C", f => f);

    static readonly Dictionary<string, Dictionary<string, string>> DoorComponents = new()
    {
        ["BP_DoorActor_Slide1"] = new() { ["Mesh_Wall"] = "JinjyaWall_4", ["WoodDoor"] = "WoodDoor", ["WoodDoor1"] = "WoodDoor", ["WoodDoor_DM"] = "WoodDoor_DM", ["WoodDoor_DM1"] = "WoodDoor_DM", ["WoodDoor_DM_Vine"] = "WoodDoor_DM_Vine", ["WoodDoor_DM_Vine1"] = "WoodDoor_DM_Vine" },
        ["BP_DoorActor_Slide2"] = new() { ["Mesh_Wall"] = "JinjyaWall_4", ["Syouzi"] = "Syouzi", ["Syouzi1"] = "Syouzi", ["Syouzi_DM_1"] = "Syouzi_DM", ["Syouzi_DM_2"] = "Syouzi_DM", ["Syouzi_DM_Vine"] = "Syouzi_DM_Vine", ["Syouzi_DM_Vine1"] = "Syouzi_DM_Vine" },
        ["BP_DoorActor_Slide3"] = new() { ["Mesh_Wall"] = "JinjyaWall_4", ["Husuma"] = "Husuma", ["Husuma1"] = "Husuma", ["Husuma_DM"] = "Husuma_DM", ["Husuma_DM1"] = "Husuma_DM", ["Husuma_DM_Vine"] = "Husuma_DM_Vine", ["Husuma_DM_Vine1"] = "Husuma_DM_Vine" },
        ["BP_DoorActor_Push"] = new() { ["Mesh_Wall"] = "JinjyaWall_2", ["Mesh_Door"] = "WoodDoor4", ["WoodDoor4_DM"] = "WoodDoor4_DM" },
    };

    static readonly Dictionary<string, (string Pair0, string Pair1)[]> DistrictDmPairs = new()
    {
        ["BP_DoorActor_Slide1"] = new[] { ("WoodDoor_DM", "WoodDoor_DM_Vine"), ("WoodDoor_DM1", "WoodDoor_DM_Vine1") },
        ["BP_DoorActor_Slide2"] = new[] { ("Syouzi_DM_1", "Syouzi_DM_Vine"), ("Syouzi_DM_2", "Syouzi_DM_Vine1") },
        ["BP_DoorActor_Slide3"] = new[] { ("Husuma_DM", "Husuma_DM_Vine"), ("Husuma_DM1", "Husuma_DM_Vine1") },
    };

    static readonly Dictionary<string, Dictionary<string, (string Mesh, int Slot)>> FurnitureComponents = new()
    {
        ["/Game/Blueprints/Items/BP_Rack.BP_Rack_C"] = new() { ["Mesh_Body"] = ("lack", 0) },
        ["/Game/Blueprints/Items/BP_Tans_Low.BP_Tans_Low_C"] = new()
        {
            ["Mesh_Body"] = ("Tans_Body_Low", 0), ["Mesh_PullBox1"] = ("Tans_PullBox", 3),
            ["Mesh_PullBox2"] = ("Tans_PullBox", 3), ["Mesh_PullBox3"] = ("Tans_PullBox", 3),
        },
    };

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    static (string Family, int Manual) NativeDoorFamily(NormalExport actor, int nativeStage)
    {
        var values = actor.Data.Where(p => p.Name.ToString() == "ManualDoorMaterial").ToList();
        Require(values.Count <= 1, "duplicate ManualDoorMaterial");
        int manual = 0;
        if (values.Count == 1)
        {
            Require(values[0] is IntPropertyData, "unexpected ManualDoorMaterial type");
            manual = ((IntPropertyData)values[0]).Value;
        }
        var family = manual == 1 ? "normal" : manual == 2 ? "vine" : nativeStage == 6 ? "vine" : "normal";
        return (family, manual);
    }

    /// <summary>wall_styles._door_targets for a mesh that is not a wall mesh (kept for the schema-6 style path).</summary>
    static (Dictionary<string, string> Unused, int Dummy) Unused() => (new(), 0);

    static (IReadOnlyList<(string Path, string Role)> Slots, SortedDictionary<int, string> Targets) DistrictDoorTargets(
        string source, string family, string componentName, string meshName, NormalExport actor, List<string?> authored, List<string?>? sourceAuthored)
    {
        var slots = WallSlots.TryGetValue(meshName, out var ws) ? ws : DoorMeshSlots[meshName];
        var (sourceFamily, _) = NativeDoorFamily(actor, NativeStages[source]);
        bool vine = sourceFamily == "vine";
        var materialSlots = slots;
        if (componentName.Contains("_DM") && family != "BP_DoorActor_Push")
        {
            var nativeMesh = meshName.EndsWith("_Vine", StringComparison.Ordinal) ? meshName[..^5] : meshName;
            if (vine) nativeMesh += "_Vine";
            materialSlots = DoorMeshSlots[nativeMesh];
            authored = sourceAuthored ?? new List<string?>();
        }
        var effective = materialSlots.Select((s, i) => i < authored.Count && !string.IsNullOrEmpty(authored[i]) ? authored[i]! : s.Path).ToList();
        if (componentName == "Mesh_Wall")
        {
            if (family == "BP_DoorActor_Push")
            {
                int doorType = Props.Of(actor).TryGetValue("DoorType", out var dt) && dt is IntPropertyData ip ? ip.Value : 1;
                if (doorType == 1)
                {
                    if (source == "soul") for (int i = 0; i < 4 && i < effective.Count; i++) effective[i] = Vine;
                    else if (source == "abyss") { var abyss = new[] { Frame, Red, Red, Frame }; for (int i = 0; i < 4 && i < effective.Count; i++) effective[i] = abyss[i]; }
                }
            }
            else
            {
                bool originalWall = Props.Of(actor).TryGetValue("OriginalWall", out var ow) && ow is BoolPropertyData { Value: true };
                if (source == "abyss" && (family == "BP_DoorActor_Slide3" || !originalWall)) effective[0] = Red;
                if (vine) { effective[0] = Vine; effective[1] = Vine; }
            }
        }
        else if (!componentName.Contains("_DM") && family != "BP_DoorActor_Push" && vine)
        {
            if (meshName == "WoodDoor") { effective[0] = VineBase; effective[1] = VineBase; }
            else if (meshName == "Syouzi") { effective[0] = VineBase; effective[1] = VineBase; effective[2] = SyoujiVine; }
            else if (meshName == "Husuma") effective[0] = HusumaVine;
        }
        var targets = new SortedDictionary<int, string>();
        for (int i = 0; i < effective.Count; i++)
            if (materialSlots[i].Role != "preserve") targets[i] = effective[i];
        return (slots, targets);
    }

    // ---- entry point (scu_map_patch.district_look_data) ----

    public static (Asset Data, LookSummary? Summary) Apply(Asset data, string baseStage)
    {
        int level = HostComposer.LevelIds(data).Level;
        var styles = new Dictionary<int, string>();
        foreach (var p in ((LevelExport)data.Export(level)).Actors)
        {
            if (p.Index <= 0) continue;
            var item = data.Export(p.Index);
            var m = Pattern.Match(item.ObjectName.ToString());
            if (m.Success && item.GetType() == typeof(NormalExport) && item.OuterIndex.Index == level && LookStages[m.Groups[1].Value] != baseStage)
                styles[p.Index] = LookStages[m.Groups[1].Value];
        }
        if (styles.Count == 0) return (data, null);

        var state = new State(data, baseStage, styles);
        state.Run();
        if (!state.HelperInstalled) return (data, null);
        return (data, new LookSummary(styles.Count,
            new SortedDictionary<string, int>(styles.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal),
            state.ChangedDoorLeaves, state.ChangedDoorWalls, state.ChangedFurniture, state.RuntimeTargets.Values.Sum(t => t.Count), 0.25));
    }

    sealed class State
    {
        readonly Asset _a;
        readonly string _baseStage;
        readonly Dictionary<int, string> _styles;
        readonly int _nativeStage;
        readonly int _oldExportCount;
        readonly Dictionary<(string, string, string), int> _importLookup = new();
        readonly Dictionary<int, string?> _paths = new();
        public SortedDictionary<int, SortedDictionary<int, string>> RuntimeTargets = new();
        public bool HelperInstalled;
        public int ChangedDoorLeaves, ChangedDoorWalls, ChangedFurniture;

        public State(Asset a, string baseStage, Dictionary<int, string> styles)
        {
            _a = a; _baseStage = baseStage; _styles = styles;
            _nativeStage = NativeStages[baseStage];
            _oldExportCount = a.Exports.Count;
        }

        string? PathOf(int r)
        {
            if (r == 0) return null;
            if (_paths.TryGetValue(r, out var cached)) return cached;
            return _paths[r] = _a.Path(r);
        }

        void AddName(string n) => DonorCallbacks.AddNames(_a, n);

        int MaterialRef(string path)
        {
            var dot = path.LastIndexOf('.');
            string package = path[..dot], objectName = path[(dot + 1)..];
            var key = (path, "/Script/Engine", "Material");
            if (_importLookup.TryGetValue(key, out int hit)) return hit;
            var packageKey = (package, "/Script/CoreUObject", "Package");
            if (!_importLookup.TryGetValue(packageKey, out int outer))
            {
                _a.Imports.Add(new Import(Names.Make(_a, "/Script/CoreUObject"), Names.Make(_a, "Package"), new FPackageIndex(0), Names.Make(_a, package), false));
                outer = -_a.Imports.Count;
                _importLookup[packageKey] = outer;
                _paths[outer] = package;
                foreach (var n in new[] { package, "/Script/CoreUObject", "Package" }) AddName(n);
            }
            _a.Imports.Add(new Import(Names.Make(_a, "/Script/Engine"), Names.Make(_a, "Material"), new FPackageIndex(outer), Names.Make(_a, objectName), false));
            int r = -_a.Imports.Count;
            _importLookup[key] = r;
            _paths[r] = path;
            foreach (var n in new[] { objectName, "/Script/Engine", "Material" }) AddName(n);
            return r;
        }

        static List<ObjectPropertyData> OverrideValues(NormalExport export, out ArrayPropertyData? prop)
        {
            var overrides = export.Data.Where(p => p.Name.ToString() == "OverrideMaterials").ToList();
            Require(overrides.Count <= 1, "duplicate OverrideMaterials");
            prop = overrides.Count == 1 ? overrides[0] as ArrayPropertyData : null;
            Require(overrides.Count == 0 || (prop != null && prop.ArrayType.ToString() == "ObjectProperty"), "unsupported material array");
            var values = prop?.Value.Select(v => v as ObjectPropertyData ?? throw new InvalidOperationException("unsupported material slot")).ToList() ?? new List<ObjectPropertyData>();
            return values;
        }

        ArrayPropertyData EnsureOverrides(NormalExport export)
        {
            var existing = export.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "OverrideMaterials");
            if (existing != null) return existing;
            var created = new ArrayPropertyData(Names.Make(_a, "OverrideMaterials")) { ArrayType = Names.Make(_a, "ObjectProperty"), Value = Array.Empty<PropertyData>() };
            export.Data.Add(created);
            return created;
        }

        public void Run()
        {
            var districtComponents = new Dictionary<(int, string), int>();
            for (int id = 1; id <= _oldExportCount; id++)
            {
                var component = _a.Exports[id - 1];
                if (!_styles.ContainsKey(component.OuterIndex.Index)) continue;
                var key = (component.OuterIndex.Index, component.ObjectName.ToString());
                Require(districtComponents.TryAdd(key, id), "duplicate district component name");
            }
            for (int i = 0; i < _a.Imports.Count; i++)
            {
                var item = _a.Imports[i];
                _importLookup.TryAdd((PathOf(-(i + 1))!, item.ClassPackage.ToString(), item.ClassName.ToString()), -(i + 1));
            }
            var nativeDoorStates = new Dictionary<int, (string Family, int Manual)>();
            var original = _a.Exports.ToList();
            int originalImports = _a.Imports.Count;

            for (int index = 1; index <= _oldExportCount; index++)
            {
                var export = original[index - 1];
                var componentClass = PathOf(export.ClassIndex.Index);
                if (componentClass is not ("/Script/Engine.StaticMeshComponent" or "/Script/ApexDestruction.DestructibleComponent")) continue;
                int actorId = export.OuterIndex.Index;
                if (actorId <= 0) continue;
                var actor = (NormalExport)_a.Export(actorId);
                if (!_styles.TryGetValue(actorId, out var componentStyle)) continue;
                var actorClass = PathOf(actor.ClassIndex.Index)!;
                if (!DoorClasses.TryGetValue(actorClass, out var family)) continue;
                var componentName = export.ObjectName.ToString();
                if (!DoorComponents[family].TryGetValue(componentName, out var meshName)) continue;
                var expectedTemplate = actorClass + "." + componentName + "_GEN_VARIABLE";
                if (PathOf(export.TemplateIndex.Index) != expectedTemplate) continue;
                var actorProps = Props.Of(actor);
                if (actorProps.TryGetValue(componentName, out var pointer) && !(pointer is ObjectPropertyData op && op.Value.Index == index)) continue;
                bool isDestructible = componentName.Contains("_DM");
                var expectedClass = isDestructible ? "/Script/ApexDestruction.DestructibleComponent" : "/Script/Engine.StaticMeshComponent";
                if (componentClass != expectedClass) continue;
                string? activeFamily = null;
                if (family != "BP_DoorActor_Push")
                {
                    if (!nativeDoorStates.TryGetValue(actorId, out var st)) nativeDoorStates[actorId] = st = NativeDoorFamily(actor, _nativeStage);
                    activeFamily = st.Family;
                    var componentFamily = componentName.Contains("_DM_Vine") ? "vine" : "normal";
                    if (isDestructible && componentFamily != activeFamily) continue;   // destroyed by native BeginPlay
                }
                if (family == "BP_DoorActor_Push")
                {
                    int doorType = actorProps.TryGetValue("DoorType", out var dt) && dt is IntPropertyData dip ? dip.Value : 1;
                    if (doorType is not (1 or 2)) continue;
                    if (doorType == 2)
                        meshName = componentName switch { "Mesh_Wall" => "Zashikirou_Wall_Door", "Mesh_Door" => "Zashikirou_Door", _ => "Zashikirou_Door_DM" };
                }
                var values = OverrideValues((NormalExport)export, out _);
                var authored = values.Select(v => PathOf(v.Value.Index)).ToList();
                List<string?>? sourceAuthored = null;
                if (isDestructible && family != "BP_DoorActor_Push")
                {
                    var (sourceFamily, _) = NativeDoorFamily(actor, NativeStages[componentStyle]);
                    var pair = DistrictDmPairs[family].First(p => p.Pair0 == componentName || p.Pair1 == componentName);
                    var sourceName = sourceFamily == "vine" ? pair.Pair1 : pair.Pair0;
                    if (districtComponents.TryGetValue((actorId, sourceName), out int sourceId))
                    {
                        var sourceComponent = (NormalExport)original[sourceId - 1];
                        Require(PathOf(sourceComponent.ClassIndex.Index) == "/Script/ApexDestruction.DestructibleComponent"
                                && PathOf(sourceComponent.TemplateIndex.Index) == actorClass + "." + sourceName + "_GEN_VARIABLE", "district door source component mismatch");
                        sourceAuthored = OverrideValues(sourceComponent, out _).Select(v => PathOf(v.Value.Index)).ToList();
                    }
                }
                var (slots, targets) = DistrictDoorTargets(componentStyle, family, componentName, meshName, actor, authored, sourceAuthored);
                var before = targets.Keys.Select(slot => (slot < values.Count ? PathOf(values[slot].Value.Index) : null) is { Length: > 0 } p ? p : slots[slot].Path).ToList();
                var after = targets.Values.ToList();
                bool changed = !before.SequenceEqual(after);
                RuntimeTargets[index] = targets;
                if (!changed) continue;
                var refs = targets.ToDictionary(kv => kv.Key, kv => MaterialRef(kv.Value));
                var ne = (NormalExport)export;
                var arr = EnsureOverrides(ne);
                var newValues = arr.Value.Select(v => (ObjectPropertyData)v).ToList();
                for (int slot = newValues.Count; slot <= targets.Keys.Max(); slot++)
                {
                    newValues.Add(new ObjectPropertyData(FName.DefineDummy(_a.Raw, slot.ToString(), 0)) { Value = new FPackageIndex(0) });
                    AddName(slot.ToString());
                }
                foreach (var (slot, r) in refs) newValues[slot].Value = new FPackageIndex(r);
                arr.Value = newValues.Cast<PropertyData>().ToArray();
                foreach (var r in refs.Values)
                    if (!ne.CreateBeforeSerializationDependencies.Any(d => d.Index == r)) ne.CreateBeforeSerializationDependencies.Add(new FPackageIndex(r));
                ChangedDoorLeaves += componentName == "Mesh_Wall" || isDestructible ? 0 : 1;
                if (componentName == "Mesh_Wall") ChangedDoorWalls++;
                foreach (var n in new[] { "OverrideMaterials", "ArrayProperty", "ObjectProperty" }) AddName(n);
            }

            for (int index = 1; index <= _oldExportCount; index++)
            {
                var export = original[index - 1];
                if (PathOf(export.ClassIndex.Index) != "/Script/Engine.StaticMeshComponent") continue;
                int ownerId = export.OuterIndex.Index;
                if (ownerId < 1 || ownerId > _oldExportCount) continue;
                var owner = (NormalExport)_a.Export(ownerId);
                if (!_styles.TryGetValue(ownerId, out var componentStyle)) continue;
                var actorClass = PathOf(owner.ClassIndex.Index)!;
                var componentName = export.ObjectName.ToString();
                var template = PathOf(export.TemplateIndex.Index);
                var ne = (NormalExport)export;
                var meshes = ne.Data.Where(p => p.Name.ToString() == "StaticMesh").ToList();
                string meshName; int slot;
                if (actorClass == "/Script/Engine.StaticMeshActor")
                {
                    if (template != "/Script/Engine.Default__StaticMeshActor.StaticMeshComponent0") continue;
                    if (meshes.Count != 1) continue;
                    meshName = "lack"; slot = 0;
                }
                else
                {
                    if (!FurnitureComponents.TryGetValue(actorClass, out var comps) || !comps.TryGetValue(componentName, out var definition)
                        || template != actorClass + "." + componentName + "_GEN_VARIABLE") continue;
                    (meshName, slot) = definition;
                    var pointer = owner.Data.Where(p => p.Name.ToString() == componentName).ToList();
                    Require(pointer.Count <= 1 && (pointer.Count == 0 || (pointer[0] is ObjectPropertyData po && po.Value.Index == index)), "furniture owner pointer differs");
                    Require(meshes.Count <= 1, "duplicate furniture mesh");
                }
                var mesh = "/Game/Props/" + meshName + "." + meshName;
                if (meshes.Count > 0)
                {
                    Require(meshes[0] is ObjectPropertyData, "unexpected furniture mesh type");
                    if (PathOf(((ObjectPropertyData)meshes[0]).Value.Index) != mesh) continue;
                }
                var values = OverrideValues(ne, out _);
                var beforePath = (slot < values.Count ? PathOf(values[slot].Value.Index) : null) is { Length: > 0 } bp ? bp : Wood;
                // Drawer fronts and static shelves already carry the donor's authored materials. Only BP_Rack has a native stage setter.
                var target = componentStyle == "soul" && actorClass == "/Game/Blueprints/Items/BP_Rack.BP_Rack_C" ? VineBase : beforePath;
                if (_baseStage == "soul" && componentStyle != "soul" && actorClass == "/Game/Blueprints/Items/BP_Rack.BP_Rack_C")
                    RuntimeTargets[index] = new SortedDictionary<int, string> { [slot] = target };
                if (beforePath == target) continue;
                int r = MaterialRef(target);
                var arr = EnsureOverrides(ne);
                var newValues = arr.Value.Select(v => (ObjectPropertyData)v).ToList();
                for (int missing = newValues.Count; missing <= slot; missing++)
                {
                    newValues.Add(new ObjectPropertyData(FName.DefineDummy(_a.Raw, missing.ToString(), 0)) { Value = new FPackageIndex(0) });
                    AddName(missing.ToString());
                }
                newValues[slot].Value = new FPackageIndex(r);
                arr.Value = newValues.Cast<PropertyData>().ToArray();
                if (!ne.CreateBeforeSerializationDependencies.Any(d => d.Index == r)) ne.CreateBeforeSerializationDependencies.Add(new FPackageIndex(r));
                ChangedFurniture++;
                foreach (var n in new[] { "OverrideMaterials", "ArrayProperty", "ObjectProperty" }) AddName(n);
            }
            Compose.UpdateGenerations(_a);
            if (RuntimeTargets.Count > 0) HelperInstalled = StyleRuntime.Install(_a, RuntimeTargets);
        }
    }
}
