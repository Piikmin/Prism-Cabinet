using System.Collections.Generic;

namespace QuickTestPlugin.Models;

/// <summary>
/// Une association animation ↔ option telle qu'enregistrée, sans résolution du mod.
/// </summary>
/// <remarks>
/// Utilisée par la vue de gestion, qui doit pouvoir lister - et donc nettoyer - une association
/// même si le mod auquel elle se rapporte a depuis été supprimé de Penumbra.
/// </remarks>
/// <param name="ModDirectory">Dossier du mod concerné, identifiant stable côté Penumbra.</param>
/// <param name="Group">Groupe d'options visé, ou null si l'association vaut pour le mod entier.</param>
/// <param name="Option">Option visée, ou null si l'association vaut pour le mod entier.</param>
/// <param name="Bindings">Animations associées à cette option ou à ce mod.</param>
public readonly record struct AnimationBindingEntry(
    string ModDirectory,
    string? Group,
    string? Option,
    IReadOnlyList<AnimationBinding> Bindings);
