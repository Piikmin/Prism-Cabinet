using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Liste des mods Penumbra, rangée et filtrable.
/// </summary>
/// <remarks>
/// Le classement reprend l'arborescence que l'utilisateur a construite dans Penumbra plutôt que
/// d'en imposer une autre. La nature de chaque mod est déduite des objets qu'il modifie, obtenus
/// en un seul appel pour tout le catalogue - les interroger un par un demanderait des centaines
/// d'allers-retours. L'ensemble n'est recalculé que sur demande explicite.
/// </remarks>
public sealed class ModCatalog
{
    private readonly PenumbraIntegration penumbra;
    private readonly EquipResolver equipResolver;
    private readonly Configuration configuration;
    private readonly DiagnosticLog log;

    private IReadOnlyList<ModEntry> mods = [];
    private IReadOnlyList<ModInfo> allCache = [];
    private Dictionary<string, ModInfo> byDirectory = new(StringComparer.Ordinal);
    private IReadOnlyList<ModEntry> filtered = [];
    private ModFolderNode filteredTree = new(string.Empty, string.Empty);
    private string filter = string.Empty;

    public ModCatalog(
        PenumbraIntegration penumbra,
        EquipResolver equipResolver,
        Configuration configuration,
        DiagnosticLog log)
    {
        this.penumbra = penumbra;
        this.equipResolver = equipResolver;
        this.configuration = configuration;
        this.log = log;
    }

    public bool IsLoaded { get; private set; }

    public int Count => this.mods.Count;

    /// <summary>
    /// Tous les mods connus, indépendamment du filtre d'affichage.
    /// </summary>
    /// <remarks>
    /// Calculé une fois par <see cref="Refresh"/> plutôt qu'à chaque accès : cette liste est
    /// consultée à chaque image tant qu'un mod ayant des prérequis est sélectionné, et une
    /// projection LINQ à cet endroit allouait un tableau de plusieurs centaines d'éléments par
    /// image pour rien.
    /// </remarks>
    public IReadOnlyList<ModInfo> All => this.allCache;

    /// <summary>
    /// Tous les mods connus, non filtrés, avec leur chemin de tri complet. Sert au nettoyage des
    /// dossiers vides, qui doit vérifier l'occupation réelle indépendamment du filtre affiché.
    /// </summary>
    public IReadOnlyList<ModEntry> AllEntries => this.mods;

    /// <summary>Mods correspondant au filtre courant, triés par dossier puis par nom.</summary>
    public IReadOnlyList<ModEntry> Filtered => this.filtered;

    /// <summary>
    /// Les mêmes mods, rangés selon l'arborescence de Penumbra. N'a de sens qu'en dehors du tri
    /// par date, qui ignore délibérément les dossiers.
    /// </summary>
    public ModFolderNode FilteredTree => this.filteredTree;

    public bool Contains(ModInfo mod) => this.byDirectory.ContainsKey(mod.Directory);

    /// <summary>
    /// Retrouve un mod par son dossier, l'identifiant stable côté Penumbra. Sert à la vue de
    /// gestion, qui doit afficher un nom lisible pour des associations et prérequis enregistrés
    /// sous ce seul identifiant.
    /// </summary>
    public bool TryGetByDirectory(string directory, out ModInfo mod) => this.byDirectory.TryGetValue(directory, out mod);

    public string Filter
    {
        get => this.filter;
        set
        {
            if (this.filter == value)
                return;

            this.filter = value;
            this.ApplyFilter();
        }
    }

    /// <summary>Natures retenues ; vide signifie toutes.</summary>
    public HashSet<ModKind> KindFilter { get; } = [];

    /// <summary>À appeler après avoir modifié <see cref="KindFilter"/>.</summary>
    public void RefreshFilter() => this.ApplyFilter();

    public bool SortByDate
    {
        get => this.configuration.SortModsByDate;
        set
        {
            if (this.configuration.SortModsByDate == value)
                return;

            this.configuration.SortModsByDate = value;
            this.configuration.Save();
            this.ApplyFilter();
        }
    }

    public bool ShowTested
    {
        get => this.configuration.ShowTestedMods;
        set
        {
            if (this.configuration.ShowTestedMods == value)
                return;

            this.configuration.ShowTestedMods = value;
            this.configuration.Save();
            this.ApplyFilter();
        }
    }

    public bool IsTested(ModInfo mod) => this.configuration.TestedMods.Contains(mod.Directory);

    /// <summary>Nombre de mods du filtre courant pas encore jugés.</summary>
    public int UntestedCount => this.filtered.Count(e => !this.IsTested(e.Mod));

    /// <summary>Premier mod non jugé du filtre courant, dans l'ordre d'affichage.</summary>
    public ModInfo? NextUntested()
    {
        foreach (var entry in this.filtered)
        {
            if (!this.IsTested(entry.Mod))
                return entry.Mod;
        }

        return null;
    }

    /// <summary>Retient qu'un mod a été jugé, pour ne pas y revenir pendant un tri.</summary>
    public void MarkTested(ModInfo mod)
    {
        if (!this.configuration.TestedMods.Add(mod.Directory))
            return;

        this.configuration.Save();
    }

    public void ClearTested()
    {
        if (this.configuration.TestedMods.Count == 0)
            return;

        this.configuration.TestedMods.Clear();
        this.configuration.Save();
        this.ApplyFilter();
    }

    public void Refresh()
    {
        if (!this.penumbra.IsAvailable)
        {
            this.Clear();
            return;
        }

        if (!this.penumbra.TryGetMods(out var listedMods))
        {
            // Une panne IPC ne doit pas produire un catalogue vide qui serait pris pour une liste
            // fiable par les fonctions de nettoyage et de gestion.
            this.Clear();
            this.log.Warning("Catalogue Penumbra indisponible : la liste des mods n'est pas fiable.");
            return;
        }

        var root = this.penumbra.GetModDirectory();
        var changedItems = this.penumbra.GetAllChangedItems();

        this.mods = [.. listedMods.Select(mod => new ModEntry(
            mod,
            this.penumbra.GetSortPath(mod, root),
            this.Classify(mod, changedItems),
            ReadInstallDate(root, mod)))];
        this.allCache = [.. this.mods.Select(e => e.Mod)];
        this.byDirectory = this.mods.ToDictionary(e => e.Mod.Directory, e => e.Mod, StringComparer.Ordinal);

        this.IsLoaded = true;
        this.ApplyFilter();

        var folders = this.mods.Select(e => e.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        this.log.Info($"{this.mods.Count} mods lus depuis Penumbra, répartis en {folders} dossier(s).");
    }

    public void Clear()
    {
        this.mods = [];
        this.allCache = [];
        this.byDirectory = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        this.IsLoaded = false;
        this.ApplyFilter();
    }

    /// <summary>
    /// Nature d'un mod d'après ce qu'il modifie, de la plus spécifique à la plus vague.
    /// </summary>
    private ModKind Classify(
        ModInfo mod,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> changedItems)
    {
        if (!changedItems.TryGetValue(mod.Directory, out var items) || items.Count == 0)
            return ModKind.Other;

        if (items.Keys.Any(this.equipResolver.Knows))
            return ModKind.Equipment;

        if (CustomizationResolver.ResolveAll(items).Count > 0)
            return ModKind.Customization;

        if (items.Any(pair => IsAnimationChange(pair.Key, pair.Value)))
        {
            return ModKind.Animation;
        }

        return ModKind.Other;
    }

    private static bool IsAnimationChange(string name, object? data)
    {
        var type = data?.GetType().FullName ?? string.Empty;

        return type.EndsWith(".Emote", StringComparison.Ordinal)
               || type.EndsWith(".Action", StringComparison.Ordinal)
               || name.Contains("Animation", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Emote", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Date de création du dossier du mod. Penumbra ne conserve pas de date d'installation ;
    /// sous Windows, la création du dossier dans la bibliothèque en est l'approximation la plus
    /// proche. La dernière modification n'est pas utilisée : une opération globale de Penumbra
    /// peut la réécrire pour toute la bibliothèque le même jour.
    /// </summary>
    private static DateTime ReadInstallDate(string root, ModInfo mod)
    {
        if (string.IsNullOrEmpty(root))
            return DateTime.MinValue;

        try
        {
            var path = Path.Combine(root, mod.Directory);
            return Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception)
        {
            // Un dossier illisible ne doit pas empêcher d'afficher le mod.
            return DateTime.MinValue;
        }
    }

    private void ApplyFilter()
    {
        var matching = this.mods.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(this.filter))
        {
            matching = matching.Where(e =>
                e.Mod.Label.Contains(this.filter, StringComparison.CurrentCultureIgnoreCase)
                || e.Folder.Contains(this.filter, StringComparison.CurrentCultureIgnoreCase));
        }

        if (this.KindFilter.Count > 0)
            matching = matching.Where(e => this.KindFilter.Contains(e.Kind));

        if (!this.ShowTested)
            matching = matching.Where(e => !this.IsTested(e.Mod));

        var matchingList = matching.ToList();

        // Par date, l'arborescence n'a plus de sens : on veut les derniers ajouts, tous dossiers
        // confondus.
        this.filtered = this.SortByDate
                            ? [.. matchingList.OrderByDescending(e => e.Added)
                                              .ThenBy(e => e.Folder, StringComparer.CurrentCultureIgnoreCase)
                                              .ThenBy(e => e.Mod.Label, StringComparer.CurrentCultureIgnoreCase)]
                            : [.. matchingList.OrderBy(e => e.Folder, StringComparer.CurrentCultureIgnoreCase)
                                              .ThenBy(e => e.Mod.Label, StringComparer.CurrentCultureIgnoreCase)];

        this.filteredTree = BuildTree(matchingList);
    }

    /// <summary>
    /// Reconstruit l'arborescence à partir des mods retenus par le filtre. Reconstruite à chaque
    /// changement de filtre, jamais à chaque image : c'est ce qui rend un dossier fermé gratuit
    /// à l'affichage, le rendu n'ayant qu'à parcourir des listes déjà prêtes.
    /// </summary>
    private static ModFolderNode BuildTree(IReadOnlyList<ModEntry> entries)
    {
        var root = new ModFolderNode(string.Empty, string.Empty);

        foreach (var entry in entries)
        {
            var node = root;

            if (entry.Folder.Length > 0)
            {
                var path = string.Empty;

                foreach (var segment in entry.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    path = path.Length == 0 ? segment : $"{path}/{segment}";

                    var child = node.Folders.Find(
                        f => string.Equals(f.Name, segment, StringComparison.CurrentCultureIgnoreCase));

                    if (child is null)
                    {
                        child = new ModFolderNode(segment, path);
                        node.Folders.Add(child);
                    }

                    node = child;
                }
            }

            node.Mods.Add(entry);
        }

        SortTree(root);
        return root;
    }

    private static void SortTree(ModFolderNode node)
    {
        node.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        node.Mods.Sort((a, b) => string.Compare(a.Mod.Label, b.Mod.Label, StringComparison.CurrentCultureIgnoreCase));

        foreach (var child in node.Folders)
            SortTree(child);
    }
}
