using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SculptPlus;

/// <summary>
/// Sculpt+: shape your character by dragging dots on it. The window draws the dots and the panel; the sculptor keeps the
/// shape and shows it through a temporary Customize+ profile.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/sculpt";

    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly WindowSystem _windows = new("SculptPlus");
    private readonly List<IDisposable> _ipc = new();

    public IDalamudPluginInterface PluginInterface { get; }
    public IObjectTable Objects { get; }
    public IGameGui GameGui { get; }
    public ITextureProvider Textures { get; }
    public ITextureReadbackProvider Readback { get; }
    public IPluginLog Log { get; }
    public Configuration Config { get; }
    public Theme Theme { get; }
    public AnimationSpeed Speed { get; }
    public FocusCamera Camera { get; }
    public Sculptor Sculptor { get; }
    public ShapePictures Pictures { get; }
    public SculptWindow Window { get; }

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework, IObjectTable objects, IGameGui gameGui,
        ITextureProvider textures, ITextureReadbackProvider readback, IPluginLog log, ICondition condition, IGameInteropProvider interop)
    {
        PluginInterface = pi;
        _commands       = commands;
        _framework      = framework;
        _condition      = condition;
        Objects         = objects;
        GameGui         = gameGui;
        Textures        = textures;
        Readback        = readback;
        Log             = log;

        Config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        ApplyLanguage();
        Theme    = new Theme(pi.UiBuilder);
        Speed    = new AnimationSpeed(interop, log);
        Camera   = new FocusCamera(log);
        Sculptor = new Sculptor(this);
        Pictures = new ShapePictures(this);
        Window   = new SculptWindow(this);
        _windows.AddWindow(Window);

        _commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Sculpt+. /sculpt stop ends sculpting.",
        });
        pi.UiBuilder.Draw         += _windows.Draw;
        pi.UiBuilder.OpenMainUi   += Window.Toggle;
        pi.UiBuilder.OpenConfigUi += Window.Toggle;
        pi.LanguageChanged        += OnLanguageChanged;
        _framework.Update         += OnUpdate;
        ProvideIpc();
    }

    /// <summary> Where pictures and the history are kept. </summary>
    public string DataDirectory
    {
        get
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary> True while the game loads another area (nothing is read or sent then). </summary>
    public bool Zoning
        => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    public void SaveConfig()
        => PluginInterface.SavePluginConfig(Config);

    public void ApplyLanguage()
        => Loc.SetLanguage(Config.Language == "auto" ? PluginInterface.UiLanguage : Config.Language);

    private void OnLanguageChanged(string _)
        => ApplyLanguage();

    private void OnUpdate(IFramework _)
    {
        try
        {
            Sculptor.Update();

            // sculpting ended (stopped, zoned, logged out): the camera goes back to how the game had it
            if (!Sculptor.Active && Camera.On)
                Camera.End();
        }
        catch (Exception ex)
        {
            Log.Warning($"Sculpt+: update failed: {ex}");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "stop":
                Window.IsOpen = false;
                break;
            default:
                Window.Toggle();
                break;
        }
    }

    /// <summary>
    /// For other plugins (a wardrobe plugin that puts a body shape on with an outfit): the names of the kept shapes, the
    /// one that is worn, and putting one on or taking it off.
    /// </summary>
    private void ProvideIpc()
    {
        var pi = PluginInterface;

        var version = pi.GetIpcProvider<int>("SculptPlus.ApiVersion");
        version.RegisterFunc(() => 1);
        _ipc.Add(new Unregister(version.UnregisterFunc));

        var shapes = pi.GetIpcProvider<List<string>>("SculptPlus.GetShapes");
        shapes.RegisterFunc(() => Config.Shapes.Select(s => s.Name).ToList());
        _ipc.Add(new Unregister(shapes.UnregisterFunc));

        var worn = pi.GetIpcProvider<string>("SculptPlus.GetWornShape");
        worn.RegisterFunc(() => Config.Worn);
        _ipc.Add(new Unregister(worn.UnregisterFunc));

        var wear = pi.GetIpcProvider<string, bool>("SculptPlus.WearShape");
        wear.RegisterFunc(name => Sculptor.WearByName(name));
        _ipc.Add(new Unregister(wear.UnregisterFunc));

        var off = pi.GetIpcProvider<bool>("SculptPlus.TakeShapeOff");
        off.RegisterFunc(() =>
        {
            if (Config.Worn.Length == 0)
                return false;
            Sculptor.TakeShapeOff();
            return true;
        });
        _ipc.Add(new Unregister(off.UnregisterFunc));
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        public void Dispose()
            => action();
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi   -= Window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= Window.Toggle;
        PluginInterface.LanguageChanged        -= OnLanguageChanged;
        _commands.RemoveHandler(Command);
        foreach (var d in _ipc)
            d.Dispose();
        _windows.RemoveAllWindows();
        Sculptor.Dispose();
        Camera.Dispose();
        Speed.Dispose();
        Theme.Dispose();
    }
}
