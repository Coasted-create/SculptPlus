# Sculpt+

Shape your FINAL FANTASY XIV character by taking hold of it: on the character itself, or on a clay model of your head.
Sculpt+ is a Dalamud plugin that works on top of
[Customize+](https://github.com/Aether-Tools/CustomizePlus): it shows your changes live through a temporary Customize+
profile and never changes your Customize+ data.

> **Status: early build (0.2.1).** It has had little testing in game. Expect rough edges, and please report them.

## What it does

- **Take hold of a part.** The part under the mouse lights up with its name (or switch to a dot on every part). Drag
  to move it, scroll to resize, right-drag to turn, Shift+drag for nearer/further, right-click to put it back. Where
  parts lie on top of each other, Ctrl + wheel goes through them. Left and right sides move together unless you switch
  that off. The part you hold gets arrows and rings for moving, sizing and turning along one direction only.
- **A map of the face and body.** Click an area to work on it; its own sliders appear (eye shape, lids, brows, nose,
  lips, cheekbones, chin…).
- **Focus camera.** Front, three-quarter and side views, a zoom far closer than the game allows, and the picked area in
  the middle of the screen. You keep turning and zooming with the mouse as always.
- **Workbench.** A window with your own head as a plain clay model: turn it freely, come as close as you like, and take
  hold of the skin itself. The area a part moves lights up, and pulling the skin takes the neighbouring parts along.
  Your character in the game changes along and stays the place to judge the result.
- **Sliders in plain words.** Fuller lips, bigger eyes, wider jaw, wider hips, thicker thighs, taller, and more.
  One-click starting points (softer, sharper, doll-like, curvier, athletic…) set several sliders at once.
- **Dice and Evolve.** Roll chosen parts at random with a strength you set. Evolve shows four variations, you take the
  one you like best, and the next four vary around it.
- **Tools.** Soft brush (neighbouring parts follow a drag), blend between two shapes, make one side like the other, a
  touch of asymmetry, lock parts against the dice, "keep it natural" limits, undo/redo and an automatic history.
- **Seeing what you do.** Freeze or slow the character while you sculpt, hold a button to compare with before or with a
  kept shape (A/B), play an expression to check the face in motion, filter the dots by part, hide dots on the far side
  of the head.
- **Keeping shapes.** Keep a shape with a picture, wear it (it is put back after area changes and logins), share it as a
  line of text, or copy it as a Customize+ template.
- **Your Customize+ templates.** Switch the templates of your active profile on and off from the same window.

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) (through XIVLauncher), API level 15
- [Customize+](https://github.com/Aether-Tools/CustomizePlus), installed and enabled

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/Coasted-create/SculptPlus/main/repo.json`, tick it, and save.
3. Open `/xlplugins`, search for **Sculpt+**, and install it.

## Use

`/sculpt` opens the window, and **Sculpt me** starts. `/sculpt stop` ends sculpting.

| On a part (lit, or its dot) | |
|---|---|
| Drag | Move the part (in the plane you look at) |
| Ctrl+drag | The same, ten times finer |
| Shift+drag | Move it nearer or further |
| Ctrl + wheel | The next part underneath |
| Right-drag | Turn it |
| Mouse wheel | Make it bigger or smaller |
| Right-click | Put it back |

| On the part you hold | |
|---|---|
| Drag an arrow | Move in that direction only (red: left/right, green: up/down, blue: front/back) |
| Wheel on an arrow | Bigger or smaller in that direction |
| Drag round a ring | Turn around that direction |

| Anywhere | |
|---|---|
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Hold Alt | Hide the glow and dots (the mouse turns the camera over the face again) |
| Alt + arrow keys | Move the part you hold a tiny step |
| Alt+F | Freeze, or move again |
| Hold Ctrl+Alt | Show the character without the sculpt |
| Hold Alt+B | Show the kept shape chosen for A/B instead of the sculpt |

## Good to know

- **Who sees it.** A sculpt, and a shape you wear, live in a *temporary* Customize+ profile. As far as the Customize+
  source shows, temporary profiles are not handed to other plugins, so people who sync with you most likely do not see
  them. To make a shape part of what others see, use **Copy for Customize+** and import it there as a template.
- **Saving into Customize+.** Customize+ does not let other plugins write profiles or templates, which is why keeping a
  shape permanently in Customize+ goes through the clipboard.
- **Expressions.** The expression buttons run your normal emotes (`/smile motion` and so on), so people near you see
  them.
- **Freezing** only stops the animation on your screen.

## For other plugins

Sculpt+ offers a small IPC so that, for example, a wardrobe plugin can put a body shape on together with an outfit:

| Name | Signature | |
|---|---|---|
| `SculptPlus.ApiVersion` | `() → int` | `1` |
| `SculptPlus.GetShapes` | `() → List<string>` | Names of the kept shapes |
| `SculptPlus.GetWornShape` | `() → string` | The shape worn now, or empty |
| `SculptPlus.WearShape` | `(string name) → bool` | Puts a kept shape on the local player |
| `SculptPlus.TakeShapeOff` | `() → bool` | Takes the worn shape off |

## Build

You need the .NET 10 SDK and Dalamud's files (installed with XIVLauncher under
`%AppData%\XIVLauncher\addon\Hooks\dev`, or set `DALAMUD_HOME` to a folder that holds them).

```
dotnet build SculptPlus.csproj -c Release
dotnet run --project tests -c Release
```

To publish a version: set it in `SculptPlus.json` and `SculptPlus.csproj`, add its section to `CHANGELOG.md`, push, then
push a tag such as `v0.1.2`. The Release workflow builds the zip, creates the GitHub release and points `repo.json` at it.

The tests cover the arithmetic that needs nothing from the game (rotations, sizes, mirroring, the shape text).
Translations live in `Lang/` (English is the text in the code; `de.json` is German).

## Credits and licence

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). The bone names, their grouping and their left/right twins come
from Customize+ (Apache-2.0). Sculpt+ is not affiliated with Customize+, Dalamud or SQUARE ENIX.
