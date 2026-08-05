namespace QuickTestPlugin.Models;

/// <summary>
/// Un élément modifié qu'aucun automatisme n'applique : émote, animation, effet, monture.
/// </summary>
/// <param name="Label">Libellé rapporté par Penumbra.</param>
/// <param name="Detail">
/// Précision exploitable quand elle existe - la commande à taper pour une émote, le nombre de
/// fichiers pour un lot. Null quand Penumbra n'en dit pas plus.
/// </param>
/// <param name="EmoteId">
/// Identifiant de l'émote quand l'élément en est une, ce qui permet de la jouer directement.
/// </param>
public readonly record struct OtherChange(string Label, string? Detail, uint? EmoteId = null);
