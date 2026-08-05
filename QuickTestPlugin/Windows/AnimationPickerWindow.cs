using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Windows;

/// <summary>
/// Désignation manuelle de l'animation que déclenche une option de mod.
/// </summary>
public sealed class AnimationPickerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private ModInfo? target;
    private string? targetGroup;
    private string? targetOption;
    private string search = string.Empty;

    public AnimationPickerWindow(Plugin plugin)
        : base("Link an animation###QuickTestAnimationPicker")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    /// <summary>Ouvre la fenêtre pour une option précise, ou pour le mod entier si elle est nulle.</summary>
    public void Open(ModInfo mod, string? group, string? option)
    {
        this.target = mod;
        this.targetGroup = group;
        this.targetOption = option;
        this.search = string.Empty;
        this.IsOpen = true;
    }

    public override void Draw()
    {
        this.WindowName = $"{this.T("Associer une animation", "Link an animation")}###QuickTestAnimationPicker";

        if (this.target is not { } mod)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted(this.T("Rien à associer.", "Nothing to link."));
            }

            return;
        }

        ImGui.TextWrapped(this.targetOption ?? mod.Label);

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(this.targetOption is null
                                  ? this.T("L'association vaudra pour tout le mod.", "This link will apply to the whole mod.")
                                  : this.T($"Groupe « {this.targetGroup} ».", $"Group: {this.targetGroup}."));
        }

        if (this.plugin.AnimationBindings.Has(mod, this.targetGroup, this.targetOption)
            && ImGui.SmallButton(this.T("Retirer toutes les associations", "Remove all links")))
        {
            this.plugin.AnimationBindings.Clear(mod, this.targetGroup, this.targetOption);
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##QuickTestPickerSearch", this.T("nom ou commande d'émote", "emote name or command"), ref this.search, 64);

        using var child = ImRaii.Child("##QuickTestPickerResults", Vector2.Zero, true);
        if (!child.Success)
            return;

        var results = this.plugin.AnimationIdentifier.SearchPlayable(this.search);

        if (results.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(this.search)
                                      ? this.T(
                                          "Tape un nom d'émote, en français, ou sa commande en anglais.",
                                          "Type an emote name or its command.")
                                      : this.T("Aucune émote ne correspond.", "No emote matches."));
            }

            return;
        }

        foreach (var match in results)
        {
            if (ImGui.Selectable($"{match.Label}  {match.Source}##{match.TimelineId}"))
                this.Bind(mod, match);
        }
    }

    /// <summary>Retient l'association, puis joue l'animation pour que le choix se vérifie aussitôt.</summary>
    private void Bind(ModInfo mod, AnimationMatch match)
    {
        this.plugin.AnimationBindings.Add(
            mod,
            this.targetGroup,
            this.targetOption,
            new AnimationBinding(match.TimelineId, match.Label));

        if (this.plugin.Session.PlayAnimation(match))
            this.plugin.Diagnostics.Info(this.T(
                $"Animation associée et lancée : « {match.Label} ».",
                $"Linked and played animation: {match.Label}."));
        else
            this.plugin.Diagnostics.Warning(this.T(
                "Animation non jouée : le mod n'a pas pu être appliqué.",
                "Animation could not be played: the mod could not be applied."));
        this.IsOpen = false;
    }

    private string T(string french, string english) => this.plugin.Localization.T(french, english);
}
