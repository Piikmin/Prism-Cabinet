namespace QuickTestPlugin.Models;

/// <summary>Comment ranger un mod en dossiers pour le tri automatique.</summary>
public enum SortStrategy
{
    ByCreator,
    ByKind,
    ByKindThenCreator,
    ByCreatorThenKind,
}
