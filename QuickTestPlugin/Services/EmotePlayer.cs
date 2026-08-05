using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Déclenche une émote sur le personnage local pour juger l'animation d'un mod.
/// </summary>
/// <remarks>
/// Seul endroit de QuickTest qui ne passe pas par une API publiée : la lecture d'animation
/// s'obtient en écrivant dans les structures du client, comme le font certains outils similaires.
/// <list type="bullet">
/// <item>Les paramètres ne sont pas nommés dans ces structures : la sémantique a été
/// établie empiriquement.</item>
/// <item>La disposition des structures du jeu change à chaque patch ; ce code est donc le
/// premier à casser, et il doit échouer sans bruit plutôt qu'entraîner le reste.</item>
/// <item>L'effet est purement local : le serveur n'en sait rien, les autres joueurs ne voient
/// rien. Hors GPose, la machine à états du jeu reprend la main et peut interrompre l'animation
/// au moindre mouvement - c'est une limite de fiabilité, pas un interdit.</item>
/// </list>
/// Deux mécanismes coexistent : forcer l'animation de base (<c>BaseOverride</c>), et laisser le
/// jeu entrer dans une émote pour respecter les poses. Tout ce qui est appliqué - par l'un ou
/// l'autre, sur quel acteur, avec quel état d'origine - est tenu dans un unique
/// <see cref="Hold"/>, que relâchement, reprise et interrogation traitent uniformément.
/// </remarks>
public sealed class EmotePlayer
{
    /// <summary>
    /// Index de la copie du joueur en GPose. Le GPose duplique les acteurs dans une plage
    /// dédiée de la table d'objets ; c'est cette copie qui est affichée.
    /// </summary>
    private const int GPosePlayerIndex = 201;

    private readonly DiagnosticLog log;
    private readonly Localization localization;

    private Hold? held;

    public EmotePlayer(DiagnosticLog log, Localization localization)
    {
        this.log = log;
        this.localization = localization;
    }

    /// <summary>
    /// Le GPose fige la scène : l'animation y tient sans être interrompue. Ailleurs elle part
    /// quand même, mais la machine à états du jeu peut la couper.
    /// </summary>
    public static bool IsReliable => Plugin.ClientState.IsGPosing;

    /// <summary>Vrai tant qu'une animation, un mode ou une pose est maintenu par QuickTest.</summary>
    public bool IsHolding => this.held is not null;

    /// <summary>Vrai si le guidage accompagne une animation, et pas seulement une pose debout.</summary>
    public bool IsAnimating
        => this.held is { } hold
           && (hold.OverrideTimeline != 0 || hold.EmoteId != 0 || hold.ModeAltered);

    /// <summary>Description de ce qui est maintenu, pour l'affichage, ou null si rien ne l'est.</summary>
    /// <remarks>
    /// La pose demandée est une consigne pour le joueur, jamais une pose appliquée par le plugin.
    /// </remarks>
    public string? HeldDescription
        => this.held is { } hold
               ? hold.PoseFamily is not null && hold.Pose is { } pose
                     ? this.localization.T(
                         $"{hold.Label} (variante {pose + 1})",
                         $"{hold.Label} (variant {pose + 1})")
                     : hold.Label
               : null;

    /// <summary>
    /// Compare la pose visible à la variante demandée. Cette propriété ne fait que lire l'acteur
    /// local ; elle n'envoie aucune commande et ne modifie aucune préférence du joueur.
    /// </summary>
    public unsafe PoseGuidance? CurrentPoseGuidance
    {
        get
        {
            if (this.held is not { PoseFamily: { } family } hold)
                return null;

            var highest = EmoteController.GetAvailablePoses(family);
            var target = Plugin.ObjectTable[hold.TargetIndex];
            if (target is null || target.Address == IntPtr.Zero)
                return new PoseGuidance(hold.Label, this.HumanPoseFamily(family), hold.Pose, null, highest);

            var character = (Character*)target.Address;
            byte? current = character is not null && character->EmoteController.CurrentPoseType == family
                                ? character->EmoteController.CPoseState
                                : null;

            return new PoseGuidance(hold.Label, this.HumanPoseFamily(family), hold.Pose, current, highest);
        }
    }

    public bool TryPlay(uint emoteRowId, string label)
    {
        if (!Plugin.DataManager.GetExcelSheet<Emote>().TryGetRow(emoteRowId, out var emote))
        {
            this.log.Error($"Émote {emoteRowId} introuvable dans les données du jeu.");
            return false;
        }

        if (IsChangePose(emote))
            return this.PlayNextPose(label);

        var timelines = emote.ActionTimeline.Where(t => t.RowId != 0 && t.IsValid).ToList();
        if (timelines.Count == 0)
        {
            this.log.Warning($"« {label} » n'a aucune animation associée.");
            return false;
        }

        this.log.Debug(
            $"Timelines de « {label} » : " +
            string.Join(", ", timelines.Select(t => $"{t.RowId}{(t.Value.IsLoop ? " (boucle)" : string.Empty)}")));

        // Laisser le contrôleur du jeu entrer dans l'émote conserve son contexte - posture,
        // changement de pose, entrée et boucle. Forcer uniquement sa timeline court-circuitait
        // notamment /cpose.
        return this.PlayThroughGame(emoteRowId, pose: null, family: null, label);
    }

    private static bool IsChangePose(Emote emote)
    {
        if (!emote.TextCommand.IsValid)
            return false;

        var command = emote.TextCommand.Value;
        return new[]
               {
                   command.Command.ToString(),
                   command.ShortCommand.ToString(),
                   command.Alias.ToString(),
                   command.ShortAlias.ToString(),
               }
               .Select(value => value.TrimStart('/'))
               .Any(value => value.Equals("cpose", StringComparison.OrdinalIgnoreCase)
                             || value.Equals("changepose", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// /cpose ne joue pas une timeline fixe : il avance dans les variantes de la posture
    /// courante. Le plugin prépare uniquement le guidage vers la suivante ; le joueur exécute
    /// lui-même la commande du jeu.
    /// </summary>
    private unsafe bool PlayNextPose(string label)
    {
        var target = ResolveTarget();
        if (target is null || target.Address == IntPtr.Zero)
        {
            this.log.Warning($"Aucun personnage pour jouer « {label} ».");
            return false;
        }

        var character = (Character*)target.Address;
        if (character is null)
            return false;

        var family = character->EmoteController.CurrentPoseType;
        var highest = EmoteController.GetAvailablePoses(family);
        var next = (byte)((character->EmoteController.CPoseState + 1) % (highest + 1));
        return this.PoseOnly(next, family.ToString(), label);
    }

    /// <summary>
    /// Joue une animation par son identifiant. Toutes ne sont pas déclenchées par une émote -
    /// marche, saut, poses - et restent pourtant observables.
    /// </summary>
    public bool TryPlayTimeline(
        uint timelineId, string label, byte? pose = null, string? poseFamily = null, uint? emoteId = null)
    {
        if (pose is not { } index)
            return emoteId is { } plainEmote
                       ? this.TryPlay(plainEmote, label)
                       : this.Play(timelineId, label);

        // Quand l'émote est connue, on laisse le contrôleur local du jeu entrer dans sa posture.
        // La variante reste celle du joueur jusqu'à ce qu'il utilise lui-même /cpose.
        if (emoteId is { } id)
            return this.PlayThroughGame(id, index, poseFamily, label);

        // Les poses debout n'ont pas d'émote : le personnage est déjà dans la bonne posture, il
        // n'y a rien à jouer - forcer une animation empêcherait justement la pose de s'appliquer.
        if (poseFamily is not null)
            return this.PoseOnly(index, poseFamily, label);

        this.log.Warning($"La variante {index + 1} de « {label} » n'indique pas sa famille de pose.");
        return this.Play(timelineId, label);
    }

    /// <summary>
    /// Réapplique ce qui est maintenu. Un redessin remet le personnage dans l'état que le jeu
    /// connaît : sans cela, l'animation forcée disparaîtrait au passage.
    /// </summary>
    public bool Replay()
    {
        if (this.held is not { } hold)
            return false;

        // Le chemin par émote se rejoue par le jeu ; le chemin direct réécrit le champ.
        if (hold.EmoteId != 0)
            return this.PlayThroughGame(hold.EmoteId, hold.Pose, hold.PoseFamily?.ToString(), hold.Label);

        return this.WriteOverride(hold.TargetIndex, hold.OverrideTimeline, $"reprise de « {hold.Label} »");
    }

    /// <summary>Rend la main au jeu pour l'animation ou le mode maintenu.</summary>
    public void Stop() => this.Release(announce: true);

    /// <summary>
    /// Défait tout ce que <see cref="held"/> décrit, sur l'acteur où il a été appliqué.
    /// </summary>
    /// <remarks>
    /// L'acteur est retrouvé par l'index mémorisé à l'application, pas résolu à nouveau : entre
    /// temps, l'utilisateur a pu entrer en GPose ou en sortir, et restaurer un état pris sur un
    /// acteur en l'écrivant sur un autre serait pire que de ne rien faire.
    /// </remarks>
    private unsafe void Release(bool announce)
    {
        if (this.held is not { } hold)
            return;

        this.held = null;

        var target = Plugin.ObjectTable[hold.TargetIndex];
        if (target is null || target.Address == IntPtr.Zero)
        {
            // Typiquement une sortie de GPose : la copie a disparu, et son état avec elle.
            this.log.Debug($"Acteur {hold.TargetIndex} disparu, rien à relâcher.");
            return;
        }

        try
        {
            var character = (Character*)target.Address;
            if (character is null)
                return;

            if (hold.OverrideTimeline != 0)
                character->Timeline.BaseOverride = 0;

            // Le mode d'abord : il gouverne la boucle dans laquelle le personnage est maintenu.
            if (hold.ModeAltered)
                character->SetMode(CharacterModes.Normal, 0);

            if (announce)
                this.log.Info("Animation relâchée.");
        }
        catch (Exception ex)
        {
            this.log.Error($"Relâchement impossible : {ex.Message}");
        }
    }

    /// <summary>Maintient une animation en forçant l'animation de base de l'acteur.</summary>
    /// <remarks>
    /// <c>BaseOverride</c> est un champ, pas un appel : la valeur tient jusqu'à sa remise à
    /// zéro, ce qui est justement ce qu'on veut pour observer. Les fonctions d'appel -
    /// <c>Character.PlayTimeline</c>, <c>PlayActionTimeline</c> - ont été essayées d'abord : la
    /// première refuse ces identifiants, la seconde ne produit rien.
    /// </remarks>
    private unsafe bool Play(uint timelineId, string label)
    {
        if (timelineId > ushort.MaxValue)
        {
            this.log.Error($"Identifiant d'animation hors plage pour « {label} » : {timelineId}.");
            return false;
        }

        // Quel que soit le mécanisme précédent, il est défait d'abord : le jeu ne bascule pas
        // proprement d'une animation forcée à une autre, et un BaseOverride résiduel
        // combattrait une émote.
        this.Release(announce: false);

        var target = ResolveTarget();
        if (target is null || target.Address == IntPtr.Zero)
        {
            this.log.Warning($"Aucun personnage pour jouer « {label} ».");
            return false;
        }

        try
        {
            var character = (Character*)target.Address;
            if (character is null)
                return false;

            character->Timeline.BaseOverride = (ushort)timelineId;

            this.held = new Hold
            {
                TargetIndex = target.ObjectIndex,
                Label = label,
                OverrideTimeline = (ushort)timelineId,
            };

            this.log.Info($"Animation maintenue : {label} (timeline {timelineId}, objet {target.ObjectIndex}).");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error($"Lecture de « {label} » impossible : {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Laisse le jeu jouer l'émote, seule voie qui respecte la pose.
    /// </summary>
    /// <remarks>
    /// <c>EmoteController.PlayEmote</c> est la couche de restitution : ce contrôleur existe sur
    /// chaque personnage, y compris les joueurs distants, ce qui exclut qu'il envoie quoi que ce
    /// soit au serveur. Le chemin d'exécution à la demande du joueur est ailleurs -
    /// <c>AgentEmote.ExecuteEmote</c>, avec historique - et n'est pas employé ici. Vérifié en
    /// jeu : aucun message de chat n'est produit.
    /// </remarks>
    private unsafe bool PlayThroughGame(uint emoteId, byte? pose, string? family, string label)
    {
        this.Release(announce: false);

        var target = ResolveTarget();
        if (target is null || target.Address == IntPtr.Zero)
        {
            this.log.Warning($"Aucun personnage pour jouer « {label} ».");
            return false;
        }

        try
        {
            var character = (Character*)target.Address;
            if (character is null)
                return false;

            var familyType = ResolvePoseFamily(family, pose, label, this.log)
                             ?? ResolveEmotePoseFamily(emoteId);

            // La structure d'options porte une table virtuelle : allouée sur la pile, elle doit
            // être initialisée explicitement.
            var options = default(EmoteController.PlayEmoteOption);
            options.VirtualTable = EmoteController.PlayEmoteOption.StaticVirtualTablePointer;

            if (!character->EmoteController.PlayEmote(emoteId, &options))
            {
                this.log.Warning($"Le jeu a refusé de jouer l'émote {emoteId} ({label}).");
                return false;
            }

            // Jouer l'émote ne suffit pas à y maintenir le personnage : c'est le mode qui le
            // place dans la boucle. Mode et paramètre viennent de la feuille EmoteMode - le
            // paramètre est l'identifiant du mode d'émote, pas la pose.
            var (mode, modeParam) = ReadEmoteMode(emoteId);
            var modeAltered = modeParam != 0;

            if (modeAltered)
                character->SetMode(mode, modeParam);

            this.held = new Hold
            {
                TargetIndex = target.ObjectIndex,
                Label = label,
                EmoteId = emoteId,
                Pose = pose,
                PoseFamily = familyType,
                ModeAltered = modeAltered,
            };

            this.log.Info(
                $"Émote {emoteId} jouée par le jeu, pose {pose} ({familyType?.ToString() ?? "famille inconnue"}), " +
                $"mode {mode} (param {modeParam}), objet {target.ObjectIndex} : {label}.");

            return true;
        }
        catch (Exception ex)
        {
            this.log.Error($"Lecture par le jeu impossible : {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Demande une pose sans rien jouer, pour les postures qu'aucune émote ne déclenche.
    /// </summary>
    /// <remarks>
    /// Rester debout n'est pas une émote : le personnage y est déjà, et les poses debout se
    /// choisissent comme les autres. Forcer une animation ici reviendrait à écraser la posture
    /// dont on veut justement voir la variante.
    /// </remarks>
    private bool PoseOnly(byte pose, string family, string label)
    {
        this.Release(announce: false);

        var target = ResolveTarget();
        if (target is null || target.Address == IntPtr.Zero)
        {
            this.log.Warning($"Aucun personnage pour « {label} ».");
            return false;
        }

        var familyType = ResolvePoseFamily(family, pose, label, this.log);
        if (familyType is null)
            return false;

        this.held = new Hold
        {
            TargetIndex = target.ObjectIndex,
            Label = label,
            Pose = pose,
            PoseFamily = familyType,
        };

        this.log.Info(
            $"Variante {pose + 1} demandée ({familyType}) sur l'objet {target.ObjectIndex} : " +
            $"le joueur doit utiliser /cpose.");
        return true;
    }

    private unsafe bool WriteOverride(int targetIndex, ushort timelineId, string operation)
    {
        var target = Plugin.ObjectTable[targetIndex];
        if (target is null || target.Address == IntPtr.Zero)
        {
            this.log.Warning($"Acteur {targetIndex} introuvable pour « {operation} ».");
            return false;
        }

        try
        {
            var character = (Character*)target.Address;
            if (character is null)
                return false;

            character->Timeline.BaseOverride = timelineId;
            this.log.Debug($"BaseOverride = {timelineId} sur l'objet {targetIndex}.");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error($"Échec de « {operation} » : {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Mode de personnage qu'une émote impose, et son paramètre, lus dans la feuille EmoteMode.
    /// </summary>
    /// <remarks>
    /// Les émotes en boucle - s'asseoir, somnoler - référencent une ligne EmoteMode dont
    /// <c>ConditionMode</c> est le mode à prendre ; le paramètre observé en jeu est
    /// l'identifiant de cette ligne. Une émote sans mode (paramètre 0) ne maintient rien.
    /// </remarks>
    private static (CharacterModes Mode, byte Param) ReadEmoteMode(uint emoteId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Emote>().TryGetRow(emoteId, out var emote)
            || !emote.EmoteMode.IsValid
            || emote.EmoteMode.RowId == 0)
        {
            return (CharacterModes.Normal, 0);
        }

        return ((CharacterModes)emote.EmoteMode.Value.ConditionMode, (byte)emote.EmoteMode.RowId);
    }

    private static EmoteController.PoseType? ResolvePoseFamily(
        string? family,
        byte? pose,
        string label,
        DiagnosticLog log)
    {
        if (pose is null)
            return null;

        if (family is null || !Enum.TryParse<EmoteController.PoseType>(family, out var familyType))
        {
            log.Warning($"Famille de pose inconnue pour « {label} » : guidage indisponible.");
            return null;
        }

        var highest = EmoteController.GetAvailablePoses(familyType);
        if (pose > highest)
        {
            log.Warning(
                $"Variante {pose + 1} demandée pour « {label} », mais {familyType} " +
                $"ne propose que {highest + 1} variante(s).");
            return null;
        }

        return familyType;
    }

    private string HumanPoseFamily(EmoteController.PoseType family) => family switch
    {
        EmoteController.PoseType.Idle => this.localization.T("debout", "standing"),
        EmoteController.PoseType.Sit => this.localization.T("assis", "sitting"),
        EmoteController.PoseType.GroundSit => this.localization.T("assis au sol", "ground sitting"),
        EmoteController.PoseType.Doze => this.localization.T("allongé", "lying down"),
        _ => family.ToString(),
    };

    /// <summary>
    /// Certaines émotes définissent une famille de poses même si le mod n'indique aucune
    /// variante. On peut alors guider le parcours manuel sans inventer de cible.
    /// </summary>
    private static EmoteController.PoseType? ResolveEmotePoseFamily(uint emoteId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Emote>().TryGetRow(emoteId, out var emote)
            || !emote.TextCommand.IsValid)
        {
            return null;
        }

        var command = emote.TextCommand.Value;
        var forms = new[]
        {
            command.Command.ToString(),
            command.ShortCommand.ToString(),
            command.Alias.ToString(),
            command.ShortAlias.ToString(),
        };

        foreach (var form in forms.Select(value => value.TrimStart('/')))
        {
            if (form.Equals("groundsit", StringComparison.OrdinalIgnoreCase))
                return EmoteController.PoseType.GroundSit;

            if (form.Equals("sit", StringComparison.OrdinalIgnoreCase))
                return EmoteController.PoseType.Sit;

            if (form.Equals("doze", StringComparison.OrdinalIgnoreCase))
                return EmoteController.PoseType.Doze;
        }

        return null;
    }

    /// <summary>
    /// Acteur à animer. En GPose, le personnage affiché est une copie placée plus loin dans la
    /// table d'objets : animer l'original ne se verrait pas.
    /// </summary>
    private static IGameObject? ResolveTarget()
    {
        if (!Plugin.ClientState.IsGPosing)
            return Plugin.ObjectTable.LocalPlayer;

        var gposeActor = Plugin.ObjectTable[GPosePlayerIndex];
        return gposeActor ?? Plugin.ObjectTable.LocalPlayer;
    }

    /// <summary>
    /// Tout ce que Prism Cabinet maintient sur un acteur, et ce qu'il faudra défaire.
    /// </summary>
    /// <param name="TargetIndex">Acteur sur lequel l'état a été appliqué.</param>
    /// <param name="Label">Nom affiché, pour le journal et la reprise.</param>
    /// <param name="OverrideTimeline">Animation forcée par BaseOverride, 0 si aucune.</param>
    /// <param name="EmoteId">Émote jouée par le jeu, 0 si le chemin direct a servi.</param>
    /// <param name="Pose">Variante que le joueur doit atteindre avec la commande du jeu.</param>
    /// <param name="ModeAltered">Vrai si le mode du personnage a été changé.</param>
    private sealed record Hold
    {
        public required int TargetIndex { get; init; }

        public required string Label { get; init; }

        public ushort OverrideTimeline { get; init; }

        public uint EmoteId { get; init; }

        public byte? Pose { get; init; }

        /// <summary>Famille de posture observée pour guider le joueur.</summary>
        public EmoteController.PoseType? PoseFamily { get; init; }

        public bool ModeAltered { get; init; }
    }
}
