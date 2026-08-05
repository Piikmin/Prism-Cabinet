using System;
using System.Collections.Generic;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;
using QuickTestPlugin.Models;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Integration;

/// <summary>
/// Intégration Glamourer, adossée au paquet <c>Glamourer.Api</c> (2.8.2).
/// </summary>
/// <remarks>
/// Rôle dans QuickTest : photographier l'état d'apparence avant le redessin provoqué par le
/// test, et le réappliquer à la restauration. Tous les appels utilisent la clé 0, donc sans
/// verrou : QuickTest ne s'approprie jamais l'état du personnage et ne peut pas le laisser
/// bloqué si le plugin est déchargé brutalement.
/// </remarks>
public sealed class GlamourerIntegration : ModIntegrationBase
{
    private const uint UnlockedKey = 0;

    /// <summary>
    /// Deux canaux de teinture, tous deux à « aucune teinture ».
    /// </summary>
    /// <remarks>
    /// Impérativement une <see cref="List{T}"/> et non un tableau : la couche IPC de Dalamud
    /// échoue à convertir un <c>byte[]</c> vers <c>IReadOnlyList&lt;byte&gt;</c> et rejette
    /// l'appel (« blew up when converting from Byte[] »).
    /// </remarks>
    private static readonly IReadOnlyList<byte> NoStains = new List<byte> { 0, 0 };

    private readonly ApiVersion apiVersion;
    private readonly GetStateBase64 getStateBase64;
    private readonly ApplyState applyState;
    private readonly SetItem setItem;
    private readonly GetState getState;

    public GlamourerIntegration(DiagnosticLog log)
        : base(log)
    {
        var pi = Plugin.PluginInterface;

        this.apiVersion = new ApiVersion(pi);
        this.getStateBase64 = new GetStateBase64(pi);
        this.applyState = new ApplyState(pi);
        this.setItem = new SetItem(pi);
        this.getState = new GetState(pi);
    }

    public override string DisplayName => "Glamourer";

    public override string InternalName => "Glamourer";

    /// <remarks>
    /// Attention : la version d'API rapportée par Glamourer (1.8 avec Glamourer 1.7.0.2) ne suit
    /// pas la version du paquet NuGet (2.8.2). Seule la mineure coïncide ; la majeure de rupture
    /// vaut 1. Valeur relevée à l'exécution, pas déduite du numéro de paquet.
    /// </remarks>
    protected override int ExpectedApiMajor => 1;

    protected override (int Major, int Minor) QueryApiVersion() => this.apiVersion.Invoke();

    protected override IEnumerable<IDisposable> CreateLifecycleSubscribers(Action onLifecycleEvent)
    {
        yield return Initialized.Subscriber(Plugin.PluginInterface, onLifecycleEvent);
        yield return Disposed.Subscriber(Plugin.PluginInterface, onLifecycleEvent);
    }

    /// <summary>
    /// Photographie l'état d'apparence courant. Renvoie null si Glamourer ne peut pas le
    /// fournir : ce n'est pas bloquant pour le test, seulement pour la restauration fine.
    /// </summary>
    public string? CaptureState(int objectIndex)
    {
        string? snapshot = null;

        this.TryInvoke("capture de l'état d'apparence", () =>
        {
            var (ec, state) = this.getStateBase64.Invoke(objectIndex, UnlockedKey);

            if (ec is GlamourerApiEc.Success && !string.IsNullOrEmpty(state))
                snapshot = state;
            else
                this.Log.Warning($"Glamourer n'a pas fourni d'état à restaurer ({ec}).");
        });

        return snapshot;
    }

    /// <summary>
    /// Équipe un objet, pour que l'effet d'un mod portant sur cette pièce devienne visible.
    /// </summary>
    /// <remarks>
    /// Aucune teinture n'est appliquée (0 sur les deux canaux) : l'état d'origine, teintures
    /// comprises, est rendu par <see cref="RestoreState"/> à la restauration.
    /// </remarks>
    public bool TryEquip(int objectIndex, EquipTarget target)
    {
        var equipped = false;

        this.TryInvoke($"équipement de « {target.Name} »", () =>
        {
            var ec = this.setItem.Invoke(
                objectIndex,
                target.Slot,
                target.ItemId,
                NoStains,
                UnlockedKey,
                ApplyFlag.Once);

            equipped = ec is GlamourerApiEc.Success;
            if (equipped)
                this.Log.Info($"Équipé : {target.Name} ({target.Slot}).");
            else
                this.Log.Error($"Glamourer a refusé d'équiper « {target.Name} » ({target.Slot}) : {ec}.");
        });

        return equipped;
    }

    /// <summary>
    /// État courant sous forme de JObject. C'est la représentation que <see cref="ApplyState"/>
    /// accepte en retour, ce qui permet de la relire, la modifier et la réappliquer.
    /// </summary>
    public JObject? GetStateObject(int objectIndex)
    {
        JObject? state = null;

        this.TryInvoke("lecture de l'état d'apparence (JObject)", () =>
        {
            var (ec, value) = this.getState.Invoke(objectIndex, UnlockedKey);

            if (ec is GlamourerApiEc.Success)
                state = value;
            else
                this.Log.Warning($"Glamourer n'a pas fourni d'état structuré ({ec}).");
        });

        return state;
    }

    /// <summary>
    /// Vide un emplacement d'équipement, pour ne laisser voir que ce qui est testé.
    /// </summary>
    /// <remarks>
    /// L'identifiant 0 désigne l'absence d'objet. La documentation ne l'énonce que pour
    /// <c>SetBonusItem</c> ; le code retour est donc journalisé, un refus indiquant que la
    /// convention ne vaut pas pour l'équipement.
    /// </remarks>
    public bool TryUnequip(int objectIndex, ApiEquipSlot slot)
    {
        var cleared = false;

        this.TryInvoke($"retrait de l'emplacement {slot}", () =>
        {
            var ec = this.setItem.Invoke(objectIndex, slot, 0, NoStains, UnlockedKey, ApplyFlag.Once);

            cleared = ec is GlamourerApiEc.Success;
            if (!cleared)
                this.Log.Warning($"Glamourer a refusé de vider l'emplacement {slot} : {ec}.");
        });

        return cleared;
    }

    /// <summary>
    /// Force une valeur de customisation, pour que le personnage porte le modèle que le mod
    /// remplace.
    /// </summary>
    /// <remarks>
    /// Glamourer n'expose pas de setter de customisation. On relit donc son état, on modifie le
    /// champ visé et on lui rend l'objet : <c>ApplyState</c> accepte explicitement « a
    /// Glamourer-supplied JObject ». Le nom du champ n'étant garanti par aucune documentation,
    /// sa présence et sa forme sont vérifiées avant toute écriture - en cas de doute on renonce
    /// plutôt que d'écrire à l'aveugle dans l'apparence du personnage.
    /// </remarks>
    public bool TryApplyCustomization(int objectIndex, string field, byte value)
    {
        var state = this.GetStateObject(objectIndex);
        if (state is null)
            return false;

        if (state["Customize"] is not JObject customize)
        {
            this.Log.Warning("L'état Glamourer ne contient pas de section « Customize » ; customisation ignorée.");
            return false;
        }

        if (customize[field] is not JObject entry || entry["Value"] is null)
        {
            this.Log.Warning($"L'état Glamourer n'expose pas « Customize.{field}.Value » ; customisation ignorée.");
            return false;
        }

        var previous = entry["Value"];
        entry["Value"] = value;
        entry["Apply"] = true;

        var applied = false;
        this.TryInvoke($"application de la customisation {field}={value}", () =>
        {
            var ec = this.applyState.Invoke(state, objectIndex, UnlockedKey, ApplyFlag.Customization);

            applied = ec is GlamourerApiEc.Success;
            if (applied)
                this.Log.Info($"Customisation appliquée : {field} {previous} → {value}.");
            else
                this.Log.Error($"Glamourer a refusé la customisation {field}={value} : {ec}.");
        });

        return applied;
    }

    /// <summary>Réapplique un état photographié par <see cref="CaptureState"/>.</summary>
    public bool RestoreState(int objectIndex, string snapshot)
    {
        var restored = false;

        this.TryInvoke("restauration de l'état d'apparence", () =>
        {
            var ec = this.applyState.Invoke(
                snapshot,
                objectIndex,
                UnlockedKey,
                ApplyFlag.Equipment | ApplyFlag.Customization);

            restored = ec is GlamourerApiEc.Success or GlamourerApiEc.NothingDone;
            if (!restored)
                this.Log.Error($"Glamourer a refusé la restauration de l'apparence : {ec}.");
        });

        return restored;
    }
}
