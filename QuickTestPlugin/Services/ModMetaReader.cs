using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QuickTestPlugin.Integration;
using QuickTestPlugin.Models;

namespace QuickTestPlugin.Services;

/// <summary>
/// Lit ce qu'un mod déclare sur lui-même dans son fichier de métadonnées.
/// </summary>
/// <remarks>
/// Penumbra n'expose ni description ni dépendances par son IPC - il n'a pas de notion de
/// dépendance du tout. Or c'est dans la description que les auteurs indiquent ce qu'il faut
/// activer par ailleurs. On lit donc le fichier sur disque, dans le dossier que Penumbra
/// désigne lui-même, en lecture seule et sans jamais y écrire.
/// </remarks>
public sealed class ModMetaReader
{
    private const string MetaFileName = "meta.json";

    private readonly PenumbraIntegration penumbra;
    private readonly DiagnosticLog log;

    private readonly Dictionary<string, ModMeta> cache = new(StringComparer.Ordinal);

    public ModMetaReader(PenumbraIntegration penumbra, DiagnosticLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    public void ClearCache() => this.cache.Clear();

    /// <summary>Métadonnées d'un mod, vides si le fichier est absent ou illisible.</summary>
    public ModMeta For(ModInfo mod)
    {
        if (this.cache.TryGetValue(mod.Directory, out var cached))
            return cached;

        var meta = this.Read(mod);
        this.cache[mod.Directory] = meta;
        return meta;
    }

    private ModMeta Read(ModInfo mod)
    {
        var root = this.penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(root))
            return ModMeta.Empty;

        var path = Path.Combine(root, mod.Directory, MetaFileName);

        try
        {
            if (!File.Exists(path))
                return ModMeta.Empty;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var element = document.RootElement;

            return new ModMeta(
                ReadString(element, "Author"),
                ReadString(element, "Description"),
                ReadString(element, "Version"),
                ReadString(element, "Website"));
        }
        catch (Exception ex)
        {
            // Un mod mal formé ne doit pas empêcher de le tester : on s'en passe.
            this.log.Debug($"Métadonnées illisibles pour « {mod.Label} » : {ex.Message}");
            return ModMeta.Empty;
        }
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
               ? value.GetString() ?? string.Empty
               : string.Empty;
}
