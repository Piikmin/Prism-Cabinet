using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Repère et retire les dossiers de Penumbra qui ne contiennent plus aucun mod.
/// </summary>
/// <remarks>
/// <c>organization.json</c> n'est documenté nulle part côté Penumbra et n'a aucun IPC associé -
/// ni pour le lire, ni pour l'écrire, ni pour dire à Penumbra de le relire après une édition
/// externe. Penumbra le réécrit lui-même à intervalles réguliers tant qu'il tourne : une édition
/// externe peut donc être silencieusement écrasée avant d'avoir été prise en compte. La seule
/// façon connue de la faire apparaître est de cliquer sur « Rediscover Mods » dans Penumbra après
/// coup. Chaque opération sauvegarde le fichier tel quel avant d'y toucher, pour pouvoir revenir
/// en arrière sans dépendre de ce que Penumbra en a fait entre-temps. Le fichier n'existe que si
/// Penumbra a déjà chargé sa liste de mods au moins une fois depuis le démarrage du jeu - sinon
/// il n'a simplement pas encore été écrit.
/// </remarks>
public sealed class FolderCleanup
{
    private readonly Configuration configuration;
    private readonly DiagnosticLog log;

    public FolderCleanup(Configuration configuration, DiagnosticLog log)
    {
        this.configuration = configuration;
        this.log = log;
    }

    /// <summary>Le fichier interne de Penumbra a été trouvé à l'emplacement attendu.</summary>
    public bool IsAvailable => OrganizationPath() is { } path && File.Exists(path);

    public bool CanUndo => !string.IsNullOrEmpty(this.configuration.LastFolderCleanupBackup);

    /// <summary>
    /// Chemin du fichier interne de Penumbra, déduit du dossier de configuration Dalamud voisin
    /// du nôtre (tous deux rangés sous le même dossier <c>pluginConfigs</c>).
    /// </summary>
    private static string? OrganizationPath()
    {
        var mine = Plugin.PluginInterface.ConfigDirectory;
        var pluginConfigsRoot = mine.Parent;
        return pluginConfigsRoot is null
            ? null
            : Path.Combine(pluginConfigsRoot.FullName, "Penumbra", "mod_filesystem", "organization.json");
    }

    /// <summary>Dossiers connus de Penumbra qu'aucun mod du catalogue n'occupe plus.</summary>
    public IReadOnlyList<string> FindEmpty(IReadOnlyList<ModEntry> allMods)
    {
        var path = OrganizationPath();
        if (path is null || !File.Exists(path))
            return [];

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["Folders"] is not JsonObject folders)
                return [];

            var occupied = allMods.Select(m => Normalize(m.SortPath)).ToList();
            var empty = new List<string>();

            foreach (var (folderPath, _) in folders)
            {
                var normalized = Normalize(folderPath);
                var hasContent = occupied.Any(sortPath =>
                    sortPath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));

                if (!hasContent)
                    empty.Add(folderPath);
            }

            return empty;
        }
        catch (Exception ex)
        {
            this.log.Debug($"Lecture de organization.json impossible : {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Retire les dossiers choisis du fichier de Penumbra, après en avoir sauvegardé le contenu
    /// actuel. Ne touche à rien d'autre que la clé « Folders ».
    /// </summary>
    public bool Remove(IReadOnlyList<string> folderPaths)
    {
        var path = OrganizationPath();
        if (path is null || !File.Exists(path) || folderPaths.Count == 0)
            return false;

        try
        {
            var raw = File.ReadAllText(path);
            var root = JsonNode.Parse(raw);
            if (root?["Folders"] is not JsonObject folders)
                return false;

            var removed = 0;
            foreach (var folderPath in folderPaths)
            {
                if (folders.Remove(folderPath))
                    removed++;
            }

            if (removed == 0)
                return false;

            this.configuration.LastFolderCleanupBackup = raw;
            this.configuration.Save();

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            this.log.Info(
                $"{removed} dossier(s) vide(s) retiré(s) de Penumbra. " +
                "Clique sur « Rediscover Mods » dans Penumbra pour que ça se voie.");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error($"Nettoyage des dossiers impossible : {ex.Message}");
            return false;
        }
    }

    /// <summary>Réécrit le fichier tel qu'il était avant le dernier nettoyage.</summary>
    public bool Undo()
    {
        var path = OrganizationPath();
        if (path is null || string.IsNullOrEmpty(this.configuration.LastFolderCleanupBackup))
            return false;

        try
        {
            File.WriteAllText(path, this.configuration.LastFolderCleanupBackup);
            this.configuration.LastFolderCleanupBackup = string.Empty;
            this.configuration.Save();

            this.log.Info("Dossiers restaurés. Clique sur « Rediscover Mods » dans Penumbra pour que ça se voie.");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error($"Restauration des dossiers impossible : {ex.Message}");
            return false;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
