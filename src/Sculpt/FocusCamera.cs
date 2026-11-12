using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace SculptPlus;

/// <summary>
/// The focus camera: the game's own camera, helped along while you sculpt. It can zoom in further than the game lets
/// you (by narrowing the lens, so nothing is cut off or fades as it does when the camera itself comes close), turn to a
/// view of the face (front, three-quarter, side) and bring the part you work on to the middle of the screen. You keep
/// turning and zooming with the mouse as always; everything is put back when sculpting ends.
///
/// Nothing here knows which way the game counts its angles: every turn is steered by looking at what the screen shows
/// and correcting towards the goal, and a step that made things worse flips the direction.
/// </summary>
public sealed unsafe class FocusCamera : IDisposable
{
    private readonly IPluginLog _log;

    private bool _on;
    private float _minFoV, _maxFoV, _foV, _tilt;
    private float _zoom = 1f;

    private float? _turnTo;      // where the camera should stand around the head (radians), null: not turning
    private float _raiseTo;      // how high above level it should look from
    private DateTime _turnUntil;
    private bool _centre;        // bring the aim point to the middle of the screen (up and down)
    private DateTime _centreUntil;
    private DateTime _lostSince;

    private readonly Seek _seekH = new(), _seekV = new(), _seekTilt = new();

    public FocusCamera(IPluginLog log)
        => _log = log;

    /// <summary> True while the camera's limits are widened (between Begin and End). </summary>
    public bool On => _on;

    /// <summary> How much closer than the game's own closest view: 1 is as the game has it. </summary>
    public float Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, 1f, 8f);
    }

    /// <summary> For the test line. </summary>
    public string State { get; private set; } = string.Empty;

    private static GameCamera* Camera()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return null;
        var active = manager->GetActiveCamera();
        // only the normal camera (not a cutscene's, not the lobby's)
        return active != null && active == manager->Camera ? active : null;
    }

    /// <summary> Remembers how the camera is, so End can put it back. </summary>
    public void Begin()
    {
        if (_on)
            return;
        var cam = Camera();
        if (cam == null)
            return;
        _minFoV = cam->MinFoV;
        _maxFoV = cam->MaxFoV;
        _foV    = cam->FoV;
        _tilt   = cam->TiltOffset;
        _on     = true;
    }

    /// <summary> Puts the camera back as it was (zoom, lens limits, tilt). The way you turned it stays. </summary>
    public void End()
    {
        if (!_on)
            return;
        _on     = false;
        _zoom   = 1f;
        _turnTo = null;
        _centre = false;
        try
        {
            var cam = Camera();
            if (cam == null)
                return;
            cam->MinFoV     = _minFoV;
            cam->MaxFoV     = _maxFoV;
            cam->FoV        = Math.Clamp(_foV, _minFoV, _maxFoV);
            cam->TiltOffset = _tilt;
        }
        catch (Exception ex)
        {
            _log.Warning($"Sculpt+: could not put the camera back: {ex.Message}");
        }
    }

    /// <summary> Turn to stand at this angle around the head (as Atan2(x, z) of the way from the head to the camera). </summary>
    public void TurnTo(float around, float raise)
    {
        _turnTo    = around;
        _raiseTo   = raise;
        _turnUntil = DateTime.UtcNow.AddSeconds(3);
        _seekH.Restart();
        _seekV.Restart();
    }

    /// <summary> Bring the aim point to the middle of the screen (up and down) over the next moments. </summary>
    public void Centre()
    {
        _centre      = true;
        _centreUntil = DateTime.UtcNow.AddSeconds(3);
        _lostSince   = DateTime.MinValue;
        _seekTilt.Restart();
    }

    /// <summary> Back to the view height the game had (for the whole body). </summary>
    public void Uncentre()
    {
        _centre = false;
        var cam = Camera();
        if (_on && cam != null)
            cam->TiltOffset = _tilt;
    }

    /// <summary>
    /// Every frame while sculpting. <paramref name="away"/>: the way you look, into the screen (null: unknown this frame).
    /// <paramref name="aimY"/>: where the aim point is on screen, 0 top to 1 bottom (null: not on screen).
    /// </summary>
    public void Update(Vector3? away, float? aimY)
    {
        if (!_on)
            return;
        var cam = Camera();
        if (cam == null)
            return;

        // the lens: narrower is closer
        if (_zoom > 1.01f)
        {
            var want = Math.Clamp(_maxFoV / _zoom, 0.05f, _maxFoV);
            cam->MinFoV = MathF.Min(_minFoV, want);
            cam->FoV    = want;
        }
        else if (cam->MinFoV < _minFoV)
        {
            cam->MinFoV = _minFoV;
            cam->FoV    = Math.Clamp(cam->FoV, _minFoV, _maxFoV);
        }

        var now = DateTime.UtcNow;
        if (_turnTo is { } goal)
        {
            // (while the head is not on screen the turn waits; it gives up after a moment)
            if (now > _turnUntil)
                _turnTo = null;
            else if (away is { } into)
            {
                var toCamera = -into;
                var around   = MathF.Atan2(toCamera.X, toCamera.Z);
                var raise    = MathF.Asin(Math.Clamp(toCamera.Y, -1f, 1f));
                var errH     = Wrap(goal - around);
                var errV     = _raiseTo - raise;
                if (MathF.Abs(errH) < 0.02f && MathF.Abs(errV) < 0.04f)
                    _turnTo = null;
                else
                {
                    cam->DirH = Wrap(cam->DirH + _seekH.Step(errH, 0.2f, 0.09f));
                    var v = cam->DirV + _seekV.Step(errV, 0.2f, 0.05f);
                    if (cam->DirVMin < cam->DirVMax)
                        v = Math.Clamp(v, cam->DirVMin, cam->DirVMax);
                    cam->DirV = v;
                }

                State = $"turning: {errH:0.00} / {errV:0.00} rad to go";
            }

            if (_turnTo is null && _centre)
                _centreUntil = now.AddSeconds(3);
        }
        else if (_centre)
        {
            // only once the turn is done: both move the picture
            if (aimY is { } y)
            {
                var err = y - 0.5f;
                if (MathF.Abs(err) < 0.03f)
                    _centre = false;
                else if (now <= _centreUntil)
                    cam->TiltOffset = Math.Clamp(cam->TiltOffset + _seekTilt.Step(err, 0.25f, 0.02f), -0.6f, 0.6f);
                else if (MathF.Abs(err) > 0.38f && _zoom > 1.05f)
                {
                    // Tilting did not bring it in: come back out a little at a time until the part is on screen.
                    if ((now - _lostSince).TotalSeconds > 0.12)
                    {
                        _zoom      = MathF.Max(1f, _zoom * 0.93f);
                        _lostSince = now;
                    }
                }
                else
                    _centre = false;

                State = $"centring: {err:0.00} of the screen to go, tilt {cam->TiltOffset:0.00}, zoom {_zoom:0.0}×";
            }
            else
            {
                // behind the camera: nothing to steer by
                if (now > _centreUntil.AddSeconds(2))
                    _centre = false;
                State = "centring: the part is not in front of the camera";
            }
        }
        else
        {
            State = _zoom > 1.01f ? $"zoom {_zoom:0.0}×" : "idle";
        }
    }

    private static float Wrap(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;
        while (angle < -MathF.PI)
            angle += MathF.Tau;
        return angle;
    }

    /// <summary>
    /// Steers one number towards a goal without knowing which way it counts: steps for a few frames, then looks whether
    /// the distance to the goal shrank. If it grew, the direction is turned round (and the look after that is skipped,
    /// because what the screen shows runs a frame or two behind).
    /// </summary>
    private sealed class Seek
    {
        private float _sign = 1f;
        private float _from = float.NaN;
        private int _frames;
        private bool _flipped;

        public void Restart()
        {
            _from    = float.NaN;
            _frames  = 0;
            _flipped = false;
        }

        public float Step(float error, float gain, float max)
        {
            if (float.IsNaN(_from))
                _from = MathF.Abs(error);
            if (++_frames >= 6)
            {
                var size = MathF.Abs(error);
                if (!_flipped && size > _from + 0.004f)
                {
                    _sign    = -_sign;
                    _flipped = true;
                }
                else
                    _flipped = false;

                _from   = size;
                _frames = 0;
            }

            return _sign * Math.Clamp(error * gain, -max, max);
        }
    }

    public void Dispose()
        => End();
}
