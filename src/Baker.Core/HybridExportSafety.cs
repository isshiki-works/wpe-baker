using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Pure checks shared by hybrid export and paired candidate validation.</summary>
internal static class HybridExportSafety
{
    internal static JsonArray MergeRuntimeDependencies(JsonArray first, JsonArray second)
    {
        var merged = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in first.Concat(second))
            if (dependency is not null && seen.Add(dependency.ToJsonString())) merged.Add(dependency.DeepClone());
        return merged;
    }

    internal static void GuardPublicLayerQueries(IEnumerable<JsonObject> originalObjects,
        IEnumerable<JsonObject> finalObjects, JsonArray dependencies)
    {
        var original = originalObjects.Select(HybridScenePlanner.Id).ToArray();
        var final = finalObjects.ToArray();
        var finalById = final.ToDictionary(HybridScenePlanner.Id);
        foreach (var query in dependencies.OfType<JsonObject>().Where(dependency =>
            dependency["operation"]?.GetValue<string>() == "query" && dependency["property"]?.GetValue<string>() is
                "layer_numeric_index" or "layer_count" or "layer_enumeration" or "layer_index" or "layer_order"))
        {
            int? owner = HybridScenePlanner.Int(query["owner"]);
            if (owner is not int id || !finalById.TryGetValue(id, out var objectWithScript) ||
                !SceneAnalyzer.Walk(objectWithScript).OfType<JsonObject>().Any(node => node["script"] is JsonValue)) continue;
            string property = query["property"]!.GetValue<string>();
            if ((property == "layer_count" ? original.Length != final.Length : !original.SequenceEqual(final.Select(HybridScenePlanner.Id))) &&
                !SelfAnchoredInsert(originalObjects, finalById, query, property))
                throw new InvalidDataException($"A retained script queried {property}, but hybrid export changes the public layer {(property == "layer_count" ? "count" : "order")}. Re-analyze without baking layers that alter this script's public layer view.");
        }
    }

    private static bool SelfAnchoredInsert(IEnumerable<JsonObject> originalObjects, IReadOnlyDictionary<int, JsonObject> final,
        JsonObject query, string property)
    {
        if (property is not ("layer_index" or "layer_order") || query["initialization"]?.GetValue<bool>() != true ||
            HybridScenePlanner.Int(query["owner"]) is not int owner || query["binding"]?.GetValue<string>() is not string binding) return false;
        JsonObject? source = originalObjects.FirstOrDefault(obj => HybridScenePlanner.Id(obj) == owner);
        if (source is null || !final.TryGetValue(owner, out var candidate) || HybridScenePlanner.Int(source["parent"]) != HybridScenePlanner.Int(candidate["parent"])) return false;
        string? sourceScript = source[binding]?["script"]?.GetValue<string>();
        return sourceScript is not null && sourceScript == candidate[binding]?["script"]?.GetValue<string>() && IsSelfAnchoredInsert(sourceScript);
    }

    private static bool IsSelfAnchoredInsert(string code)
    {
        var tokens = new List<string>();
        for (int i = 0; i < code.Length;)
        {
            char c = code[i]; if (char.IsWhiteSpace(c)) { ++i; continue; } if (c == '`') return false;
            if (c is '\'' or '"') { char quote = c; ++i; while (i < code.Length && code[i] != quote) { if (code[i++] == '\\' && i < code.Length) ++i; } if (i == code.Length) return false; ++i; tokens.Add("$literal"); continue; }
            if (c == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*')) { bool line = code[i + 1] == '/'; i += 2; if (line) while (i < code.Length && code[i] is not '\r' and not '\n') ++i; else { while (i + 1 < code.Length && (code[i] != '*' || code[i + 1] != '/')) ++i; if (i + 1 >= code.Length) return false; i += 2; } continue; }
            if (c == '/' && (tokens.Count == 0 || tokens[^1] is "=" or "(" or "[" or "{" or "," or ":" or ";" or "!")) { ++i; while (i < code.Length && code[i] != '/') { if (code[i++] == '\\' && i < code.Length) ++i; } if (i == code.Length) return false; ++i; while (i < code.Length && char.IsLetter(code[i])) ++i; tokens.Add("$literal"); continue; }
            if (char.IsLetter(c) || c is '_' or '$') { int start = i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$')) ++i; tokens.Add(code[start..i]); continue; }
            if (char.IsDigit(c)) { int start = i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.')) ++i; tokens.Add(code[start..i]); continue; }
            if (c == '=' && i + 1 < code.Length && code[i + 1] == '>') { tokens.Add("=>"); i += 2; continue; }
            if ("(){};,.=<>+-*![]:?/".Contains(c)) { tokens.Add(c.ToString()); ++i; continue; } return false;
        }
        bool At(int at, params string[] expected) => at + expected.Length <= tokens.Count && expected.Select((value, offset) => tokens[at + offset] == value).All(match => match);
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "eval", "Function", "Reflect", "Proxy", "with", "globalThis", "constructor", "__proto__", "prototype", "import" }; if (tokens.Any(forbidden.Contains)) return false;
        int[] depth = new int[tokens.Count]; int level = 0; for (int i = 0; i < tokens.Count; ++i) { depth[i] = level; if (tokens[i] == "{") ++level; else if (tokens[i] == "}" && --level < 0) return false; } if (level != 0) return false;
        int Match(int open, string left, string right) { int nesting = 0; for (int i = open; i < tokens.Count; ++i) { if (tokens[i] == left) ++nesting; else if (tokens[i] == right && --nesting == 0) return i; } return -1; }
        int init = -1, initBody = -1, initEnd = -1; for (int i = 0; i < tokens.Count; ++i) { if (!At(i, "function", "init", "(")) continue; int close = Match(i + 2, "(", ")"); if (close != i + 3 || close + 1 >= tokens.Count || tokens[close + 1] != "{" || init >= 0) return false; init = i; initBody = close + 2; initEnd = Match(close + 1, "{", "}"); if (initEnd < 0) return false; } if (init < 0) return false;
        int anchor = -1, bar = -1, create = -1, sort = -1; for (int i = initBody; i < initEnd; ++i) { if (tokens[i] is "function" or "async" or "=>" or "setTimeout" or "setInterval") return false; if (At(i, "let") && i + 9 < initEnd && depth[i] == depth[initBody] && At(i + 2, "=", "thisScene", ".", "getLayerIndex", "(", "thisLayer", ")", ";")) { if (anchor >= 0) return false; anchor = i + 1; } if (At(i, "let") && i + 9 < initEnd && At(i + 2, "=", "thisScene", ".", "createLayer", "(", "$literal", ")", ";")) { if (bar >= 0) return false; bar = i + 1; create = i; } if (At(i, "thisScene", ".", "sortLayer", "(")) { if (sort >= 0) return false; sort = i; } }
        if (anchor < 0 || bar < 0 || sort < 0 || anchor >= create || create >= sort || depth[create] != depth[sort] || !At(sort, "thisScene", ".", "sortLayer", "(", tokens[bar], ",", tokens[anchor], ")", ";")) return false;
        for (int i = 0; i < tokens.Count; ++i) { if (tokens[i] == "thisScene" && i != anchor + 2 && i != create + 3 && i != sort) return false; if (tokens[i] == "thisLayer" && i + 1 < tokens.Count && tokens[i + 1] == "=") return false; if (i + 1 < tokens.Count && (tokens[i] is "let" or "const" or "var") && tokens[i + 1] == "thisLayer") return false; if (tokens[i] == tokens[anchor] && i != anchor && i != sort + 6) return false; }
        for (int i = create + 10; i < sort;) { if (!At(i, tokens[bar], ".") || i + 3 >= sort || tokens[i + 3] != "=") return false; int start = i; while (i < sort && tokens[i] != ";") { if (i > start && tokens[i] == tokens[bar]) return false; ++i; } if (i++ >= sort) return false; }
        return true;
    }

    internal static JsonObject ClassifyLateSourceScriptErrors(JsonArray errors, IReadOnlySet<int> groupLayers,
        IReadOnlySet<int> liveLayerIds, IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        var protectedLayers = ProtectedLayers(groupLayers, sourceObjects);
        var unsafeErrors = new JsonArray();
        var externalLiveErrors = new JsonArray();
        foreach (var error in errors.OfType<JsonObject>())
        {
            if (HybridScenePlanner.Int(error["owner_layer_id"]) is not int owner || !sourceObjects.ContainsKey(owner))
                throw new InvalidDataException("A full capture source script fault lacks a known authored owner.");
            string? reason = groupLayers.Contains(owner) ? "source_script_fault_in_baked_layer" :
                protectedLayers.Contains(owner) ? "source_script_fault_in_retained_ancestor" :
                !liveLayerIds.Contains(owner) ? "source_script_fault_in_unretained_layer" : null;
            var copy = error.DeepClone().AsObject();
            if (reason is null) externalLiveErrors.Add(copy);
            else { copy["rejection_reason"] = reason; unsafeErrors.Add(copy); }
        }
        return new JsonObject { ["unsafe"] = unsafeErrors, ["external_live"] = externalLiveErrors };
    }

    internal static JsonArray LateExternalDependencies(JsonObject master, IReadOnlySet<int> groupLayers,
        IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        if (master["native_result"]?["runtime_dependencies"] is not JsonArray dependencies)
            throw new InvalidDataException("A full group capture did not return the requested runtime dependency trace.");
        var protectedLayers = ProtectedLayers(groupLayers, sourceObjects);
        var unsafeDependencies = new JsonArray();
        foreach (var dependency in dependencies.OfType<JsonObject>())
        {
            int? owner = HybridScenePlanner.Int(dependency["owner"]), target = HybridScenePlanner.Int(dependency["target"]);
            string? operation = dependency["operation"]?.GetValue<string>();
            string? reason = null;
            if (operation == "input" && owner is int inputOwner && protectedLayers.Contains(inputOwner) &&
                dependency["property"]?.GetValue<string>() is "wall_clock" or "audio" or "pointer" or "media")
                reason = groupLayers.Contains(inputOwner) ? "external_input_to_baked_layer" : "external_input_to_retained_ancestor";
            if (operation == "write" && dependency["initialization"]?.GetValue<bool>() != true && target is int written)
            {
                bool groupWriter = owner is int writer && groupLayers.Contains(writer);
                if (!groupWriter && protectedLayers.Contains(written))
                    reason = groupLayers.Contains(written) ? "external_controller_write_to_baked_layer" : "external_controller_write_to_retained_ancestor";
                else if (groupWriter && !groupLayers.Contains(written) && sourceObjects.ContainsKey(written))
                    reason = protectedLayers.Contains(written) ? "baked_controller_write_to_retained_ancestor" : "baked_controller_write_to_external_layer";
            }
            if (reason is null) continue;
            var rejected = dependency.DeepClone().AsObject();
            rejected["rejection_reason"] = reason;
            unsafeDependencies.Add(rejected);
        }
        return unsafeDependencies;
    }

    private static HashSet<int> ProtectedLayers(IReadOnlySet<int> groupLayers,
        IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        var protectedLayers = groupLayers.ToHashSet();
        foreach (int layer in groupLayers)
        {
            int id = layer;
            var seen = new HashSet<int>();
            while (sourceObjects.TryGetValue(id, out var obj) && HybridScenePlanner.Int(obj["parent"]) is int parent && sourceObjects.ContainsKey(parent))
            {
                if (!seen.Add(id)) throw new InvalidDataException("Scene parent cycle during capture dependency validation.");
                protectedLayers.Add(parent);
                id = parent;
            }
        }
        return protectedLayers;
    }
}
