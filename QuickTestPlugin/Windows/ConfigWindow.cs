using System;
using System.Numerics;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("Prism Cabinet Settings###QuickTestConfigWindow",
               ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        this.configuration = plugin.Configuration;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 430),
            MaximumSize = new Vector2(640, 720),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.WindowName = $"{this.plugin.Localization.T("Réglages Prism Cabinet", "Prism Cabinet Settings")}###QuickTestConfigWindow";

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.plugin.Localization.T(
                "Configure les automatismes utilisés pendant un test.",
                "Configure the automation used during a test."));
        }

        ImGuiHelpers.ScaledDummy(8f);
        DrawSectionHeader(this.plugin.Localization.T("Test", "Testing"));
        this.DrawToggle(
            this.plugin.Localization.T("Équiper les pièces concernées", "Equip affected gear"),
            this.configuration.AutoEquip,
            value => this.configuration.AutoEquip = value,
            this.plugin.Localization.T(
                "Un mod d'équipement n'est visible que si le personnage porte la pièce concernée.\n" +
                "L'équipement d'origine est remis par Restaurer.",
                "An equipment mod is visible only when your character wears the affected slot.\n" +
                "The original gear is restored by Restore."));
        this.DrawToggle(
            this.plugin.Localization.T("Isoler le mod du reste de la tenue", "Isolate the mod from the rest of the outfit"),
            this.configuration.IsolateEquipment,
            value => this.configuration.IsolateEquipment = value,
            this.plugin.Localization.T(
                "Vide les emplacements d'équipement que le mod ne concerne pas,\n" +
                "pour le juger sans le reste de la tenue. Les armes sont conservées.",
                "Clears gear slots the mod does not affect,\n" +
                "so it can be judged without the rest of the outfit. Weapons are kept."));
        this.DrawToggle(
            this.plugin.Localization.T("Adapter automatiquement la coiffure et le visage", "Automatically adapt hair and face"),
            this.configuration.AutoApplyCustomization,
            value => this.configuration.AutoApplyCustomization = value,
            this.plugin.Localization.T(
                "Un mod de customisation reste invisible si le personnage porte une autre coupe\n" +
                "ou un autre visage. Rien n'est appliqué si plusieurs valeurs sont ambiguës.",
                "A customization mod remains invisible if your character has different hair\n" +
                "or a different face. Nothing is applied when several values are ambiguous."));
        this.DrawToggle(
            this.plugin.Localization.T("Compléter les groupes d'options vides", "Fill empty option groups"),
            this.configuration.AutoSelectEmptyGroups,
            value => this.configuration.AutoSelectEmptyGroups = value,
            this.plugin.Localization.T(
                "Certains mods n'ont aucune option sélectionnée par défaut. La première option\n" +
                "de chaque groupe compatible est alors retenue.",
                "Some mods have no option selected by default. The first compatible option\n" +
                "in each group is selected."));
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(
                this.plugin.Localization.T(
                    "Les variantes de pose restent sous ton contrôle : Prism Cabinet indique la cible, " +
                    "mais n'exécute jamais /cpose à ta place.",
                    "Pose variants remain under your control: Prism Cabinet shows the target, " +
                    "but never runs /cpose for you."));
        }

        ImGuiHelpers.ScaledDummy(8f);
        DrawSectionHeader(this.plugin.Localization.T("Interface", "Interface"));
        this.DrawLanguageSelector();
        this.DrawToggle(
            this.plugin.Localization.T("Ouvrir Prism Cabinet au démarrage", "Open Prism Cabinet on startup"),
            this.configuration.OpenOnStartup,
            value => this.configuration.OpenOnStartup = value,
            this.plugin.Localization.T(
                "Ouvre Prism Cabinet automatiquement lorsque le plugin est chargé.",
                "Automatically opens Prism Cabinet when the plugin loads."));

        if (ImGui.Button(
                this.plugin.Localization.T("Réinitialiser le suivi des tests", "Reset test tracking"),
                new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            this.plugin.Catalog.ClearTested();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.plugin.Localization.T(
                "Efface les pastilles de la bibliothèque pour repartir d'un tri vierge.",
                "Clears the library markers so you can start a fresh review."));

        ImGuiHelpers.ScaledDummy(8f);
        DrawSectionHeader(this.plugin.Localization.T("Diagnostic", "Diagnostics"));
        this.DrawToggle(
            this.plugin.Localization.T("Afficher les messages détaillés", "Show detailed messages"),
            this.configuration.VerboseDiagnostics,
            value => this.configuration.VerboseDiagnostics = value,
            this.plugin.Localization.T(
                "Ajoute les messages de niveau Debug au Journal. À activer pour enquêter sur un problème.",
                "Adds Debug-level messages to the Log. Enable this when investigating a problem."));

        ImGuiHelpers.ScaledDummy(8f);
        DrawSectionHeader(this.plugin.Localization.T("Outils", "Tools"));
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.plugin.Localization.T(
                "Consulte les erreurs ou corrige les associations manuelles.",
                "Review errors or edit manual associations."));
        }

        if (ImGui.Button(
                this.plugin.Localization.T("Ouvrir le journal", "Open log"),
                new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            this.plugin.ToggleLogUi();

        ImGui.SameLine();
        if (ImGui.Button(
                this.plugin.Localization.T("Gérer les associations", "Manage associations"),
                new Vector2(0, 28f * ImGuiHelpers.GlobalScale)))
            this.plugin.ToggleManagementUi();

    }

    private void DrawLanguageSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(this.plugin.Localization.T("Langue de l'interface", "Interface language"));
        ImGui.SameLine();

        var preview = this.configuration.Language switch
        {
            LanguageMode.French => "Français",
            LanguageMode.English => "English",
            _ => this.plugin.Localization.T(
                "Automatique - langue du jeu ({0})",
                "Automatic - game language ({0})").Replace("{0}", this.plugin.Localization.ActiveLanguageLabel),
        };

        if (ImGui.BeginCombo("##QuickTestLanguage", preview))
        {
            this.DrawLanguageOption(LanguageMode.Automatic, this.plugin.Localization.T(
                "Automatique - langue du jeu", "Automatic - game language"));
            this.DrawLanguageOption(LanguageMode.French, "Français");
            this.DrawLanguageOption(LanguageMode.English, "English");
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.plugin.Localization.T(
                "Le changement est appliqué immédiatement.",
                "Changes apply immediately."));

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawLanguageOption(LanguageMode mode, string label)
    {
        if (ImGui.Selectable(label, this.configuration.Language == mode))
        {
            this.configuration.Language = mode;
            this.configuration.Save();
        }
    }

    private void DrawToggle(string label, bool current, Action<bool> assign, string tooltip)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value))
        {
            assign(value);
            this.configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void DrawSectionHeader(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet))
        {
            ImGui.TextUnformatted(label);
        }

        ImGui.Separator();
    }
}
