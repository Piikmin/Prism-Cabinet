using System;
using System.Collections.Generic;
using Glamourer.Api.Enums;
using Lumina.Excel.Sheets;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Traduit les noms d'objets rapportés par Penumbra en objets équipables concrets.
/// </summary>
/// <remarks>
/// Penumbra rend ses « changed items » sous forme de noms d'objets accompagnés d'une valeur
/// dont la documentation ne garantit que « known objects or null ». Plutôt que de parier sur la
/// forme de cette valeur, on résout les noms dans la feuille Item du jeu, qui est la même source
/// que celle utilisée par Penumbra pour les nommer.
/// </remarks>
public sealed class EquipResolver
{
    private readonly DiagnosticLog log;
    private Dictionary<string, EquipTarget>? index;

    public EquipResolver(DiagnosticLog log)
    {
        this.log = log;
    }

    /// <summary>Vrai si ce nom désigne un objet équipable connu du jeu.</summary>
    public bool Knows(string name) => this.GetIndex().ContainsKey(name);

    /// <summary>
    /// Retient un objet équipable par emplacement, dans l'ordre où Penumbra les rapporte.
    /// Les noms sans correspondance équipable (customisations, VFX, montures…) sont ignorés.
    /// </summary>
    public IReadOnlyList<EquipTarget> Resolve(IEnumerable<string> changedItemNames)
    {
        var lookup = this.GetIndex();
        var perSlot = new Dictionary<ApiEquipSlot, EquipTarget>();
        var ignored = 0;

        foreach (var name in changedItemNames)
        {
            if (!lookup.TryGetValue(name, out var target))
            {
                ignored++;
                continue;
            }

            // Premier arrivé par emplacement : un mod « set » touche souvent plusieurs
            // variantes du même slot, une seule peut être portée.
            perSlot.TryAdd(target.Slot, target);
        }

        if (ignored > 0)
            this.log.Debug($"{ignored} objet(s) modifié(s) sans correspondance équipable, ignoré(s).");

        return [.. perSlot.Values];
    }

    /// <summary>
    /// Construit une fois pour toutes l'index nom → objet équipable. La feuille Item compte des
    /// dizaines de milliers de lignes : on ne la parcourt pas à chaque test.
    /// </summary>
    private Dictionary<string, EquipTarget> GetIndex()
    {
        if (this.index is not null)
            return this.index;

        var map = new Dictionary<string, EquipTarget>(StringComparer.OrdinalIgnoreCase);
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();

        foreach (var item in sheet)
        {
            if (item.EquipSlotCategory.RowId == 0 || !item.EquipSlotCategory.IsValid)
                continue;

            var slot = SlotOf(item.EquipSlotCategory.Value);
            if (slot is ApiEquipSlot.Unknown)
                continue;

            var name = item.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            // Des objets homonymes existent ; le premier rencontré fait foi.
            map.TryAdd(name, new EquipTarget(slot, item.RowId, name));
        }

        this.log.Debug($"Index d'équipement construit : {map.Count} objets.");
        this.index = map;
        return map;
    }

    /// <summary>
    /// Un EquipSlotCategory vaut 1 sur la colonne de l'emplacement qu'il occupe. La ceinture et
    /// le cristal d'âme n'ont pas d'équivalent dans l'API Glamourer et sont écartés.
    /// </summary>
    private static ApiEquipSlot SlotOf(EquipSlotCategory category)
    {
        if (category.Head == 1) return ApiEquipSlot.Head;
        if (category.Body == 1) return ApiEquipSlot.Body;
        if (category.Gloves == 1) return ApiEquipSlot.Hands;
        if (category.Legs == 1) return ApiEquipSlot.Legs;
        if (category.Feet == 1) return ApiEquipSlot.Feet;
        if (category.Ears == 1) return ApiEquipSlot.Ears;
        if (category.Neck == 1) return ApiEquipSlot.Neck;
        if (category.Wrists == 1) return ApiEquipSlot.Wrists;
        if (category.FingerR == 1) return ApiEquipSlot.RFinger;
        if (category.FingerL == 1) return ApiEquipSlot.LFinger;
        if (category.MainHand == 1) return ApiEquipSlot.MainHand;
        if (category.OffHand == 1) return ApiEquipSlot.OffHand;

        return ApiEquipSlot.Unknown;
    }
}
