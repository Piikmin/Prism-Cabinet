namespace QuickTestPlugin.Models;

/// <summary>
/// Un mod tel que Penumbra le décrit : le dossier est l'identifiant stable, le nom est
/// l'étiquette affichable (et peut être vide pour les mods jamais renommés).
/// </summary>
/// <param name="Directory">Nom du dossier du mod, identifiant utilisé par l'IPC Penumbra.</param>
/// <param name="Name">Nom affiché dans Penumbra.</param>
public readonly record struct ModInfo(string Directory, string Name)
{
    public string Label => string.IsNullOrEmpty(this.Name) ? this.Directory : this.Name;
}
