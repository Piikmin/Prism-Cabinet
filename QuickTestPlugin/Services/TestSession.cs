using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Glamourer.Api.Enums;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Orchestration d'un test de mod : photographie de l'apparence, application temporaire du mod,
/// redessin, puis restauration à l'identique.
/// </summary>
/// <remarks>
/// Rien n'est écrit dans la configuration de Penumbra : le test repose sur les réglages
/// temporaires joueur, retirés en bloc à la restauration comme au déchargement du plugin.
/// </remarks>
public sealed class TestSession : IDisposable
{
    /// <summary>
    /// Index du joueur local dans la table d'objets du jeu. Il vaut toujours 0 ; c'est aussi la
    /// valeur qu'attendent les API « Player » de Penumbra.
    /// </summary>
    private const int PlayerObjectIndex = Plugin.PlayerObjectIndex;

    private readonly ModIntegrationHub integrations;
    private readonly EquipResolver equipResolver;
    private readonly CustomizationResolver customizationResolver;
    private readonly ModOptionCatalog optionCatalog;
    private readonly EmotePlayer emotePlayer;
    private readonly PrerequisiteStore prerequisites;
    private readonly AnimationIdentifier animationIdentifier;
    private readonly AnimationBindingStore animationBindings;
    private readonly Configuration configuration;
    private readonly Localization localization;
    private readonly DiagnosticLog log;

    private string? appearanceSnapshot;
    private string? originalGender;
    private int? appliedPriority;
    private bool appearanceAltered;
    private bool disposed;
    private object? pendingAnimationTicket;
    private Func<bool>? lastPlayback;
    private string? lastPlaybackLabel;
    private bool lastPlaybackWasAnimation;
    private IReadOnlyList<AnimationMatch> poseChoicesRequiringSelection = [];

    public TestSession(
        ModIntegrationHub integrations,
        EquipResolver equipResolver,
        CustomizationResolver customizationResolver,
        ModOptionCatalog optionCatalog,
        EmotePlayer emotePlayer,
        AnimationIdentifier animationIdentifier,
        AnimationBindingStore animationBindings,
        PrerequisiteStore prerequisites,
        Configuration configuration,
        Localization localization,
        DiagnosticLog log)
    {
        this.emotePlayer = emotePlayer;
        this.prerequisites = prerequisites;
        this.animationIdentifier = animationIdentifier;
        this.animationBindings = animationBindings;
        this.integrations = integrations;
        this.equipResolver = equipResolver;
        this.customizationResolver = customizationResolver;
        this.optionCatalog = optionCatalog;
        this.configuration = configuration;
        this.localization = localization;
        this.log = log;
    }

    /// <summary>
    /// Émis quand un mod vient d'être appliqué, pour que le catalogue le marque comme jugé.
    /// </summary>
    /// <remarks>
    /// Un événement plutôt qu'une dépendance : la session orchestre le test, elle n'a pas à
    /// connaître la liste qui l'affiche.
    /// </remarks>
    public event Action<ModInfo>? OnTested;

    public TestState State { get; private set; } = TestState.Idle;

    public ModInfo? SelectedMod { get; private set; }

    /// <summary>Mod effectivement appliqué, qui peut différer de la sélection courante.</summary>
    public ModInfo? AppliedMod { get; private set; }

    public bool HasSelection => this.SelectedMod is not null;

    /// <summary>
    /// Un mod déjà appliqué n'empêche pas d'en tester un autre : le test remplace, en
    /// restaurant l'état d'origine avant d'appliquer la nouvelle sélection.
    /// </summary>
    public bool CanTest => this.BlockedReason is null;

    /// <summary>Vrai dès qu'il y a quelque chose à défaire, mod appliqué ou apparence modifiée.</summary>
    public bool CanRestore =>
        !this.disposed
        && (this.integrations.Penumbra.IsAvailable || this.integrations.Glamourer.IsAvailable)
        && (this.appearanceAltered || this.State is TestState.Applied or TestState.Restoring or TestState.Failed);

    /// <summary>Vrai quand la sélection affichée est déjà le mod effectivement testé.</summary>
    public bool IsSelectedApplied =>
        this.State is TestState.Applied
        && this.SelectedMod is { } selected
        && this.AppliedMod is { } applied
        && selected.Directory == applied.Directory;

    public bool CanReplayLastAnimation => this.IsSelectedApplied && this.lastPlayback is not null;

    public bool LastPlaybackWasAnimation => this.lastPlaybackWasAnimation;

    /// <summary>Variantes reconnues mais trop nombreuses pour en lancer une arbitrairement.</summary>
    public IReadOnlyList<AnimationMatch> PoseChoicesRequiringSelection => this.poseChoicesRequiringSelection;

    /// <summary>Vrai si lancer un test va d'abord défaire le test courant.</summary>
    public bool TestWouldReplace => this.AppliedMod is not null || this.appearanceAltered;

    /// <summary>Raison lisible pour laquelle un test ne peut pas démarrer, ou null s'il le peut.</summary>
    public string? BlockedReason
    {
        get
        {
            if (this.disposed)
                return this.localization.T("Session terminée.", "Session ended.");

            if (!this.integrations.Penumbra.IsAvailable)
                return this.localization.T(
                    $"Penumbra requis : {this.integrations.Penumbra.StatusText}.",
                    $"Penumbra required: {this.integrations.Penumbra.StatusText}.");

            if (!Plugin.PlayerState.IsLoaded)
                return this.localization.T("Aucun personnage connecté.", "No character is logged in.");

            if (!this.HasSelection)
                return this.localization.T("Aucun mod sélectionné.", "No mod selected.");

            return null;
        }
    }

    /// <summary>
    /// Genre dans lequel tester, ou null pour celui du personnage. Appliqué dès qu'il change :
    /// c'est une transformation qu'on veut voir immédiatement, pas au prochain test.
    /// </summary>
    public string? TestGender { get; private set; }

    /// <summary>Ce que le mod sélectionné modifie, lu une fois à la sélection.</summary>
    public ModImpact Impact { get; private set; } = ModImpact.Empty;

    /// <summary>Genres couverts par le mod sélectionné, vide si l'information n'existe pas.</summary>
    public IReadOnlySet<string> Coverage => this.Impact.GenderCoverage;

    /// <summary>
    /// Genre d'origine du personnage. Une fois l'apparence modifiée, on ne peut plus le relire
    /// sur l'acteur : c'est celui d'avant qui sert de référence à l'interface.
    /// </summary>
    public string CurrentGender => this.originalGender ?? PlayerIdentity.Current().Gender;

    public void SelectMod(ModInfo? mod)
    {
        this.ClearAnimationMemory();
        this.SelectedMod = mod;
        this.Impact = mod is { } selected ? this.ReadImpact(selected) : ModImpact.Empty;

        if (this.State is TestState.Idle or TestState.Ready or TestState.Failed)
            this.State = mod is null ? TestState.Idle : TestState.Ready;

        if (mod is not null)
            this.log.Info($"Mod sélectionné : {mod.Value.Label}");
    }

    /// <summary>Applique le mod sélectionné au personnage local.</summary>
    public bool Test(bool playAnimation = true)
    {
        if (!this.CanTest || this.SelectedMod is not { } mod)
        {
            this.log.Warning($"Test refusé : {this.BlockedReason ?? "état incompatible"}");
            return false;
        }

        this.ClearAnimationMemory();

        // Tester un second mod remplace le premier : on défait tout avant, sinon son
        // équipement et sa coiffure resteraient posés par-dessus le nouveau.
        if (this.TestWouldReplace)
        {
            var previousGender = this.TestGender;
            if (!this.Restore())
            {
                this.TestGender = previousGender;
                return false;
            }

            this.TestGender = previousGender;
        }

        this.State = TestState.Applying;
        this.log.Info($"Test de « {mod.Label} ».");

        // Best-effort : sans photo d'apparence le test reste valable, seule la restauration
        // fine de Glamourer sera indisponible.
        this.EnsureSnapshot();

        // Les prérequis d'abord : un mod de vêtement posé sur un corps qui n'est pas encore actif
        // ne montrerait rien. Chacun garde ses propres options et sa priorité.
        int? highestPrerequisite = null;

        foreach (var prerequisite in this.prerequisites.For(mod))
        {
            var required = this.optionCatalog.OverridesFor(prerequisite);
            var applied = this.integrations.Penumbra.TryApplyMod(
                PlayerObjectIndex, prerequisite, required, this.ShouldFillEmptyGroups(required));

            if (applied is not { } prerequisitePriority)
            {
                this.State = TestState.Failed;
                this.log.Error(
                    $"Le prérequis « {prerequisite.Label} » n'a pas pu être appliqué ; " +
                    "le test est annulé pour éviter un résultat trompeur.");
                this.integrations.Penumbra.TryRemoveOurSettings(PlayerObjectIndex);
                return false;
            }

            highestPrerequisite = Math.Max(highestPrerequisite ?? prerequisitePriority, prerequisitePriority);
            this.log.Info($"Prérequis appliqué : « {prerequisite.Label} » (priorité {prerequisitePriority}).");
        }

        var overrides = this.optionCatalog.OverridesFor(mod);

        // La priorité est un nombre, pas un ordre : sans l'imposer, rien ne garantit que le mod
        // testé l'emporte sur ses prérequis en cas de conflit de fichiers.
        var testPriority = highestPrerequisite is { } floor ? floor + 1 : (int?)null;

        var appliedPriority = this.integrations.Penumbra.TryApplyMod(
            PlayerObjectIndex, mod, overrides, this.ShouldFillEmptyGroups(overrides), testPriority);

        if (appliedPriority is not { } testAppliedPriority)
        {
            this.State = TestState.Failed;
            this.log.Error("Application échouée ; retrait de tout réglage partiel.");
            this.integrations.Penumbra.TryRemoveOurSettings(PlayerObjectIndex);
            return false;
        }

        this.AppliedMod = mod;
        this.appliedPriority = testAppliedPriority;
        this.OnTested?.Invoke(mod);

        var changed = this.integrations.Penumbra.GetChangedItems(mod);
        var equipTargets = this.equipResolver.Resolve(changed.Keys);

        // Le genre change le modèle de corps entier : il passe avant l'équipement et les autres
        // customisations, qui viennent se poser dessus.
        this.ApplyTestGender();
        this.Isolate(equipTargets);
        this.AutoEquip(equipTargets, changed.Count);
        this.AutoCustomize(changed);

        this.integrations.Penumbra.Redraw(PlayerObjectIndex);
        this.State = TestState.Applied;
        this.log.Info($"« {mod.Label} » appliqué.");

        if (playAnimation)
            this.ScheduleAutomaticAnimation(mod);

        return true;
    }

    /// <summary>
    /// Équipe les pièces que le mod modifie, sans quoi un mod d'équipement reste invisible tant
    /// que le personnage ne porte pas la pièce concernée.
    /// </summary>
    private void AutoEquip(IReadOnlyList<EquipTarget> targets, int changedCount)
    {
        if (!this.configuration.AutoEquip || !this.CanAlterAppearance("Équipement automatique"))
            return;

        if (targets.Count == 0)
        {
            this.log.Info($"Aucun objet équipable parmi les {changedCount} éléments modifiés.");
            return;
        }

        foreach (var target in targets)
            this.integrations.Glamourer.TryEquip(PlayerObjectIndex, target);
    }

    /// <summary>
    /// Emplacements vidés pendant un test. Les armes en sont exclues : les retirer n'aide pas à
    /// juger un mod et modifie la posture du personnage.
    /// </summary>
    private static readonly ApiEquipSlot[] StrippableSlots =
    [
        ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs, ApiEquipSlot.Feet,
        ApiEquipSlot.Ears, ApiEquipSlot.Neck, ApiEquipSlot.Wrists, ApiEquipSlot.RFinger, ApiEquipSlot.LFinger,
    ];

    /// <summary>
    /// Vide tout ce que le mod ne concerne pas, pour ne laisser voir que lui.
    /// </summary>
    private void Isolate(IReadOnlyList<EquipTarget> targets)
    {
        if (!this.configuration.IsolateEquipment || !this.CanAlterAppearance("Isolation du mod"))
            return;

        var kept = targets.Select(t => t.Slot).ToHashSet();
        var cleared = 0;

        foreach (var slot in StrippableSlots)
        {
            if (kept.Contains(slot))
                continue;

            if (this.integrations.Glamourer.TryUnequip(PlayerObjectIndex, slot))
                cleared++;
        }

        this.log.Info($"{cleared} emplacement(s) vidé(s) pour isoler le mod.");
    }

    /// <summary>
    /// Bascule le personnage dans le genre demandé, pour tester un mod qui ne couvre pas le
    /// sien.
    /// </summary>
    /// <remarks>
    /// Glamourer emploie 0 pour le masculin et 1 pour le féminin, même convention que le champ
    /// <c>Sex</c> de Dalamud. Le champ est écrit par le même chemin vérifié que les autres
    /// customisations, donc avec la même garantie : si Glamourer ne l'expose pas, on renonce.
    /// </remarks>
    private void ApplyTestGender()
    {
        if (this.TestGender is { } target && target != this.CurrentGender)
            this.ApplyGender(target);
    }

    /// <summary>
    /// Retient le genre demandé. Il n'est appliqué immédiatement que pendant un test déjà actif ;
    /// avant le test, le modifier visuellement créerait une restauration concurrente inutile.
    /// </summary>
    public void SetTestGender(string? gender)
    {
        if (this.TestGender == gender)
            return;

        this.TestGender = gender;
        if (this.IsSelectedApplied)
            this.ApplyGender(gender ?? this.CurrentGender);
    }

    private void ApplyGender(string target)
    {
        if (!this.CanAlterAppearance("Changement de genre"))
            return;

        var value = (byte)(target == "Female" ? 1 : 0);
        if (this.integrations.Glamourer.TryApplyCustomization(PlayerObjectIndex, "Gender", value))
        {
            if (this.emotePlayer.IsHolding)
                this.RedrawAndReplay();
            else
                this.integrations.Penumbra.Redraw(PlayerObjectIndex);
        }
    }

    /// <summary>
    /// Photographie l'apparence si ce n'est pas déjà fait. La première modification, quelle
    /// qu'elle soit, fixe l'état de référence - les suivantes s'empilent dessus.
    /// </summary>
    private void EnsureSnapshot()
    {
        if (this.appearanceSnapshot is not null || !this.integrations.Glamourer.IsAvailable)
            return;

        this.appearanceSnapshot = this.integrations.Glamourer.CaptureState(PlayerObjectIndex);

        if (this.appearanceSnapshot is not null)
            this.originalGender = PlayerIdentity.Current().Gender;
    }

    /// <summary>
    /// Inventaire de ce qu'un mod modifie, lu une fois à la sélection plutôt qu'à chaque frame.
    /// </summary>
    private ModImpact ReadImpact(ModInfo mod)
    {
        if (!this.integrations.Penumbra.IsAvailable)
            return ModImpact.Empty;

        var changed = this.integrations.Penumbra.GetChangedItems(mod);

        var equipment = this.equipResolver.Resolve(changed.Keys);
        var customizations = CustomizationResolver.ResolveAll(changed);

        // Tout ce qu'aucun automatisme ne couvre : animations, émotes, VFX, montures.
        var handled = customizations.Select(c => c.Label).ToHashSet();
        var others = changed.Keys
                            .Where(name => !handled.Contains(name) && !this.equipResolver.Knows(name))
                            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                            .Select(name => ChangedItemDescriber.Describe(name, changed[name]))
                            .ToList();

        var coverage = CustomizationResolver.GenderCoverage(customizations);
        var heuristic = false;

        // Un mod purement vestimentaire ne porte pas cette information dans ses données : les
        // noms de ses options sont alors le seul indice disponible.
        if (coverage.Count == 0)
        {
            coverage = this.GuessCoverage(mod);
            heuristic = coverage.Count > 0;
        }

        return new ModImpact(equipment, customizations, others, coverage, heuristic);
    }

    /// <summary>
    /// Déduit les genres couverts des noms de groupes et d'options. Heuristique assumée : ces
    /// libellés sont du texte libre, un mod qui n'en dit rien ne sera pas deviné.
    /// </summary>
    private IReadOnlySet<string> GuessCoverage(ModInfo mod)
    {
        var labels = this.integrations.Penumbra.GetAvailableSettings(mod)
                         .SelectMany(g => g.Options.Append(g.Name));

        return labels.Select(PlayerIdentity.GenderOf)
                     .Where(g => g is not null)
                     .Select(g => g!)
                     .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Fait porter au personnage la customisation que le mod remplace : un mod de coiffure ne
    /// substitue qu'un modèle précis, invisible tant qu'on porte une autre coupe.
    /// </summary>
    private void AutoCustomize(IReadOnlyDictionary<string, object?> changed)
    {
        if (!this.configuration.AutoApplyCustomization || !this.CanAlterAppearance("Customisation automatique"))
            return;

        var targets = this.customizationResolver.Resolve(changed);
        if (targets.Count == 0)
            return;

        foreach (var index in targets.Select(t => t.Index).Distinct())
        {
            if (this.customizationResolver.ValueFor(targets, index) is { } value)
                this.integrations.Glamourer.TryApplyCustomization(PlayerObjectIndex, index, value);
        }
    }

    /// <summary>
    /// Vrai si l'on peut modifier l'apparence sans risque : Glamourer répond, et l'état
    /// d'origine a bien été photographié pour pouvoir revenir en arrière.
    /// </summary>
    private bool CanAlterAppearance(string operation)
    {
        if (!this.integrations.Glamourer.IsAvailable)
        {
            this.log.Warning($"{operation} impossible : Glamourer indisponible.");
            return false;
        }

        this.EnsureSnapshot();

        if (this.appearanceSnapshot is null)
        {
            this.log.Warning($"{operation} annulée : aucun état d'apparence à restaurer ensuite.");
            return false;
        }

        // Marqué avant l'opération : mieux vaut proposer une restauration inutile que d'oublier
        // une modification partiellement appliquée.
        this.appearanceAltered = true;
        return true;
    }

    /// <summary>
    /// Joue une émote depuis l'interface. Si le mod vient d'être appliqué, la lecture attend la
    /// fin du redessin ; sinon Penumbra peut effacer l'animation juste après son lancement.
    /// </summary>
    public bool PlayEmote(uint emoteId, string label)
    {
        var needsRedraw = !this.IsSelectedApplied;
        if (!this.EnsureApplied())
            return false;

        return needsRedraw
                   ? this.SchedulePlayback(label, () => this.emotePlayer.TryPlay(emoteId, label))
                   : this.PlayAndRemember(label, () => this.emotePlayer.TryPlay(emoteId, label));
    }

    /// <summary>Joue une animation connue en respectant le redessin éventuel du test.</summary>
    public bool PlayAnimation(AnimationMatch animation)
    {
        this.poseChoicesRequiringSelection = [];
        var needsRedraw = !this.IsSelectedApplied;
        if (!this.EnsureApplied())
            return false;

        bool Play() => this.emotePlayer.TryPlayTimeline(
            animation.TimelineId,
            animation.Label,
            animation.Pose,
            animation.PoseFamily,
            animation.EmoteId);

        return needsRedraw
                   ? this.SchedulePlayback(animation.Label, Play)
                   : this.PlayAndRemember(animation.Label, Play);
    }

    /// <summary>
    /// Réapplique une option puis joue son animation une fois le redessin achevé.
    /// </summary>
    public bool PreviewAnimation(ModInfo mod, AnimationMatch animation)
    {
        this.poseChoicesRequiringSelection = [];
        this.emotePlayer.Stop();

        if (!this.EnsureApplied() || !this.ReapplyOptions(mod))
            return false;

        return this.SchedulePlayback(
            animation.Label,
            () => this.emotePlayer.TryPlayTimeline(
                animation.TimelineId,
                animation.Label,
                animation.Pose,
                animation.PoseFamily,
                animation.EmoteId));
    }

    /// <summary>
    /// Lance automatiquement l'unique animation identifiable du test. En cas d'ambiguïté,
    /// aucune animation n'est choisie arbitrairement : les variantes restent dans Options.
    /// </summary>
    private void ScheduleAutomaticAnimation(ModInfo mod)
    {
        var candidates = new List<AnimationMatch>();
        var overrides = this.optionCatalog.OverridesFor(mod);

        foreach (var (group, options) in overrides)
        {
            foreach (var option in options)
            {
                candidates.AddRange(this.animationBindings.Resolve(mod, group, option)
                                        .Select(binding => new AnimationMatch(
                                            binding.Label,
                                            "association manuelle",
                                            binding.TimelineId,
                                            false)));
                candidates.AddRange(this.animationIdentifier.FromLabel(option));
            }
        }

        if (candidates.Count == 0)
        {
            candidates.AddRange(this.animationBindings.Resolve(mod, null, null)
                                    .Select(binding => new AnimationMatch(
                                        binding.Label,
                                        "association manuelle",
                                        binding.TimelineId,
                                        false)));
        }

        var distinct = candidates
                       .DistinctBy(candidate => (candidate.TimelineId, candidate.Pose, candidate.EmoteId))
                       .ToList();

        if (distinct.Count == 1)
        {
            var animation = distinct[0];
            this.SchedulePlayback(
                animation.Label,
                () => this.emotePlayer.TryPlayTimeline(
                    animation.TimelineId,
                    animation.Label,
                    animation.Pose,
                    animation.PoseFamily,
                    animation.EmoteId));
            return;
        }

        if (distinct.Count > 1)
        {
            this.poseChoicesRequiringSelection = distinct.Where(candidate => candidate.Pose is not null).ToList();
            this.log.Info($"{distinct.Count} animations possibles : choisis la variante dans Options.");
            return;
        }

        this.poseChoicesRequiringSelection = [];

        var emotes = this.Impact.Others
                         .Where(change => change.EmoteId is not null)
                         .DistinctBy(change => change.EmoteId)
                         .ToList();
        if (emotes.Count == 1 && emotes[0].EmoteId is { } emoteId)
        {
            var change = emotes[0];
            this.SchedulePlayback(change.Label, () => this.emotePlayer.TryPlay(emoteId, change.Label));
        }
        else if (emotes.Count > 1)
        {
            this.log.Info($"{emotes.Count} émotes possibles : aucune lecture automatique.");
        }
    }

    public bool ReplayLastAnimation()
    {
        if (!this.CanReplayLastAnimation || this.lastPlayback is not { } playback)
            return false;

        this.CancelPendingAnimation();
        return this.PlayAndRemember(this.lastPlaybackLabel ?? "animation", playback);
    }

    public void StopAnimation()
    {
        this.CancelPendingAnimation();
        this.emotePlayer.Stop();
    }

    private bool PlayAndRemember(string label, Func<bool> playback)
    {
        if (!playback())
            return false;

        this.lastPlayback = playback;
        this.lastPlaybackLabel = label;
        this.lastPlaybackWasAnimation = this.emotePlayer.IsAnimating;
        return true;
    }

    private bool SchedulePlayback(
        string label,
        Func<bool> playback,
        int delayMs = 180,
        bool remember = true)
    {
        var ticket = new object();
        this.pendingAnimationTicket = ticket;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (this.disposed
                        || !ReferenceEquals(this.pendingAnimationTicket, ticket)
                        || this.State is not TestState.Applied)
                    {
                        return;
                    }

                    this.pendingAnimationTicket = null;
                    var played = remember
                                     ? this.PlayAndRemember(label, playback)
                                     : playback();
                    if (!played)
                        this.log.Warning($"Lecture automatique de « {label} » impossible.");
                });
            }
            catch (Exception ex)
            {
                this.log.Error($"Lecture différée de « {label} » impossible : {ex.Message}");
            }
        });

        this.log.Debug($"Lecture de « {label} » programmée après le redessin.");
        return true;
    }

    private void CancelPendingAnimation() => this.pendingAnimationTicket = null;

    private void ClearAnimationMemory()
    {
        this.CancelPendingAnimation();
        this.lastPlayback = null;
        this.lastPlaybackLabel = null;
        this.lastPlaybackWasAnimation = false;
        this.poseChoicesRequiringSelection = [];
    }

    /// <summary>
    /// Réapplique le mod en cours de test avec les options courantes, pour que le personnage
    /// reflète immédiatement un changement fait dans la fenêtre d'options.
    /// </summary>
    /// <remarks>
    /// L'état d'apparence n'est pas rephotographié : celui pris au démarrage du test reste la
    /// référence à restaurer. Les pièces équipées et la customisation ne sont pas rejouées non
    /// plus - Penumbra rapporte les objets modifiés indépendamment des réglages, elles seraient
    /// donc identiques.
    /// </remarks>
    public bool ReapplyOptions(ModInfo mod)
    {
        if (this.disposed || this.State is not TestState.Applied)
            return false;

        if (this.AppliedMod is not { } applied || applied.Directory != mod.Directory)
            return false;

        var overrides = this.optionCatalog.OverridesFor(applied);

        var reappliedPriority = this.integrations.Penumbra.TryApplyMod(
            PlayerObjectIndex,
            applied,
            overrides,
            this.ShouldFillEmptyGroups(overrides),
            this.appliedPriority);

        if (reappliedPriority is not { } priority)
        {
            this.State = TestState.Failed;
            return false;
        }

        this.appliedPriority = priority;

        // Un redessin remet le personnage dans l'état connu du jeu, donc coupe l'animation
        // forcée. Tant qu'elle tourne, on s'en abstient et on observe si le changement prend
        // effet malgré tout - c'est ce qui décidera de la faisabilité d'un mode « parcourir ».
        if (this.emotePlayer.IsHolding)
        {
            this.log.Info("Options réappliquées sans redessin : une animation est en cours.");
            return true;
        }

        this.integrations.Penumbra.Redraw(PlayerObjectIndex);
        return true;
    }

    /// <summary>
    /// Le remplissage automatique cesse dès que l'utilisateur a choisi quelque chose pour ce mod.
    /// </summary>
    /// <remarks>
    /// Sur un mod-catalogue, combler tous les groupes vides active une dizaine d'animations à la
    /// fois, dont des variantes destinées à d'autres races : le personnage change alors de
    /// morphologie sans raison apparente. Dès qu'une option est choisie dans <em>ce</em> mod, on
    /// ne teste plus que celle-ci - indépendamment du fait qu'un prérequis, lui, reste sans
    /// sélection et continue donc d'être comblé.
    /// </remarks>
    private bool ShouldFillEmptyGroups(IReadOnlyDictionary<string, IReadOnlyList<string>> overrides)
        => this.configuration.AutoSelectEmptyGroups && overrides.Count == 0;

    /// <summary>Redessine puis reprend l'animation lorsque Penumbra a terminé.</summary>
    public void RedrawAndReplay()
    {
        this.integrations.Penumbra.Redraw(PlayerObjectIndex);
        if (this.emotePlayer.IsHolding)
        {
            this.SchedulePlayback("animation en cours", () =>
            {
                var replayed = this.emotePlayer.Replay();
                if (replayed)
                    this.log.Info("Personnage redessiné, animation reprise.");

                return replayed;
            }, remember: false);
        }
        else
        {
            this.log.Info("Personnage redessiné.");
        }
    }

    /// <summary>
    /// Applique le mod sélectionné s'il ne l'est pas déjà. Jouer une émote d'un mod suppose
    /// qu'il soit actif : l'exiger en deux clics serait une formalité inutile.
    /// </summary>
    public bool EnsureApplied()
    {
        if (this.AppliedMod is { } applied && this.SelectedMod is { } selected
                                            && applied.Directory == selected.Directory
                                            && this.State is TestState.Applied
                                            && this.integrations.Penumbra.IsAvailable)
        {
            return true;
        }

        this.Test(playAnimation: false);
        return this.AppliedMod is not null
               && this.State is TestState.Applied
               && this.integrations.Penumbra.IsAvailable;
    }

    /// <summary>Retire le mod de test et remet l'apparence photographiée.</summary>
    public bool Restore()
    {
        if (!this.CanRestore)
        {
            this.log.Warning("Restauration refusée : rien à restaurer.");
            return false;
        }

        this.ClearAnimationMemory();
        this.State = TestState.Restoring;

        // Une animation forcée est un champ qui reste en place : la relâcher fait partie de la
        // remise en état, au même titre que les réglages et l'apparence.
        this.emotePlayer.Stop();

        var requiresPenumbraCleanup = this.AppliedMod is not null
                                      || this.State is TestState.Applied
                                      or TestState.Applying
                                      or TestState.Restoring;
        var removed = !requiresPenumbraCleanup
                      || (this.integrations.Penumbra.IsAvailable
                          && this.integrations.Penumbra.TryRemoveOurSettings(PlayerObjectIndex));

        // L'apparence est remise avant le redessin, pour que celui-ci reflète déjà
        // l'équipement d'origine plutôt que celui posé pour le test.
        var appearanceRestored = true;
        if (this.appearanceSnapshot is { } snapshot)
        {
            appearanceRestored = this.integrations.Glamourer.IsAvailable
                                  && this.integrations.Glamourer.RestoreState(PlayerObjectIndex, snapshot);

            if (!appearanceRestored)
                this.log.Error("L'état d'apparence n'a pas pu être restauré ; le snapshot est conservé pour une nouvelle tentative.");
        }

        if (this.integrations.Penumbra.IsAvailable)
            this.integrations.Penumbra.Redraw(PlayerObjectIndex);

        if (removed && appearanceRestored)
        {
            this.appearanceSnapshot = null;
            this.originalGender = null;
            this.appearanceAltered = false;
            this.TestGender = null;
            this.AppliedMod = null;
            this.appliedPriority = null;
            this.State = this.HasSelection ? TestState.Ready : TestState.Idle;
            this.log.Info("État d'origine restauré.");
            return true;
        }

        this.State = TestState.Failed;
        this.log.Error(
            "Restauration incomplète. Vérifie que Penumbra et Glamourer sont chargés, puis réessaie " +
            "avant de désactiver ou réactiver la collection.");
        return false;
    }

    /// <summary>Recalcule l'état après un changement de disponibilité des intégrations.</summary>
    public void Reevaluate()
    {
        if (this.State is TestState.Idle && this.HasSelection)
            this.State = TestState.Ready;
    }

    /// <summary>
    /// Filet de sécurité : un plugin déchargé pendant qu'un mod de test est appliqué laisserait
    /// le personnage modifié sans moyen évident de revenir en arrière.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
            return;

        this.ClearAnimationMemory();
        this.disposed = true;

        var modApplied = this.State is TestState.Applied or TestState.Applying or TestState.Restoring;
        if (!modApplied && !this.appearanceAltered)
            return;

        this.log.Info("Déchargement en cours de test : remise en état.");
        this.emotePlayer.Stop();

        if (modApplied)
            this.integrations.Penumbra.TryRemoveOurSettings(PlayerObjectIndex);

        // L'apparence aussi : un genre ou une coiffure de test ne doit pas survivre au plugin.
        if (this.appearanceAltered && this.appearanceSnapshot is { } snapshot)
            this.integrations.Glamourer.RestoreState(PlayerObjectIndex, snapshot);

        this.integrations.Penumbra.Redraw(PlayerObjectIndex);
    }
}
