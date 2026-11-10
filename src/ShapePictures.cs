using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace SculptPlus;

/// <summary> Small pictures of kept shapes: a square of the game screen (without any plugin windows or dots) around the face. </summary>
public sealed class ShapePictures
{
    private readonly Plugin _plugin;
    private Task? _running;
    private (SculptShape Shape, string File, string Old)? _taken;

    public ShapePictures(Plugin plugin)
        => _plugin = plugin;

    public string PathOf(string file)
        => Path.Combine(_plugin.DataDirectory, file);

    /// <summary> Draw thread: notes a picture that was just taken on its shape (the settings are only touched from here). </summary>
    public void Tick()
    {
        if (_taken is not { } done)
            return;
        _taken = null;
        done.Shape.Picture = done.File;
        _plugin.SaveConfig();
        if (done.Old.Length > 0 && done.Old != done.File)
        {
            try
            {
                File.Delete(PathOf(done.Old));
            }
            catch
            {
                // a leftover file harms nobody
            }
        }
    }

    /// <summary> Draw thread: the picture of a shape for this frame (null: none, or still loading). </summary>
    public IDalamudTextureWrap? Get(SculptShape shape)
    {
        if (shape.Picture.Length == 0)
            return null;
        var path = PathOf(shape.Picture);
        return File.Exists(path) ? _plugin.Textures.GetFromFile(path).GetWrapOrDefault() : null;
    }

    /// <summary>
    /// Takes the picture: the square of the screen between two corners (in pixels of the game window), saved as a JPEG and
    /// noted on the shape. One at a time; a second request while one runs is dropped.
    /// </summary>
    public void Take(SculptShape shape, uint viewportId, Vector2 screen, Vector2 min, Vector2 max)
    {
        if (_running is { IsCompleted: false } || screen.X < 8 || screen.Y < 8)
            return;
        var uv0 = Vector2.Clamp(min / screen, Vector2.Zero, Vector2.One);
        var uv1 = Vector2.Clamp(max / screen, Vector2.Zero, Vector2.One);
        if (uv1.X - uv0.X < 0.01f || uv1.Y - uv0.Y < 0.01f)
            return;

        var old  = shape.Picture;
        var file = $"shape-{Guid.NewGuid():N}.jpg";
        _running = Task.Run(async () =>
        {
            try
            {
                var args = new ImGuiViewportTextureArgs
                {
                    ViewportId = viewportId, TakeBeforeImGuiRender = true, KeepTransparency = false, AutoUpdate = false, Uv0 = uv0, Uv1 = uv1,
                };
                using var shot = await _plugin.Textures.CreateFromImGuiViewportAsync(args, "SculptPlus.Shot").ConfigureAwait(false);
                const int side = 256;
                using var small = shot.Height > side
                    ? await _plugin.Textures.CreateFromExistingTextureAsync(shot,
                        new TextureModificationArgs { NewWidth = Math.Max(1, side * shot.Width / Math.Max(1, shot.Height)), NewHeight = side, MakeOpaque = true }, true,
                        "SculptPlus.Small").ConfigureAwait(false)
                    : null;
                var codec = _plugin.Readback.GetSupportedImageEncoderInfos()
                    .First(i => i.Extensions.Any(e => e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))).ContainerGuid;
                await _plugin.Readback.SaveToFileAsync(small ?? shot, codec, PathOf(file), new Dictionary<string, object> { ["ImageQuality"] = 0.88f }, true)
                    .ConfigureAwait(false);
                _taken = (shape, file, old); // noted on the shape by Tick, on the draw thread
            }
            catch (Exception ex)
            {
                _plugin.Log.Warning($"Sculpt+: the picture of \"{shape.Name}\" could not be taken: {ex.Message}");
            }
        });
    }
}
