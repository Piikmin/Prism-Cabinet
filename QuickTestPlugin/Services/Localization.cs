using Dalamud.Game;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Localisation légère de l'interface. Les données de mods restent volontairement dans leur
/// langue d'origine ; seuls les textes produits par Prism Cabinet passent par ce service.
/// </summary>
public sealed class Localization
{
    private readonly Configuration configuration;

    public Localization(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool IsEnglish
    {
        get
        {
            return this.configuration.Language switch
            {
                LanguageMode.French => false,
                LanguageMode.English => true,
                _ => Plugin.ClientState.ClientLanguage != ClientLanguage.French,
            };
        }
    }

    public string ActiveLanguageLabel => this.IsEnglish ? "English" : "Français";

    public string T(string french, string english) => this.IsEnglish ? english : french;

    public string Count(int count, string singularFrench, string pluralFrench, string singularEnglish, string pluralEnglish)
        => count == 1
               ? this.T(singularFrench, singularEnglish)
               : this.T(pluralFrench, pluralEnglish);

    public string Gender(string gender)
        => gender == "Female" ? this.T("Féminin", "Female") : this.T("Masculin", "Male");

    public string Kind(ModKind kind) => kind switch
    {
        ModKind.Equipment => this.T("Équipement", "Equipment"),
        ModKind.Customization => this.T("Apparence", "Appearance"),
        ModKind.Animation => this.T("Animations", "Animations"),
        _ => this.T("Autres", "Other"),
    };

    public string StateLabel(TestState state) => state switch
    {
        TestState.Idle => this.T("Inactif", "Idle"),
        TestState.Ready => this.T("Prêt", "Ready"),
        TestState.Applying => this.T("Application en cours", "Applying"),
        TestState.Applied => this.T("Mod appliqué", "Mod applied"),
        TestState.Restoring => this.T("Restauration en cours", "Restoring"),
        TestState.Failed => this.T("Échec - voir le journal", "Failed - see log"),
        _ => this.T("Inconnu", "Unknown"),
    };
}
