namespace QuickTestPlugin.Models;

/// <summary>
/// Une customisation qu'un mod remplace : « coiffure 157 pour Miqo'te féminine ».
/// </summary>
/// <param name="Race">Nom de la race de modèle, tel que Penumbra le nomme.</param>
/// <param name="Gender">Genre visé.</param>
/// <param name="Index">
/// Attribut de customisation (<c>Hairstyle</c>, <c>Face</c>…). Ce nom est celui de l'énumération
/// de Penumbra, qui coïncide avec le nom de champ employé par Glamourer dans son état JSON.
/// </param>
/// <param name="Value">Valeur de l'attribut, celle qu'il faut porter pour voir le mod.</param>
/// <param name="Label">Libellé d'origine rapporté par Penumbra, pour le journal.</param>
public readonly record struct CustomizationTarget(
    string Race,
    string Gender,
    string Index,
    byte Value,
    string Label);
