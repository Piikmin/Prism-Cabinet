using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Remonte d'un fichier d'animation redirigé jusqu'à l'émote qui le déclenche.
/// </summary>
/// <remarks>
/// Le chemin de jeu d'une animation se termine par la clé que porte la feuille
/// <c>ActionTimeline</c> - « emote/sit » pour « …/emote/sit.pap ». De cette timeline on remonte
/// à l'émote qui la référence. Rien n'est deviné : c'est un rapprochement entre deux données du
/// jeu, et ce qui ne correspond à rien est signalé comme tel plutôt qu'interprété.
/// </remarks>
public sealed class AnimationIdentifier
{
    private const string AnimationExtension = ".pap";

    private readonly DiagnosticLog log;

    private static readonly char[] NonAlphanumeric =
        [' ', '(', ')', '[', ']', '/', '\\', '-', '_', '.', ',', ';', ':', '+', '&', '~', '!', '\'', '"', '=', '<', '>'];

    private static readonly char[] Digits = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

    /// <summary>
    /// Abréviations d'émotes de base employées par les auteurs de mods, que rien dans les
    /// données du jeu ne permet de résoudre.
    /// </summary>
    /// <remarks>
    /// Volontairement réduite aux cas dont l'usage est attesté : les émotes à débloquer sont
    /// citées par leur commande exacte et se résolvent seules. Ce n'est pas une table à
    /// compléter à l'aveugle - pour tout le reste, l'association manuelle est exacte là où une
    /// abréviation devinée serait fausse silencieusement.
    /// </remarks>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gsit"] = "groundsit",
        ["bed"] = "doze",
    };

    /// <summary>
    /// Famille de posture déduite du nom d'émote. Le jeu numérote ses poses par famille, il faut
    /// donc savoir laquelle viser en plus de l'index.
    /// </summary>
    private static readonly (string Token, string Family)[] PoseFamilies =
    [
        ("groundsit", "GroundSit"),
        ("doze", "Doze"),
        ("sit", "Sit"),
        ("idle", "Idle"),
    ];

    private Dictionary<string, uint>? timelineByKey;
    private Dictionary<uint, uint>? emoteByTimeline;
    private Dictionary<string, uint>? timelineByName;

    private readonly Dictionary<string, IReadOnlyList<AnimationMatch>> labelCache = new(StringComparer.Ordinal);

    public AnimationIdentifier(DiagnosticLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Identifie les animations parmi des chemins de jeu redirigés. Les fichiers qui ne sont pas
    /// des animations sont ignorés ; ceux qu'on ne sait pas rattacher sont rendus tels quels.
    /// </summary>
    public IReadOnlyList<AnimationMatch> Identify(IEnumerable<string> gamePaths)
    {
        var keys = this.TimelineByKey();
        var found = new List<AnimationMatch>();
        var seen = new HashSet<uint>();

        foreach (var path in gamePaths)
        {
            if (!path.EndsWith(AnimationExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            if (MatchKey(path, keys) is not { } timelineId)
            {
                this.log.Debug($"Animation non rattachée : {ShortName(path)}");
                continue;
            }

            if (this.Describe(timelineId, "fichier chargé") is { } match && seen.Add(match.TimelineId))
                found.Add(match);
        }

        return found;
    }

    /// <summary>
    /// Émotes nommées dans un libellé d'option. Les auteurs de mods-catalogues indiquent le
    /// déclencheur entre parenthèses - « (Dom-Gsit2/Sub-Beesknees) » - en employant les noms
    /// internes du jeu, ceux-là mêmes que porte la colonne <c>Key</c> d'<c>ActionTimeline</c>.
    /// </summary>
    /// <remarks>
    /// Reconnaissance par jetons : le libellé est découpé sur tout ce qui n'est pas alphanumérique
    /// et chaque morceau confronté aux noms d'animations connus. C'est une heuristique sur du
    /// texte libre - elle propose, elle ne décide pas.
    /// </remarks>
    /// <remarks>
    /// Le résultat est mémorisé : cette méthode est appelée à chaque image pour chaque option
    /// affichée, alors qu'un libellé ne change jamais. Sans cache, un mod-catalogue impose des
    /// centaines de découpages de chaînes par seconde pour un résultat identique.
    /// </remarks>
    public IReadOnlyList<AnimationMatch> FromLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
            return [];

        if (this.labelCache.TryGetValue(label, out var cached))
            return cached;

        var found = this.MatchLabel(label);
        this.labelCache[label] = found;
        return found;
    }

    private IReadOnlyList<AnimationMatch> MatchLabel(string label)
    {
        var index = this.TimelineByName();
        var found = new List<AnimationMatch>();
        var seen = new HashSet<uint>();

        var parts = label.Split(NonAlphanumeric, StringSplitOptions.RemoveEmptyEntries);
        for (var partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            var raw = parts[partIndex];

            // « Gsit2 » et « Bed0 » désignent la même animation que « gsit » et « bed » : les
            // auteurs numérotent les variantes, la clé du jeu ne les numérote pas.
            var token = raw.TrimEnd(Digits);

            // Le chiffre final n'est pas décoratif : « Gsit2 » désigne la troisième pose assise
            // au sol, « Idle5 » la sixième pose debout.
            byte? pose = token.Length < raw.Length && byte.TryParse(raw[token.Length..], out var index2)
                             ? index2
                             : null;

            // Certains auteurs séparent le numéro par une espace ou un tiret : « Gsit 2 » doit
            // produire le même guidage que « Gsit2 ». Le nombre n'est consommé que si le jeton
            // précédent est ensuite reconnu comme une animation, pour éviter de capturer les
            // nombres ordinaires d'une description.
            byte? separatedPose = pose is null
                                  && partIndex + 1 < parts.Length
                                  && byte.TryParse(parts[partIndex + 1], out var separatedIndex)
                                      ? separatedIndex
                                      : null;

            if (Abbreviations.TryGetValue(token, out var expanded))
                token = expanded;

            // En dessous de quatre caractères, les collisions fortuites l'emportent.
            if (token.Length < 4 || !index.TryGetValue(token, out var timelineId))
                continue;

            if (separatedPose is { } separated)
            {
                pose = separated;
                partIndex++;
            }

            if (this.Describe(timelineId, $"d'après « {raw} »") is not { } match)
                continue;

            // « sit » est contenu dans « groundsit » : le premier motif trouvé, du plus précis au
            // plus général, l'emporte.
            var family = PoseFamilies.FirstOrDefault(f => token.Contains(f.Token, StringComparison.OrdinalIgnoreCase));

            var withPose = match with { Pose = pose, PoseFamily = family.Family };
            if (seen.Add((uint)HashCode.Combine(withPose.TimelineId, pose)))
                found.Add(withPose);
        }

        return found;
    }

    /// <summary>
    /// Nomme une animation par l'émote qui la déclenche, ou par sa clé si aucune ne le fait.
    /// </summary>
    private AnimationMatch? Describe(uint timelineId, string source)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Emote>();

        if (this.EmoteByTimeline().TryGetValue(timelineId, out var emoteId)
            && sheet.TryGetRow(emoteId, out var emote))
        {
            // L'émote se joue sur sa boucle, pas sur son entrée.
            var loop = emote.ActionTimeline.FirstOrDefault(t => t.RowId != 0 && t.IsValid && t.Value.IsLoop);
            var play = loop.RowId != 0 ? loop.RowId : timelineId;

            return new AnimationMatch(emote.Name.ToString(), source, play, true, EmoteId: emoteId);
        }

        if (!Plugin.DataManager.GetExcelSheet<ActionTimeline>().TryGetRow(timelineId, out var timeline))
            return null;

        return new AnimationMatch(timeline.Key.ToString(), source, timelineId, false);
    }

    /// <summary>
    /// Dernier segment de la clé de chaque animation - « beesknees » pour « emote/beesknees ».
    /// Toutes sont indexées, y compris celles qu'aucune émote ne déclenche : elles restent
    /// jouables, et les mods-catalogues les citent volontiers.
    /// </summary>
    private Dictionary<string, uint> TimelineByName()
    {
        if (this.timelineByName is not null)
            return this.timelineByName;

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        // Les commandes d'émotes restent en anglais quelle que soit la langue du client, et
        // c'est ce vocabulaire qu'emploient les auteurs de mods. Elles priment donc sur les clés
        // d'animation, souvent opaques (« emote/jmn » pour /groundsit).
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            if (!emote.TextCommand.IsValid)
                continue;

            var loop = emote.ActionTimeline.FirstOrDefault(t => t.RowId != 0 && t.IsValid && t.Value.IsLoop);
            if (loop.RowId == 0)
                continue;

            foreach (var form in new[]
                     {
                         emote.TextCommand.Value.Command.ToString(),
                         emote.TextCommand.Value.ShortCommand.ToString(),
                         emote.TextCommand.Value.Alias.ToString(),
                         emote.TextCommand.Value.ShortAlias.ToString(),
                     })
            {
                var command = form.TrimStart('/');
                if (command.Length >= 4)
                    map.TryAdd(command, loop.RowId);
            }
        }

        var emotes = this.EmoteByTimeline();

        foreach (var (key, timelineId) in this.TimelineByKey())
        {
            // Une animation déclenchée par une émote est toujours pertinente. Sinon on se limite
            // aux animations du joueur : sans ce filtre, un mot anglais courant du descriptif
            // tombe sur un geste de PNJ ou une animation d'événement.
            if (!emotes.ContainsKey(timelineId) && !IsPlayerAnimation(key))
                continue;

            var slash = key.LastIndexOf('/');
            var name = (slash < 0 ? key : key[(slash + 1)..]).TrimEnd(Digits);

            if (name.Length >= 4)
                map.TryAdd(name, timelineId);
        }

        this.log.Debug($"Index des noms d'animations : {map.Count} entrées.");
        this.timelineByName = map;
        return map;
    }

    /// <summary>
    /// Émotes jouables correspondant à une recherche, pour que l'utilisateur en désigne une.
    /// </summary>
    public IReadOnlyList<AnimationMatch> SearchPlayable(string fragment, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return [];

        var found = new List<AnimationMatch>();

        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            var name = emote.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;

            var command = emote.TextCommand.IsValid ? emote.TextCommand.Value.Command.ToString() : string.Empty;

            if (!name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                && !command.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var loop = emote.ActionTimeline.FirstOrDefault(t => t.RowId != 0 && t.IsValid && t.Value.IsLoop);
            if (loop.RowId == 0)
                continue;

            found.Add(new AnimationMatch(name, command, loop.RowId, true));

            if (found.Count >= limit)
                break;
        }

        return found;
    }

    /// <summary>
    /// Clés d'animation contenant un fragment, avec l'émote qui les déclenche s'il y en a une.
    /// </summary>
    /// <remarks>
    /// Outil d'inspection : les conventions de nommage du jeu ne sont documentées nulle part, et
    /// c'est en les lisant qu'on découvre comment il désigne les variantes d'une même émote.
    /// </remarks>
    public IReadOnlyList<string> Search(string fragment, int limit = 40)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return ["Recherche vide."];

        var all = this.TimelineByKey();
        var hits = all.Where(pair => pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                      .ToList();

        // Toujours rendre une ligne, même sans résultat : sinon on ne distingue pas « rien
        // trouvé » de « l'outil n'a pas répondu ».
        var lines = new List<string> { $"Recherche « {fragment} » : {hits.Count} résultat(s) sur {all.Count} animations." };
        lines.AddRange(hits.Take(limit).Select(this.DescribeKey));

        // Les clés du jeu sont souvent opaques (« emote/act_emot02 ») : chercher aussi du côté
        // des émotes, par leur nom ou leur commande, et détailler leurs animations.
        lines.AddRange(this.SearchEmotes(fragment, limit));
        lines.AddRange(SearchCommands(fragment, limit));
        return lines;
    }

    /// <summary>Actions générales et commandes principales correspondant à la recherche.</summary>
    private static IReadOnlyList<string> SearchCommands(string fragment, int limit)
    {
        var lines = new List<string>();

        foreach (var command in Plugin.DataManager.GetExcelSheet<MainCommand>())
        {
            var name = command.Name.ToString();
            if (string.IsNullOrEmpty(name) || !name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                continue;

            lines.Add($"Commande principale #{command.RowId} : {name}");

            if (lines.Count >= limit)
                break;
        }

        foreach (var action in Plugin.DataManager.GetExcelSheet<GeneralAction>())
        {
            var name = action.Name.ToString();
            if (string.IsNullOrEmpty(name) || !name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                continue;

            lines.Add($"Action générale #{action.RowId} : {name}");
        }

        return lines;
    }

    /// <summary>
    /// Émotes dont le nom ou la commande contient le fragment, avec toutes leurs animations.
    /// </summary>
    /// <remarks>
    /// C'est la vue qui manque pour comprendre les poses : une émote référence plusieurs
    /// animations, et c'est en les listant qu'on voit si une pose est une animation distincte
    /// ou un paramètre séparé.
    /// </remarks>
    private IReadOnlyList<string> SearchEmotes(string fragment, int limit)
    {
        var lines = new List<string>();
        var matches = 0;

        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            var name = emote.Name.ToString();
            var command = emote.TextCommand.IsValid ? emote.TextCommand.Value.Command.ToString() : string.Empty;

            if (string.IsNullOrEmpty(name))
                continue;

            if (!name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                && !command.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++matches > limit)
                break;

            var timelines = emote.ActionTimeline
                                 .Select((t, i) => (Index: i, t.RowId, Ref: t))
                                 .Where(t => t.RowId != 0)
                                 .Select(t =>
                                 {
                                     var key = t.Ref.IsValid ? t.Ref.Value.Key.ToString() : "?";
                                     var loop = t.Ref.IsValid && t.Ref.Value.IsLoop ? " boucle" : string.Empty;
                                     return $"[{t.Index}] #{t.RowId} {key}{loop}";
                                 });

            lines.Add($"Émote « {name} » {command} → {string.Join(" | ", timelines)}");
        }

        if (lines.Count == 0)
            lines.Add($"Aucune émote ne correspond à « {fragment} ».");

        return lines;
    }

    private string DescribeKey(KeyValuePair<string, uint> pair)
    {
        var emotes = this.EmoteByTimeline();
        var sheet = Plugin.DataManager.GetExcelSheet<Emote>();

        var emoteName = emotes.TryGetValue(pair.Value, out var emoteId) && sheet.TryGetRow(emoteId, out var emote)
                            ? $" → {emote.Name}"
                            : string.Empty;

        return $"{pair.Key} (#{pair.Value}){emoteName}";
    }

    /// <summary>
    /// Animations du joueur, par opposition à celles des PNJ, montures et événements.
    /// </summary>
    private static bool IsPlayerAnimation(string key)
        => key.StartsWith("emote/", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("normal/", StringComparison.OrdinalIgnoreCase);

    /// <summary>La clé de la timeline est le suffixe du chemin, extension ôtée.</summary>
    private static uint? MatchKey(string gamePath, Dictionary<string, uint> keys)
    {
        var withoutExtension = gamePath[..^AnimationExtension.Length];

        foreach (var (key, rowId) in keys)
        {
            if (withoutExtension.EndsWith(key, StringComparison.OrdinalIgnoreCase))
                return rowId;
        }

        return null;
    }

    private static string ShortName(string gamePath)
    {
        var slash = gamePath.LastIndexOf('/');
        return slash < 0 ? gamePath : gamePath[(slash + 1)..];
    }

    private Dictionary<string, uint> TimelineByKey()
    {
        if (this.timelineByKey is not null)
            return this.timelineByKey;

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeline in Plugin.DataManager.GetExcelSheet<ActionTimeline>())
        {
            var key = timeline.Key.ToString();
            if (!string.IsNullOrEmpty(key))
                map.TryAdd(key, timeline.RowId);
        }

        this.log.Debug($"Index des animations : {map.Count} timelines nommées.");
        this.timelineByKey = map;
        return map;
    }

    private Dictionary<uint, uint> EmoteByTimeline()
    {
        if (this.emoteByTimeline is not null)
            return this.emoteByTimeline;

        var map = new Dictionary<uint, uint>();

        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            foreach (var timeline in emote.ActionTimeline.Where(t => t.RowId != 0))
                map.TryAdd(timeline.RowId, emote.RowId);
        }

        this.emoteByTimeline = map;
        return map;
    }
}
