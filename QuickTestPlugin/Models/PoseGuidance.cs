namespace QuickTestPlugin.Models;

/// <summary>État local d'une variante de pose que le joueur doit atteindre lui-même.</summary>
/// <param name="Label">Animation ou option qui demande cette pose.</param>
/// <param name="Family">Nom lisible de la posture concernée.</param>
/// <param name="Target">Index interne de la pose attendue, ou null si le mod ne précise pas sa variante.</param>
/// <param name="Current">Index interne actuellement visible, ou null si la posture n'est pas encore active.</param>
/// <param name="Highest">Dernier index disponible dans cette famille.</param>
public readonly record struct PoseGuidance(
    string Label,
    string Family,
    byte? Target,
    byte? Current,
    byte Highest)
{
    public int? TargetNumber => this.Target is { } target ? target + 1 : null;

    public int? CurrentNumber => this.Current is { } current ? current + 1 : null;

    public int PoseCount => this.Highest + 1;

    public bool HasTarget => this.Target is not null;

    public bool IsReached => this.Target is { } target && this.Current == target;

    public int? ChangesRemaining
        => this.Target is { } target && this.Current is { } current
               ? (target - current + this.PoseCount) % this.PoseCount
               : null;
}
