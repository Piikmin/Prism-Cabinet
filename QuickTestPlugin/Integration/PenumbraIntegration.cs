using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using QuickTestPlugin.Models;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Integration;

/// <summary>
/// Intégration Penumbra, adossée au paquet <c>Penumbra.Api</c> (5.15.1).
/// </summary>
/// <remarks>
/// Toutes les modifications passent par les <em>réglages temporaires joueur</em>
/// (<see cref="SetTemporaryModSettingsPlayer"/>) : ils s'empilent par-dessus la collection sans
/// jamais toucher la configuration enregistrée par l'utilisateur, et sont retirés d'un bloc via
/// <see cref="RemoveAllTemporaryModSettingsPlayer"/>. C'est ce qui rend la restauration exacte.
/// </remarks>
public sealed class PenumbraIntegration : ModIntegrationBase
{
    /// <summary>Clé historique des réglages temporaires posés par le plugin (« QTES »).</summary>
    public const int OwnershipKey = 0x51544553;

    /// <summary>Source affichée dans l'UI de Penumbra en face de nos réglages temporaires.</summary>
    private const string SourceName = "Prism Cabinet";

    private readonly ApiVersion apiVersion;
    private readonly GetModList getModList;
    private readonly GetChangedItems getChangedItems;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly GetCurrentModSettings getCurrentModSettings;
    private readonly GetCurrentModSettingsWithTemp getCurrentModSettingsWithTemp;
    private readonly GetAvailableModSettings getAvailableModSettings;
    private readonly SetTemporaryModSettingsPlayer setTemporarySettings;
    private readonly RemoveAllTemporaryModSettingsPlayer removeTemporarySettings;
    private readonly RedrawObject redrawObject;
    private readonly DeleteMod deleteMod;
    private readonly GetModDirectory getModDirectory;
    private readonly GetModPath getModPath;
    private readonly SetModPath setModPath;
    private readonly GetChangedItemAdapterDictionary getAllChangedItems;
    private readonly GetPlayerResourcePaths getPlayerResourcePaths;

    public PenumbraIntegration(DiagnosticLog log)
        : base(log)
    {
        var pi = Plugin.PluginInterface;

        this.apiVersion = new ApiVersion(pi);
        this.getModList = new GetModList(pi);
        this.getChangedItems = new GetChangedItems(pi);
        this.getCollectionForObject = new GetCollectionForObject(pi);
        this.getCurrentModSettings = new GetCurrentModSettings(pi);
        this.getCurrentModSettingsWithTemp = new GetCurrentModSettingsWithTemp(pi);
        this.getAvailableModSettings = new GetAvailableModSettings(pi);
        this.setTemporarySettings = new SetTemporaryModSettingsPlayer(pi);
        this.removeTemporarySettings = new RemoveAllTemporaryModSettingsPlayer(pi);
        this.redrawObject = new RedrawObject(pi);
        this.deleteMod = new DeleteMod(pi);
        this.getModDirectory = new GetModDirectory(pi);
        this.getModPath = new GetModPath(pi);
        this.setModPath = new SetModPath(pi);
        this.getAllChangedItems = new GetChangedItemAdapterDictionary(pi);
        this.getPlayerResourcePaths = new GetPlayerResourcePaths(pi);
    }

    public override string DisplayName => "Penumbra";

    public override string InternalName => "Penumbra";

    protected override int ExpectedApiMajor => 5;

    /// <summary>Notifie le plugin quand la liste ou le rangement des mods change.</summary>
    public event Action? OnModsChangedEvent;

    protected override (int Major, int Minor) QueryApiVersion() => this.apiVersion.Invoke();

    protected override IEnumerable<IDisposable> CreateLifecycleSubscribers(Action onLifecycleEvent)
    {
        yield return Initialized.Subscriber(Plugin.PluginInterface, onLifecycleEvent);
        yield return Disposed.Subscriber(Plugin.PluginInterface, onLifecycleEvent);
        yield return ModAdded.Subscriber(Plugin.PluginInterface, _ => this.OnModsChanged(onLifecycleEvent));
        yield return ModDeleted.Subscriber(Plugin.PluginInterface, _ => this.OnModsChanged(onLifecycleEvent));
        yield return ModMoved.Subscriber(Plugin.PluginInterface, (_, _) => this.OnModsChanged(onLifecycleEvent));
        yield return ModDirectoryChanged.Subscriber(
            Plugin.PluginInterface, (_, _) => this.OnModsChanged(onLifecycleEvent));
    }

    private void OnModsChanged(Action onLifecycleEvent)
    {
        onLifecycleEvent();
        this.OnModsChangedEvent?.Invoke();
    }

    /// <summary>
    /// Liste les mods connus de Penumbra, triée par nom affiché. Un échec IPC est distingué d'une
    /// liste réellement vide afin que l'interface ne traite jamais une lecture interrompue comme
    /// la disparition de tous les mods.
    /// </summary>
    public bool TryGetMods(out IReadOnlyList<ModInfo> mods)
    {
        var result = new List<ModInfo>();

        var success = this.TryInvoke("lecture de la liste des mods", () =>
        {
            // Clé = dossier du mod (identifiant stable), valeur = nom affiché.
            foreach (var (directory, name) in this.getModList.Invoke())
                result.Add(new ModInfo(directory, name));
        });

        mods = result.OrderBy(m => m.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        return success;
    }

    /// <summary>Groupes d'options d'un mod, tels que Penumbra les décrit.</summary>
    public IReadOnlyList<ModOptionGroup> GetAvailableSettings(ModInfo mod)
    {
        var groups = new List<ModOptionGroup>();

        this.TryInvoke($"lecture des options de « {mod.Label} »", () =>
        {
            var available = this.getAvailableModSettings.Invoke(mod.Directory, mod.Name);
            if (available is null)
            {
                this.Log.Warning($"Penumbra ne décrit pas les options de « {mod.Label} ».");
                return;
            }

            foreach (var (name, (options, type)) in available)
                groups.Add(new ModOptionGroup(name, type, options));
        });

        return groups;
    }

    /// <summary>
    /// Active le mod pour le seul objet indiqué, en conservant les options déjà choisies par
    /// l'utilisateur dans sa collection.
    /// </summary>
    /// <param name="overrides">
    /// Options explicitement choisies par l'utilisateur. Elles priment sur les réglages de la
    /// collection comme sur le remplissage automatique.
    /// </param>
    /// <param name="fillEmptyGroups">
    /// Sélectionner une option pour les groupes qui ressortent vides après application. Sans
    /// cela, un mod dont les options par défaut ne sélectionnent rien s'active sans rien
    /// afficher. Ne s'applique jamais aux groupes réglés par l'utilisateur.
    /// </param>
    /// <param name="priorityOverride">
    /// Priorité à imposer. Dans Penumbra la priorité est un nombre, pas un ordre d'application :
    /// appliquer un mod après un autre ne le place pas au-dessus. Sert à faire gagner le mod
    /// testé sur ses prérequis en cas de conflit de fichiers.
    /// </param>
    /// <returns>La priorité effectivement appliquée, ou null si l'application a échoué.</returns>
    public int? TryApplyMod(
        int objectIndex,
        ModInfo mod,
        IReadOnlyDictionary<string, IReadOnlyList<string>> overrides,
        bool fillEmptyGroups,
        int? priorityOverride = null)
    {
        var (settings, existing) = this.ReadExistingSettings(objectIndex, mod);
        var priority = priorityOverride ?? existing;

        foreach (var (group, options) in overrides)
            settings[group] = options;

        if (overrides.Count > 0)
            this.Log.Debug($"{overrides.Count} groupe(s) d'options imposés par ta sélection.");

        var applied = false;
        this.TryInvoke($"application temporaire de « {mod.Label} »", () =>
        {
            // Les groupes absents du dictionnaire sont mis à leurs options par défaut par
            // Penumbra : transmettre un dictionnaire vide est donc légitime.
            var ec = this.Apply(objectIndex, mod, settings, priority);
            this.Log.Debug(
                $"SetTemporaryModSettingsPlayer → {ec} (dossier « {mod.Directory} », priorité {priority}, " +
                $"{settings.Count} groupe(s) d'options transmis, le reste aux valeurs par défaut).");

            applied = this.Check(ec, $"application de « {mod.Label} »");
            if (!applied)
                return;

            var resolved = this.ReadResolvedSettings(objectIndex, mod);
            if (resolved is not null && fillEmptyGroups)
                this.FillEmptyOptionGroups(objectIndex, mod, resolved, priority, overrides.Keys);
        });

        return applied ? priority : null;
    }

    private PenumbraApiEc Apply(
        int objectIndex, ModInfo mod, IReadOnlyDictionary<string, IReadOnlyList<string>> settings, int priority)
        => this.setTemporarySettings.Invoke(
            objectIndex,
            mod.Directory,
            inherit: false,
            enabled: true,
            priority,
            settings,
            SourceName,
            OwnershipKey,
            mod.Name);

    /// <summary>
    /// Deuxième passe : les options réellement retenues sont connues, on comble celles qui sont
    /// restées vides avec la première option disponible du groupe.
    /// </summary>
    /// <remarks>
    /// Un groupe <see cref="GroupType.Single"/> vide est une anomalie - la documentation impose
    /// exactement une option. Un groupe <see cref="GroupType.Multi"/> ou
    /// <see cref="GroupType.Combining"/> vide est légitime, mais le remplir est justement ce qui
    /// rend le mod visible pendant un test. Les groupes Imc et Complex sont laissés tels quels :
    /// leurs options ne se choisissent pas à plat.
    /// </remarks>
    private void FillEmptyOptionGroups(
        int objectIndex,
        ModInfo mod,
        Dictionary<string, IReadOnlyList<string>> resolved,
        int priority,
        IEnumerable<string> userControlled)
    {
        var reserved = new HashSet<string>(userControlled, StringComparer.Ordinal);

        var available = this.getAvailableModSettings.Invoke(mod.Directory, mod.Name);
        if (available is null)
        {
            this.Log.Warning($"Penumbra ne décrit pas les options de « {mod.Label} » ; remplissage impossible.");
            return;
        }

        var identity = PlayerIdentity.Current();
        var filled = new List<string>();
        var skipped = new List<string>();

        foreach (var (group, (options, type)) in available)
        {
            // Un groupe vide parce que l'utilisateur l'a vidé doit le rester.
            if (reserved.Contains(group))
                continue;

            if (resolved.TryGetValue(group, out var current) && current.Count > 0)
                continue;

            if (options.Length == 0)
                continue;

            if (type is GroupType.Imc or GroupType.Complex)
            {
                skipped.Add($"{group} ({type})");
                continue;
            }

            var chosen = Choose(options, group, identity);
            resolved[group] = [chosen];
            filled.Add($"{group}={chosen}");
        }

        if (skipped.Count > 0)
            this.Log.Debug($"Groupes non remplissables ignorés : {string.Join(", ", skipped)}.");

        if (filled.Count == 0)
        {
            this.Log.Debug("Aucun groupe d'options vide à combler.");
            return;
        }

        var ec = this.Apply(objectIndex, mod, resolved, priority);
        if (this.Check(ec, $"sélection automatique d'options pour « {mod.Label} »"))
            this.Log.Info($"Options sélectionnées automatiquement : {string.Join(", ", filled)}.");
    }

    /// <summary>
    /// Retient l'option qui désigne le mieux le personnage. Beaucoup de mods nomment leurs
    /// options par race et par genre (« ♀ Miqo'te ») : prendre la première de la liste
    /// installerait la variante d'une autre race.
    /// </summary>
    private string Choose(string[] options, string group, PlayerIdentity identity)
    {
        var best = options[0];
        var bestScore = identity.Score(best);

        foreach (var option in options.Skip(1))
        {
            var score = identity.Score(option);
            if (score <= bestScore)
                continue;

            best = option;
            bestScore = score;
        }

        if (bestScore > 0)
            this.Log.Debug($"Groupe « {group} » : « {best} » retenu pour {identity} (score {bestScore}).");
        else
            this.Log.Debug($"Groupe « {group} » : aucune option ne désigne {identity}, première option retenue.");

        return best;
    }

    /// <summary>Retire tous les réglages temporaires posés par QuickTest sur cet objet.</summary>
    public bool TryRemoveOurSettings(int objectIndex)
    {
        var removed = false;

        this.TryInvoke("retrait des réglages temporaires", () =>
        {
            var ec = this.removeTemporarySettings.Invoke(objectIndex, OwnershipKey);

            // NothingChanged signifie qu'il n'y avait rien à retirer : c'est un succès pour nous.
            removed = ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;
            if (!removed)
                this.Log.Error($"Penumbra a refusé le retrait des réglages temporaires : {ec}.");
        });

        return removed;
    }

    /// <summary>
    /// Objets de jeu que ce mod modifie, indépendamment de ses réglages, avec la donnée que
    /// Penumbra leur associe. Sert à savoir quoi équiper ou quelle customisation porter.
    /// </summary>
    public IReadOnlyDictionary<string, object?> GetChangedItems(ModInfo mod)
    {
        var items = new Dictionary<string, object?>();

        this.TryInvoke($"lecture des objets modifiés par « {mod.Label} »", () =>
        {
            foreach (var (name, data) in this.getChangedItems.Invoke(mod.Directory, mod.Name))
                items[name] = data;
        });

        if (items.Count > 0)
            this.Log.Debug($"Objets modifiés ({items.Count}) : {string.Join(" | ", items.Keys)}");

        return items;
    }

    /// <summary>
    /// Chemins de jeu actuellement redirigés vers des fichiers de mods sur le personnage.
    /// </summary>
    /// <remarks>
    /// Penumbra rend un dictionnaire « chemin réel → chemins de jeu ». Un chemin réel qui
    /// désigne un fichier du disque signale une redirection ; un chemin de jeu désigne un
    /// échange ou du contenu d'origine. Ne voit que ce qui est chargé à cet instant : une émote
    /// non jouée n'apparaît pas.
    /// </remarks>
    public IReadOnlyList<string> GetRedirectedGamePaths(int objectIndex)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        this.TryInvoke("lecture des fichiers appliqués", () =>
        {
            if (!this.getPlayerResourcePaths.Invoke().TryGetValue((ushort)objectIndex, out var resources))
                return;

            foreach (var (actualPath, gamePaths) in resources)
            {
                if (!IsFileSystemPath(actualPath))
                    continue;

                foreach (var gamePath in gamePaths)
                    paths.Add(gamePath);
            }
        });

        return [.. paths];
    }

    /// <summary>Un chemin de jeu s'écrit « chara/… » ; un chemin disque porte une lettre de lecteur.</summary>
    private static bool IsFileSystemPath(string path)
        => path.Contains(":\\", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal);

    /// <summary>
    /// Chemin de tri d'un mod dans Penumbra - l'arborescence que l'utilisateur y a construite.
    /// </summary>
    /// <remarks>
    /// Vide quand le mod est à la racine ou que Penumbra ne le connaît pas. C'est le classement
    /// que l'utilisateur maintient déjà : le reprendre évite de lui imposer une autre taxonomie.
    /// </remarks>
    public string GetSortPath(ModInfo mod, string root)
    {
        var path = string.Empty;

        this.TryInvoke($"lecture du rangement de « {mod.Label} »", () =>
        {
            var (ec, full, _, _) = this.getModPath.Invoke(mod.Directory, mod.Name);
            if (ec is PenumbraApiEc.Success)
            {
                // GetModPath renvoie un chemin de fichier complet. Le catalogue manipule un
                // chemin relatif pour ne pas afficher le dossier Penumbra dans chaque ligne.
                path = !string.IsNullOrEmpty(root) && Path.IsPathRooted(full)
                           ? Path.GetRelativePath(root, full)
                           : full;
            }
        });

        return path;
    }

    /// <summary>
    /// Range un mod à un autre chemin dans l'arborescence de Penumbra. N'écrit rien sur le disque :
    /// c'est le même rangement virtuel que celui que l'utilisateur construit dans l'interface de
    /// Penumbra, via <see cref="GetSortPath"/>.
    /// </summary>
    public bool TrySetPath(ModInfo mod, string newPath)
    {
        var ok = false;

        this.TryInvoke($"rangement de « {mod.Label} »", () =>
        {
            var ec = this.setModPath.Invoke(mod.Directory, newPath, mod.Name);
            ok = this.Check(ec, $"rangement de « {mod.Label} »");
        });

        return ok;
    }

    /// <summary>
    /// Objets modifiés de tous les mods, en un seul appel.
    /// </summary>
    /// <remarks>
    /// Interroger chaque mod séparément demanderait des centaines d'allers-retours. Penumbra
    /// avertit que la structure devient invalide quand sa liste change : elle n'est donc jamais
    /// conservée, seulement parcourue immédiatement.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> GetAllChangedItems()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        this.TryInvoke("lecture des objets modifiés de tous les mods", () =>
        {
            foreach (var (identifier, items) in this.getAllChangedItems.Invoke())
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, data) in items)
                    copy[name] = data;

                result[identifier] = copy;
            }
        });

        return result;
    }

    /// <summary>
    /// Dossier racine où Penumbra range les mods, pour lire ce que son IPC n'expose pas.
    /// </summary>
    public string GetModDirectory()
    {
        var root = string.Empty;
        this.TryInvoke("lecture du dossier des mods", () => root = this.getModDirectory.Invoke());
        return root;
    }

    /// <summary>
    /// Supprime définitivement un mod de Penumbra, fichiers compris.
    /// </summary>
    /// <remarks>
    /// Irréversible : Penumbra efface le dossier du mod du disque. L'appel n'est émis qu'après
    /// confirmation explicite de l'utilisateur. Penumbra signale un appel réussi, pas une
    /// suppression réussie - la liste est donc rechargée pour constater le résultat.
    /// </remarks>
    public bool TryDeleteMod(ModInfo mod)
    {
        var deleted = false;

        this.TryInvoke($"suppression de « {mod.Label} »", () =>
        {
            var ec = this.deleteMod.Invoke(mod.Directory, mod.Name);

            deleted = ec is PenumbraApiEc.Success;
            if (deleted)
                this.Log.Warning($"Mod supprimé définitivement : « {mod.Label} » ({mod.Directory}).");
            else
                this.Log.Error($"Penumbra a refusé la suppression de « {mod.Label} » : {ec}.");
        });

        return deleted;
    }

    public void Redraw(int objectIndex)
        => this.TryInvoke("redessin du personnage", () =>
        {
            this.redrawObject.Invoke(objectIndex, RedrawType.Redraw);
            this.Log.Debug($"Redessin demandé pour l'objet {objectIndex}.");
        });

    /// <summary>
    /// Relit les réglages actuels du mod dans la collection de l'objet, pour que le test
    /// n'écrase pas les options choisies par l'utilisateur. Retombe sur « aucune option » si
    /// Penumbra ne sait rien de ce mod.
    /// </summary>
    private (Dictionary<string, IReadOnlyList<string>> Settings, int Priority) ReadExistingSettings(
        int objectIndex, ModInfo mod)
    {
        var settings = new Dictionary<string, IReadOnlyList<string>>();
        var priority = 0;

        this.TryInvoke($"lecture des réglages actuels de « {mod.Label} »", () =>
        {
            var (objectValid, _, collection) = this.getCollectionForObject.Invoke(objectIndex);
            if (!objectValid)
            {
                this.Log.Warning("Penumbra ne reconnaît pas l'objet de jeu ciblé ; options par défaut utilisées.");
                return;
            }

            this.Log.Debug($"Collection active pour le personnage : « {collection.Item2} ».");

            var (ec, current) = this.getCurrentModSettings.Invoke(collection.Item1, mod.Directory, mod.Name, ignoreInheritance: false);
            if (ec is not PenumbraApiEc.Success || current is null)
            {
                // Cas courant : le mod n'a jamais été configuré dans cette collection. Penumbra
                // appliquera alors ses options par défaut, ce qui est le comportement voulu.
                this.Log.Debug($"Aucun réglage enregistré pour « {mod.Label} » ({ec}) ; options par défaut.");
                return;
            }

            var (existingEnabled, existingPriority, existingSettings, inherited) = current.Value;
            priority = existingPriority;

            foreach (var (group, options) in existingSettings)
                settings[group] = options;

            this.Log.Debug(
                $"Réglages relus : activé={existingEnabled}, priorité={existingPriority}, " +
                $"hérité={inherited}, {existingSettings.Count} groupe(s) d'options.");
        });

        return (settings, priority);
    }

    /// <summary>
    /// Relit auprès de Penumbra l'état qu'il a réellement retenu, réglages temporaires compris.
    /// C'est la seule façon de distinguer « notre appel n'a rien fait » de « le mod est bien
    /// actif mais son effet n'est pas visible sur ce personnage ».
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>>? ReadResolvedSettings(int objectIndex, ModInfo mod)
    {
        try
        {
            var (objectValid, _, collection) = this.getCollectionForObject.Invoke(objectIndex);
            if (!objectValid)
                return null;

            var (ec, state) = this.getCurrentModSettingsWithTemp.Invoke(
                collection.Item1,
                mod.Directory,
                mod.Name,
                ignoreInheritance: false,
                ignoreTemporary: false,
                OwnershipKey);

            if (ec is not PenumbraApiEc.Success || state is null)
            {
                this.Log.Warning($"Vérification après application : Penumbra ne rend aucun réglage ({ec}).");
                return null;
            }

            var (enabled, priority, options, inherited, temporary) = state.Value;
            var chosen = options.Count == 0
                             ? "aucun groupe d'options"
                             : string.Join(", ", options.Select(kv => $"{kv.Key}=[{string.Join('|', kv.Value)}]"));

            this.Log.Debug(
                $"Vérification : activé={enabled}, priorité={priority}, temporaire={temporary}, " +
                $"hérité={inherited}, options → {chosen}.");

            if (!enabled)
                this.Log.Error("Penumbra a accepté l'appel mais le mod ressort désactivé.");
            else if (!temporary)
                this.Log.Warning("Le mod est actif, mais pas via nos réglages temporaires.");

            return options.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        }
        catch (Exception ex)
        {
            this.Log.Debug($"Vérification après application impossible : {ex.Message}");
            return null;
        }
    }

    private bool Check(PenumbraApiEc ec, string operation)
    {
        if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
            return true;

        var hint = ec switch
        {
            PenumbraApiEc.ModMissing => " - le mod n'existe plus dans Penumbra ; recharge la liste.",
            PenumbraApiEc.TemporarySettingDisallowed =>
                " - un autre plugin détient déjà des réglages temporaires sur ce mod.",
            PenumbraApiEc.TemporarySettingImpossible =>
                " - Penumbra refuse les réglages temporaires dans cet état.",
            PenumbraApiEc.CollectionMissing => " - aucune collection n'est assignée à ce personnage.",
            _ => string.Empty,
        };

        // Pas d'article devant {operation} : certains appelants passent un nom qui commence par une
        // consonne (« rangement »), d'autres par une voyelle (« application ») - une élision fixe
        // (« l'{operation} ») serait fausse pour la moitié d'entre eux.
        this.Log.Error($"Penumbra a refusé cette opération ({operation}) : {ec}{hint}");
        return false;
    }
}
