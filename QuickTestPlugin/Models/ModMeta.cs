namespace QuickTestPlugin.Models;

/// <summary>
/// Ce qu'un mod déclare sur lui-même, lu dans son fichier de métadonnées.
/// </summary>
/// <remarks>
/// L'API de Penumbra n'expose ni description ni étiquettes : seuls le nom, le dossier, les
/// objets modifiés et les options. Ces informations sont donc lues sur le disque, dans le
/// dossier que Penumbra indique lui-même.
/// </remarks>
/// <param name="Author">Auteur déclaré, vide s'il n'en a pas mis.</param>
/// <param name="Description">
/// Texte libre de l'auteur. C'est là qu'il indique généralement ce que le mod exige - corps,
/// squelette, autre mod - Penumbra n'ayant aucune notion de dépendance.
/// </param>
/// <param name="Version">Version déclarée, vide le plus souvent.</param>
/// <param name="Website">Page d'origine, utile pour retrouver les prérequis.</param>
public readonly record struct ModMeta(string Author, string Description, string Version, string Website)
{
    public static ModMeta Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool HasDescription => !string.IsNullOrWhiteSpace(this.Description);
}
