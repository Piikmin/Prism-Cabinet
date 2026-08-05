namespace QuickTestPlugin.Models;

/// <summary>
/// Nature d'un mod, déduite de ce qu'il modifie.
/// </summary>
/// <remarks>
/// Penumbra ne classe pas les mods : cette nature est établie à partir des objets qu'ils
/// touchent. Un mod pouvant en toucher plusieurs sortes, c'est la plus spécifique qui l'emporte
/// - un vêtement accompagné de sons reste un vêtement.
/// </remarks>
public enum ModKind
{
    /// <summary>Aucun objet modifié connu, ou mod inclassable.</summary>
    Other,

    /// <summary>Pièces d'équipement.</summary>
    Equipment,

    /// <summary>Coiffures, visages, peaux.</summary>
    Customization,

    /// <summary>Émotes et animations.</summary>
    Animation,
}
