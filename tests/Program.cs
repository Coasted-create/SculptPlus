using System.Numerics;
using SculptPlus;

// Tests for the arithmetic that needs nothing from the game. Run with: dotnet run --project tests

var failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok)
        failed++;
}

bool Near(Vector3 a, Vector3 b, float eps = 1e-3f)
    => (a - b).Length() < eps;

bool SameTurn(Quaternion a, Quaternion b)
    => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 0.9999f;

var random = new Random(7);
float R(float range) => (float)(random.NextDouble() * 2 - 1) * range;
Quaternion AnyTurn() => Quaternion.Normalize(new Quaternion(R(1), R(1), R(1), R(1)));

// turn numbers: there and back
var ok = true;
for (var i = 0; i < 500; ++i)
{
    var degrees = new Vector3(R(170), R(80), R(170));
    ok &= Near(SculptMath.ToEuler(SculptMath.ToQuaternion(degrees)), degrees, 0.05f);
}

Check("turn numbers survive the round trip", ok);
ok = true;
for (var i = 0; i < 500; ++i)
{
    var q = AnyTurn();
    ok &= SameTurn(SculptMath.ToQuaternion(SculptMath.ToEuler(q)), q);
}

Check("any rotation survives the round trip", ok);

// sizes given in the model's directions
Check("unturned bone: wide is its X", Near(SculptMath.LocalSize(Quaternion.Identity, new Vector3(0.2f, 0, 0)), new Vector3(MathF.Exp(0.2f), 1, 1)));
var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2); // the bone's X points up
Check("bone turned a quarter: wide is its Y", Near(SculptMath.LocalSize(quarter, new Vector3(0.2f, 0, 0)), new Vector3(1, MathF.Exp(0.2f), 1)));
ok = true;
for (var i = 0; i < 200; ++i)
{
    var size = SculptMath.LocalSize(AnyTurn(), new Vector3(0.15f));
    ok &= Near(size, new Vector3(MathF.Exp(0.15f)));
}

Check("even size is even for any bone", ok);
ok = true;
for (var i = 0; i < 200; ++i)
{
    var size = SculptMath.LocalSize(AnyTurn(), new Vector3(0.3f, 0, 0.1f));
    // the volume changes by the same amount whichever way the bone is turned
    ok &= MathF.Abs(MathF.Log(size.X * size.Y * size.Z) - 0.4f) < 1e-3f;
}

Check("the total size change does not depend on the bone's turn", ok);

// moves
ok = true;
for (var i = 0; i < 200; ++i)
{
    var turn  = AnyTurn();
    var model = new Vector3(R(0.01f), R(0.01f), R(0.01f));
    ok &= Near(Vector3.Transform(SculptMath.ToLocal(turn, model), turn), model, 1e-5f);
}

Check("a move comes out where it was meant in the model", ok);

// mirroring a turn
ok = true;
for (var i = 0; i < 200; ++i)
{
    // twin bones: the twin's rotation is the mirror image of the bone's
    var bone   = AnyTurn();
    var twin   = new Quaternion(bone.X, -bone.Y, -bone.Z, bone.W);
    var local  = AnyTurn();
    var there  = SculptMath.MirrorTurn(local, bone, twin);
    var back   = SculptMath.MirrorTurn(there, twin, bone);
    ok &= SameTurn(back, local);
    // seen in the model, the two turns are mirror images
    var a = bone * local * Quaternion.Inverse(bone);
    var b = twin * there * Quaternion.Inverse(twin);
    ok &= SameTurn(new Quaternion(a.X, -a.Y, -a.Z, a.W), b);
}

Check("a mirrored turn is the mirror image, and mirrors back", ok);
Check("no turn mirrors to no turn", SameTurn(SculptMath.MirrorTurn(Quaternion.Identity, AnyTurn(), AnyTurn()), Quaternion.Identity));

// shape text
var text = "{\"n\":\"Søft ✿\",\"h\":{\"j_f_ulip_01_l\":{\"Scaling\":{\"X\":1.1,\"Y\":1.1,\"Z\":1.1}}}}";
Check("shape text survives packing", SculptMath.Unpack(SculptMath.Pack(text)) == text);
Check("packed text is letters and digits", SculptMath.Pack(text).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='));
var threw = false;
try
{
    SculptMath.Unpack(SculptMath.Pack(new string('x', 5 * 1024 * 1024)));
}
catch (InvalidDataException)
{
    threw = true;
}

Check("an oversized shape text is refused", threw);
threw = false;
try
{
    SculptMath.Unpack("not a shape");
}
catch (Exception)
{
    threw = true;
}

Check("garbage is refused", threw);

// translations
Loc.SetLanguage("en");
Check("English is the text itself", Loc.T("Keep") == "Keep");
Check("placeholders are filled", Loc.F("Sculpt {0}", "Remi") == "Sculpt Remi");

Console.WriteLine(failed == 0 ? "all passed" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;
