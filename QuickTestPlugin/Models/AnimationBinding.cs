namespace QuickTestPlugin.Models;

/// <summary>
/// Animation que l'utilisateur a lui-même associée à une option de mod.
/// </summary>
/// <remarks>
/// Les auteurs de mods emploient des abréviations communautaires - « Gsit » pour
/// <c>/groundsit</c>, « Tdance » - que rien dans les données du jeu ne permet de résoudre.
/// Plutôt que de figer une table de correspondances devinée, on retient ce que l'utilisateur
/// désigne : c'est exact, et ça couvre n'importe quel mod plutôt que ceux qu'on aurait prévus.
/// </remarks>
/// <param name="TimelineId">Animation à jouer.</param>
/// <param name="Label">Nom sous lequel l'afficher.</param>
public readonly record struct AnimationBinding(uint TimelineId, string Label);
