using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using Mendix.StudioPro.ExtensionsAPI.Model.Projects;
using Mendix.StudioPro.ExtensionsAPI.Services;
using Mendix.StudioPro.ExtensionsAPI.UI.Events;
using Mendix.StudioPro.ExtensionsAPI.UI.Menu;

namespace DocHighlighter;

/// <summary>
/// Adds "Extensions > Document Highlighter" to the menu bar. Studio Pro asks menu extensions for their
/// menus when an app opens, which starts the painter and the App Explorer context menu.
/// </summary>
/// <remarks>
/// The App Explorer "Highlight" submenu is not built with ContextMenuExtension: Studio Pro 10.24 only offers
/// that for units with a public API model (pages, microflows, ...), not nanoflows, snippets, folders, modules, etc.
/// See <see cref="AppExplorerContextMenu"/>.
/// </remarks>
[Export(typeof(MenuExtension))]
[method: ImportingConstructor]
public sealed class HighlightMenuExtension(ILogService log, IUntypedModelAccessService untypedModelAccess) : MenuExtension
{
    ModuleIndex? modules;

    public override IEnumerable<MenuViewModel> GetMenus()
    {
        if (modules is null)
        {
            modules = new ModuleIndex(() => CurrentApp, untypedModelAccess);
            Subscribe<ExtensionUnloading>(AppExplorerPainter.Shutdown);
        }
        AppExplorerPainter.EnsureStarted(AppDirectory, modules, log);

        yield return new MenuViewModel("Document Highlighter", new[]
        {
            new MenuViewModel("Remove all highlights in this app", ClearAll),
            new MenuViewModel("Open highlights folder", OpenFolder),
        });
    }

    void ClearAll()
    {
        var appDirectory = AppDirectory();
        if (appDirectory is null) return;
        try
        {
            HighlightStore.ForApp(appDirectory, log).Clear();
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: could not clear highlights", ex);
        }
        AppExplorerPainter.RequestRepaint();
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(HighlightStore.StorageRoot);
            Process.Start("explorer.exe", HighlightStore.StorageRoot);
        }
        catch (Exception ex)
        {
            log.Error("DocHighlighter: could not open highlights folder", ex);
        }
    }

    string? AppDirectory() => (CurrentApp?.Root as IProject)?.DirectoryPath;
}
