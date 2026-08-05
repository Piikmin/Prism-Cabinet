using System;
using System.Collections.Generic;
using System.Linq;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Associations animation ↔ option retenues par l'utilisateur.
/// </summary>
/// <remarks>
/// Les clés sont composées du dossier du mod, du groupe et de l'option - le dossier étant
/// l'identifiant stable côté Penumbra. Une association posée sans groupe ni option vaut pour le
/// mod entier, ce qui couvre les mods d'animation sans options.
/// </remarks>
public sealed class AnimationBindingStore
{
    private const char Separator = '\u001F';

    private readonly Configuration configuration;

    public AnimationBindingStore(Configuration configuration)
    {
        this.configuration = configuration;

        // Une fois au démarrage : la reprise était jusqu'ici tentée à chaque accès, donc à
        // chaque image de dessin.
        this.Migrate();
    }

    /// <summary>
    /// Associations applicables, de la plus précise à la plus large : cette option, puis le mod.
    /// </summary>
    public IReadOnlyList<AnimationBinding> Resolve(ModInfo mod, string? group, string? option)
    {
        if (group is not null && option is not null
                              && this.configuration.AnimationBindingSets.TryGetValue(Key(mod, group, option), out var precise))
        {
            return precise;
        }

        return this.configuration.AnimationBindingSets.TryGetValue(Key(mod, null, null), out var wide) ? wide : [];
    }

    public bool Has(ModInfo mod, string? group, string? option)
    {
        return this.configuration.AnimationBindingSets.ContainsKey(Key(mod, group, option));
    }

    /// <summary>Ajoute une animation sans écraser celles déjà associées.</summary>
    public void Add(ModInfo mod, string? group, string? option, AnimationBinding binding)
    {
        var key = Key(mod, group, option);
        if (!this.configuration.AnimationBindingSets.TryGetValue(key, out var bindings))
        {
            bindings = [];
            this.configuration.AnimationBindingSets[key] = bindings;
        }

        if (bindings.Any(b => b.TimelineId == binding.TimelineId))
            return;

        bindings.Add(binding);
        this.configuration.Save();
    }

    public void Remove(ModInfo mod, string? group, string? option, uint timelineId)
    {
        var key = Key(mod, group, option);
        if (!this.configuration.AnimationBindingSets.TryGetValue(key, out var bindings))
            return;

        if (bindings.RemoveAll(b => b.TimelineId == timelineId) == 0)
            return;

        if (bindings.Count == 0)
            this.configuration.AnimationBindingSets.Remove(key);

        this.configuration.Save();
    }

    public void Clear(ModInfo mod, string? group, string? option)
    {
        if (this.configuration.AnimationBindingSets.Remove(Key(mod, group, option)))
            this.configuration.Save();
    }

    /// <summary>
    /// Toutes les associations enregistrées, décomposées pour la vue de gestion. Inclut celles
    /// dont le mod a depuis été supprimé de Penumbra : c'est justement ce que cette vue doit
    /// permettre de repérer et de nettoyer.
    /// </summary>
    public IReadOnlyList<AnimationBindingEntry> AllDeclarations()
    {
        var result = new List<AnimationBindingEntry>();

        foreach (var (key, bindings) in this.configuration.AnimationBindingSets)
        {
            if (bindings.Count == 0)
                continue;

            var parts = key.Split(Separator);
            var directory = parts.Length > 0 ? parts[0] : string.Empty;
            var group = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            var option = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

            // Copie : sinon retirer une association en cours d'affichage modifierait la liste que
            // la fenêtre de gestion est en train de parcourir.
            result.Add(new AnimationBindingEntry(directory, group, option, [.. bindings]));
        }

        return result;
    }

    /// <summary>
    /// Retire toutes les associations dont le mod n'existe plus. Renvoie le nombre d'associations
    /// retirées.
    /// </summary>
    public int RemoveOrphaned(Func<string, bool> modExists)
    {
        var orphaned = this.configuration.AnimationBindingSets.Keys
            .Where(key => !modExists(key.Split(Separator)[0]))
            .ToList();

        foreach (var key in orphaned)
            this.configuration.AnimationBindingSets.Remove(key);

        if (orphaned.Count > 0)
            this.configuration.Save();

        return orphaned.Count;
    }

    /// <summary>Reprend les associations uniques enregistrées par la version précédente.</summary>
    private void Migrate()
    {
        if (this.configuration.AnimationBindings.Count == 0)
            return;

        foreach (var (key, binding) in this.configuration.AnimationBindings)
        {
            if (!this.configuration.AnimationBindingSets.ContainsKey(key))
                this.configuration.AnimationBindingSets[key] = [binding];
        }

        this.configuration.AnimationBindings.Clear();
        this.configuration.Save();
    }

    /// <summary>
    /// Le séparateur est un caractère de contrôle : un nom d'option peut contenir à peu près
    /// n'importe quoi, y compris des barres verticales et des crochets.
    /// </summary>
    private static string Key(ModInfo mod, string? group, string? option)
        => string.Join(Separator, mod.Directory, group ?? string.Empty, option ?? string.Empty);
}
