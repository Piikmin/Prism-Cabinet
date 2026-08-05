using System.Collections.Generic;
using Penumbra.Api.Enums;

namespace QuickTestPlugin.Models;

/// <summary>
/// Un groupe d'options tel que Penumbra le décrit, avec son mode de sélection.
/// </summary>
/// <param name="Name">Nom du groupe, identifiant utilisé par l'IPC.</param>
/// <param name="Type">Mode de sélection du groupe.</param>
/// <param name="Options">Options disponibles, dans l'ordre défini par l'auteur du mod.</param>
public readonly record struct ModOptionGroup(string Name, GroupType Type, IReadOnlyList<string> Options)
{
    /// <summary>Exactement une option doit être retenue.</summary>
    public bool IsSingleSelect => this.Type is GroupType.Single;

    /// <summary>
    /// Les groupes <see cref="GroupType.Complex"/> réunissent des sous-groupes dont les options
    /// dépendent les unes des autres : elles ne se choisissent pas à plat, donc pas ici.
    /// </summary>
    public bool IsSupported => this.Type is not GroupType.Complex;

    public string TypeLabel => this.Type switch
    {
        GroupType.Single => "choix unique",
        GroupType.Multi => "choix multiple",
        GroupType.Imc => "choix multiple (IMC)",
        GroupType.Combining => "choix multiple (combiné)",
        GroupType.Complex => "groupe complexe",
        _ => this.Type.ToString(),
    };
}
