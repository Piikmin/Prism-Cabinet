using System;

namespace QuickTestPlugin.Models;

/// <summary>
/// Un mod avec de quoi le ranger : son classement dans Penumbra, sa nature et son ancienneté.
/// </summary>
/// <param name="Mod">Le mod lui-même.</param>
/// <param name="SortPath">
/// Chemin complet dans l'arborescence de Penumbra, celle que l'utilisateur y a construite.
/// </param>
/// <param name="Kind">Nature déduite des objets modifiés.</param>
/// <param name="Added">Date du dossier sur disque, qui approche la date d'installation.</param>
public readonly record struct ModEntry(ModInfo Mod, string SortPath, ModKind Kind, DateTime Added)
{
    /// <summary>
    /// Dossier de rangement, vide à la racine. Le chemin de tri se termine par le nom du mod :
    /// c'est ce qui précède qui constitue le classement.
    /// </summary>
    public string Folder
    {
        get
        {
            var normalized = this.SortPath.Replace('\\', '/').Trim('/');
            var separator = normalized.LastIndexOf('/');
            return separator <= 0 ? string.Empty : normalized[..separator];
        }
    }
}
