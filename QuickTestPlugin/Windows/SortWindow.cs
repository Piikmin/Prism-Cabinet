using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Tri automatique des mods en dossiers, et nettoyage des dossiers personnalisés devenus vides
/// dans Penumbra.
/// </summary>
public sealed class SortWindow : Window, IDisposable
{
    private static readonly Vector4 PrimaryButtonBackground = new(0.38f, 0.2f, 0.62f, 1f);
    private static readonly Vector4 PrimaryButtonHover = new(0.5f, 0.3f, 0.76f, 1f);
    private static readonly Vector4 PreviewBackground = new(0.045f, 0.052f, 0.075f, 0.98f);

    private readonly Plugin plugin;

    private SortStrategy sortStrategy = SortStrategy.ByCreator;
    private bool detailedEquipment;
    private bool sortFilteredOnly;
    private IReadOnlyList<SortProposal>? sortProposals;
    private readonly HashSet<string> sortExcluded = new(StringComparer.Ordinal);
    private bool isComputingSort;
    private object? sortComputeTicket;
    private bool openApplySortPopup;
    private string? sortStatus;
    private bool sortStatusError;

    private IReadOnlyList<IReadOnlyList<ModInfo>>? duplicateNames;
    private ModInfo? pendingDuplicateDeletion;
    private bool openDuplicateDeletePopup;
    private string? duplicateDeletionStatus;

    private IReadOnlyList<string>? emptyFolders;
    private readonly HashSet<string> folderCleanupExcluded = new(StringComparer.Ordinal);
    private bool openFolderCleanupPopup;
    private string? folderCleanupStatus;
    private bool folderCleanupStatusError;

    public SortWindow(Plugin plugin)
        : base("Prism Cabinet - Automatic sorting###QuickTestSort")
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
        => this.DrawEmbedded();

    public void DrawEmbedded()
    {
        this.WindowName = $"{this.T("Prism Cabinet - Tri automatique", "Prism Cabinet - Automatic sorting")}###QuickTestSort";
        this.DrawSort();
        ImGuiHelpers.ScaledDummy(8f);

        if (ImGui.TreeNodeEx(
                "QuickTestAdvancedMaintenance",
                ImGuiTreeNodeFlags.None,
                this.T("Maintenance avancée", "Advanced maintenance")))
        {
            if (ImGui.Button(
                    this.T("Rechercher les noms en double", "Find duplicate names"),
                    new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            {
                this.plugin.RefreshCatalog();
                this.duplicateNames = this.plugin.Sorter.FindDuplicateNames(this.plugin.Catalog.AllEntries);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T("Repère les mods qui partagent exactement le même nom.", "Finds mods with exactly the same name."));

            ImGuiHelpers.ScaledDummy(6f);
            this.DrawFolderCleanup();
            ImGui.TreePop();
        }
    }

    private const string ApplySortPopupId = "Appliquer ce tri ?###QuickTestApplySort";

    /// <summary>
    /// Range les mods en dossiers par créateur et/ou par nature. Le calcul relit le nom de
    /// l'auteur de chaque mod sur le disque : sur une grosse bibliothèque, il tourne en tâche de
    /// fond pour ne jamais geler l'affichage pendant qu'il travaille.
    /// </summary>
    private void DrawSort()
    {
        this.SectionHeader(this.T("Trier les mods", "Sort mods"));

        ImGui.TextWrapped(
            this.T(
                "Organise les dossiers Penumbra. Aucun changement n'est appliqué sans confirmation.",
                "Organizes Penumbra folders. No change is applied without confirmation."));

        var catalogReady = this.plugin.Integrations.Penumbra.IsAvailable && this.plugin.Catalog.IsLoaded;
        var testerFiltersActive = !string.IsNullOrWhiteSpace(this.plugin.Catalog.Filter)
                                  || this.plugin.Catalog.KindFilter.Count > 0;
        if (!testerFiltersActive)
            this.sortFilteredOnly = false;

        var sourceCount = this.sortFilteredOnly
                              ? this.plugin.Catalog.Filtered.Count
                              : this.plugin.Catalog.AllEntries.Count;
        var canCompute = catalogReady && sourceCount > 0;

        if (!catalogReady)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(this.T("Le tri attend une liste Penumbra chargée de façon fiable.", "Sorting is waiting for a reliable Penumbra list."));
            }
        }
        else if (!canCompute)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(this.T("Aucun mod n'est disponible dans la portée choisie.", "No mod is available in the selected scope."));
            }
        }

        var splitLayout = ImGui.GetContentRegionAvail().X >= 760f * ImGuiHelpers.GlobalScale;
        if (splitLayout)
        {
            ImGui.PushStyleVar(
                ImGuiStyleVar.CellPadding,
                new Vector2(16f * ImGuiHelpers.GlobalScale, 10f * ImGuiHelpers.GlobalScale));
        }

        var layoutOpen = splitLayout && ImGui.BeginTable(
            "##QuickTestSortLayout",
            2,
            ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp);

        if (layoutOpen)
        {
            ImGui.TableSetupColumn(this.T("Configuration", "Configuration"), ImGuiTableColumnFlags.WidthStretch, 0.4f);
            ImGui.TableSetupColumn(this.T("Aperçu", "Preview"), ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
        }

        ImGuiHelpers.ScaledDummy(8f);
        this.StepHeader(this.T("1 · Choisir l'organisation", "1 · Choose the organization"));

        using (ImRaii.Disabled(this.isComputingSort || !catalogReady))
        {
            if (ImGui.RadioButton(
                        this.T(
                            $"Toute la bibliothèque ({this.plugin.Catalog.AllEntries.Count})",
                            $"Entire library ({this.plugin.Catalog.AllEntries.Count})"),
                    !this.sortFilteredOnly))
            {
                this.sortFilteredOnly = false;
            }

            if (testerFiltersActive)
            {
                if (ImGui.RadioButton(
                        this.T(
                            $"Filtres du mode Tester ({this.plugin.Catalog.Filtered.Count})",
                            $"Test mode filters ({this.plugin.Catalog.Filtered.Count})"),
                        this.sortFilteredOnly))
                {
                    this.sortFilteredOnly = true;
                }
            }
            else
            {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                {
                    ImGui.TextUnformatted(this.T(
                        "Filtres du mode Tester · aucun filtre actif",
                        "Test mode filters · no active filters"));
                }
            }
        }

        using (ImRaii.Disabled(this.isComputingSort || !canCompute))
        {
            ImGuiHelpers.ScaledDummy(4f);
            var strategyGap = ImGui.GetStyle().ItemSpacing.X;
            var strategyWidth = MathF.Max(
                120f * ImGuiHelpers.GlobalScale,
                (ImGui.GetContentRegionAvail().X - strategyGap) / 2f);

            this.DrawStrategyButton(SortStrategy.ByCreator, this.T("Par créateur", "By creator"), strategyWidth);
            ImGui.SameLine();
            this.DrawStrategyButton(SortStrategy.ByKind, this.T("Par type", "By type"), strategyWidth);
            ImGui.NewLine();
            this.DrawStrategyButton(SortStrategy.ByKindThenCreator, this.T("Type puis créateur", "Type then creator"), strategyWidth);
            ImGui.SameLine();
            this.DrawStrategyButton(SortStrategy.ByCreatorThenKind, this.T("Créateur puis type", "Creator then type"), strategyWidth);

            ImGui.NewLine();

            ImGui.Checkbox(
                this.T("Créer un dossier par emplacement d'équipement", "Create a folder per equipment slot"),
                ref this.detailedEquipment);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    this.T(
                        "Sépare les mods d'équipement par Tête, Torse, Mains...\n" +
                        "plutôt qu'un seul dossier Équipement, quand l'emplacement\n" +
                        "est identifiable sans ambiguïté.",
                        "Separates equipment mods into Head, Body, Hands... folders\n" +
                        "instead of one Equipment folder when the slot\n" +
                        "can be identified unambiguously."));
            }

            ImGuiHelpers.ScaledDummy(4f);

            this.StepHeader(this.T("2 · Prévisualiser", "2 · Preview"));

            using (ImRaii.PushColor(ImGuiCol.Button, PrimaryButtonBackground))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryButtonHover))
            {
                if (ImGui.Button(this.T("Prévisualiser le tri", "Preview sorting"), new Vector2(220f * ImGuiHelpers.GlobalScale, 30f * ImGuiHelpers.GlobalScale)))
                {
                    this.plugin.RefreshCatalog();
                    this.ComputeSortProposals();
                }
            }

        }

        if (layoutOpen)
            ImGui.TableNextColumn();

        this.StepHeader(this.T("Aperçu", "Preview"));
        if (this.plugin.Sorter.CanUndo)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Dernier tri appliqué", "Last applied sort"));
            }

            if (ImGui.Button(
                    this.T("Annuler le dernier tri", "Undo last sort"),
                    new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            {
                var result = this.plugin.Sorter.Undo();
                this.plugin.RefreshCatalog();
                this.sortStatus = result.Failed == 0
                                      ? this.T(
                                          $"Tri annulé : {result.Restored} mod(s) restauré(s).",
                                          $"Sort undone: {result.Restored} mod(s) restored.")
                                      : this.T(
                                          $"Annulation partielle : {result.Restored} restauré(s), {result.Failed} échec(s). Consulte le Journal.",
                                          $"Partial undo: {result.Restored} restored, {result.Failed} failed. Check the Log.");
                this.sortStatusError = result.Failed > 0;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T(
                    "Remet chaque mod déplacé par le dernier tri à son chemin précédent.",
                    "Returns every mod moved by the last sort to its previous path."));

            ImGuiHelpers.ScaledDummy(8f);
        }

        if (this.sortProposals is null && !this.isComputingSort && this.sortStatus is null)
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, PreviewBackground))
            using (var emptyPreview = ImRaii.Child(
                       "##QuickTestEmptySortPreview",
                       new Vector2(-1, 120f * ImGuiHelpers.GlobalScale),
                       true))
            {
                if (emptyPreview.Success)
                {
                    ImGuiHelpers.ScaledDummy(10f);
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                    {
                        ImGui.TextWrapped(
                            this.T(
                                "Choisis une organisation, puis prévisualise le tri pour voir chaque déplacement proposé.",
                                "Choose an organization, then preview sorting to see every proposed move."));
                    }
                }
            }
        }

        if (this.duplicateNames is { Count: > 0 } duplicates)
        {
            ImGuiHelpers.ScaledDummy(4f);

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(this.T(
                    $"{duplicates.Count} nom(s) partagé(s) par plusieurs mods :",
                    $"{duplicates.Count} name(s) shared by multiple mods:"));
            }

            using var indent = ImRaii.PushIndent(12f);
            foreach (var group in duplicates)
            {
                ImGui.TextWrapped(this.T(
                    $"« {group[0].Label} » - {group.Count} exemplaires :",
                    $"{group[0].Label} - {group.Count} copies:"));

                using var innerIndent = ImRaii.PushIndent(12f);
                foreach (var mod in group)
                {
                    if (ImGui.SmallButton($"{this.T("Supprimer", "Delete")}##dup-{mod.Directory}"))
                    {
                        this.pendingDuplicateDeletion = mod;
                        this.openDuplicateDeletePopup = true;
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(this.T("Supprime ce mod de Penumbra, fichiers compris.", "Deletes this mod from Penumbra, including its files."));

                    ImGui.SameLine();

                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                    {
                        ImGui.TextUnformatted(mod.Directory);
                    }
                }
            }
        }
        else if (this.duplicateNames is not null)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextUnformatted(this.T("Aucun nom en double trouvé.", "No duplicate names found."));
        }

        if (this.sortStatus is { } status)
        {
            using (ImRaii.PushColor(
                       ImGuiCol.Text,
                       this.sortStatusError ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen))
            {
                ImGui.TextWrapped(status);
            }
        }

        if (this.isComputingSort)
        {
            ImGui.TextUnformatted(this.T("Calcul en cours…", "Calculating…"));
        }

        if (this.sortProposals is { Count: > 0 } proposals)
        {
            var changing = proposals.Where(p => p.Changes && !this.sortExcluded.Contains(p.Mod.Directory)).ToList();
            var unchanged = proposals.Count(p => !p.Changes);

            ImGuiHelpers.ScaledDummy(8f);
            this.StepHeader(this.T("3 · Vérifier et appliquer", "3 · Review and apply"));
            ImGui.TextUnformatted(this.T(
                $"{proposals.Count - unchanged} mod(s) à déplacer, {unchanged} déjà à leur place.",
                $"{proposals.Count - unchanged} mod(s) to move, {unchanged} already in place."));

            var proposalLineHeight = ImGui.GetTextLineHeightWithSpacing();
            var proposalHeight = MathF.Min(
                360f * ImGuiHelpers.GlobalScale,
                MathF.Max(
                    96f * ImGuiHelpers.GlobalScale,
                    (changing.Count + 1) * proposalLineHeight + (24f * ImGuiHelpers.GlobalScale)));

            using (var child = ImRaii.Child(
                       "##QuickTestSortProposals",
                       new Vector2(-1, proposalHeight),
                       true))
            {
                if (child.Success)
                {
                    using (var table = ImRaii.Table(
                               "##QuickTestSortTable",
                               3,
                               ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                    {
                        if (table.Success)
                        {
                            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 24f, 0);
                            ImGui.TableSetupColumn(this.T("Mod", "Mod"), ImGuiTableColumnFlags.WidthStretch, 0.4f, 0);
                            ImGui.TableSetupColumn(this.T("Chemin proposé", "Proposed path"), ImGuiTableColumnFlags.WidthStretch, 0.6f, 0);
                            ImGui.TableHeadersRow();

                            foreach (var proposal in proposals)
                            {
                                if (!proposal.Changes)
                                    continue;

                                ImGui.TableNextRow();

                                ImGui.TableNextColumn();
                                var included = !this.sortExcluded.Contains(proposal.Mod.Directory);
                                if (ImGui.Checkbox($"##sort-{proposal.Mod.Directory}", ref included))
                                {
                                    if (included)
                                        this.sortExcluded.Remove(proposal.Mod.Directory);
                                    else
                                        this.sortExcluded.Add(proposal.Mod.Directory);
                                }

                                ImGui.TableNextColumn();
                                ImGui.TextWrapped(proposal.Mod.Label);

                                ImGui.TableNextColumn();
                                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange, proposal.Disambiguated))
                                {
                                    ImGui.TextWrapped(proposal.ProposedPath);
                                }

                                if (proposal.Disambiguated && ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip(
                                        this.T(
                                            "Un autre mod porte déjà ce nom à cet endroit ; un suffixe a été\n" +
                                            "ajouté pour éviter la collision.",
                                            "Another mod already has this name here; a suffix was added\n" +
                                            "to avoid a collision."));
                                }
                            }
                        }
                    }
                }
            }

            ImGuiHelpers.ScaledDummy(8f);

            using (ImRaii.Disabled(changing.Count == 0))
            using (ImRaii.PushColor(ImGuiCol.Button, PrimaryButtonBackground, changing.Count > 0))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryButtonHover, changing.Count > 0))
            {
                if (ImGui.Button(this.T($"Appliquer ({changing.Count})", $"Apply ({changing.Count})")))
                    this.openApplySortPopup = true;
            }

            ImGui.SameLine();

            if (ImGui.Button(this.T("Annuler la proposition", "Cancel proposal")))
                this.sortProposals = null;
        }

        if (layoutOpen)
            ImGui.EndTable();

        if (splitLayout)
            ImGui.PopStyleVar();

        this.DrawApplySortConfirmation();
        this.DrawDuplicateDeleteConfirmation();
    }

    private const string DuplicateDeletePopupId = "Supprimer ce mod ?###QuickTestSortDeleteConfirm";

    /// <summary>
    /// Même garde-fou que la suppression depuis la fenêtre principale : Penumbra efface le dossier
    /// du mod du disque, c'est définitif, donc pas de suppression en un clic.
    /// </summary>
    private void DrawDuplicateDeleteConfirmation()
    {
        if (this.openDuplicateDeletePopup)
        {
            this.openDuplicateDeletePopup = false;
            this.duplicateDeletionStatus = null;
            ImGui.OpenPopup(DuplicateDeletePopupId);
        }

        if (!ImGui.BeginPopupModal(DuplicateDeletePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (this.pendingDuplicateDeletion is not { } mod)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(mod.Label);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextUnformatted(mod.Directory);
        }

        ImGuiHelpers.ScaledDummy(4f);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
        {
            ImGui.TextWrapped(
                this.T(
                    "Penumbra effacera le dossier du mod du disque. C'est définitif : ni Prism Cabinet " +
                    "ni Penumbra ne peuvent le rendre.",
                    "Penumbra will delete this mod folder from disk. This is permanent: neither Prism Cabinet " +
                    "nor Penumbra can restore it."));
        }

        if (this.duplicateDeletionStatus is { } status)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(status);
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(this.T("Supprimer définitivement", "Delete permanently")))
        {
            if (this.plugin.DeleteMod(mod))
            {
                this.pendingDuplicateDeletion = null;
                // Le catalogue vient d'être rafraîchi par la suppression : la liste se recalcule pour
                // ne plus montrer le mod qu'on vient de retirer.
                this.duplicateNames = this.plugin.Sorter.FindDuplicateNames(this.plugin.Catalog.AllEntries);
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.duplicateDeletionStatus = this.T(
                    "La suppression n'a pas été confirmée par Penumbra. Vérifie le Journal et réessaie.",
                    "Penumbra did not confirm the deletion. Check the Log and try again.");
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(this.T("Annuler", "Cancel")))
        {
            this.pendingDuplicateDeletion = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawStrategyButton(SortStrategy strategy, string label, float width)
    {
        var active = this.sortStrategy == strategy;
        using (ImRaii.PushColor(ImGuiCol.Button, PrimaryButtonBackground, active))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryButtonHover, active))
        {
            if (ImGui.Button(label, new Vector2(width, 30f * ImGuiHelpers.GlobalScale)))
                this.sortStrategy = strategy;
        }
    }

    private void ComputeSortProposals()
    {
        var source = this.sortFilteredOnly
                         ? this.plugin.Catalog.Filtered
                         : this.plugin.Catalog.AllEntries;

        if (!this.plugin.Integrations.Penumbra.IsAvailable
            || !this.plugin.Catalog.IsLoaded
            || source.Count == 0)
        {
            this.sortStatus = this.T(
                "Impossible de calculer le tri : aucun mod fiable n'est disponible dans la portée choisie.",
                "Unable to calculate sorting: no reliable mod is available in the selected scope.");
            this.sortStatusError = true;
            return;
        }

        this.isComputingSort = true;
        this.sortProposals = null;
        this.sortExcluded.Clear();
        this.sortStatus = null;
        this.sortStatusError = false;

        var ticket = new object();
        this.sortComputeTicket = ticket;
        var strategy = this.sortStrategy;
        var mods = source.Select(entry => entry).ToList();
        var allMods = this.plugin.Catalog.AllEntries.Select(e => e).ToList();
        var changedItems = this.plugin.Integrations.Penumbra.GetAllChangedItems();
        var detailed = this.detailedEquipment;

        Task.Run(() =>
        {
            try
            {
                var proposals = this.plugin.Sorter.ComputeProposals(mods, allMods, strategy, detailed, changedItems);

                _ = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!ReferenceEquals(this.sortComputeTicket, ticket))
                        return;

                    this.sortProposals = proposals;
                    this.isComputingSort = false;
                    this.sortStatus = proposals.Count == 0
                                          ? this.T(
                                              "Aucune proposition de déplacement pour le filtre courant.",
                                              "No move proposal for the current filter.")
                                          : null;
                    this.sortStatusError = false;
                });
            }
            catch (Exception ex)
            {
                _ = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!ReferenceEquals(this.sortComputeTicket, ticket))
                        return;

                    this.isComputingSort = false;
                    this.sortProposals = null;
                    this.sortStatus = this.T(
                        $"Calcul du tri impossible : {ex.Message}",
                        $"Unable to calculate sorting: {ex.Message}");
                    this.sortStatusError = true;
                });
            }
        });
    }

    private void DrawApplySortConfirmation()
    {
        if (this.openApplySortPopup)
        {
            this.openApplySortPopup = false;
            ImGui.OpenPopup(ApplySortPopupId);
        }

        if (!ImGui.BeginPopupModal(ApplySortPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (this.sortProposals is not { Count: > 0 } proposals)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var changing = proposals.Where(p => p.Changes && !this.sortExcluded.Contains(p.Mod.Directory)).ToList();

        ImGui.TextWrapped(this.T(
            $"{changing.Count} mod(s) vont changer de dossier dans Penumbra.",
            $"{changing.Count} mod(s) will change folders in Penumbra."));
        ImGui.TextWrapped(this.T(
            "Un seul tri peut être annulé après coup, le précédent si tu en refais un autre.",
            "Only the most recent sort can be undone."));

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(this.T("Confirmer", "Confirm")))
        {
            var result = this.plugin.Sorter.Apply(changing);
            this.plugin.RefreshCatalog();

            if (result.Failed == 0)
            {
                this.sortProposals = null;
                this.sortStatus = this.T(
                    $"Tri appliqué : {result.Applied} mod(s) déplacé(s).",
                    $"Sort applied: {result.Applied} mod(s) moved.");
                this.sortStatusError = false;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.sortProposals = result.FailedProposals;
                this.sortExcluded.Clear();
                this.sortStatus = this.T(
                    $"Tri partiel : {result.Applied} déplacé(s), {result.Failed} échec(s). Les échecs restent proposés ; consulte le Journal.",
                    $"Partial sort: {result.Applied} moved, {result.Failed} failed. Failed moves remain proposed; check the Log.");
                this.sortStatusError = true;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(this.T("Annuler", "Cancel")))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private const string FolderCleanupPopupId = "Nettoyer ces dossiers ?###QuickTestFolderCleanup";

    /// <summary>
    /// Retire les dossiers de Penumbra qui ne contiennent plus aucun mod. Édite un fichier interne
    /// de Penumbra, non documenté et sans IPC : à utiliser en connaissance de cause.
    /// </summary>
    private void DrawFolderCleanup()
    {
        var catalogReady = this.plugin.Integrations.Penumbra.IsAvailable && this.plugin.Catalog.IsLoaded;
        if (!catalogReady)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(
                    this.T(
                        "Le nettoyage attend une liste Penumbra chargée de façon fiable, afin de ne " +
                        "pas prendre des dossiers occupés pour des dossiers vides.",
                        "Cleanup is waiting for a reliable Penumbra list, so occupied folders are not " +
                        "mistaken for empty ones."));
            }

            return;
        }

        if (!this.plugin.FolderCleanup.IsAvailable)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(
                    this.T(
                        "organization.json introuvable pour l'instant. Ouvre au moins une fois la liste " +
                        "de mods dans Penumbra depuis le dernier démarrage du jeu, puis réessaie.",
                        "organization.json was not found. Open the mod list in Penumbra at least once " +
                        "since the last game start, then try again."));
            }

            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
        {
            ImGui.TextWrapped(
                this.T(
                    "Cette maintenance modifie le fichier d'organisation interne de Penumbra.",
                    "This maintenance action modifies Penumbra's internal organization file."));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                this.T(
                    "Ce fichier n'est ni documenté ni exposé par l'API de Penumbra.\n" +
                    "Évite de réorganiser les dossiers dans Penumbra pendant le nettoyage.",
                    "This file is undocumented and not exposed by Penumbra's API.\n" +
                    "Avoid reorganizing folders in Penumbra during cleanup."));
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.T(
                "Après le nettoyage, utilise « Rediscover Mods » dans Penumbra.",
                "After cleanup, use Rediscover Mods in Penumbra."));
        }

        if (this.folderCleanupStatus is { } status)
        {
            using (ImRaii.PushColor(
                       ImGuiCol.Text,
                       this.folderCleanupStatusError ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen))
            {
                ImGui.TextWrapped(status);
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(
                this.T("Rechercher les dossiers vides", "Find empty folders"),
                new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
        {
            // Un tri déplace les mods sans forcément déclencher le rafraîchissement automatique du
            // catalogue (Penumbra ne semble pas émettre ModMoved pour un simple SetModPath) : sans ce
            // rechargement explicite, l'occupation se vérifierait contre d'anciens chemins et ferait
            // passer des dossiers pleins pour vides.
            this.plugin.RefreshCatalog();
            this.emptyFolders = this.plugin.FolderCleanup.FindEmpty(this.plugin.Catalog.AllEntries);
            this.folderCleanupExcluded.Clear();
            this.folderCleanupStatus = null;
            this.folderCleanupStatusError = false;
        }

        if (this.emptyFolders is { Count: > 0 } folders)
        {
            ImGuiHelpers.ScaledDummy(4f);

            foreach (var folder in folders)
            {
                var included = !this.folderCleanupExcluded.Contains(folder);
                if (ImGui.Checkbox($"{folder}##folder-{folder}", ref included))
                {
                    if (included)
                        this.folderCleanupExcluded.Remove(folder);
                    else
                        this.folderCleanupExcluded.Add(folder);
                }
            }

            var selected = folders.Where(f => !this.folderCleanupExcluded.Contains(f)).ToList();

            ImGuiHelpers.ScaledDummy(4f);

            using (ImRaii.Disabled(selected.Count == 0))
            {
                if (ImGui.Button(this.T(
                        $"Nettoyer les dossiers sélectionnés ({selected.Count})",
                        $"Clean selected folders ({selected.Count})")))
                    this.openFolderCleanupPopup = true;
            }
        }
        else if (this.emptyFolders is not null)
        {
            ImGui.TextUnformatted(this.T("Aucun dossier vide trouvé.", "No empty folder found."));
        }

        ImGuiHelpers.ScaledDummy(4f);

        using (ImRaii.Disabled(!this.plugin.FolderCleanup.CanUndo))
        {
            if (ImGui.Button(
                    this.T("Annuler le dernier nettoyage", "Undo last cleanup"),
                    new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            {
                var undone = this.plugin.FolderCleanup.Undo();
                this.folderCleanupStatus = undone
                                             ? this.T(
                                                 "Dernier nettoyage annulé. Clique sur « Rediscover Mods » dans Penumbra pour actualiser l'affichage.",
                                                 "Last cleanup undone. Click Rediscover Mods in Penumbra to refresh the display.")
                                             : this.T(
                                                 "L'annulation du nettoyage a échoué. Consulte le Journal et réessaie.",
                                                 "Cleanup could not be undone. Check the Log and try again.");
                this.folderCleanupStatusError = !undone;
            }
        }

        this.DrawFolderCleanupConfirmation();
    }

    private void DrawFolderCleanupConfirmation()
    {
        if (this.openFolderCleanupPopup)
        {
            this.openFolderCleanupPopup = false;
            ImGui.OpenPopup(FolderCleanupPopupId);
        }

        if (!ImGui.BeginPopupModal(FolderCleanupPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (this.emptyFolders is not { Count: > 0 } folders)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var selected = folders.Where(f => !this.folderCleanupExcluded.Contains(f)).ToList();

        ImGui.TextWrapped(this.T(
            $"{selected.Count} dossier(s) vont être retirés du fichier interne de Penumbra.",
            $"{selected.Count} folder(s) will be removed from Penumbra's internal file."));

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
        {
            ImGui.TextWrapped(this.T(
                "Pense à cliquer sur « Rediscover Mods » dans Penumbra juste après.",
                "Remember to click Rediscover Mods in Penumbra afterwards."));
        }

        if (this.folderCleanupStatus is { } status)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(status);
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        if (ImGui.Button(this.T("Confirmer", "Confirm")))
        {
            if (this.plugin.FolderCleanup.Remove(selected))
            {
                this.emptyFolders = null;
                this.folderCleanupStatus = this.T(
                    $"{selected.Count} dossier(s) nettoyé(s). Clique sur « Rediscover Mods » dans Penumbra pour actualiser l'affichage.",
                    $"{selected.Count} folder(s) cleaned. Click Rediscover Mods in Penumbra to refresh the display.");
                this.folderCleanupStatusError = false;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.folderCleanupStatus = this.T(
                    "Le nettoyage n'a pas pu être écrit dans organization.json. Consulte le Journal et réessaie.",
                    "Cleanup could not be written to organization.json. Check the Log and try again.");
                this.folderCleanupStatusError = true;
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(this.T("Annuler", "Cancel")))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// <summary>Titre de section. ImGui.SeparatorText n'existe pas dans ces bindings.</summary>
    private void StepHeader(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(label);
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

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
