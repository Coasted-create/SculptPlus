using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace SculptPlus;

/// <summary> The arithmetic of sculpting that needs nothing from the game, so it can be tested on its own. </summary>
public static class SculptMath
{
    /// <summary> Customize+'s reading of its three turn numbers (degrees): yaw from X, pitch from Y, roll from Z. </summary>
    public static Quaternion ToQuaternion(Vector3 degrees)
        => Quaternion.CreateFromYawPitchRoll(degrees.X * MathF.PI / 180, degrees.Y * MathF.PI / 180, degrees.Z * MathF.PI / 180);

    /// <summary> The three turn numbers that give this rotation (the reverse of <see cref="ToQuaternion"/>). </summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var pitch = MathF.Asin(Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1f, 1f));
        var yaw   = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        var roll  = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        return new Vector3(yaw, pitch, roll) * (180 / MathF.PI);
    }

    /// <summary>
    /// A size change given in the model's directions (wide, high, deep; as powers, 0.2 is about a fifth bigger), as factors
    /// for a bone's own three axes, whichever way the bone is turned.
    /// </summary>
    public static Vector3 LocalSize(Quaternion boneRotation, Vector3 wideHighDeep)
    {
        var ax = Vector3.Transform(Vector3.UnitX, boneRotation);
        var ay = Vector3.Transform(Vector3.UnitY, boneRotation);
        var az = Vector3.Transform(Vector3.UnitZ, boneRotation);
        return new Vector3(MathF.Exp(Vector3.Dot(ax * ax, wideHighDeep)), MathF.Exp(Vector3.Dot(ay * ay, wideHighDeep)), MathF.Exp(Vector3.Dot(az * az, wideHighDeep)));
    }

    /// <summary> A move in the model's directions, in a bone's own directions (Customize+ adds moves turned by the bone). </summary>
    /// <summary>
    /// Thicker (or thinner) around a limb: a size change across the direction the limb runs in, none along it, as factors
    /// for the bone's own axes. <paramref name="stand"/> is how the bone is turned, <paramref name="along"/> the limb's
    /// direction in the same space, <paramref name="power"/> the change as a power of e.
    /// </summary>
    public static Vector3 AroundSize(Quaternion stand, Vector3 along, float power)
    {
        if (along.LengthSquared() < 1e-10f)
            return new Vector3(MathF.Exp(power));
        along = Vector3.Normalize(along);
        float Axis(Vector3 own)
        {
            var d = Vector3.Dot(Vector3.Transform(own, stand), along);
            return MathF.Exp(power * (1f - d * d));
        }

        return new Vector3(Axis(Vector3.UnitX), Axis(Vector3.UnitY), Axis(Vector3.UnitZ));
    }

    /// <summary>
    /// The move for a bone's twin on the other side, in the twin's own directions. <paramref name="seen"/> is the move as
    /// it shows on the posed character. It is taken into the bone's own directions (by how the bone stands right now),
    /// from there to the resting body (by how the bone rests), flipped left to right, and into the twin's own directions
    /// (by how the twin rests). So the mirror goes through the middle of the body or face itself, however it is posed.
    /// </summary>
    public static Vector3 TwinMove(Vector3 seen, Quaternion boneNow, Quaternion boneRest, Quaternion twinRest)
    {
        var body = Vector3.Transform(Vector3.Transform(seen, Quaternion.Inverse(boneNow)), boneRest);
        return Vector3.Transform(body with { X = -body.X }, Quaternion.Inverse(twinRest));
    }

    public static Vector3 ToLocal(Quaternion boneRotation, Vector3 model)
        => Vector3.Transform(model, Quaternion.Inverse(boneRotation));

    /// <summary>
    /// A turn made on one bone (as a rotation in its own directions), for its twin on the other side of the body: taken into
    /// the model's directions, mirrored left to right, and brought into the twin's own directions.
    /// </summary>
    public static Quaternion MirrorTurn(Quaternion localTurn, Quaternion boneRotation, Quaternion twinRotation)
    {
        var model = boneRotation * localTurn * Quaternion.Inverse(boneRotation);
        var flip  = new Quaternion(model.X, -model.Y, -model.Z, model.W); // a mirror across X keeps X and W and flips Y and Z
        return Quaternion.Inverse(twinRotation) * flip * twinRotation;
    }

    /// <summary> Text squeezed into letters and digits (gzip, then base64). </summary>
    public static string Pack(string text)
    {
        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionLevel.SmallestSize))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            zip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(compressed.ToArray());
    }

    /// <summary> The reverse of <see cref="Pack"/>; refuses anything that unpacks to more than four megabytes. </summary>
    public static string Unpack(string code)
    {
        using var zip    = new GZipStream(new MemoryStream(Convert.FromBase64String(code)), CompressionMode.Decompress);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = zip.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, read);
            if (result.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("too large");
        }

        return Encoding.UTF8.GetString(result.ToArray());
    }
}
