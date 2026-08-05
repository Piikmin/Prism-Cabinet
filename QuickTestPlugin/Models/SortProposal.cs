using System;

namespace QuickTestPlugin.Models;

/// <summary>Chemin de tri proposé pour un mod par une stratégie, comparé à son chemin actuel.</summary>
/// <param name="Mod">Le mod concerné.</param>
/// <param name="CurrentPath">Chemin de tri actuel dans Penumbra, dossiers séparés par « / ».</param>
/// <param name="ProposedPath">Chemin calculé par la stratégie choisie, même format.</param>
/// <param name="Disambiguated">
/// Vrai si <see cref="ProposedPath"/> a dû recevoir un suffixe (« (2) », « (3) »...) parce qu'un
/// autre mod portait déjà exactement ce nom à cet endroit.
/// </param>
public readonly record struct SortProposal(ModInfo Mod, string CurrentPath, string ProposedPath, bool Disambiguated)
{
    /// <summary>Si vrai, appliquer cette proposition déplacerait réellement le mod.</summary>
    public bool Changes => !string.Equals(this.CurrentPath, this.ProposedPath, StringComparison.Ordinal);
}
