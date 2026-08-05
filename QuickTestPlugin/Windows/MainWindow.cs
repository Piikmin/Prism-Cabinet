using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Models;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Fenêtre principale. Le titre visible est « Prism Cabinet » ; la partie après ### est un
/// identifiant ImGui stable, indépendant du titre.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const string DeletePopupId = "Supprimer ce mod ?###QuickTestDeleteConfirm";
    private static readonly Vector4 PanelBackground = new(0.025f, 0.03f, 0.045f, 0.96f);
    private static readonly Vector4 PanelSurface = new(0.045f, 0.052f, 0.075f, 0.98f);
    private static readonly Vector4 PanelSurfaceHover = new(0.08f, 0.065f, 0.13f, 0.98f);
    private static readonly Vector4 FilterActiveBackground = new(0.34f, 0.18f, 0.55f, 1f);
    private static readonly Vector4 FilterActiveHover = new(0.44f, 0.25f, 0.68f, 1f);
    private static readonly Vector4 PrimaryButtonBackground = new(0.38f, 0.2f, 0.62f, 1f);
    private static readonly Vector4 PrimaryButtonHover = new(0.5f, 0.3f, 0.76f, 1f);

    private readonly Plugin plugin;
    private readonly SortWindow sortWindow;

    private MainMode mode;
    private bool modeSelectionRequested;

    private ModInfo? pendingDeletion;
    private bool openDeletePopup;
    private string? deletionStatus;

    public MainWindow(Plugin plugin, SortWindow sortWindow)
        : base(
            "Prism Cabinet###QuickTestMainWindow",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.sortWindow = sortWindow;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.WindowName = $"Prism Cabinet###QuickTestMainWindow";
        this.DrawTopNavigation();
        ImGuiHelpers.ScaledDummy(8f);

        if (this.mode is MainMode.Test)
        {
            this.DrawWorkspace();
        }
        else
        {
            this.DrawSortWorkspace();
        }

        // Popups modaux : ouverts et dessinés au niveau de la fenêtre, jamais dans un enfant ou
        // une section repliable - sinon le OpenPopup émis ailleurs ne serait jamais retrouvé par
        // le BeginPopupModal correspondant.
        this.DrawDeleteConfirmation();
    }

    public void OpenSortMode()
    {
        this.mode = MainMode.Sort;
        this.modeSelectionRequested = true;
        this.IsOpen = true;
    }

    private void DrawTopNavigation()
    {
        if (ImGui.BeginTable("##QuickTestNavigation", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(this.T("Modes", "Modes"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(this.T("Réglages", "Settings"), ImGuiTableColumnFlags.WidthFixed, 38f * ImGuiHelpers.GlobalScale);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (ImGui.BeginTabBar("##QuickTestModes"))
            {
                this.DrawModeTab(MainMode.Test, this.T("Tester", "Test"));
                this.DrawModeTab(MainMode.Sort, this.T("Trier", "Sort"));
                ImGui.EndTabBar();
            }

            ImGui.TableNextColumn();
            var settingsSize = 34f * ImGuiHelpers.GlobalScale;
            if (ImGuiComponents.IconButton(
                    "QuickTestSettings",
                    FontAwesomeIcon.Cog,
                    new Vector2(settingsSize, 30f * ImGuiHelpers.GlobalScale)))
                this.plugin.ToggleConfigUi();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T("Réglages", "Settings"));

            ImGui.EndTable();
        }

        this.modeSelectionRequested = false;
    }

    private void DrawModeTab(MainMode target, string label)
    {
        var flags = this.modeSelectionRequested && this.mode == target
                        ? ImGuiTabItemFlags.SetSelected
                        : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem(label, flags))
        {
            this.mode = target;
            ImGui.EndTabItem();
        }
    }

    private void DrawSortWorkspace()
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackground))
        using (var panel = ImRaii.Child("##QuickTestSortWorkspace", Vector2.Zero, true))
        {
            if (panel.Success)
                this.sortWindow.DrawEmbedded();
        }
    }

    private void DrawWorkspace()
    {
        var available = ImGui.GetContentRegionAvail();
        var split = available.X >= 720f * ImGuiHelpers.GlobalScale;

        if (split)
        {
            var leftWidth = MathF.Max(280f * ImGuiHelpers.GlobalScale, available.X * 0.4f);

            using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackground))
            using (var library = ImRaii.Child("##QuickTestLibraryPanel", new Vector2(leftWidth, 0), true))
            {
                if (library.Success)
                    this.DrawLibraryPanel();
            }

            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackground))
            using (var details = ImRaii.Child("##QuickTestDetailsPanel", new Vector2(0, 0), true))
            {
                if (details.Success)
                    this.DrawDetailsPanel();
            }

            return;
        }

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackground))
        using (var library = ImRaii.Child(
                   "##QuickTestLibraryPanelStacked",
                   new Vector2(-1, 340f * ImGuiHelpers.GlobalScale),
                   true))
        {
            if (library.Success)
                this.DrawLibraryPanel();
        }

        ImGuiHelpers.ScaledDummy(8f);

        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelBackground))
        using (var details = ImRaii.Child("##QuickTestDetailsPanelStacked", new Vector2(-1, 0), true))
        {
            if (details.Success)
                this.DrawDetailsPanel();
        }
    }

    private void DrawLibraryPanel()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(this.T("Bibliothèque", "Library"));
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.T("Sélectionne un mod pour préparer son test.", "Select a mod to prepare its test."));
        }

        var catalog = this.plugin.Catalog;
        if (!catalog.IsLoaded)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(this.T("La bibliothèque n'est pas encore disponible.", "The library is not available yet."));
            }

            if (ImGui.SmallButton(this.T("Réessayer", "Retry")))
                this.plugin.RefreshCatalog();

            return;
        }

        var filter = catalog.Filter;
        var searchWidth = MathF.Min(
            300f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X);

        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##QuickTestModFilter", this.T("Rechercher un mod…", "Search for a mod…"), ref filter, 128))
            catalog.Filter = filter;

        this.DrawListControls(catalog);
        this.DrawSortControl(catalog);
        this.DrawTestedControl(catalog);
        ImGuiHelpers.ScaledDummy(4f);
        this.DrawModList(catalog, this.plugin.Session);
        this.DrawNextTestAction(catalog);
    }

    private void DrawDetailsPanel()
    {
        if (this.plugin.Session.SelectedMod is not { } mod)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
            {
                ImGui.TextUnformatted(this.T("Prêt à tester", "Ready to test"));
            }

            ImGuiHelpers.ScaledDummy(8f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(
                    this.T(
                        "Sélectionne un mod dans la bibliothèque. Tu pourras ensuite voir ce qu'il modifie, " +
                        "choisir ses options et le tester immédiatement.",
                        "Select a mod from the library to see what it changes, choose its options, and test it immediately."));
            }

            ImGuiHelpers.ScaledDummy(8f);
            this.DrawWelcomeSteps();
            ImGuiHelpers.ScaledDummy(12f);
            this.DrawIntegrationWarning();
            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(mod.Label);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextUnformatted(this.SelectedKindLabel());
        }

        ImGuiHelpers.ScaledDummy(8f);
        this.DrawImpactSummary();
        ImGuiHelpers.ScaledDummy(8f);
        this.DrawOptionSummary(mod);
        ImGuiHelpers.ScaledDummy(8f);
        this.DrawActions();

        if (this.plugin.Session.State is not TestState.Ready
            || this.plugin.Integrations.All.Any(integration => !integration.IsAvailable))
        {
            ImGuiHelpers.ScaledDummy(8f);
            this.DrawTestState();
        }

        ImGuiHelpers.ScaledDummy(8f);
        this.DrawAdvancedDetails();
    }

    private string SelectedKindLabel()
    {
        var impact = this.plugin.Session.Impact;
        if (impact.Equipment.Count > 0)
            return this.plugin.Localization.Kind(ModKind.Equipment);

        if (impact.Customizations.Count > 0)
            return this.plugin.Localization.Kind(ModKind.Customization);

        if (impact.Others.Any(other => other.EmoteId is not null))
            return this.T("Animation", "Animation");

        return this.T("Autre modification", "Other change");
    }

    private void DrawImpactSummary()
    {
        var impact = this.plugin.Session.Impact;
        if (impact.Total == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun impact détaillé détecté.", "No detailed impact detected."));
            }

            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(this.T("Ce que le mod touche", "What the mod affects"));
        }

        var labels = new List<string>();
        if (impact.Equipment.Count > 0)
            labels.Add(this.plugin.Localization.Count(
                impact.Equipment.Count, $"{impact.Equipment.Count} équipement", $"{impact.Equipment.Count} équipements",
                $"{impact.Equipment.Count} piece", $"{impact.Equipment.Count} pieces"));

        if (impact.Customizations.Count > 0)
            labels.Add(this.plugin.Localization.Count(
                impact.Customizations.Count, $"{impact.Customizations.Count} apparence", $"{impact.Customizations.Count} apparences",
                $"{impact.Customizations.Count} appearance", $"{impact.Customizations.Count} appearances"));

        if (impact.Others.Count > 0)
        {
            var animationCount = impact.Others.Count(other => other.EmoteId is not null);
            labels.Add(animationCount == impact.Others.Count
                           ? this.plugin.Localization.Count(
                               animationCount, $"{animationCount} animation", $"{animationCount} animations",
                               $"{animationCount} animation", $"{animationCount} animations")
                           : this.plugin.Localization.Count(
                               impact.Others.Count, $"{impact.Others.Count} élément", $"{impact.Others.Count} éléments",
                               $"{impact.Others.Count} item", $"{impact.Others.Count} items"));
        }

        foreach (var label in labels)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen))
            {
                ImGui.TextUnformatted($"· {label}");
            }
        }
    }

    private void DrawWelcomeSteps()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var scale = ImGuiHelpers.GlobalScale;

        if (availableWidth < 520f * scale)
        {
            this.DrawWelcomeStep("1", this.T("Choisir", "Choose"), this.T("Sélectionne un mod dans la bibliothèque.", "Select a mod from the library."));
            this.DrawWelcomeStep("2", this.T("Préparer", "Prepare"), this.T("Ajuste les variantes ou garde les choix automatiques.", "Adjust variants or keep automatic choices."));
            this.DrawWelcomeStep("3", this.T("Tester", "Test"), this.T("Applique, compare, puis restaure en un clic.", "Apply, compare, then restore with one click."));
            return;
        }

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var width = (availableWidth - (2f * gap)) / 3f;
        this.DrawWelcomeStepCard("1", this.T("Choisir", "Choose"), this.T("Sélectionne un mod dans la bibliothèque.", "Select a mod from the library."), width);
        ImGui.SameLine();
        this.DrawWelcomeStepCard("2", this.T("Préparer", "Prepare"), this.T("Ajuste les variantes si nécessaire.", "Adjust variants if needed."), width);
        ImGui.SameLine();
        this.DrawWelcomeStepCard("3", this.T("Tester", "Test"), this.T("Compare puis restaure en un clic.", "Compare, then restore with one click."), width);
    }

    private void DrawWelcomeStep(string number, string title, string description)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(number);
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(title);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(description);
        }

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawWelcomeStepCard(string number, string title, string description, float width)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, PanelSurface))
        using (var card = ImRaii.Child($"##QuickTestWelcomeStep{number}", new Vector2(width, 88f * ImGuiHelpers.GlobalScale), true))
        {
            if (!card.Success)
                return;

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
            {
                ImGui.TextUnformatted(number);
            }

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
            {
                ImGui.TextUnformatted(title);
            }

            ImGuiHelpers.ScaledDummy(4f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(description);
            }
        }
    }

    private void DrawAdvancedDetails()
    {
        this.DrawPrerequisites();
        this.DrawImpact();
    }

    private void DrawOptionSummary(ModInfo mod)
    {
        var groups = this.plugin.OptionCatalog.GroupsFor(mod);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(this.T("Options", "Options"));
        }

        if (groups.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Ce mod n'expose aucune option.", "This mod exposes no options."));
            }

            return;
        }

        var customized = groups.Count(group => this.plugin.OptionCatalog.IsUserControlled(mod, group.Name));
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(customized == 0
                                  ? this.T("Les choix automatiques seront utilisés.", "Automatic choices will be used.")
                                  : this.T($"{customized} groupe(s) personnalisé(s).", $"{customized} customized group(s)."));
        }

        var optionLabel = groups.Count == 1
                              ? this.T("Configurer l'option", "Configure option")
                              : this.T($"Configurer les options ({groups.Count})", $"Configure options ({groups.Count})");
        if (ImGui.Button(
                optionLabel,
                new Vector2(
                    MathF.Min(240f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X),
                    30f * ImGuiHelpers.GlobalScale)))
        {
            this.plugin.ToggleOptionsUi();
        }
    }

    /// <summary>Filtres par type de mod.</summary>
    private void DrawListControls(ModCatalog catalog)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextUnformatted(this.T("Types", "Types"));
        }

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(
            72f * ImGuiHelpers.GlobalScale,
            (ImGui.GetContentRegionAvail().X - (2f * gap)) / 3f);
        var allKinds = catalog.KindFilter.Count == 0;
        if (this.DrawFilterButton(this.T("Tous", "All"), "kind-all", allKinds, width))
        {
            catalog.KindFilter.Clear();
            catalog.RefreshFilter();
        }

        ImGui.SameLine();
        this.DrawKindFilterButton(catalog, KindLabels[0], width);
        ImGui.SameLine();
        this.DrawKindFilterButton(catalog, KindLabels[1], width);

        this.DrawKindFilterButton(catalog, KindLabels[2], width);
        ImGui.SameLine();
        this.DrawKindFilterButton(catalog, KindLabels[3], width);
    }

    private void DrawSortControl(ModCatalog catalog)
    {
        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextUnformatted(this.T("Ordre", "Order"));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(
            180f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X));
        var preview = catalog.SortByDate ? this.T("Plus récents", "Most recent") : this.T("Par dossiers", "By folder");
        if (ImGui.BeginCombo("##QuickTestSortOrder", preview))
        {
            if (ImGui.Selectable(this.T("Par dossiers", "By folder"), !catalog.SortByDate))
                catalog.SortByDate = false;

            if (ImGui.Selectable(this.T("Plus récents", "Most recent"), catalog.SortByDate))
                catalog.SortByDate = true;

            ImGui.EndCombo();
        }
    }

    private void DrawNextTestAction(ModCatalog catalog)
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        var untested = catalog.UntestedCount;
        var startX = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        var buttonWidth = MathF.Min(180f * ImGuiHelpers.GlobalScale, available);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextUnformatted(this.T(
                $"{untested} mod{(untested > 1 ? "s" : string.Empty)} non testé{(untested > 1 ? "s" : string.Empty)}",
                $"{untested} untested mod{(untested == 1 ? string.Empty : "s")}"));
        }

        if (available >= 320f * ImGuiHelpers.GlobalScale)
            ImGui.SameLine(startX + available - buttonWidth);

        using (ImRaii.Disabled(untested == 0))
        {
            if (ImGui.Button(
                    this.T("Tester le suivant", "Test next"),
                    new Vector2(buttonWidth, 30f * ImGuiHelpers.GlobalScale)))
                this.plugin.TestNext();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(untested == 0
                                  ? this.T("Tous les mods affichés ont déjà été testés.", "All displayed mods have already been tested.")
                                  : this.T(
                                      "Sélectionne et teste le prochain mod non encore jugé selon les filtres actifs.",
                                      "Select and test the next unreviewed mod using the active filters."));
        }
    }

    private void DrawTestedControl(ModCatalog catalog)
    {
        var showTested = catalog.ShowTested;
        if (ImGui.Checkbox(
                this.T("Afficher les mods déjà testés", "Show tested mods"),
                ref showTested))
        {
            catalog.ShowTested = showTested;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T(
                "Masque les mods déjà testés pour se concentrer sur ceux qui restent à juger.",
                "Hides tested mods so you can focus on the ones still to review."));
    }

    private void DrawKindFilterButton(
        ModCatalog catalog,
        ModKind kind,
        float width)
    {
        var label = this.plugin.Localization.Kind(kind);
        var active = catalog.KindFilter.Contains(kind);
        if (!this.DrawFilterButton(label, $"kind-{kind}", active, width))
            return;

        if (!catalog.KindFilter.Add(kind))
            catalog.KindFilter.Remove(kind);

        if (catalog.KindFilter.Count == KindLabels.Length)
            catalog.KindFilter.Clear();

        catalog.RefreshFilter();
    }

    private bool DrawFilterButton(string label, string id, bool active, float width)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, FilterActiveBackground, active))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, FilterActiveHover, active))
        {
            return ImGui.Button(
                $"{label}##{id}",
                new Vector2(width, 26f * ImGuiHelpers.GlobalScale));
        }
    }

    private void DrawModList(ModCatalog catalog, TestSession session)
    {
        var narrowActionBar = ImGui.GetContentRegionAvail().X < 320f * ImGuiHelpers.GlobalScale;
        var actionBarHeight = (narrowActionBar ? 88f : 60f) * ImGuiHelpers.GlobalScale;
        var height = MathF.Max(
            100f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().Y - actionBarHeight);
        using var child = ImRaii.Child("##QuickTestModList", new Vector2(-1, height), true);
        if (!child.Success)
            return;

        if (catalog.Filtered.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(catalog.Filter)
                                      ? catalog.ShowTested
                                          ? this.T("Aucun mod disponible dans cette catégorie.", "No mod is available in this category.")
                                          : this.T(
                                              "Tous les mods de cette catégorie ont déjà été testés.",
                                              "All mods in this category have already been tested.")
                                      : this.T(
                                          "Aucun mod ne correspond à la recherche et aux filtres courants.",
                                          "No mod matches the current search and filters."));
            }

            return;
        }

        // Par date, l'arborescence n'a pas de sens (on veut les derniers ajouts, tous dossiers
        // confondus) : la liste reste plate, et c'est le seul mode où le nombre de lignes est
        // fixe d'une image à l'autre, condition nécessaire pour que le clipper fonctionne.
        if (catalog.SortByDate)
            this.DrawFlatModList(catalog, session);
        else
            this.DrawFolderTree(catalog, session, catalog.FilteredTree);
    }

    private void DrawFlatModList(ModCatalog catalog, TestSession session)
    {
        var mods = catalog.Filtered;

        using (var table = ImRaii.Table(
                   "##QuickTestRecentMods",
                   2,
                   ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX))
        {
            if (!table.Success)
                return;

            ImGui.TableSetupColumn(this.T("Mod", "Mod"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(this.T("Date", "Date"), ImGuiTableColumnFlags.WidthFixed, 72f * ImGuiHelpers.GlobalScale);

            // Plusieurs centaines de mods : seules les lignes visibles sont dessinées.
            var clipper = new ImGuiListClipper();
            clipper.Begin(mods.Count, ImGui.GetTextLineHeightWithSpacing());

            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var entry = mods[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    this.DrawModEntry(catalog, session, entry);

                    ImGui.TableNextColumn();
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                    {
                        ImGui.TextUnformatted(
                            entry.Added == DateTime.MinValue
                                ? "-"
                                : entry.Added.ToLocalTime().ToString("dd/MM/yy"));
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            entry.Added == DateTime.MinValue
                                ? this.T("Date d'ajout indisponible.", "Installation date unavailable.")
                                : this.T(
                                    "Date d'ajout estimée à partir du dossier du mod.",
                                    "Installation date estimated from the mod folder."));
                    }
                }
            }

            clipper.End();
        }
    }

    /// <summary>
    /// Rendu récursif de l'arborescence de dossiers.
    /// </summary>
    /// <remarks>
    /// Pas de découpage ici : un dossier fermé ne descend jamais dans ses enfants - c'est
    /// <c>TreeNodeEx</c> qui l'indique via sa valeur de retour - donc son coût reste constant
    /// quelle que soit sa taille. Seul un dossier ouvert redessine son contenu à chaque image,
    /// ce qui reste équivalent au coût de la liste plate d'avant pour ce qui est visible.
    /// </remarks>
    private void DrawFolderTree(ModCatalog catalog, TestSession session, ModFolderNode node)
    {
        foreach (var folder in node.Folders)
        {
            var opened = ImGui.TreeNodeEx(folder.Path, ImGuiTreeNodeFlags.SpanAvailWidth, folder.Name);

            if (!opened)
                continue;

            this.DrawFolderTree(catalog, session, folder);
            ImGui.TreePop();
        }

        foreach (var entry in node.Mods)
            this.DrawModEntry(catalog, session, entry);
    }

    private void DrawModEntry(ModCatalog catalog, TestSession session, ModEntry entry)
    {
        var mod = entry.Mod;
        var isSelected = session.SelectedMod?.Directory == mod.Directory;
        var tested = catalog.IsTested(mod);

        // Une pastille discrète plutôt qu'une colonne : elle ne sert qu'à ne pas repasser deux
        // fois sur le même mod pendant un tri.
        var marker = tested ? "· " : "  ";

        using (ImRaii.PushColor(ImGuiCol.Header, PanelSurfaceHover, isSelected))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, FilterActiveHover, isSelected))
        using (ImRaii.PushColor(
                   ImGuiCol.Text,
                   isSelected ? ImGuiColors.DalamudWhite : tested ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudWhite))
        {
            // Le dossier sert d'ID ImGui : deux mods peuvent porter le même nom affiché.
            if (ImGui.Selectable($"{marker}{mod.Label}##{mod.Directory}", isSelected))
                session.SelectMod(isSelected ? null : mod);
        }

        if (ImGui.IsItemHovered())
        {
            // Dossier tu en mode arborescence, où il se lit déjà dans la position du mod ;
            // rappelé en mode plat (tri par date), où rien d'autre ne l'indique.
            var folder = catalog.SortByDate
                             ? this.T(
                                 $"Dossier : {(string.IsNullOrEmpty(entry.Folder) ? "(racine)" : entry.Folder)}\n",
                                 $"Folder: {(string.IsNullOrEmpty(entry.Folder) ? "(root)" : entry.Folder)}\n")
                             : string.Empty;

            ImGui.SetTooltip(
                $"{this.KindLabel(entry.Kind)}{(tested ? this.T(" · déjà testé", " · tested") : string.Empty)}\n" +
                $"{folder}" +
                this.T("Clic droit : supprimer ce mod.", "Right-click to delete this mod."));
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            this.pendingDeletion = mod;
            this.openDeletePopup = true;
        }
    }

    private static readonly ModKind[] KindLabels =
    [
        ModKind.Equipment,
        ModKind.Customization,
        ModKind.Animation,
        ModKind.Other,
    ];

    private string KindLabel(ModKind kind) => this.plugin.Localization.Kind(kind);

    /// <summary>
    /// Confirmation de suppression. Penumbra efface le dossier du mod du disque : l'action est
    /// définitive, elle ne peut pas tenir en un seul clic.
    /// </summary>
    /// <remarks>
    /// Le popup s'ouvre ici et non au clic : le clic droit a lieu dans l'enfant de la liste, et
    /// un OpenPopup émis dans une autre portée d'identifiants ne serait jamais retrouvé par le
    /// BeginPopupModal de celle-ci.
    /// </remarks>
    private void DrawDeleteConfirmation()
    {
        if (this.openDeletePopup)
        {
            this.openDeletePopup = false;
            this.deletionStatus = null;
            ImGui.OpenPopup(DeletePopupId);
        }

        if (!ImGui.BeginPopupModal(DeletePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (this.pendingDeletion is not { } mod)
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

        if (this.deletionStatus is { } status)
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
                this.pendingDeletion = null;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                this.deletionStatus = this.T(
                    "La suppression n'a pas été confirmée par Penumbra. Vérifie le Journal et réessaie.",
                    "Penumbra did not confirm the deletion. Check the Log and try again.");
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(this.T("Annuler", "Cancel")))
        {
            this.pendingDeletion = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// Ce que l'auteur du mod déclare, les prérequis configurés, et l'inventaire d'impact visible
    /// plus bas pour les changements qu'aucun automatisme ne couvre.
    /// </summary>
    /// <remarks>
    /// Penumbra n'a aucune notion de dépendance : un mod exigeant un corps particulier ne le dit
    /// que dans sa description, en texte libre. L'afficher répond souvent seule à la question,
    /// et la déclaration manuelle couvre le reste.
    /// </remarks>
    private void DrawPrerequisites()
    {
        if (this.plugin.Session.SelectedMod is not { } mod)
            return;

        var meta = this.plugin.ModMeta.For(mod);
        var required = this.plugin.Prerequisites.For(mod);

        if (!ImGui.CollapsingHeader(
                $"{this.T("Description et prérequis", "Description and prerequisites")}###QuickTestPrerequisites"))
            return;

        using var indent = ImRaii.PushIndent(12f);

        if (meta.HasDescription)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(meta.Description);
            }

            ImGuiHelpers.ScaledDummy(4f);
        }

        foreach (var prerequisite in required)
        {
            if (ImGui.SmallButton($"×##prereq-{prerequisite.Directory}"))
                this.plugin.Prerequisites.Remove(mod, prerequisite.Directory);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T("Ne plus activer ce mod avec celui-ci.", "Stop enabling this mod with it."));

            ImGui.SameLine();
            ImGui.TextWrapped(this.T($"Activé avec : {prerequisite.Label}", $"Enabled with: {prerequisite.Label}"));
        }

        if (required.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(this.T("Aucun mod requis n'est associé à ce test.", "No required mod is associated with this test."));
            }

            ImGuiHelpers.ScaledDummy(4f);
        }

        // Les prérequis déclarés dont le mod n'existe plus sont tus : ils ne doivent pas
        // encombrer, et le test fonctionne sans eux.
        if (ImGui.Button(
                this.T("Associer un mod requis", "Link a required mod"),
                new Vector2(0, 30f * ImGuiHelpers.GlobalScale)))
            this.plugin.OpenPrerequisitePicker(mod);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T(
                "Choisit un mod qui sera activé automatiquement avec celui-ci.",
                "Choose a mod that will be enabled automatically with this one."));

        ImGuiHelpers.ScaledDummy(8f);
    }

    private void DrawImpact()
    {
        var impact = this.plugin.Session.Impact;
        if (impact.Total == 0)
            return;

        if (!ImGui.CollapsingHeader(
                $"{this.T("Ce que le mod modifie", "What the mod changes")} ({impact.Total})###QuickTestImpact"))
            return;

        using var indent = ImRaii.PushIndent(12f);

        foreach (var target in impact.Equipment)
            ImGui.TextWrapped($"{target.Slot} - {target.Name}");

        foreach (var custom in impact.Customizations)
            ImGui.TextWrapped($"{custom.Index} {custom.Value} - {custom.Race} {custom.Gender}");

        if (impact.Others.Count == 0)
            return;

        ImGuiHelpers.ScaledDummy(4f);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
        {
            ImGui.TextWrapped(impact.Others.Any(change => change.EmoteId is not null)
                                  ? this.T(
                                      "Prism Cabinet lance automatiquement l'unique animation qu'il reconnaît. " +
                                      "S'il existe plusieurs variantes, choisis celle à essayer dans Options.",
                                      "Prism Cabinet automatically plays the only animation it recognizes. " +
                                      "If there are several variants, choose one in Options.")
                                  : this.T(
                                      "Ces effets ne peuvent pas être déclenchés automatiquement. " +
                                      "Le mod est actif : utilise l'action correspondante dans le jeu pour les voir.",
                                      "These effects cannot be triggered automatically. " +
                                      "The mod is active: use the corresponding in-game action to see them."));
        }

        foreach (var other in impact.Others)
            this.DrawOtherChange(other);
    }

    /// <summary>
    /// Une émote se joue depuis la fenêtre ; le reste s'affiche avec ce que Penumbra en dit.
    /// </summary>
    private void DrawOtherChange(OtherChange other)
    {
        if (other.EmoteId is { } emoteId)
        {
            if (ImGui.SmallButton($"{this.T("Jouer", "Play")}##{emoteId}"))
            {
                if (!this.plugin.Session.PlayEmote(emoteId, other.Label))
                    this.plugin.Diagnostics.Warning(this.T(
                        "Émote non jouée : le mod n'a pas pu être appliqué.",
                        "Emote could not be played: the mod could not be applied."));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    this.T(
                        "Applique le mod si besoin, puis maintient l'animation.\n" +
                        "L'effet est local : les autres joueurs ne la voient pas.",
                        "Applies the mod if needed, then holds the animation.\n" +
                        "The effect is local: other players cannot see it."));

            ImGui.SameLine();
        }

        ImGui.TextUnformatted(other.Label);

        if (other.Detail is null)
            return;

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen))
        {
            ImGui.TextUnformatted($"- {other.Detail}");
        }
    }

    private void DrawTestState()
    {
        SectionHeader(this.T("État du test", "Test status"));

        var session = this.plugin.Session;
        using (ImRaii.PushColor(ImGuiCol.Text, ColorFor(session.State)))
        {
            ImGui.TextUnformatted(this.plugin.Localization.StateLabel(session.State));
        }

        if (session.AppliedMod is { } applied)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted($"- {applied.Label}");
            }
        }

        if (session.State is TestState.Failed)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(this.T("Voir le journal", "View log")))
                this.plugin.ToggleLogUi();
        }

        this.DrawIntegrationWarning();
    }

    private void DrawIntegrationWarning()
    {
        var unavailable = this.plugin.Integrations.All.Where(integration => !integration.IsAvailable).ToList();
        if (unavailable.Count == 0)
            return;

        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
        {
            ImGui.TextWrapped(this.T(
                "Le test est limité car une intégration requise n'est pas disponible.",
                "Testing is limited because a required integration is unavailable."));
        }

        foreach (var integration in unavailable)
        {
            ImGui.TextUnformatted($"{integration.DisplayName} :");
            ImGui.SameLine(140f * ImGuiHelpers.GlobalScale);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextUnformatted(integration.StatusText);
            }
        }

        if (ImGui.SmallButton(this.T("Revérifier", "Recheck")))
            this.plugin.RefreshIntegrations();
    }

    /// <summary>
    /// Couverture du mod et genre de test. Rien n'est basculé automatiquement : changer le genre
    /// change le modèle de corps entier, c'est trop visible pour être décidé sans l'utilisateur.
    /// </summary>
    private void DrawGenderChoice()
    {
        var session = this.plugin.Session;
        if (!session.HasSelection)
            return;

        var coverage = session.Coverage;
        var current = session.CurrentGender;

        if (coverage.Count == 1 && !coverage.Contains(current))
        {
            var target = coverage.Contains("Female") ? this.T("féminin", "female") : this.T("masculin", "male");
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextWrapped(this.T(
                    $"Ce mod cible le modèle {target}. Sélectionne {Human(coverage.Single())} pour le prévisualiser.",
                    $"This mod targets the {target} model. Select {Human(coverage.Single())} to preview it."));
            }

            if (session.Impact.CoverageIsHeuristic && ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T("Couverture déduite des noms d'options du mod.", "Coverage inferred from the mod's option names."));
        }

        var other = current == "Female" ? "Male" : "Female";

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(this.T("Genre :", "Gender:"));

        // Deux choix seulement : celui du personnage et l'autre. Le genre d'origine se lit dans
        // la sélection, il n'a pas besoin d'être nommé « Actuel ».
        // Avant le test, le choix est mémorisé ; pendant un test actif, la bascule est immédiate.
        ImGui.SameLine();
        if (ImGui.RadioButton(Human(current), session.TestGender is null))
            session.SetTestGender(null);

        ImGui.SameLine();
        if (ImGui.RadioButton(Human(other), session.TestGender == other))
            session.SetTestGender(other);
    }

    private string Human(string gender) => this.plugin.Localization.Gender(gender);

    private void DrawActions()
    {
        var session = this.plugin.Session;

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudWhite))
        {
            ImGui.TextUnformatted(this.T("Action de test", "Test action"));
        }

        this.DrawGenderChoice();

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var showTest = !session.IsSelectedApplied;
        var showRestore = session.CanRestore && session.IsSelectedApplied;
        var buttonWidth = showTest && showRestore
                              ? MathF.Max(112f * ImGuiHelpers.GlobalScale, (availableWidth - gap) / 2f)
                              : MathF.Min(320f * ImGuiHelpers.GlobalScale, availableWidth);

        if (showTest)
        {
            using (ImRaii.Disabled(!session.CanTest))
            using (ImRaii.PushColor(ImGuiCol.Button, PrimaryButtonBackground, session.CanTest))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryButtonHover, session.CanTest))
            {
                if (ImGui.Button(this.T("Tester ce mod", "Test this mod"), new Vector2(buttonWidth, 34f * ImGuiHelpers.GlobalScale)))
                    session.Test();
            }

            if (ImGui.IsItemHovered() && session.TestWouldReplace)
                ImGui.SetTooltip(this.T(
                    "Restaure l'état d'origine, puis applique la sélection courante.",
                    "Restores the original state, then applies the current selection."));
        }

        if (showRestore)
        {
            if (showTest)
                ImGui.SameLine();

            if (ImGui.Button(this.T("Restaurer", "Restore"), new Vector2(buttonWidth, 34f * ImGuiHelpers.GlobalScale)))
                session.Restore();
        }

        var poseGuidance = this.plugin.EmotePlayer.CurrentPoseGuidance;
        if (poseGuidance is { } guidance)
            this.DrawPoseGuidance(guidance);
        else if (session.PoseChoicesRequiringSelection.Count > 0)
            this.DrawPoseChoiceNotice(session.PoseChoicesRequiringSelection.Count);

        // Ce qui est maintenu doit rester visible et arrêtable en permanence : enfoui dans une
        // section dépliable, le contrôle disparaissait avec elle pendant que l'animation tournait.
        if (this.plugin.EmotePlayer.HeldDescription is { } holding)
        {
            var isAnimating = this.plugin.EmotePlayer.IsAnimating;
            var statusColor = poseGuidance is { IsReached: false }
                                  ? ImGuiColors.DalamudOrange
                                  : ImGuiColors.ParsedGreen;
            using (ImRaii.PushColor(ImGuiCol.Text, statusColor))
            {
                ImGui.TextUnformatted(isAnimating
                                          ? this.T($"Animation en cours : {holding}", $"Animation playing: {holding}")
                                          : this.T($"Guidage de pose actif : {holding}", $"Pose guidance active: {holding}"));
            }

            if (ImGui.Button(
                    isAnimating ? this.T("Arrêter", "Stop") : this.T("Terminer", "Finish"),
                    new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
                session.StopAnimation();

            if (isAnimating)
            {
                ImGui.SameLine();

                if (ImGui.Button(
                        this.T("Relancer l'animation", "Replay animation"),
                        new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
                    this.plugin.Session.RedrawAndReplay();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        this.T(
                            "Redessine le personnage, puis relance l'animation.\n" +
                            "À utiliser seulement si l'aperçu ne s'est pas actualisé.",
                            "Redraws the character, then replays the animation.\n" +
                            "Use only if the preview did not update."));
            }
        }
        else if (session.CanReplayLastAnimation)
        {
            var wasAnimation = session.LastPlaybackWasAnimation;
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(wasAnimation
                                          ? this.T("Animation arrêtée", "Animation stopped")
                                          : this.T("Guidage de pose terminé", "Pose guidance finished"));
            }

            if (ImGui.Button(
                    wasAnimation
                        ? this.T("Relancer l'animation", "Replay animation")
                        : this.T("Réafficher le guidage", "Show guidance again"),
                    new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
                session.ReplayLastAnimation();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(wasAnimation
                                     ? this.T(
                                         "Relance la dernière animation sans restaurer ni réappliquer le mod.",
                                         "Replays the last animation without restoring or reapplying the mod.")
                                     : this.T(
                                         "Réaffiche la variante attendue sans modifier la pose du personnage.",
                                         "Shows the expected variant again without changing the character's pose."));
        }

        var reason = session.BlockedReason;
        if (reason is not null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(reason);
            }
        }
    }

    private void DrawPoseGuidance(PoseGuidance guidance)
    {
        ImGuiHelpers.ScaledDummy(8f);

        var background = guidance.IsReached
                             ? new Vector4(0.04f, 0.18f, 0.10f, 0.92f)
                             : new Vector4(0.22f, 0.13f, 0.025f, 0.94f);
        var accent = guidance.IsReached ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudOrange;
        var height = guidance.IsReached ? 88f : guidance.HasTarget ? 136f : 118f;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, background))
        using (ImRaii.PushColor(ImGuiCol.Border, accent))
        using (var card = ImRaii.Child(
                   "##QuickTestPoseGuidance",
                   new Vector2(-1, height * ImGuiHelpers.GlobalScale),
                   true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card.Success)
                return;

            using (ImRaii.PushColor(ImGuiCol.Text, accent))
            {
                ImGui.TextUnformatted(guidance.IsReached
                                          ? this.T("Pose correcte", "Correct pose")
                                          : guidance.HasTarget
                                              ? this.T("Action requise - change la pose", "Action required - change pose")
                                              : this.T("Variantes de pose - à toi de choisir", "Pose variants - choose one"));
            }

            if (guidance.HasTarget)
            {
                ImGui.TextWrapped(
                    this.T(
                        $"Posture {guidance.Family} - variante demandée {guidance.TargetNumber} sur {guidance.PoseCount}.",
                        $"{guidance.Family} pose - requested variant {guidance.TargetNumber} of {guidance.PoseCount}."));
            }
            else
            {
                var currentDisplay = guidance.CurrentNumber?.ToString() ?? this.T("en attente", "waiting");
                ImGui.TextWrapped(
                    this.T(
                        $"Posture {guidance.Family} - variante actuelle {currentDisplay} sur {guidance.PoseCount}.",
                        $"{guidance.Family} pose - current variant {currentDisplay} of {guidance.PoseCount}."));
                ImGui.TextWrapped(this.T(
                    "Le mod ne précise pas de cible. Utilise /cpose pour parcourir les variantes.",
                    "The mod does not specify a target. Use /cpose to cycle through variants."));
            }

            if (guidance.IsReached)
            {
                ImGui.TextWrapped(this.T(
                    "La bonne variante est active. Tu peux maintenant vérifier le mod.",
                    "The correct variant is active. You can now check the mod."));
                return;
            }

            if (!guidance.HasTarget)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                {
                    ImGui.TextWrapped(this.T(
                        "Prism Cabinet observe ton choix sans envoyer la commande.",
                        "Prism Cabinet observes your choice without sending the command."));
                }

                return;
            }

            if (guidance.CurrentNumber is { } current)
            {
                var remaining = guidance.ChangesRemaining ?? 0;
                ImGui.TextWrapped(
                    this.T(
                        $"Variante actuelle : {current} sur {guidance.PoseCount}. Utilise /cpose {remaining} fois.",
                        $"Current variant: {current} of {guidance.PoseCount}. Use /cpose {remaining} time(s)."));
            }
            else
            {
                ImGui.TextWrapped(
                    this.T(
                        $"La posture {guidance.Family} n'est pas encore active. Lance-la dans le jeu si nécessaire.",
                        $"The {guidance.Family} pose is not active yet. Start it in-game if needed."));
            }

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(this.T(
                    "Prism Cabinet détectera automatiquement la variante choisie, sans envoyer la commande.",
                    "Prism Cabinet will detect the selected variant without sending the command."));
            }
        }
    }

    private void DrawPoseChoiceNotice(int count)
    {
        ImGuiHelpers.ScaledDummy(8f);

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.22f, 0.13f, 0.025f, 0.94f)))
        using (ImRaii.PushColor(ImGuiCol.Border, ImGuiColors.DalamudOrange))
        using (var card = ImRaii.Child(
                   "##QuickTestPoseChoice",
                   new Vector2(-1, 108f * ImGuiHelpers.GlobalScale),
                   true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card.Success)
                return;

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
            {
                ImGui.TextUnformatted(this.T("Action requise - choisis une variante", "Action required - choose a variant"));
            }

            ImGui.TextWrapped(count == 1
                                  ? this.T(
                                      "Une variante de pose a été reconnue parmi plusieurs animations possibles. " +
                                      "Choisis-la explicitement pour afficher son guidage.",
                                      "A pose variant was recognized among several possible animations. " +
                                      "Choose it explicitly to show its guidance.")
                                  : this.T(
                                      $"{count} variantes de pose ont été reconnues. Prism Cabinet ne peut pas en choisir une à ta place.",
                                      $"{count} pose variants were recognized. Prism Cabinet cannot choose one for you."));

            if (ImGui.Button(
                    this.T("Choisir dans les options", "Choose in options"),
                    new Vector2(220f * ImGuiHelpers.GlobalScale, 28f * ImGuiHelpers.GlobalScale)))
                this.plugin.ToggleOptionsUi();
        }
    }

    private string T(string french, string english) => this.plugin.Localization.T(french, english);

    /// <summary>Titre de section. ImGui.SeparatorText n'existe pas dans ces bindings.</summary>
    private static void SectionHeader(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(label);
        }

        ImGui.Separator();
    }

    private static Vector4 ColorFor(TestState state) => state switch
    {
        TestState.Applied => ImGuiColors.HealerGreen,
        TestState.Failed => ImGuiColors.DalamudRed,
        TestState.Applying or TestState.Restoring => ImGuiColors.DalamudOrange,
        TestState.Ready => ImGuiColors.ParsedGreen,
        _ => ImGuiColors.DalamudWhite,
    };

    private static Vector4 ColorFor(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Debug => ImGuiColors.DalamudGrey,
        DiagnosticLevel.Warning => ImGuiColors.DalamudOrange,
        DiagnosticLevel.Error => ImGuiColors.DalamudRed,
        _ => ImGuiColors.DalamudWhite,
    };

    private enum MainMode
    {
        Test,
        Sort,
    }
}
