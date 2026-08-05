using System;

namespace QuickTestPlugin.Integration;

/// <summary>
/// Contrat commun aux plugins tiers dont QuickTest dépend (Penumbra, Glamourer).
/// </summary>
/// <remarks>
/// Volontairement neutre : il ne décrit que la disponibilité de l'intégration, pas les appels
/// métier. Les méthodes propres à chaque plugin seront ajoutées sur les implémentations
/// concrètes une fois l'IPC réel branché, afin de ne rien supposer de leurs signatures.
/// </remarks>
public interface IModIntegration : IDisposable
{
    /// <summary>Nom affiché dans l'UI et le journal.</summary>
    string DisplayName { get; }

    /// <summary>Nom interne du plugin tel que Dalamud le connaît.</summary>
    string InternalName { get; }

    /// <summary>Vrai si le plugin tiers est installé et chargé.</summary>
    bool IsAvailable { get; }

    /// <summary>Détail lisible de l'état courant, affiché dans l'UI.</summary>
    string StatusText { get; }

    /// <summary>Réévalue la disponibilité. Appelée au démarrage et sur demande de l'utilisateur.</summary>
    void Refresh();
}
