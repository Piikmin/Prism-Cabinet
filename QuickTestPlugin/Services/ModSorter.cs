using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Glamourer.Api.Enums;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Calcule et applique un tri automatique des mods en dossiers, par créateur et/ou par nature.
/// </summary>
/// <remarks>
/// Volontairement réduit face à des plugins de tri plus complets : une seule sauvegarde de
/// secours (pas d'historique multi-versions), pas d'export, pas de protection de dossiers - un
/// mod à exclure se décoche dans la fenêtre elle-même avant d'appliquer. Le calcul lit
/// <c>meta.json</c> de chaque mod pour son auteur ; sur une grosse bibliothèque, l'appelant doit
/// le faire hors du thread de rendu.
/// </remarks>
public sealed class ModSorter
{
    private readonly PenumbraIntegration penumbra;
    private readonly EquipResolver equipResolver;
    private readonly ModMetaReader modMeta;
    private readonly Configuration configuration;
    private readonly DiagnosticLog log;

    public ModSorter(PenumbraIntegration penumbra, EquipResolver equipResolver, ModMetaReader modMeta, Configuration configuration, DiagnosticLog log)
    {
        this.penumbra = penumbra;
        this.equipResolver = equipResolver;
        this.modMeta = modMeta;
        this.configuration = configuration;
        this.log = log;
    }

    /// <summary>Une sauvegarde de secours existe : le dernier tri appliqué peut être annulé.</summary>
    public bool CanUndo => this.configuration.LastSortBackup.Count > 0;

    /// <summary>
    /// Calcule le chemin proposé pour chaque mod, sans rien écrire. Coûteux (lecture de
    /// <c>meta.json</c> par mod, mise en cache ensuite) : à appeler hors du thread de rendu.
    /// </summary>
    public IReadOnlyList<SortProposal> ComputeProposals(
        IReadOnlyList<ModEntry> mods,
        IReadOnlyList<ModEntry> allMods,
        SortStrategy strategy,
        bool detailedEquipment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> changedItems)
    {
        var scope = new HashSet<string>(mods.Select(m => m.Mod.Directory), StringComparer.Ordinal);

        // Réserve d'abord la place de tout ce qui existe déjà en dehors du tri courant : deux mods
        // au même nom ne sont pas rares (variantes, doublons d'installation), et une proposition ne
        // doit jamais entrer en collision avec un mod qu'on ne trie pas - Penumbra refuse deux mods
        // au même chemin.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in allMods)
        {
            if (!scope.Contains(entry.Mod.Directory))
                taken.Add(Normalize(entry.SortPath));
        }

        var raw = mods
            .Select(entry => (
                Entry: entry,
                Current: Normalize(entry.SortPath),
                Proposed: this.BuildPath(entry, strategy, detailedEquipment, changedItems)))
            .ToList();

        // Un mod qui ne bouge pas continue d'occuper sa place.
        foreach (var item in raw)
        {
            if (string.Equals(item.Current, item.Proposed, StringComparison.Ordinal))
                taken.Add(item.Current);
        }

        var result = new List<SortProposal>(raw.Count);
        foreach (var item in raw)
        {
            var unchanged = string.Equals(item.Current, item.Proposed, StringComparison.Ordinal);
            var proposed = unchanged ? item.Proposed : Disambiguate(item.Proposed, taken);
            var disambiguated = !unchanged && !string.Equals(proposed, item.Proposed, StringComparison.Ordinal);

            taken.Add(proposed);
            result.Add(new SortProposal(item.Entry.Mod, item.Current, proposed, disambiguated));
        }

        return result;
    }

    /// <summary>
    /// Mods dont le nom affiché est strictement identique à celui d'un autre, tous dossiers
    /// confondus. C'est la source des collisions que <see cref="ComputeProposals"/> résout en
    /// ajoutant un suffixe ; les lister permet de les régler à la racine (suppression, renommage)
    /// plutôt que de laisser le tri les distinguer par un numéro.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ModInfo>> FindDuplicateNames(IReadOnlyList<ModEntry> allMods)
        => [.. allMods
              .GroupBy(e => e.Mod.Label, StringComparer.OrdinalIgnoreCase)
              .Where(g => g.Count() > 1)
              .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
              .Select(g => (IReadOnlyList<ModInfo>)[.. g.Select(e => e.Mod)])];

    /// <summary>Ajoute « (2) », « (3) »... jusqu'à trouver un chemin encore libre.</summary>
    private static string Disambiguate(string path, HashSet<string> taken)
    {
        if (!taken.Contains(path))
            return path;

        var attempt = 2;
        string candidate;
        do
        {
            candidate = $"{path} ({attempt})";
            attempt++;
        }
        while (taken.Contains(candidate));

        return candidate;
    }

    private string BuildPath(
        ModEntry entry,
        SortStrategy strategy,
        bool detailedEquipment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> changedItems)
    {
        var creator = this.modMeta.For(entry.Mod).Author;
        if (string.IsNullOrWhiteSpace(creator))
            creator = "Inconnu";

        var kind = KindLabel(entry.Kind);

        if (detailedEquipment && entry.Kind == ModKind.Equipment
            && changedItems.TryGetValue(entry.Mod.Directory, out var items))
        {
            var slots = this.equipResolver.Resolve(items.Keys).Select(t => t.Slot).Distinct().ToList();

            // Un seul emplacement identifié sans ambiguïté : on le nomme précisément. Plusieurs
            // emplacements (mod « tenue complète ») ou aucun : le dossier générique reste plus juste
            // qu'un choix arbitraire entre plusieurs candidats.
            if (slots.Count == 1)
                kind = SlotLabel(slots[0]);
        }

        IEnumerable<string> segments = strategy switch
        {
            SortStrategy.ByCreator => [creator, entry.Mod.Label],
            SortStrategy.ByKind => [kind, entry.Mod.Label],
            SortStrategy.ByKindThenCreator => [kind, creator, entry.Mod.Label],
            SortStrategy.ByCreatorThenKind => [creator, kind, entry.Mod.Label],
            _ => [entry.Mod.Label],
        };

        return string.Join('/', segments.Select(Sanitize));
    }

    private static string KindLabel(ModKind kind) => kind switch
    {
        ModKind.Equipment => "Équipement",
        ModKind.Customization => "Apparence",
        ModKind.Animation => "Animations",
        _ => "Autres",
    };

    private static string SlotLabel(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head => "Tête",
        ApiEquipSlot.Body => "Torse",
        ApiEquipSlot.Hands => "Mains",
        ApiEquipSlot.Legs => "Jambes",
        ApiEquipSlot.Feet => "Pieds",
        ApiEquipSlot.Ears => "Oreilles",
        ApiEquipSlot.Neck => "Cou",
        ApiEquipSlot.Wrists => "Poignets",
        ApiEquipSlot.RFinger or ApiEquipSlot.LFinger => "Anneaux",
        ApiEquipSlot.MainHand or ApiEquipSlot.OffHand => "Armes",
        _ => "Équipement",
    };

    /// <summary>Un segment de dossier ne peut pas contenir les caractères interdits d'un nom de fichier.</summary>
    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Applique les propositions qui déplacent réellement un mod. Sauvegarde d'abord leur chemin
    /// actuel, pour permettre d'annuler.
    /// </summary>
    public (int Applied, int Failed, IReadOnlyList<SortProposal> FailedProposals) Apply(
        IReadOnlyList<SortProposal> proposals)
    {
        var changing = proposals.Where(p => p.Changes).ToList();
        if (changing.Count == 0)
            return (0, 0, []);

        this.configuration.LastSortBackup = changing.ToDictionary(p => p.Mod.Directory, p => p.CurrentPath);
        this.configuration.Save();

        var applied = 0;
        var failed = 0;
        var failedProposals = new List<SortProposal>();

        foreach (var proposal in changing)
        {
            if (this.penumbra.TrySetPath(proposal.Mod, proposal.ProposedPath))
                applied++;
            else
            {
                failed++;
                failedProposals.Add(proposal);
            }
        }

        this.log.Info($"Tri appliqué : {applied} mod(s) déplacé(s)" + (failed > 0 ? $", {failed} échec(s)." : "."));
        return (applied, failed, failedProposals);
    }

    /// <summary>Restaure les chemins d'avant le dernier tri appliqué, puis vide la sauvegarde.</summary>
    public (int Restored, int Failed) Undo()
    {
        if (this.configuration.LastSortBackup.Count == 0)
            return (0, 0);

        var restored = 0;
        var failed = 0;

        foreach (var (directory, path) in this.configuration.LastSortBackup)
        {
            if (this.penumbra.TrySetPath(new ModInfo(directory, string.Empty), path))
                restored++;
            else
                failed++;
        }

        this.configuration.LastSortBackup.Clear();
        this.configuration.Save();

        this.log.Info($"Tri annulé : {restored} mod(s) restauré(s)" + (failed > 0 ? $", {failed} échec(s)." : "."));
        return (restored, failed);
    }
}
