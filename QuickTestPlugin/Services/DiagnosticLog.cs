using System;
using System.Collections.Generic;

namespace QuickTestPlugin.Services;

public enum DiagnosticLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public readonly record struct DiagnosticEntry(DateTime Timestamp, DiagnosticLevel Level, string Message)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss} [{Level}] {Message}";
}

/// <summary>
/// Journal circulaire consulté par la fenêtre de diagnostic. Chaque entrée est également
/// relayée vers le log Dalamud (/xllog) pour pouvoir enquêter après coup.
/// </summary>
/// <remarks>
/// Les intégrations Penumbra/Glamourer notifieront depuis des callbacks IPC, qui ne sont pas
/// nécessairement sur le thread de dessin : les accès sont donc verrouillés.
/// </remarks>
public sealed class DiagnosticLog
{
    private const int Capacity = 200;

    private readonly object gate = new();
    private readonly Queue<DiagnosticEntry> entries = new(Capacity);

    public void Debug(string message) => Add(DiagnosticLevel.Debug, message);
    public void Info(string message) => Add(DiagnosticLevel.Info, message);
    public void Warning(string message) => Add(DiagnosticLevel.Warning, message);
    public void Error(string message) => Add(DiagnosticLevel.Error, message);

    public void Add(DiagnosticLevel level, string message)
    {
        var entry = new DiagnosticEntry(DateTime.Now, level, message);

        lock (this.gate)
        {
            if (this.entries.Count >= Capacity)
                this.entries.Dequeue();

            this.entries.Enqueue(entry);
        }

        switch (level)
        {
            case DiagnosticLevel.Debug:
                Plugin.Log.Debug(message);
                break;
            case DiagnosticLevel.Warning:
                Plugin.Log.Warning(message);
                break;
            case DiagnosticLevel.Error:
                Plugin.Log.Error(message);
                break;
            default:
                Plugin.Log.Information(message);
                break;
        }
    }

    /// <summary>Copie immuable destinée au rendu, de la plus ancienne à la plus récente.</summary>
    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (this.gate)
        {
            return this.entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
        }
    }
}
