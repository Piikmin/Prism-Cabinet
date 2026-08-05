using System;
using System.Collections.Generic;
using System.Linq;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Mods qu'il faut activer en même temps qu'un autre pour qu'il produise son effet.
/// </summary>
/// <remarks>
/// Penumbra n'a pas de notion de dépendance : un mod de vêtement exigeant un corps particulier
/// ne l'indique que dans sa description, en texte libre. Plutôt que de deviner, on retient ce
/// que l'utilisateur établit - mêmes principes que les associations d'animation : indexé sur le
/// dossier, qui est l'identifiant stable.
/// </remarks>
public sealed class PrerequisiteStore
{
    private readonly Configuration configuration;
    private readonly ModCatalog catalog;

    public PrerequisiteStore(Configuration configuration, ModCatalog catalog)
    {
        this.configuration = configuration;
        this.catalog = catalog;
    }

    /// <summary>Dossiers des prérequis déclarés pour ce mod.</summary>
    public IReadOnlyList<string> DirectoriesFor(ModInfo mod)
        => this.configuration.ModPrerequisites.TryGetValue(mod.Directory, out var required) ? required : [];

    /// <summary>
    /// Prérequis résolus en mods connus de Penumbra. Un prérequis supprimé entre-temps est
    /// simplement ignoré : il ne doit pas empêcher le test.
    /// </summary>
    public IReadOnlyList<ModInfo> For(ModInfo mod)
    {
        var directories = this.DirectoriesFor(mod);
        if (directories.Count == 0)
            return [];

        return [.. this.catalog.All.Where(m => directories.Contains(m.Directory, StringComparer.Ordinal))];
    }

    public bool Has(ModInfo mod, string directory)
        => this.DirectoriesFor(mod).Contains(directory, StringComparer.Ordinal);

    public void Add(ModInfo mod, ModInfo prerequisite)
    {
        // Un mod ne peut pas être son propre prérequis : la boucle serait appliquée deux fois.
        if (prerequisite.Directory == mod.Directory)
            return;

        if (!this.configuration.ModPrerequisites.TryGetValue(mod.Directory, out var required))
        {
            required = [];
            this.configuration.ModPrerequisites[mod.Directory] = required;
        }

        if (required.Contains(prerequisite.Directory, StringComparer.Ordinal))
            return;

        required.Add(prerequisite.Directory);
        this.configuration.Save();
    }

    public void Remove(ModInfo mod, string directory)
    {
        if (!this.configuration.ModPrerequisites.TryGetValue(mod.Directory, out var required))
            return;

        if (!required.Remove(directory))
            return;

        if (required.Count == 0)
            this.configuration.ModPrerequisites.Remove(mod.Directory);

        this.configuration.Save();
    }

    /// <summary>
    /// Tous les prérequis déclarés, sans résolution. Inclut ceux dont le mod propriétaire ou le
    /// prérequis lui-même ont depuis disparu de Penumbra : la vue de gestion doit pouvoir les
    /// repérer pour les nettoyer, pas seulement ceux qui fonctionnent encore.
    /// </summary>
    public IReadOnlyList<(string ModDirectory, IReadOnlyList<string> Prerequisites)> AllDeclarations()
        // Copie : sinon retirer un prérequis en cours d'affichage modifierait la liste que la
        // vue de gestion est en train de parcourir.
        => [.. this.configuration.ModPrerequisites
                  .Where(kv => kv.Value.Count > 0)
                  .Select(kv => (kv.Key, (IReadOnlyList<string>)[.. kv.Value]))];

    /// <summary>
    /// Retire tous les prérequis dont le mod propriétaire ou le prérequis lui-même n'existe plus.
    /// Renvoie le nombre d'entrées retirées.
    /// </summary>
    public int RemoveOrphaned(Func<string, bool> modExists)
    {
        var removed = 0;

        foreach (var owner in this.configuration.ModPrerequisites.Keys.ToList())
        {
            if (!modExists(owner))
            {
                removed += this.configuration.ModPrerequisites[owner].Count;
                this.configuration.ModPrerequisites.Remove(owner);
                continue;
            }

            var list = this.configuration.ModPrerequisites[owner];
            var before = list.Count;
            list.RemoveAll(directory => !modExists(directory));
            removed += before - list.Count;

            if (list.Count == 0)
                this.configuration.ModPrerequisites.Remove(owner);
        }

        if (removed > 0)
            this.configuration.Save();

        return removed;
    }
}
