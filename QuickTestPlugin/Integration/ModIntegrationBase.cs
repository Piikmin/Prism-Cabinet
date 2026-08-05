using System;
using System.Collections.Generic;
using System.Linq;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Integration;

/// <summary>
/// Base commune : détection de présence du plugin tiers, vérification de la version d'API et
/// abonnement à ses événements de cycle de vie.
/// </summary>
public abstract class ModIntegrationBase : IModIntegration
{
    private readonly List<IDisposable> lifecycleSubscribers = [];
    private bool disposed;
    private bool lifecycleBound;

    protected ModIntegrationBase(DiagnosticLog log)
    {
        this.Log = log;
    }

    public abstract string DisplayName { get; }

    public abstract string InternalName { get; }

    /// <summary>
    /// Version majeure d'API contre laquelle ce code est compilé. Une majeure différente côté
    /// plugin signifie une rupture de contrat : on refuse alors d'émettre le moindre appel.
    /// </summary>
    protected abstract int ExpectedApiMajor { get; }

    public bool IsAvailable { get; private set; }

    public string StatusText { get; private set; } = "Non vérifié";

    /// <summary>Version d'API rapportée par le plugin tiers, ou null si non interrogée.</summary>
    public (int Major, int Minor)? ApiVersion { get; private set; }

    /// <summary>Notifie le plugin principal quand la disponibilité de l'intégration change.</summary>
    public event Action<bool>? OnAvailabilityChangedEvent;

    protected DiagnosticLog Log { get; }

    /// <summary>Interroge la version d'API du plugin tiers. Peut lever si l'IPC n'est pas prêt.</summary>
    protected abstract (int Major, int Minor) QueryApiVersion();

    /// <summary>Crée les abonnements aux événements Initialized/Disposed du plugin tiers.</summary>
    protected abstract IEnumerable<IDisposable> CreateLifecycleSubscribers(Action onLifecycleEvent);

    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.BindLifecycle();

        var wasAvailable = this.IsAvailable;

        var exposed = Plugin.PluginInterface.InstalledPlugins
                            .FirstOrDefault(p => string.Equals(p.InternalName, this.InternalName, StringComparison.OrdinalIgnoreCase));

        if (exposed is null)
        {
            this.SetUnavailable("Non installé");
        }
        else if (!exposed.IsLoaded)
        {
            this.SetUnavailable("Installé mais non chargé");
        }
        else
        {
            try
            {
                var version = this.QueryApiVersion();
                this.ApiVersion = version;

                if (version.Major != this.ExpectedApiMajor)
                {
                    this.SetUnavailable($"API v{version.Major}.{version.Minor} incompatible (attendu v{this.ExpectedApiMajor}.x)");
                    this.Log.Error(
                        $"{this.DisplayName} expose l'API v{version.Major}.{version.Minor} alors que Prism Cabinet est compilé " +
                        $"pour v{this.ExpectedApiMajor}.x. Aucun appel ne sera émis : mettre à jour le paquet NuGet correspondant.");
                }
                else
                {
                    this.IsAvailable = true;
                    this.StatusText = $"Disponible (plugin v{exposed.Version}, API v{version.Major}.{version.Minor})";
                }
            }
            catch (Exception ex)
            {
                // Cas normal pendant le chargement : le plugin est là mais n'a pas encore
                // enregistré ses points d'entrée IPC.
                this.SetUnavailable("IPC pas encore prêt");
                this.Log.Debug($"{this.DisplayName} : interrogation de l'API impossible ({ex.GetType().Name}).");
            }
        }

        if (this.IsAvailable != wasAvailable)
        {
            this.OnAvailabilityChanged(this.IsAvailable);
            this.OnAvailabilityChangedEvent?.Invoke(this.IsAvailable);
        }

        this.Log.Debug($"{this.DisplayName} : {this.StatusText}");
    }

    private void SetUnavailable(string reason)
    {
        this.IsAvailable = false;
        this.StatusText = reason;
    }

    /// <summary>
    /// S'abonne une seule fois aux événements du plugin tiers. L'abonnement n'exige pas que le
    /// plugin soit chargé : c'est justement ce qui permet de réagir à son arrivée.
    /// </summary>
    private void BindLifecycle()
    {
        if (this.lifecycleBound)
            return;

        this.lifecycleBound = true;

        try
        {
            this.lifecycleSubscribers.AddRange(this.CreateLifecycleSubscribers(this.OnLifecycleEvent));
        }
        catch (Exception ex)
        {
            this.Log.Warning($"{this.DisplayName} : abonnement aux événements impossible ({ex.GetType().Name}).");
        }
    }

    private void OnLifecycleEvent()
    {
        if (this.disposed)
            return;

        this.Log.Debug($"{this.DisplayName} a signalé un changement de cycle de vie.");
        this.Refresh();
    }

    /// <summary>Appelé quand la disponibilité bascule dans un sens ou dans l'autre.</summary>
    protected virtual void OnAvailabilityChanged(bool available)
    {
        this.Log.Info(available
                          ? $"{this.DisplayName} disponible."
                          : $"{this.DisplayName} indisponible : {this.StatusText}.");
    }

    /// <summary>Exécute un appel IPC en convertissant toute panne en échec journalisé.</summary>
    protected bool TryInvoke(string operation, Action call)
    {
        if (!this.IsAvailable)
        {
            this.Log.Warning($"{this.DisplayName} indisponible : « {operation} » ignoré.");
            return false;
        }

        try
        {
            call();
            return true;
        }
        catch (Exception ex)
        {
            this.Log.Error($"{this.DisplayName} - échec de « {operation} » : {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        foreach (var subscriber in this.lifecycleSubscribers)
        {
            try
            {
                subscriber.Dispose();
            }
            catch (Exception ex)
            {
                this.Log.Warning($"{this.DisplayName} : désabonnement imparfait ({ex.GetType().Name}).");
            }
        }

        this.lifecycleSubscribers.Clear();
        this.IsAvailable = false;
        this.StatusText = "Libéré";
    }
}
