using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Journal de diagnostic et outils d'inspection.
/// </summary>
/// <remarks>
/// Séparés de la fenêtre principale : ils ne servent qu'en cas de problème, alors qu'ils
/// occupaient la moitié de la hauteur utile en permanence.
/// </remarks>
public sealed class LogWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string animationSearch = string.Empty;

    public LogWindow(Plugin plugin)
        : base("Prism Cabinet - Log###QuickTestLogWindow")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.WindowName = $"{this.T("Prism Cabinet - Journal", "Prism Cabinet - Log")}###QuickTestLogWindow";

        if (ImGui.SmallButton(this.T("Vider", "Clear")))
            this.plugin.Diagnostics.Clear();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);

        var submitted = ImGui.InputTextWithHint(
            "##QuickTestAnimationSearch",
            this.T("chercher une animation", "search for an animation"),
            ref this.animationSearch,
            64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(this.T(
                "Cherche une émote par son nom ou sa commande.\nLe résultat s'affiche dans le journal.",
                "Searches for an emote by name or command.\nThe result is added to the log."));

        ImGui.SameLine();

        if (ImGui.SmallButton(this.T("Chercher", "Search")) || submitted)
        {
            foreach (var line in this.plugin.AnimationIdentifier.Search(this.animationSearch))
                this.plugin.Diagnostics.Info(line);
        }

        if (this.plugin.Configuration.VerboseDiagnostics)
        {
            ImGui.SameLine();

            if (ImGui.SmallButton(this.T("Sonder l'apparence", "Probe appearance")))
                this.plugin.AppearanceProbe.Run();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.T(
                    "Compare l'état de Glamourer aux valeurs typées de Dalamud.\nNe modifie rien.",
                    "Compares Glamourer's state with Dalamud's typed values.\nDoes not change anything."));
        }

        ImGui.Separator();

        using var child = ImRaii.Child("##QuickTestDiagnostics", Vector2.Zero, false);
        if (!child.Success)
            return;

        var verbose = this.plugin.Configuration.VerboseDiagnostics;

        foreach (var entry in this.plugin.Diagnostics.Snapshot())
        {
            if (entry.Level == DiagnosticLevel.Debug && !verbose)
                continue;

            using (ImRaii.PushColor(ImGuiCol.Text, ColorFor(entry.Level)))
            {
                ImGui.TextWrapped(entry.ToString());
            }
        }
    }

    private static Vector4 ColorFor(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Debug => ImGuiColors.DalamudGrey,
        DiagnosticLevel.Warning => ImGuiColors.DalamudOrange,
        DiagnosticLevel.Error => ImGuiColors.DalamudRed,
        _ => ImGuiColors.DalamudWhite,
    };

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
