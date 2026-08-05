using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Sélection des options du mod courant, avant application.
/// </summary>
/// <remarks>
/// Un groupe réglé ici échappe au remplissage automatique : le choix de l'utilisateur prime,
/// y compris quand il consiste à ne rien sélectionner.
/// </remarks>
public sealed class OptionsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public OptionsWindow(Plugin plugin)
        : base("Configure mod###QuickTestOptionsWindow")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.WindowName = $"{this.T("Configurer le mod", "Configure mod")}###QuickTestOptionsWindow";

        if (this.plugin.Session.SelectedMod is not { } mod)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun mod sélectionné.", "No mod selected."));
            }

            return;
        }

        var catalog = this.plugin.OptionCatalog;

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(mod.Label);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.T(
                "Choisis les variantes à tester. Les changements sont appliqués immédiatement si le mod est déjà actif.",
                "Choose the variants to test. Changes apply immediately if the mod is already active."));
        }

        ImGuiHelpers.ScaledDummy(6f);

        using (ImRaii.Disabled(!catalog.HasSelection(mod)))
        {
            if (ImGui.SmallButton(this.T("Rendre tous les groupes automatiques", "Make all groups automatic")))
                this.Commit(mod, () => catalog.ClearMod(mod));
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T("Rend tous les groupes à la gestion automatique.", "Returns all groups to automatic control."));

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.T(
                "Chaque option peut aussi être associée à une animation avec le bouton « Associer ».",
                "Each option can also be linked to an animation with the Link button."));
        }

        ImGui.Separator();

        var groups = catalog.GroupsFor(mod);
        if (groups.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Ce mod n'expose aucune option.", "This mod exposes no options."));
            }

            return;
        }

        using var child = ImRaii.Child("##QuickTestOptionGroups", Vector2.Zero, false);
        if (!child.Success)
            return;

        foreach (var group in groups)
            this.DrawGroup(mod, group);
    }

    /// <summary>
    /// Enregistre un changement d'option et, si le mod est en cours de test, le réapplique
    /// aussitôt : l'intérêt de la fenêtre est de voir le résultat, pas de le décrire.
    /// </summary>
    private void Commit(ModInfo mod, Action change)
    {
        change();
        this.plugin.Session.ReapplyOptions(mod);
    }

    /// <summary>
    /// Le clic droit sur une option ouvre la désignation manuelle d'animation.
    /// </summary>
    /// <remarks>
    /// Remplace un bouton dédié : celui-ci s'affichait sous chaque option, y compris les
    /// centaines qui ne déclenchent aucune animation, pour une action qu'on fait rarement.
    /// </remarks>
    private void OfferBinding(ModInfo mod, ModOptionGroup group, string option)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T("Clic droit : associer une animation à cette option.", "Right-click to link an animation to this option."));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            this.plugin.OpenAnimationPicker(mod, group.Name, option);

        ImGui.SameLine();
        if (ImGui.SmallButton($"{this.T("Associer", "Link")}##bind-{group.Name}-{option}"))
            this.plugin.OpenAnimationPicker(mod, group.Name, option);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T("Associer manuellement une animation à cette option.", "Manually link an animation to this option."));
    }

    private void DrawEmoteShortcuts(ModInfo mod, ModOptionGroup group, string option)
    {
        // Ce que l'utilisateur a désigné prime, mais ne remplace pas ce qui a été reconnu : une
        // option peut déclencher plusieurs émotes.
        var bindings = this.plugin.AnimationBindings.Resolve(mod, group.Name, option);
        var recognized = this.plugin.AnimationIdentifier.FromLabel(option);

        // Rien à jouer : pas de ligne du tout. Un bouton d'association sous chaque option
        // doublerait la hauteur de la liste pour une action rare - le clic droit sur l'option
        // le remplace.
        if (bindings.Count == 0 && recognized.Count == 0)
            return;

        using var indent = ImRaii.PushIndent(20f);
        var first = true;

        void Place()
        {
            if (!first)
                ImGui.SameLine();

            first = false;
        }

        foreach (var bound in bindings)
        {
            Place();

            if (ImGui.SmallButton($"{bound.Label}##bound-{option}-{bound.TimelineId}"))
                this.Preview(mod, group, option, new AnimationMatch(bound.Label, "associée", bound.TimelineId, true));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T(
                    "Animation que tu as associée.\nClic droit pour la retirer.",
                    "Animation you linked.\nRight-click to remove it."));

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                this.plugin.AnimationBindings.Remove(mod, group.Name, option, bound.TimelineId);
        }

        foreach (var animation in recognized)
        {
            Place();

            // La pose fait partie de l'identité du bouton : deux poses de la même émote
            // porteraient sinon le même identifiant ImGui, et le second ne répondrait pas.
            var pose = animation.Pose is { } index ? $" · variante {index + 1}" : string.Empty;

            if (ImGui.SmallButton($"{animation.Label}{pose}##{option}-{animation.TimelineId}-{animation.Pose}"))
                this.Preview(mod, group, option, animation);

            if (ImGui.IsItemHovered())
            {
                var kind = animation.IsEmote ? this.T("émote", "emote") : this.T("animation sans émote", "animation without an emote");
                ImGui.SetTooltip(
                    this.T(
                        $"Sélectionne cette option seule dans son groupe, applique le mod,\npuis joue cette {kind}. Reconnue {animation.Source}.\nClic droit si la reconnaissance se trompe.",
                        $"Select this option alone in its group, apply the mod,\nthen play this {kind}. Recognized from {animation.Source}.\nRight-click if recognition is wrong."));
            }

            // Une reconnaissance automatique reste une supposition : elle doit toujours pouvoir
            // être corrigée, pas seulement complétée quand elle échoue.
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                this.plugin.OpenAnimationPicker(mod, group.Name, option);
        }

    }

    /// <summary>
    /// Essaie une variante d'un seul geste : l'option est retenue en exclusivité, le mod
    /// réappliqué, puis l'animation jouée.
    /// </summary>
    /// <remarks>
    /// Sans la sélection préalable, le bouton jouait l'animation d'origine du jeu : le mod ne
    /// remplace un fichier que si l'option correspondante est cochée. L'exclusivité évite le
    /// va-et-vient de décochage manuel d'une variante à l'autre.
    /// </remarks>
    private void Preview(ModInfo mod, ModOptionGroup group, string option, AnimationMatch animation)
    {
        var session = this.plugin.Session;

        // Tracé dès l'entrée : sans cela, un clic qui n'aboutit pas est indiscernable d'un clic
        // qui n'a jamais eu lieu.
        this.plugin.Diagnostics.Info($"Essai de « {animation.Label} » pour l'option « {option} ».");

        this.plugin.OptionCatalog.Select(mod, group.Name, [option]);
        session.PreviewAnimation(mod, animation);
    }

    private void DrawGroup(ModInfo mod, ModOptionGroup group)
    {
        var catalog = this.plugin.OptionCatalog;
        var controlled = catalog.IsUserControlled(mod, group.Name);

        var status = controlled ? this.T("personnalisé", "customized") : this.T("automatique", "automatic");
        if (!ImGui.CollapsingHeader(
                $"{group.Name}  ·  {group.TypeLabel}  ·  {status}##option-group-{group.Name}",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!group.IsSupported)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                using (ImRaii.PushIndent(12f))
                {
                    ImGui.TextWrapped(
                        this.T(
                            "Les options de ce groupe dépendent les unes des autres ; elles se règlent " +
                            "dans Penumbra. Prism Cabinet les laisse intactes.",
                            "The options in this group depend on each other; configure them in " +
                            "Penumbra. Prism Cabinet leaves them unchanged."));
                }
            }

            ImGuiHelpers.ScaledDummy(6f);
            return;
        }

        var selection = catalog.SelectionFor(mod, group.Name);

        using (ImRaii.PushIndent(12f))
        {
            if (group.IsSingleSelect)
                this.DrawSingleSelect(mod, group, selection, controlled);
            else
                this.DrawMultiSelect(mod, group, selection, controlled);
        }

        ImGuiHelpers.ScaledDummy(6f);
    }

    private void DrawSingleSelect(
        ModInfo mod, ModOptionGroup group, IReadOnlyList<string> selection, bool controlled)
    {
        var catalog = this.plugin.OptionCatalog;

        // Une entrée explicite pour rendre le groupe à la gestion automatique : sans elle, on ne
        // pourrait plus revenir en arrière après un premier clic.
        if (ImGui.RadioButton($"({this.T("automatique", "automatic")})##{group.Name}", !controlled))
            this.Commit(mod, () => catalog.ClearGroup(mod, group.Name));

        foreach (var option in group.Options)
        {
            var isSelected = controlled && selection.Contains(option);

            // Le libellé est rendu à part et replié : intégré au contrôle, un libellé long est
            // tronqué au bord de la fenêtre - et la parenthèse finale, qui porte souvent le
            // déclencheur, est justement ce qui disparaît.
            if (ImGui.RadioButton($"##{group.Name}-{option}", isSelected))
                this.Commit(mod, () => catalog.Select(mod, group.Name, [option]));

            ImGui.SameLine();
            ImGui.TextWrapped(option);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                this.Commit(mod, () => catalog.Select(mod, group.Name, [option]));

            this.OfferBinding(mod, group, option);
            this.DrawEmoteShortcuts(mod, group, option);
        }
    }

    private void DrawMultiSelect(ModInfo mod, ModOptionGroup group, IReadOnlyList<string> selection, bool controlled)
    {
        var catalog = this.plugin.OptionCatalog;

        foreach (var option in group.Options)
        {
            var isSelected = controlled && selection.Contains(option);
            var toggled = isSelected;
            var changed = ImGui.Checkbox($"##{group.Name}-{option}", ref toggled);

            ImGui.SameLine();
            ImGui.TextWrapped(option);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var labelSelection = new List<string>(selection);
                if (isSelected)
                    labelSelection.Remove(option);
                else
                    labelSelection.Add(option);

                this.Commit(mod, () => catalog.Select(mod, group.Name, labelSelection));
            }

            this.OfferBinding(mod, group, option);
            this.DrawEmoteShortcuts(mod, group, option);

            if (!changed)
                continue;

            var next = new List<string>(selection);
            if (toggled)
                next.Add(option);
            else
                next.Remove(option);

            this.Commit(mod, () => catalog.Select(mod, group.Name, next));
        }

        using (ImRaii.Disabled(!controlled))
        {
            if (ImGui.SmallButton($"{this.T("Automatique", "Automatic")}##{group.Name}"))
                this.Commit(mod, () => catalog.ClearGroup(mod, group.Name));
        }
    }

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
