using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace SculptPlus;

/// <summary>
/// Holds one character's animations at a chosen speed (0: frozen), only on your screen. The game works the speed out anew
/// for every character all the time, so writing the number once does not last: this listens in where the game sets it and
/// puts the chosen speed in right after, for that one character only. Without a speed to hold, nothing is listened to.
/// </summary>
public sealed unsafe class AnimationSpeed : IDisposable
{
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CalculateSpeedDelegate(TimelineContainer* container);

    private readonly IPluginLog _log;
    private readonly Hook<CalculateSpeedDelegate>? _hook;
    private nint _character;
    private float _speed = 1f;

    /// <summary> False when the game's function was not found: then the speed can only be written each frame, which may not hold. </summary>
    public bool Listening => _hook is { IsDisposed: false };

    public AnimationSpeed(IGameInteropProvider interop, IPluginLog log)
    {
        _log = log;
        try
        {
            var address = (nint)TimelineContainer.MemberFunctionPointers.CalculateAndApplyOverallSpeed;
            if (address != nint.Zero)
                _hook = interop.HookFromAddress<CalculateSpeedDelegate>(address, Detour);
        }
        catch (Exception ex)
        {
            _log.Warning($"Sculpt+: could not listen for the animation speed, freezing may not hold: {ex.Message}");
        }
    }

    /// <summary> Framework thread: holds this character at this speed from now on. </summary>
    public void Hold(nint character, float speed)
    {
        _character = character;
        _speed     = Math.Clamp(speed, 0f, 1f);
        if (_hook is { IsEnabled: false })
            _hook.Enable();
        Write(character, _speed);
    }

    /// <summary> The character being held (zero: none). </summary>
    public nint Held => _character;

    /// <summary>
    /// Framework thread: lets the character go; the game's own speed applies again. Say whether the character is still
    /// there: one that has left is not written to any more.
    /// </summary>
    public void Release(bool stillThere)
    {
        if (_character == nint.Zero)
            return;
        var character = _character;
        _character = nint.Zero;
        if (_hook is { IsEnabled: true })
            _hook.Disable();
        if (stillThere)
            Write(character, 1f);
    }

    /// <summary> The speed the character has right now (for the test line). </summary>
    public static float Read(nint character)
        => character == nint.Zero ? 1f : ((Character*)character)->Timeline.OverallSpeed;

    private static void Write(nint character, float speed)
    {
        if (character != nint.Zero)
            ((Character*)character)->Timeline.OverallSpeed = speed;
    }

    private bool Detour(TimelineContainer* container)
    {
        var result = _hook!.Original(container);
        var held   = _character;
        if (held != nint.Zero && container != null && (nint)container->OwnerObject == held)
            container->OverallSpeed = _speed;
        return result;
    }

    public void Dispose()
    {
        try
        {
            _hook?.Disable();
        }
        catch
        {
            // going away anyway
        }

        _hook?.Dispose();
    }
}
