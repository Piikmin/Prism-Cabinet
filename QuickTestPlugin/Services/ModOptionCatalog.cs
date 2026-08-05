using System.Collections.Generic;
using System.Linq;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Options disponibles par mod, et choix mémorisés de l'utilisateur.
/// </summary>
/// <remarks>
/// Les choix sont indexés par dossier de mod : c'est l'identifiant stable côté Penumbra, alors
/// que le nom affiché peut être renommé par l'utilisateur.
/// </remarks>
public sealed class ModOptionCatalog
{
    private readonly PenumbraIntegration penumbra;
    private readonly Configuration configuration;

    private readonly Dictionary<string, IReadOnlyList<ModOptionGroup>> groupCache = [];

    public ModOptionCatalog(PenumbraIntegration penumbra, Configuration configuration)
    {
        this.penumbra = penumbra;
        this.configuration = configuration;
    }

    /// <summary>Groupes d'options d'un mod. Le résultat est mis en cache : l'IPC serait
    /// autrement interrogé à chaque frame de dessin.</summary>
    public IReadOnlyList<ModOptionGroup> GroupsFor(ModInfo mod)
    {
        if (this.groupCache.TryGetValue(mod.Directory, out var cached))
            return cached;

        var groups = this.penumbra.GetAvailableSettings(mod);
        this.groupCache[mod.Directory] = groups;
        return groups;
    }

    public void ClearCache() => this.groupCache.Clear();

    /// <summary>Vrai si l'utilisateur a explicitement réglé ce groupe.</summary>
    public bool IsUserControlled(ModInfo mod, string group)
        => this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups)
           && groups.ContainsKey(group);

    /// <summary>Options retenues par l'utilisateur pour ce groupe, vide s'il n'a rien réglé.</summary>
    public IReadOnlyList<string> SelectionFor(ModInfo mod, string group)
        => this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups)
           && groups.TryGetValue(group, out var options)
               ? options
               : [];

    public void Select(ModInfo mod, string group, IEnumerable<string> options)
    {
        if (!this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups))
        {
            groups = [];
            this.configuration.ModOptions[mod.Directory] = groups;
        }

        groups[group] = [.. options];
        this.configuration.Save();
    }

    /// <summary>Rend le groupe à la gestion automatique.</summary>
    public void ClearGroup(ModInfo mod, string group)
    {
        if (!this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups))
            return;

        if (!groups.Remove(group))
            return;

        if (groups.Count == 0)
            this.configuration.ModOptions.Remove(mod.Directory);

        this.configuration.Save();
    }

    public void ClearMod(ModInfo mod)
    {
        if (this.configuration.ModOptions.Remove(mod.Directory))
            this.configuration.Save();
    }

    public bool HasSelection(ModInfo mod)
        => this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups) && groups.Count > 0;

    /// <summary>Choix de l'utilisateur, sous la forme attendue par l'IPC Penumbra.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> OverridesFor(ModInfo mod)
        => this.configuration.ModOptions.TryGetValue(mod.Directory, out var groups)
               ? groups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value)
               : new Dictionary<string, IReadOnlyList<string>>();
}
