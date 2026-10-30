using System;
using System.Collections.Generic;
using System.Linq;
using Mendix.StudioPro.ExtensionsAPI.Model;
using Mendix.StudioPro.ExtensionsAPI.Model.UntypedModel;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Answers "which units belong to this module or folder" through the untyped model API, which (unlike the typed API)
/// covers every unit type: nanoflows, snippets, folders, domain model, module settings, ...
/// Read-only; it never changes the model.
/// </summary>
internal sealed class ModuleIndex(Func<IModel?> getModel, IUntypedModelAccessService untypedModelAccess)
{
    const string ModuleType = "Projects$Module";
    const string FolderType = "Projects$Folder";

    /// <summary>Module id to module name, for all modules in the current app.</summary>
    public Dictionary<string, string> GetModules()
    {
        var root = Root();
        if (root is null) return new();
        return root.GetUnitsOfType(ModuleType)
            .Select(module => (Id: IdUtil.Normalize(module.ID), module.Name))
            .Where(m => m.Id != null)
            .GroupBy(m => m.Id!)
            .ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ids of every module and every folder (at any depth): the App Explorer items that hold other items and so get
    /// counts, a flag and bulk removal. The App and System rows are not among them.
    /// </summary>
    public HashSet<string> GetContainerIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Root() is not { } root) return ids;
        foreach (var unit in root.GetUnitsOfType(ModuleType).Concat(root.GetUnitsOfType(FolderType)))
            if (IdUtil.Normalize(unit.ID) is { } id)
                ids.Add(id);
        return ids;
    }

    /// <summary>Ids of the module or folder itself and of every unit nested in it (folders, documents, domain model, ...).</summary>
    public HashSet<string> GetUnitIdsIn(string containerId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Root() is not { } root) return ids;
        var container = root.GetUnitsOfType(ModuleType).Concat(root.GetUnitsOfType(FolderType))
            .FirstOrDefault(u => string.Equals(IdUtil.Normalize(u.ID), containerId, StringComparison.OrdinalIgnoreCase));
        if (container is null) return ids;

        var pending = new Stack<IModelUnit>();
        pending.Push(container);
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
    /// Ids of everything in the modules or folders: the containers themselves, their model units, and the highlighted
    /// files (items that are not model units, such as Maia skills) recorded as being in one of these modules.
    /// </summary>
    public HashSet<string> GetIdsIn(IReadOnlyCollection<string> containerIds, HighlightStore store)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var containerId in containerIds)
            ids.UnionWith(GetUnitIdsIn(containerId));
        ids.UnionWith(store.IdsInModules(containerIds.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        return ids;
    }

    IModelRoot? Root()
    {
        var model = getModel();
        return model is null ? null : untypedModelAccess.GetUntypedModel(model);
    }
}
