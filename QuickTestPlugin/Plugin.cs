using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;
using QuickTestPlugin.Services;
using QuickTestPlugin.Windows;

namespace QuickTestPlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/prism";

    /// <summary>
    /// Index du joueur local dans la table d'objets du jeu. Il vaut toujours 0 ; c'est aussi la
    /// valeur qu'attendent les API « Player » de Penumbra et Glamourer.
    /// </summary>
    internal const int PlayerObjectIndex = 0;

    private readonly WindowSystem windowSystem = new("Prism Cabinet");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly OptionsWindow optionsWindow;
    private readonly AnimationPickerWindow animationPicker;
    private readonly LogWindow logWindow;
    private readonly PrerequisitePickerWindow prerequisitePicker;
    private readonly ManagementWindow managementWindow;
    private readonly SortWindow sortWindow;

    /// <summary>Dernier rafraîchissement de catalogue programmé ; voir <see cref="ScheduleCatalogRefresh"/>.</summary>
    private object? catalogRefreshTicket;

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Localization = new Localization(this.Configuration);

        this.Diagnostics = new DiagnosticLog();
        this.Integrations = new ModIntegrationHub(this.Diagnostics);
        this.EquipResolver = new EquipResolver(this.Diagnostics);
        this.CustomizationResolver = new CustomizationResolver(this.Diagnostics);
        this.EmotePlayer = new EmotePlayer(this.Diagnostics, this.Localization);
        this.AnimationIdentifier = new AnimationIdentifier(this.Diagnostics);
        this.AnimationBindings = new AnimationBindingStore(this.Configuration);
        this.AppearanceProbe = new AppearanceProbe(this.Integrations.Glamourer, this.Diagnostics);
        this.OptionCatalog = new ModOptionCatalog(this.Integrations.Penumbra, this.Configuration);
        this.ModMeta = new ModMetaReader(this.Integrations.Penumbra, this.Diagnostics);
        this.Sorter = new ModSorter(this.Integrations.Penumbra, this.EquipResolver, this.ModMeta, this.Configuration, this.Diagnostics);
        this.FolderCleanup = new FolderCleanup(this.Configuration, this.Diagnostics);

        // Le catalogue précède les services qui en dépendent : les prérequis résolvent des
        // dossiers de mods en mods connus.
        this.Catalog = new ModCatalog(
            this.Integrations.Penumbra, this.EquipResolver, this.Configuration, this.Diagnostics);
        this.Prerequisites = new PrerequisiteStore(this.Configuration, this.Catalog);
        this.Session = new TestSession(
            this.Integrations,
            this.EquipResolver,
            this.CustomizationResolver,
            this.OptionCatalog,
            this.EmotePlayer,
            this.AnimationIdentifier,
            this.AnimationBindings,
            this.Prerequisites,
            this.Configuration,
            this.Localization,
            this.Diagnostics);

        this.Session.OnTested += this.Catalog.MarkTested;

        this.sortWindow = new SortWindow(this);
        this.mainWindow = new MainWindow(this, this.sortWindow);
        this.configWindow = new ConfigWindow(this);
        this.optionsWindow = new OptionsWindow(this);
        this.animationPicker = new AnimationPickerWindow(this);
        this.logWindow = new LogWindow(this);
        this.prerequisitePicker = new PrerequisitePickerWindow(this);
        this.managementWindow = new ManagementWindow(this);

        this.windowSystem.AddWindow(this.mainWindow);
        this.windowSystem.AddWindow(this.configWindow);
        this.windowSystem.AddWindow(this.optionsWindow);
        this.windowSystem.AddWindow(this.animationPicker);
        this.windowSystem.AddWindow(this.logWindow);
        this.windowSystem.AddWindow(this.prerequisitePicker);
        this.windowSystem.AddWindow(this.managementWindow);

        CommandManager.AddHandler(CommandName, this.CreateCommandInfo());

        // Dalamud masque les fenêtres de plugin en GPose. Or c'est justement là qu'on inspecte
        // une animation : sans ça, la fenêtre disparaît au moment de s'en servir.
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;

        ClientState.Login += this.OnLogin;
        ClientState.Logout += this.OnLogout;

        this.RefreshIntegrations();
        this.Integrations.Penumbra.OnAvailabilityChangedEvent += this.OnIntegrationAvailabilityChanged;
        this.Integrations.Glamourer.OnAvailabilityChangedEvent += this.OnIntegrationAvailabilityChanged;
        this.Integrations.Penumbra.OnModsChangedEvent += this.OnPenumbraModsChanged;
        this.Diagnostics.Info(this.Localization.T("Prism Cabinet chargé.", "Prism Cabinet loaded."));

        if (this.Configuration.OpenOnStartup)
            this.mainWindow.IsOpen = true;
    }

    public Configuration Configuration { get; }

    public Localization Localization { get; }

    public DiagnosticLog Diagnostics { get; }

    public ModIntegrationHub Integrations { get; }

    public EquipResolver EquipResolver { get; }

    public CustomizationResolver CustomizationResolver { get; }

    public AppearanceProbe AppearanceProbe { get; }

    public ModOptionCatalog OptionCatalog { get; }

    public EmotePlayer EmotePlayer { get; }

    public AnimationIdentifier AnimationIdentifier { get; }

    public AnimationBindingStore AnimationBindings { get; }

    public ModMetaReader ModMeta { get; }

    public ModSorter Sorter { get; }

    public FolderCleanup FolderCleanup { get; }

    public PrerequisiteStore Prerequisites { get; }

    public TestSession Session { get; }

    public ModCatalog Catalog { get; }

    public void Dispose()
    {
        this.Session.OnTested -= this.Catalog.MarkTested;
        this.Integrations.Penumbra.OnAvailabilityChangedEvent -= this.OnIntegrationAvailabilityChanged;
        this.Integrations.Glamourer.OnAvailabilityChangedEvent -= this.OnIntegrationAvailabilityChanged;
        this.Integrations.Penumbra.OnModsChangedEvent -= this.OnPenumbraModsChanged;

        ClientState.Login -= this.OnLogin;
        ClientState.Logout -= this.OnLogout;

        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;

        CommandManager.RemoveHandler(CommandName);

        this.windowSystem.RemoveAllWindows();
        this.mainWindow.Dispose();
        this.configWindow.Dispose();
        this.optionsWindow.Dispose();
        this.animationPicker.Dispose();
        this.logWindow.Dispose();
        this.prerequisitePicker.Dispose();
        this.managementWindow.Dispose();
        this.sortWindow.Dispose();

        // Ordre important : la session retire ses réglages temporaires via Penumbra, donc les
        // intégrations doivent encore être vivantes à ce moment-là.
        this.Session.Dispose();
        this.Integrations.Dispose();
    }

    public void ToggleMainUi() => this.mainWindow.Toggle();

    public void ToggleConfigUi() => this.configWindow.Toggle();

    public void ToggleOptionsUi() => this.optionsWindow.Toggle();

    public void ToggleLogUi() => this.logWindow.Toggle();

    public void ToggleManagementUi() => this.managementWindow.Toggle();

    public void ToggleSortUi() => this.mainWindow.OpenSortMode();

    /// <summary>
    /// Supprime un mod de Penumbra et remet l'interface dans un état cohérent.
    /// </summary>
    /// <remarks>
    /// L'état de test est défait d'abord : laisser des réglages temporaires pointer vers un mod
    /// qui n'existe plus n'aurait aucun sens. La liste est ensuite rechargée, Penumbra ne
    /// garantissant que la bonne réception de l'appel, pas la suppression elle-même.
    /// </remarks>
    public bool DeleteMod(ModInfo mod)
    {
        if (this.Session.CanRestore && !this.Session.Restore())
            return false;

        if (!this.Integrations.Penumbra.TryDeleteMod(mod))
            return false;

        this.Session.SelectMod(null);
        this.OptionCatalog.ClearCache();
        this.RefreshCatalog();
        return true;
    }

    /// <summary>Ouvre la désignation manuelle d'animation pour une option, ou pour un mod entier.</summary>
    public void OpenAnimationPicker(ModInfo mod, string? group, string? option)
        => this.animationPicker.Open(mod, group, option);

    /// <summary>Ouvre le choix d'un mod à activer en même temps que celui-ci.</summary>
    public void OpenPrerequisitePicker(ModInfo mod) => this.prerequisitePicker.Open(mod);

    /// <summary>
    /// Retire en bloc les associations et prérequis dont le mod n'existe plus dans Penumbra. La
    /// fenêtre de gestion les affiche déjà en rouge pour les repérer ; ce bouton évite de les
    /// retirer une par une.
    /// </summary>
    public (int Bindings, int Prerequisites) CleanupOrphaned()
    {
        if (!this.Integrations.Penumbra.IsAvailable || !this.Catalog.IsLoaded)
        {
            this.Diagnostics.Warning(
                "Nettoyage refusé : la liste Penumbra n'est pas chargée de façon fiable.");
            return (0, 0);
        }

        bool ModExists(string directory) => this.Catalog.TryGetByDirectory(directory, out _);

        var bindings = this.AnimationBindings.RemoveOrphaned(ModExists);
        var prerequisites = this.Prerequisites.RemoveOrphaned(ModExists);

        if (bindings > 0 || prerequisites > 0)
        {
            this.Diagnostics.Info(
                $"Nettoyage : {bindings} association(s) et {prerequisites} prérequis retirés (mod introuvable).");
        }
        else
        {
            this.Diagnostics.Info("Nettoyage : rien à retirer.");
        }

        return (bindings, prerequisites);
    }

    /// <summary>
    /// Sélectionne et teste le prochain mod non jugé du filtre courant. Le marquage « jugé » est
    /// déjà automatique (voir l'abonnement à <c>Session.OnTested</c>) : ce bouton ne fait qu'avancer
    /// dans la liste sans repasser par un clic dans la liste principale.
    /// </summary>
    public bool TestNext()
    {
        var next = this.Catalog.NextUntested();
        if (next is not { } mod)
        {
            this.Diagnostics.Info("Aucun mod restant à trier avec le filtre courant.");
            return false;
        }

        this.Session.SelectMod(mod);

        if (!this.Session.CanTest)
        {
            this.Diagnostics.Warning($"« {mod.Label} » ne peut pas être testé maintenant.");
            return false;
        }

        return this.Session.Test();
    }

    /// <summary>Recharge la liste des mods et retire une sélection devenue obsolète.</summary>
    public void RefreshCatalog()
    {
        if (!this.Integrations.Penumbra.IsAvailable)
        {
            this.Catalog.Clear();
            return;
        }

        this.Catalog.Refresh();

        if (this.Session.SelectedMod is { } selected && !this.Catalog.Contains(selected))
            this.Session.SelectMod(null);
    }

    /// <summary>Réinterroge les plugins tiers et recharge la liste des mods si possible.</summary>
    public void RefreshIntegrations()
    {
        this.Integrations.RefreshAll();
        this.Session.Reevaluate();
        this.OptionCatalog.ClearCache();
        this.ModMeta.ClearCache();

        this.RefreshCatalog();
    }

    private void OnCommand(string command, string args) => this.mainWindow.Toggle();

    private CommandInfo CreateCommandInfo() => new(this.OnCommand)
    {
        HelpMessage = this.Localization.T(
            "Ouvre ou ferme la fenêtre Prism Cabinet.",
            "Opens or closes the Prism Cabinet window."),
    };

    private void OnLogin() => this.RefreshIntegrations();

    private void OnLogout(int type, int code)
    {
        // Le personnage disparaît : on retire nos réglages temporaires plutôt que de les
        // laisser en place pour le prochain personnage connecté.
        if (this.Session.CanRestore)
            this.Session.Restore();
    }

    private void OnIntegrationAvailabilityChanged(bool available)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                this.Session.Reevaluate();
                this.OptionCatalog.ClearCache();
                this.ModMeta.ClearCache();
                this.ScheduleCatalogRefresh();
            }
            catch (Exception ex)
            {
                this.Diagnostics.Error($"Rafraîchissement après changement d'intégration impossible : {ex.Message}");
            }
        });
    }

    private void OnPenumbraModsChanged()
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                this.OptionCatalog.ClearCache();
                this.ModMeta.ClearCache();
                this.ScheduleCatalogRefresh();
            }
            catch (Exception ex)
            {
                this.Diagnostics.Error($"Rafraîchissement après changement de mod impossible : {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Regroupe les rafraîchissements rapprochés du catalogue en un seul.
    /// </summary>
    /// <remarks>
    /// Un import de plusieurs mods dans Penumbra déclenche un événement par mod : sans ce délai,
    /// chacun relancerait son propre balayage complet (un appel Penumbra par mod, plus la liste
    /// des objets modifiés de tout le catalogue). Le jeton garantit que seul le dernier appel
    /// programmé déclenche réellement le rafraîchissement ; les précédents se voient court-circuités
    /// sans qu'il soit nécessaire d'annuler quoi que ce soit explicitement.
    /// </remarks>
    private void ScheduleCatalogRefresh()
    {
        var ticket = new object();
        this.catalogRefreshTicket = ticket;

        _ = Plugin.Framework.RunOnTick(
            () =>
            {
                if (!ReferenceEquals(this.catalogRefreshTicket, ticket))
                    return;

                try
                {
                    this.RefreshCatalog();
                }
                catch (Exception ex)
                {
                    this.Diagnostics.Error($"Rafraîchissement du catalogue impossible : {ex.Message}");
                }
            },
            TimeSpan.FromMilliseconds(400));
    }
}
