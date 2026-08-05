using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using QuickTestPlugin.Models;
using Dalamud.Interface.Windowing;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Vue globale des associations d'animation et des prérequis déclarés.
/// </summary>
/// <remarks>
/// Corriger une association ou un prérequis demandait jusqu'ici de retrouver le mod et l'option
/// concernés. Cette fenêtre les liste tous, y compris ceux dont le mod a depuis disparu de
/// Penumbra : c'est précisément ce qui permet de les repérer et de les nettoyer.
/// </remarks>
public sealed class ManagementWindow : Window, IDisposable
{
    private const string CleanupPopupId = "Nettoyer les entrées introuvables ?###QuickTestCleanupOrphaned";

    private readonly Plugin plugin;

    private string filter = string.Empty;
    private bool openCleanupPopup;
    private string? cleanupStatus;

    public ManagementWindow(Plugin plugin)
        : base("Prism Cabinet - Links and prerequisites###QuickTestManagement")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.WindowName = $"{this.T("Prism Cabinet - Associations et prérequis", "Prism Cabinet - Links and prerequisites")}###QuickTestManagement";

        var catalogReady = this.plugin.Integrations.Penumbra.IsAvailable && this.plugin.Catalog.IsLoaded;

        using (ImRaii.Disabled(!catalogReady))
        {
            if (ImGui.SmallButton(this.T("Nettoyer les entrées introuvables", "Clean missing entries")))
                this.openCleanupPopup = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(catalogReady
                                 ? this.T(
                                     "Retire d'un coup toutes les associations et prérequis\ndont le mod n'existe plus.",
                                     "Removes all links and prerequisites\nwhose mod no longer exists.")
                                 : this.T(
                                     "Indisponible tant que la liste Penumbra n'est pas chargée de façon fiable.",
                                     "Unavailable until the Penumbra list has loaded reliably."));
        }

        if (!catalogReady)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(
                    this.T(
                        "La liste des mods Penumbra n'est pas disponible : les entrées ne sont pas " +
                        "considérées comme introuvables tant que la lecture n'est pas confirmée.",
                        "The Penumbra mod list is unavailable: entries are not considered missing " +
                        "until the list has been confirmed."));
            }
        }

        if (this.cleanupStatus is { } status)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen))
            {
                ImGui.TextWrapped(status);
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##QuickTestManagementFilter", this.T("Filtrer par mod ou prérequis", "Filter by mod or prerequisite"), ref this.filter, 128);
        ImGuiHelpers.ScaledDummy(4f);

        this.DrawAnimationBindings();
        ImGuiHelpers.ScaledDummy(8f);
        this.DrawPrerequisites();
        this.DrawCleanupConfirmation();
    }

    private bool CatalogReady
        => this.plugin.Integrations.Penumbra.IsAvailable && this.plugin.Catalog.IsLoaded;

    /// <summary>Nom lisible d'un mod par son dossier, ou le dossier lui-même s'il a disparu.</summary>
    private (string Label, bool Missing) ResolveMod(string directory)
        => !this.CatalogReady
               ? (directory, false)
               : this.plugin.Catalog.TryGetByDirectory(directory, out var mod)
               ? (mod.Label, false)
               : (directory, true);

    private bool MatchesFilter(string label)
        => string.IsNullOrWhiteSpace(this.filter) || label.Contains(this.filter, StringComparison.CurrentCultureIgnoreCase);

    private void DrawAnimationBindings()
    {
        this.SectionHeader(this.T("Animations associées", "Linked animations"));

        var entries = this.plugin.AnimationBindings.AllDeclarations();
        if (entries.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucune association enregistrée.", "No links are registered."));
            }

            return;
        }

        var groups = entries.GroupBy(e => e.ModDirectory, StringComparer.Ordinal)
                            .Where(g => this.MatchesFilter(this.ResolveMod(g.Key).Label))
                            .OrderBy(g => this.ResolveMod(g.Key).Label, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();

        if (groups.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucune association ne correspond au filtre.", "No links match the filter."));
            }

            return;
        }

        using var table = ImRaii.Table("##QuickTestBindingsTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn(this.T("Mod", "Mod"), ImGuiTableColumnFlags.WidthStretch, 0.35f, 0);
        ImGui.TableSetupColumn(this.T("Option", "Option"), ImGuiTableColumnFlags.WidthStretch, 0.35f, 0);
        ImGui.TableSetupColumn(this.T("Animations", "Animations"), ImGuiTableColumnFlags.WidthStretch, 0.3f, 0);
        ImGui.TableHeadersRow();

        // Groupées par mod puis par option : un mod-catalogue en a facilement une dizaine.
        foreach (var byMod in groups)
        {
            var (label, missing) = this.ResolveMod(byMod.Key);
            var first = true;

            foreach (var entry in byMod.OrderBy(e => e.Option, StringComparer.CurrentCultureIgnoreCase))
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (first)
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, missing))
                    {
                        ImGui.TextWrapped(missing ? $"{label} ({this.T("introuvable", "missing")})" : label);
                    }

                    first = false;
                }

                ImGui.TableNextColumn();
                ImGui.TextWrapped(entry.Option ?? this.T("(mod entier)", "(whole mod)"));

                ImGui.TableNextColumn();
                foreach (var binding in entry.Bindings)
                {
                    if (ImGui.SmallButton($"× {binding.Label}##{byMod.Key}-{entry.Group}-{entry.Option}-{binding.TimelineId}"))
                    {
                        this.plugin.AnimationBindings.Remove(
                            new ModInfo(byMod.Key, label), entry.Group, entry.Option, binding.TimelineId);
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(this.T("Retirer cette association.", "Remove this link."));
                }
            }
        }
    }

    private void DrawPrerequisites()
    {
        this.SectionHeader(this.T("Prérequis", "Prerequisites"));

        var declarations = this.plugin.Prerequisites.AllDeclarations();
        if (declarations.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun prérequis déclaré.", "No prerequisites declared."));
            }

            return;
        }

        var filtered = declarations
            .Where(d => this.MatchesFilter(this.ResolveMod(d.ModDirectory).Label)
                        || d.Prerequisites.Any(directory =>
                            this.MatchesFilter(this.ResolveMod(directory).Label)))
            .OrderBy(d => this.ResolveMod(d.ModDirectory).Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (filtered.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun prérequis ne correspond au filtre.", "No prerequisites match the filter."));
            }

            return;
        }

        foreach (var (ownerDirectory, prerequisiteDirectories) in filtered)
        {
            var (ownerLabel, ownerMissing) = this.ResolveMod(ownerDirectory);

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, ownerMissing))
            {
                ImGui.TextWrapped(ownerMissing ? $"{ownerLabel} ({this.T("introuvable", "missing")})" : ownerLabel);
            }

            using var indent = ImRaii.PushIndent(12f);

            foreach (var prerequisiteDirectory in prerequisiteDirectories)
            {
                var (prerequisiteLabel, prerequisiteMissing) = this.ResolveMod(prerequisiteDirectory);

                if (ImGui.SmallButton($"×##prereq-{ownerDirectory}-{prerequisiteDirectory}"))
                    this.plugin.Prerequisites.Remove(new ModInfo(ownerDirectory, ownerLabel), prerequisiteDirectory);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(this.T("Retirer ce prérequis.", "Remove this prerequisite."));

                ImGui.SameLine();

                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, prerequisiteMissing))
                {
                    ImGui.TextWrapped(prerequisiteMissing ? $"{prerequisiteLabel} ({this.T("introuvable", "missing")})" : prerequisiteLabel);
                }
            }

            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    /// <summary>Titre de section. ImGui.SeparatorText n'existe pas dans ces bindings.</summary>
    private void SectionHeader(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(label);
        }

        ImGui.Separator();
    }

    private void DrawCleanupConfirmation()
    {
        if (this.openCleanupPopup)
        {
            this.openCleanupPopup = false;
            this.cleanupStatus = null;
            ImGui.OpenPopup(CleanupPopupId);
        }

        if (!ImGui.BeginPopupModal(CleanupPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped(
            this.T(
                "Les associations et prérequis dont le mod n'existe pas dans le catalogue actuellement " +
                "chargé seront supprimés de la configuration de Prism Cabinet.",
                "Links and prerequisites whose mod is not in the currently loaded catalog " +
                "will be removed from Prism Cabinet's configuration."));

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
        {
            ImGui.TextWrapped(this.T(
                "Cette action ne supprime aucun fichier de mod, mais elle n'a pas d'annulation automatique.",
                "This does not delete any mod files, but it cannot be automatically undone."));
        }

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(this.T("Confirmer le nettoyage", "Confirm cleanup")))
        {
            var result = this.plugin.CleanupOrphaned();
            this.cleanupStatus = this.T(
                $"Nettoyage terminé : {result.Bindings} association(s), {result.Prerequisites} prérequis retiré(s).",
                $"Cleanup complete: {result.Bindings} link(s), {result.Prerequisites} prerequisite(s) removed.");
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button(this.T("Annuler", "Cancel")))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
