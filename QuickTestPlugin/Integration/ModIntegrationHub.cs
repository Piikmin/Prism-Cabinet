using System;
using System.Collections.Generic;
using QuickTestPlugin.Services;

namespace QuickTestPlugin.Integration;

/// <summary>
/// Possède les intégrations tierces et centralise leur cycle de vie.
/// </summary>
public sealed class ModIntegrationHub : IDisposable
{
    private readonly IModIntegration[] integrations;
    private bool disposed;

    public ModIntegrationHub(DiagnosticLog log)
    {
        this.Penumbra = new PenumbraIntegration(log);
        this.Glamourer = new GlamourerIntegration(log);
        this.integrations = [this.Penumbra, this.Glamourer];
    }

    public PenumbraIntegration Penumbra { get; }

    public GlamourerIntegration Glamourer { get; }

    public IReadOnlyList<IModIntegration> All => this.integrations;

    /// <summary>Vrai quand Penumbra, indispensable pour lancer un test, est disponible.</summary>
    /// <remarks>Glamourer enrichit le test et la restauration, mais n'est pas obligatoire.</remarks>
    public bool IsReady => this.Penumbra.IsAvailable;

    public void RefreshAll()
    {
        foreach (var integration in this.integrations)
            integration.Refresh();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        foreach (var integration in this.integrations)
            integration.Dispose();
    }
}
