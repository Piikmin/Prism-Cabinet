namespace QuickTestPlugin.Models;

/// <summary>
/// Une animation reconnue et jouable.
/// </summary>
/// <param name="Label">Nom affiché - celui de l'émote, ou la clé de l'animation à défaut.</param>
/// <param name="Source">Ce qui a permis de la reconnaître, pour que l'utilisateur en juge.</param>
/// <param name="TimelineId">Animation à jouer.</param>
/// <param name="IsEmote">
/// Vrai si une émote la déclenche. Sinon c'est une animation de base - marche, saut, pose -
/// jouable tout de même, mais qu'aucune commande ne lance.
/// </param>
/// <param name="Pose">
/// Variante de pose demandée, quand le libellé la précise - « Gsit2 » désigne la troisième pose
/// assise au sol. Le jeu la range dans un état distinct de l'animation.
/// </param>
/// <param name="PoseFamily">
/// Posture à laquelle la pose se rapporte - « GroundSit », « Sit », « Doze », « Idle ». Le jeu
/// numérote les poses par famille : la pose 2 assise au sol n'est pas la pose 2 debout.
/// </param>
public readonly record struct AnimationMatch(
    string Label,
    string Source,
    uint TimelineId,
    bool IsEmote,
    byte? Pose = null,
    string? PoseFamily = null,
    uint? EmoteId = null);
