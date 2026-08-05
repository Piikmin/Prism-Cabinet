namespace QuickTestPlugin.Models;

/// <summary>
/// Cycle de vie d'un test de mod. L'UI dérive de cet état l'activation des boutons.
/// </summary>
public enum TestState
{
    /// <summary>Aucun mod sélectionné, rien à faire.</summary>
    Idle,

    /// <summary>Un mod est sélectionné et les intégrations requises sont disponibles.</summary>
    Ready,

    /// <summary>Application du mod en cours.</summary>
    Applying,

    /// <summary>Le mod est appliqué ; l'état d'origine peut être restauré.</summary>
    Applied,

    /// <summary>Restauration de l'état d'origine en cours.</summary>
    Restoring,

    /// <summary>La dernière opération a échoué ; voir le journal de diagnostic.</summary>
    Failed,
}
