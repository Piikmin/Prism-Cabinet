using System.Collections.Generic;

namespace QuickTestPlugin.Models;

/// <summary>
/// Ce qu'un mod modifie, classé par ce que QuickTest sait en faire.
/// </summary>
/// <param name="Equipment">Pièces équipables, une par emplacement.</param>
/// <param name="Customizations">Customisations que le personnage peut porter.</param>
/// <param name="Others">
/// Éléments qu'aucun automatisme ne couvre : animations, émotes, VFX, montures. Ils sont
/// affichés tels quels, car les voir suppose une action du joueur.
/// </param>
/// <param name="GenderCoverage">Genres couverts, vide si l'information n'existe pas.</param>
/// <param name="CoverageIsHeuristic">
/// Vrai quand la couverture est déduite des noms d'options plutôt que des données de Penumbra.
/// </param>
public sealed record ModImpact(
    IReadOnlyList<EquipTarget> Equipment,
    IReadOnlyList<CustomizationTarget> Customizations,
    IReadOnlyList<OtherChange> Others,
    IReadOnlySet<string> GenderCoverage,
    bool CoverageIsHeuristic)
{
    public static ModImpact Empty { get; } =
        new([], [], [], new HashSet<string>(), false);

    public int Total => this.Equipment.Count + this.Customizations.Count + this.Others.Count;
}
