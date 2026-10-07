// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Despada.Config;

public sealed class DespadaSettings
{
    public const string DefaultToggleKey = "Delete";

    public int    Version       { get; set; } = 1;
    public string Theme         { get; set; } = "dark";     // "dark" | "light"
    public float  GlassOpacity  { get; set; } = 0.92f;      // 0.6 .. 1.0
    public float  GlassBlur     { get; set; } = 0.5f;       // 0 (off) .. 1
    public string ToggleKey     { get; set; } = DefaultToggleKey; // Robust Keyboard.Key name

    /// <summary>Per feature (by id): on/off and its own settings.</summary>
    public Dictionary<string, FeatureSettings> Features { get; set; } = new();

    public void Normalize()
    {
        Features ??= new();
        foreach (var key in Features.Keys.ToList())
        {
            var f = Features[key] ?? new FeatureSettings();
            f.Values ??= new();
            Features[key] = f;
        }
        if (Theme is not ("dark" or "light")) Theme = "dark";
        GlassOpacity = float.IsFinite(GlassOpacity) ? Math.Clamp(GlassOpacity, 0.6f, 1f) : 0.92f;
        GlassBlur    = float.IsFinite(GlassBlur) ? Math.Clamp(GlassBlur, 0f, 1f) : 0.5f;
        if (string.IsNullOrWhiteSpace(ToggleKey)) ToggleKey = DefaultToggleKey;
    }
}

public sealed class FeatureSettings
{
    /// <summary>Null = the feature's default.</summary>
    public bool? Enabled { get; set; }

    public Dictionary<string, JsonElement> Values { get; set; } = new();
}

// JSON config in the per-user config dir. Loaded once, saved with a short debounce from the
// render hook (ticked every frame, overlay open or not), flushed when the overlay closes and on
// process exit; always via temp file + rename.
public static class DespadaConfig
{
    private const float SaveDelaySeconds = 1f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static float _saveTimer = -1f;

    public static DespadaSettings Current { get; private set; } = new();

    public static string Directory { get; } = ResolveDirectory();
    public static string FilePath => Path.Combine(Directory, "config.json");

    public static event Action? Changed;

    public static void Load()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        try
        {
            if (!File.Exists(FilePath))
            {
                Current = new DespadaSettings();
                MarseyLogger.Info($"[Config] No config at {FilePath}, using defaults.");
                return;
            }

            var loaded = JsonSerializer.Deserialize<DespadaSettings>(File.ReadAllText(FilePath), JsonOptions);
            Current = loaded ?? new DespadaSettings();
            Current.Normalize();
            MarseyLogger.Info($"[Config] Loaded {FilePath}");
        }
        catch (JsonException ex)
        {
            // Keep the broken file for inspection instead of silently overwriting it.
            MarseyLogger.Warn($"[Config] {FilePath} is not valid JSON ({ex.Message}); moved to .bak, using defaults.");
            try { File.Move(FilePath, FilePath + ".bak", overwrite: true); } catch { /* best effort */ }
            Current = new DespadaSettings();
        }
        catch (Exception ex)
        {
            // IO trouble (permissions, sharing): the file may be fine, leave it alone.
            MarseyLogger.Warn($"[Config] Could not read {FilePath} ({ex.Message}); using defaults.");
            Current = new DespadaSettings();
        }
    }

    /// <summary>Apply an edit, notify listeners and schedule a save.</summary>
    public static void Update(Action<DespadaSettings> edit)
    {
        edit(Current);
        Current.Normalize();
        _saveTimer = SaveDelaySeconds;
        Changed?.Invoke();
    }

    /// <summary>Called every frame from the render hook with wall-clock delta.</summary>
    public static void Tick(float dt)
    {
        if (_saveTimer < 0f) return;
        _saveTimer -= dt;
        if (_saveTimer <= 0f)
        {
            _saveTimer = -1f;
            Save();
        }
    }

    /// <summary>Writes a pending change now instead of waiting for the debounce.</summary>
    public static void Flush()
    {
        if (_saveTimer < 0f) return;
        _saveTimer = -1f;
        Save();
    }

    private static void OnProcessExit(object? sender, EventArgs e) => Flush();

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"[Config] Save failed: {ex.Message}");
        }
    }

    private static string ResolveDirectory()
    {
        string root;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        else
            root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(root, "Despada");
    }
}
