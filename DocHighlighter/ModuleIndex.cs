using System;
using System.Collections.Generic;
using System.Linq;
using Mendix.StudioPro.ExtensionsAPI.Model;
using Mendix.StudioPro.ExtensionsAPI.Model.UntypedModel;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Answers "which units belong to this module" through the untyped model API, which (unlike the typed API)
/// covers every unit type: nanoflows, snippets, folders, domain model, module settings, ...
/// Read-only; it never changes the model.
/// </summary>
internal sealed class ModuleIndex(Func<IModel?> getModel, IUntypedModelAccessService untypedModelAccess)
{
    /// <summary>Module id to module name, for all modules in the current app.</summary>
    public Dictionary<string, string> GetModules()
    {
        var root = Root();
        if (root is null) return new();
        return root.GetUnitsOfType("Projects$Module")
            .Select(module => (Id: IdUtil.Normalize(module.ID), module.Name))
            .Where(m => m.Id != null)
            .GroupBy(m => m.Id!)
            .ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Ids of the module itself and of every unit nested in it (folders, documents, domain model, ...).</summary>
    public HashSet<string> GetUnitIdsInModule(string moduleId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var module = Root()?.GetUnitsOfType("Projects$Module")
            .FirstOrDefault(m => string.Equals(IdUtil.Normalize(m.ID), moduleId, StringComparison.OrdinalIgnoreCase));
        if (module is null) return ids;

        var pending = new Stack<IModelUnit>();
        pending.Push(module);
        while (pending.Count > 0)
        {
            var unit = pending.Pop();
            if (IdUtil.Normalize(unit.ID) is { } id && !ids.Add(id)) continue;
            foreach (var child in unit.GetUnits())
                pending.Push(child);
        }
        return ids;
    }

    /// <summary>
    /// Ids of everything in the modules: the modules themselves, their model units, and the highlighted files
    /// (items that are not model units, such as Maia skills) recorded as being in them.
    /// </summary>
    public HashSet<string> GetIdsInModules(IReadOnlyCollection<string> moduleIds, HighlightStore store)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleId in moduleIds)
            ids.UnionWith(GetUnitIdsInModule(moduleId));
        ids.UnionWith(store.IdsInModules(moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        return ids;
    }

    IModelRoot? Root()
    {
        var model = getModel();
        return model is null ? null : untypedModelAccess.GetUntypedModel(model);
    }
}
