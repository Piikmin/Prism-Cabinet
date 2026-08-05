using System;
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
/// Choix d'un mod à activer en même temps qu'un autre.
/// </summary>
public sealed class PrerequisitePickerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private ModInfo? target;
    private string search = string.Empty;

    public PrerequisitePickerWindow(Plugin plugin)
        : base("Add prerequisite###QuickTestPrerequisitePicker")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public void Open(ModInfo mod)
    {
        this.target = mod;
        this.search = string.Empty;
        this.IsOpen = true;
    }

    public override void Draw()
    {
        this.WindowName = $"{this.T("Ajouter un prérequis", "Add prerequisite")}###QuickTestPrerequisitePicker";

        if (this.target is not { } mod)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun mod sélectionné.", "No mod selected."));
            }

            return;
        }

        ImGui.TextWrapped(this.T($"À activer avec : {mod.Label}", $"Enable with: {mod.Label}"));

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(
                this.T(
                    "Le mod choisi sera appliqué en même temps, sous une priorité plus basse : " +
                    "un corps doit passer sous le vêtement qu'on juge.",
                    "The selected mod will be applied at the same time with lower priority, " +
                    "so a body can sit underneath the outfit being tested."));
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##QuickTestPrerequisiteSearch", this.T("Rechercher un mod", "Search for a mod"), ref this.search, 128);

        using var child = ImRaii.Child("##QuickTestPrerequisiteResults", Vector2.Zero, true);
        if (!child.Success)
            return;

        var matches = this.plugin.Catalog.All
                          .Where(m => m.Directory != mod.Directory)
                          .Where(m => string.IsNullOrWhiteSpace(this.search)
                                      || m.Label.Contains(this.search, StringComparison.CurrentCultureIgnoreCase))
                          .ToList();

        if (matches.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Aucun mod ne correspond.", "No mod matches."));
            }

            return;
        }

        // Plusieurs centaines de mods : seules les lignes visibles sont dessinées.
        var clipper = new ImGuiListClipper();
        clipper.Begin(matches.Count, ImGui.GetTextLineHeightWithSpacing());

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var candidate = matches[i];
                var already = this.plugin.Prerequisites.Has(mod, candidate.Directory);

                using (ImRaii.Disabled(already))
                {
                    if (ImGui.Selectable($"{candidate.Label}##{candidate.Directory}"))
                    {
                        this.plugin.Prerequisites.Add(mod, candidate);
                        this.IsOpen = false;
                    }
                }
            }
        }

        clipper.End();
    }

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
