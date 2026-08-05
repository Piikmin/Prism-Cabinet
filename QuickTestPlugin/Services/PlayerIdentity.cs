using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuickTestPlugin.Services;

/// <summary>
/// Race et genre du personnage local, sous les noms qu'emploie Penumbra, et de quoi mesurer à
/// quel point un libellé libre leur correspond.
/// </summary>
/// <remarks>
/// Les identifiants viennent des valeurs typées de Dalamud (<c>Race</c>, <c>Tribe</c>,
/// <c>Sex</c>), pas d'un décalage supposé dans le tableau de customisation. Les Hyurs se
/// distinguent par leur clan, Penumbra raisonnant en race de modèle et non en race de jeu.
/// </remarks>
public readonly record struct PlayerIdentity(string Race, string Gender)
{
    private const string Unknown = "?";

    /// <summary>
    /// Jetons reconnaissant chaque race dans un texte libre, du plus spécifique au plus vague.
    /// Seul le premier trouvé compte, pour qu'un « Hyur Midlander » ne soit pas noté deux fois.
    /// </summary>
    private static readonly Dictionary<string, (string Token, int Weight)[]> RaceTokens = new(StringComparer.Ordinal)
    {
        ["Midlander"] = [("midlander", 3), ("hyur", 1)],
        ["Highlander"] = [("highlander", 3), ("hyur", 1)],
        ["Elezen"] = [("elezen", 3)],
        ["Lalafell"] = [("lalafell", 3)],
        ["Miqote"] = [("miqote", 3)],
        ["Roegadyn"] = [("roegadyn", 3)],
        ["AuRa"] = [("aura", 3), ("auri", 2), ("xaela", 2), ("raen", 2)],
        ["Hrothgar"] = [("hrothgar", 3)],
        ["Viera"] = [("viera", 3)],
    };

    public bool IsKnown => this.Race != Unknown;

    public static PlayerIdentity Current()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return new PlayerIdentity(Unknown, Unknown);

        var data = player.CustomizeData;

        var race = data.Race switch
        {
            1 => data.Tribe == 2 ? "Highlander" : "Midlander",
            2 => "Elezen",
            3 => "Lalafell",
            4 => "Miqote",
            5 => "Roegadyn",
            6 => "AuRa",
            7 => "Hrothgar",
            8 => "Viera",
            _ => Unknown,
        };

        return new PlayerIdentity(race, data.Sex == 1 ? "Female" : "Male");
    }

    /// <summary>
    /// Note la correspondance d'un libellé avec ce personnage. Zéro signifie « ne dit rien de
    /// pertinent », une note négative « désigne explicitement quelqu'un d'autre ».
    /// </summary>
    /// <remarks>
    /// Ces libellés sont du texte libre écrit par les auteurs de mods : la mesure est une
    /// heuristique, pas une certitude. Elle sert à départager des options, jamais à décider
    /// seule d'écrire quelque chose.
    /// </remarks>
    public int Score(string label)
    {
        if (!this.IsKnown || string.IsNullOrEmpty(label))
            return 0;

        var normalized = Normalize(label);
        var score = 0;

        if (RaceTokens.TryGetValue(this.Race, out var tokens))
        {
            foreach (var (token, weight) in tokens)
            {
                if (!normalized.Contains(token, StringComparison.Ordinal))
                    continue;

                score += weight;
                break;
            }
        }

        // Une race explicitement nommée qui n'est pas la nôtre disqualifie l'option.
        if (score == 0 && MentionsAnotherRace(normalized, this.Race))
            score -= 2;

        var gender = DetectGender(label, normalized);
        if (gender is not null)
            score += gender == this.Gender ? 1 : -2;

        return score;
    }

    private static bool MentionsAnotherRace(string normalized, string ownRace)
        => RaceTokens.Where(pair => pair.Key != ownRace)
                     .SelectMany(pair => pair.Value)
                     .Where(t => t.Weight >= 3)
                     .Any(t => normalized.Contains(t.Token, StringComparison.Ordinal));

    /// <summary>
    /// Genre désigné par un libellé libre, ou null s'il n'en désigne aucun. Sert à déduire la
    /// couverture d'un mod qui ne modifie aucune customisation.
    /// </summary>
    public static string? GenderOf(string label)
        => string.IsNullOrEmpty(label) ? null : DetectGender(label, Normalize(label));

    /// <summary>
    /// Reconnaît le genre par symbole ou par mot. « female » contient « male » : l'ordre des
    /// tests n'est pas indifférent.
    /// </summary>
    private static string? DetectGender(string raw, string normalized)
    {
        if (raw.Contains('♀'))
            return "Female";

        if (raw.Contains('♂'))
            return "Male";

        if (normalized.Contains("female", StringComparison.Ordinal)
            || normalized.Contains("feminine", StringComparison.Ordinal))
            return "Female";

        if (normalized.Contains("male", StringComparison.Ordinal)
            || normalized.Contains("masculine", StringComparison.Ordinal))
            return "Male";

        return null;
    }

    /// <summary>Minuscules sans accents ni ponctuation : « Miqo'te » et « Miqote » se rejoignent.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    public override string ToString() => $"{this.Race} {this.Gender}";
}
