using System.Collections.Generic;

namespace QuickTestPlugin.Models;

/// <summary>
/// Nœud de l'arborescence de mods, reprenant les dossiers de tri construits dans Penumbra.
/// </summary>
/// <remarks>
/// Un dossier fermé ne doit rien coûter : le rendu ne descend dans <see cref="Folders"/> que si
/// l'utilisateur l'a ouvert, ce que ce modèle rend possible en gardant les enfants à part plutôt
/// que dans une liste unique aplatie.
/// </remarks>
public sealed class ModFolderNode
{
    public ModFolderNode(string name, string path)
    {
        this.Name = name;
        this.Path = path;
    }

    /// <summary>Segment de dossier affiché, sans le chemin des parents.</summary>
    public string Name { get; }

    /// <summary>
    /// Chemin complet depuis la racine, séparé par « / ». Sert d'identifiant ImGui stable : deux
    /// dossiers de même nom à des endroits différents de l'arborescence ne doivent pas partager
    /// leur état ouvert/fermé.
    /// </summary>
    public string Path { get; }

    public List<ModFolderNode> Folders { get; } = [];

    public List<ModEntry> Mods { get; } = [];
}
