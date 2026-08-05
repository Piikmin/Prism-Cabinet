using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>Langue de l'interface ; automatique suit la langue du client de jeu.</summary>
    public LanguageMode Language { get; set; } = LanguageMode.Automatic;

    /// <summary>Ouvre la fenêtre principale au chargement du plugin.</summary>
    public bool OpenOnStartup { get; set; }

    /// <summary>Affiche les entrées de niveau Debug dans le journal de diagnostic.</summary>
    public bool VerboseDiagnostics { get; set; }

    /// <summary>
    /// Équipe automatiquement les pièces modifiées par le mod testé, pour rendre son effet
    /// visible même si le personnage ne les porte pas.
    /// </summary>
    public bool AutoEquip { get; set; } = true;

    /// <summary>
    /// Sélectionne la première option des groupes qui restent vides après application, pour
    /// qu'un mod dont les options par défaut ne choisissent rien montre malgré tout son contenu.
    /// </summary>
    public bool AutoSelectEmptyGroups { get; set; } = true;

    /// <summary>
    /// Fait porter au personnage la coiffure ou le visage que le mod remplace, sans quoi un mod
    /// de customisation reste invisible.
    /// </summary>
    public bool AutoApplyCustomization { get; set; } = true;

    /// <summary>
    /// Vide les emplacements d'équipement que le mod testé ne concerne pas, pour ne laisser
    /// voir que lui. Les armes ne sont jamais retirées.
    /// </summary>
    public bool IsolateEquipment { get; set; } = true;

    /// <summary>
    /// Options retenues par l'utilisateur : dossier du mod → groupe → options sélectionnées.
    /// Le dossier sert de clé car il est stable, contrairement au nom affiché.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<string>>> ModOptions { get; set; } = [];

    /// <summary>
    /// Animations associées à la main par l'utilisateur, indexées par mod, groupe et option.
    /// Comble ce qu'aucune donnée du jeu ne permet de deviner.
    /// </summary>
    public Dictionary<string, AnimationBinding> AnimationBindings { get; set; } = [];

    /// <summary>
    /// Animations associées à la main, plusieurs par option. Remplace
    /// <see cref="AnimationBindings"/>, dont le contenu est repris au premier accès : une option
    /// peut déclencher plusieurs émotes, et se limiter à une seule interdisait de compléter une
    /// détection partielle.
    /// </summary>
    public Dictionary<string, List<AnimationBinding>> AnimationBindingSets { get; set; } = [];

    /// <summary>
    /// Mods à activer en même temps qu'un autre : dossier du mod → dossiers de ses prérequis.
    /// </summary>
    /// <remarks>
    /// Penumbra n'a aucune notion de dépendance et n'expose rien à ce sujet. Un mod qui exige un
    /// corps ou un squelette particulier ne le déclare que dans sa description, en texte libre.
    /// C'est donc l'utilisateur qui l'établit, une fois.
    /// </remarks>
    public Dictionary<string, List<string>> ModPrerequisites { get; set; } = [];

    /// <summary>Mods déjà jugés, pour ne pas y revenir pendant un tri.</summary>
    public HashSet<string> TestedMods { get; set; } = [];

    /// <summary>Trier par date d'ajout plutôt que par l'arborescence de Penumbra.</summary>
    public bool SortModsByDate { get; set; }

    /// <summary>Affiche aussi les mods déjà jugés dans la bibliothèque.</summary>
    public bool ShowTestedMods { get; set; } = true;

    /// <summary>
    /// Chemins de tri d'avant le dernier tri automatique appliqué, pour pouvoir l'annuler.
    /// Dossier du mod → chemin de tri qu'il avait avant. Vide tant qu'aucun tri automatique n'a
    /// été appliqué, ou juste après une annulation.
    /// </summary>
    public Dictionary<string, string> LastSortBackup { get; set; } = [];

    /// <summary>
    /// Contenu brut de <c>organization.json</c> (fichier interne de Penumbra) juste avant le dernier
    /// nettoyage de dossiers vides, pour pouvoir le restaurer tel quel. Vide tant qu'aucun nettoyage
    /// n'a été appliqué, ou juste après une annulation.
    /// </summary>
    public string LastFolderCleanupBackup { get; set; } = string.Empty;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
