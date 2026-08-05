using System;
using System.Collections.Generic;
using System.Linq;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Extrait, parmi les objets modifiés par un mod, les customisations qu'il remplace.
/// </summary>
/// <remarks>
/// Penumbra associe à chaque customisation un
/// <c>ValueTuple&lt;ModelRace, Gender, CustomizeIndex, CustomizeValue&gt;</c> défini dans
/// <c>Penumbra.GameData</c>, assembly interne au plugin et absente de NuGet. La lecture se fait
/// donc par réflexion sur la valeur reçue, sans référencer cet assembly. Toute forme inattendue
/// est ignorée silencieusement : cette structure n'est couverte par aucune garantie de
/// compatibilité, et une évolution de Penumbra doit dégrader la fonctionnalité, pas la casser.
/// </remarks>
public sealed class CustomizationResolver
{
    /// <summary>
    /// Attributs dont la valeur détermine quel modèle le jeu charge. Les autres (couleurs,
    /// proportions) ne changent pas le fichier utilisé : les appliquer ne rendrait pas un mod
    /// visible et modifierait le personnage sans raison.
    /// </summary>
    private static readonly HashSet<string> ModelSelectingIndices =
        new(StringComparer.Ordinal) { "Hairstyle", "Face", "TailShape", "FacePaint" };

    private readonly DiagnosticLog log;

    public CustomizationResolver(DiagnosticLog log)
    {
        this.log = log;
    }

    /// <summary>Customisations dont la valeur détermine quel modèle le jeu charge.</summary>
    public IReadOnlyList<CustomizationTarget> Resolve(IReadOnlyDictionary<string, object?> changedItems)
        => [.. ResolveAll(changedItems).Where(t => ModelSelectingIndices.Contains(t.Index))];

    /// <summary>
    /// Toutes les customisations modifiées, y compris celles qu'on n'applique pas. Sert à savoir
    /// quels genres le mod couvre : une texture de peau n'est pas appliquable mais renseigne.
    /// </summary>
    public static IReadOnlyList<CustomizationTarget> ResolveAll(IReadOnlyDictionary<string, object?> changedItems)
    {
        var targets = new List<CustomizationTarget>();

        foreach (var (label, data) in changedItems)
        {
            if (TryRead(data, label, out var target))
                targets.Add(target);
        }

        return targets;
    }

    /// <summary>
    /// Genres couverts par un mod. Vide quand le mod ne touche aucune customisation : un mod
    /// purement d'équipement ne porte pas cette information, et rien ne permet de l'inventer.
    /// </summary>
    public static IReadOnlySet<string> GenderCoverage(IReadOnlyList<CustomizationTarget> targets)
        => targets.Select(t => t.Gender)
                  .Where(g => g is "Male" or "Female")
                  .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Choisit la valeur à appliquer pour un attribut donné, en privilégiant la variante qui
    /// correspond au personnage. Si l'ambiguïté persiste, on refuse de trancher : appliquer la
    /// mauvaise variante changerait l'apparence du personnage pour rien.
    /// </summary>
    public byte? ValueFor(IReadOnlyList<CustomizationTarget> targets, string index)
    {
        var candidates = targets.Where(t => t.Index == index).ToList();
        if (candidates.Count == 0)
            return null;

        var identity = PlayerIdentity.Current();

        // Du plus précis au plus large : race + genre, puis race seule, puis toutes variantes.
        var scoped = Narrow(candidates, t => t.Race == identity.Race && t.Gender == identity.Gender)
                     ?? Narrow(candidates, t => t.Race == identity.Race)
                     ?? candidates;

        var values = scoped.Select(t => t.Value).Distinct().ToList();
        if (values.Count == 1)
            return values[0];

        var detail = candidates.Select(t => $"{t.Race} {t.Gender} → {t.Value}");
        this.log.Warning(
            $"Le mod propose plusieurs valeurs de {index} et aucune ne correspond sans ambiguïté " +
            $"à ton personnage ({identity}) : {string.Join(", ", detail)}. Rien n'est appliqué.");

        return null;
    }

    private static List<CustomizationTarget>? Narrow(
        List<CustomizationTarget> candidates, Func<CustomizationTarget, bool> predicate)
    {
        var subset = candidates.Where(predicate).ToList();
        return subset.Count > 0 ? subset : null;
    }


    private static bool TryRead(object? data, string label, out CustomizationTarget target)
    {
        target = default;

        if (data is null)
            return false;

        var type = data.GetType();
        if (!type.IsGenericType || type.GetGenericArguments().Length != 4)
            return false;

        var race = type.GetField("Item1")?.GetValue(data);
        var gender = type.GetField("Item2")?.GetValue(data);
        var index = type.GetField("Item3")?.GetValue(data);
        var value = type.GetField("Item4")?.GetValue(data);

        if (race is null || gender is null || index is null || value is null)
            return false;

        // CustomizeValue enveloppe un simple octet.
        if (value.GetType().GetProperty("Value")?.GetValue(value) is not byte raw)
            return false;

        target = new CustomizationTarget(
            race.ToString() ?? "?",
            gender.ToString() ?? "?",
            index.ToString() ?? "?",
            raw,
            label);

        return true;
    }
}
