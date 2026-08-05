using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using QuickTestPlugin.Integration;

namespace QuickTestPlugin.Services;

/// <summary>
/// Sonde de diagnostic : compare le bloc de customisation de Glamourer aux valeurs typées que
/// Dalamud expose pour le joueur local.
/// </summary>
/// <remarks>
/// Le format des blocs Glamourer n'est pas documenté. Plutôt que de le supposer, cette sonde le
/// recoupe : Dalamud donne <c>Hairstyle</c>, <c>Face</c>, <c>Height</c>… de façon typée, ce qui
/// permet de localiser le tableau de customisation dans le bloc et de vérifier la position de
/// chaque champ au lieu de la deviner. Rien n'est écrit : la sonde ne fait que lire.
/// </remarks>
public sealed class AppearanceProbe
{
    private readonly GlamourerIntegration glamourer;
    private readonly DiagnosticLog log;

    public AppearanceProbe(GlamourerIntegration glamourer, DiagnosticLog log)
    {
        this.glamourer = glamourer;
        this.log = log;
    }

    public void Run()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            this.log.Warning("Sonde impossible : aucun joueur local.");
            return;
        }

        var customize = player.Customize.ToArray();
        var typed = player.CustomizeData;

        this.log.Info($"- Sonde d'apparence - tableau Dalamud : {customize.Length} octets, {Hex(customize)}");
        this.log.Info(
            $"Valeurs typées : Hairstyle={typed.Hairstyle}, Face={typed.Face}, Height={typed.Height}, " +
            $"SkinColor={typed.SkinColor}, HairColor={typed.HairColor}, EyeShape={typed.EyeShape}, " +
            $"BodyType={typed.BodyType}, Eyebrows={typed.Eyebrows}");

        // Où se trouve Hairstyle dans le tableau brut ? On liste toutes les positions
        // candidates plutôt que d'affirmer un décalage.
        var candidates = Positions(customize, typed.Hairstyle);
        this.log.Info($"Positions du tableau Dalamud valant Hairstyle ({typed.Hairstyle}) : {Join(candidates)}");

        var state = this.glamourer.GetStateObject(Plugin.PlayerObjectIndex);
        if (state is null)
            return;

        this.log.Info($"Sections de l'état Glamourer : {string.Join(", ", state.Properties().Select(p => p.Name))}");

        // On ne cherche pas un nom de champ supposé : on cherche où se trouvent les valeurs
        // que Dalamud nous donne déjà, ce qui identifie le champ sans rien deviner.
        LogPathsHolding(state, typed.Hairstyle, "Hairstyle", this.log);
        LogPathsHolding(state, typed.SkinColor, "SkinColor", this.log);
        LogPathsHolding(state, typed.Height, "Height", this.log);

        foreach (var property in state.Properties().Where(p => p.Name.Contains("Custom", StringComparison.OrdinalIgnoreCase)))
        {
            var json = property.Value.ToString(Newtonsoft.Json.Formatting.None);
            this.log.Info($"Section « {property.Name} » ({json.Length} caractères) : {Truncate(json, 1200)}");
        }
    }

    /// <summary>Journalise tous les chemins JSON dont la valeur vaut <paramref name="value"/>.</summary>
    private static void LogPathsHolding(JContainer root, byte value, string label, DiagnosticLog log)
    {
        var paths = root.Descendants()
                        .OfType<JValue>()
                        .Where(v => v.Type is JTokenType.Integer && Convert.ToInt64(v.Value) == value)
                        .Select(v => v.Path)
                        .Take(12)
                        .ToList();

        log.Info($"Chemins valant {label} ({value}) : {(paths.Count == 0 ? "aucun" : string.Join(", ", paths))}");
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + $" … (+{text.Length - max})";

    private static IReadOnlyList<int> Positions(byte[] data, byte value)
    {
        var found = new List<int>();
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == value)
                found.Add(i);
        }

        return found;
    }

    private static string Hex(byte[] data, int max = 64)
    {
        var shown = data.Take(max).Select(b => b.ToString("X2"));
        var suffix = data.Length > max ? $" … (+{data.Length - max})" : string.Empty;
        return string.Join(' ', shown) + suffix;
    }

    private static string Join(IReadOnlyList<int> values)
        => values.Count == 0 ? "aucune" : string.Join(", ", values);
}
