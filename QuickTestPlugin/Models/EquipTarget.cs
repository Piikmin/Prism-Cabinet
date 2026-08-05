using Glamourer.Api.Enums;

namespace QuickTestPlugin.Models;

/// <summary>
/// Un objet équipable identifié à partir des « changed items » d'un mod.
/// </summary>
/// <remarks>
/// Ce modèle utilise directement <see cref="ApiEquipSlot"/> : dupliquer l'énumération de
/// Glamourer pour la remapper juste après n'apporterait rien ici.
/// </remarks>
/// <param name="Slot">Emplacement d'équipement visé.</param>
/// <param name="ItemId">Identifiant de l'objet dans la feuille Item du jeu.</param>
/// <param name="Name">Nom de l'objet, tel que Penumbra et le jeu l'affichent.</param>
public readonly record struct EquipTarget(ApiEquipSlot Slot, ulong ItemId, string Name);
