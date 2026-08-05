using System;
using Lumina.Excel.Sheets;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Tire de la valeur associée à un élément modifié ce qui aide à le voir en jeu.
/// </summary>
/// <remarks>
/// Penumbra associe à chaque élément une ligne de feuille du jeu ou un entier. Le type est lu
/// par son nom et la ligne relue depuis nos propres feuilles à partir de son identifiant :
/// comparer les types directement supposerait que Penumbra et QuickTest chargent le même
/// assembly Lumina, ce que rien ne garantit.
/// </remarks>
public static class ChangedItemDescriber
{
    public static OtherChange Describe(string label, object? data)
    {
        if (data is null)
            return new OtherChange(label, null);

        // Un entier compte les fichiers d'un lot (« Sound », « Vfx »), il n'identifie rien.
        if (data is int count)
            return new OtherChange(label, $"{count} fichier(s)");

        var typeName = data.GetType().FullName ?? string.Empty;

        if (typeName.EndsWith(".Emote", StringComparison.Ordinal) && ReadRowId(data) is { } emoteId)
            return new OtherChange(label, DescribeEmote(emoteId), emoteId);

        return new OtherChange(label, null);
    }

    /// <summary>Commande à taper pour jouer une émote, si le jeu lui en associe une.</summary>
    private static string? DescribeEmote(uint rowId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Emote>().TryGetRow(rowId, out var emote))
            return null;

        if (!emote.TextCommand.IsValid)
            return null;

        var command = emote.TextCommand.Value.Command.ToString();
        return string.IsNullOrEmpty(command) ? null : $"joue {command}";
    }

    private static uint? ReadRowId(object data)
        => data.GetType().GetProperty("RowId")?.GetValue(data) as uint?;
}
