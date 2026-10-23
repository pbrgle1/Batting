# Batting

A Rhino 8 plug-in that draws the batting (insulation) symbol, like AutoCAD's
BATTING linetype, and keeps it in step with the curve it follows: straight,
curved or closed walls, with fanned, butt or mitred corners.

![Battings along an L-shaped wall, an arc and a circle](docs/overview.png)

## Install

In Rhino 8 open the **Package Manager** (`_PackageManager`), search for
**Batting** and click **Install**, then restart Rhino.

Requires Rhino 8 for Windows.

### Versions

| Version | |
|---|---|
| 1.1.0 | Corner types: Fan, Butt and Mitre. `BattingExtractBase` gets the base curve back. |
| 1.0.0 | First release; corners are rounded off. |

Every published version stays on the Package Manager, so an older one can
still be installed. The [Releases](https://github.com/pbrgle1/Batting/releases)
page has each version's notes and files, and each version's code is tagged
(`v1.0.0`, `v1.1.0`, ...). Battings drawn with 1.1.0 still open in 1.0.0,
which rounds their corners off again when they are next edited.

## Use

### Draw a batting: `Batting`

Click the points of a polyline along the **middle** of the insulation layer,
then press Enter. While you draw, you see the batting it will make.

| Option | |
|---|---|
| `Width` | The thickness of the insulation. Type a value or pick two points to measure it. |
| `Shape` | From 0, a zig-zag, to 1, loops (the default). See [Shape](#shape). |
| `Corners` | How sharp corners are turned: `Fan` (the default), `Butt` or `Mitre`. See [Corners](#corners). |
| `Close` | Close the polyline. |
| `Undo` | Remove the last point. |

### Turn curves into batting: `CurveToBatting`

Select curves running along the middle of the insulation (lines, arcs,
circles, polylines, splines, polycurves) and give the width. With
`DeleteInput=Yes` each curve becomes the batting and keeps its layer, colour
and linetype; with `No` the batting is added beside it.

The last width, shape and corners you used are remembered for next time.

### Edit a batting

Click a batting and its base curve appears with its points as grips. Drag a
grip and the batting follows. Click elsewhere, or press Esc, to hide them
again. F10 (PointsOn) shows the same base grips.

![A selected batting showing its base curve and a grip](docs/editing.png)

A batting is an ordinary curve, so layer, colour, linetype and print width
work as they do on any curve. Move, Rotate, Mirror, Scale, Copy and Undo all
carry it along. Opened in a Rhino without the plug-in, it is simply a curve.

### The Properties panel

With a batting selected, the **Properties** panel has a Batting page (the
loops icon):

![The Batting page in the Properties panel](docs/properties.png)

- **Width**: type a value and press Enter, or click **Pick** to measure it
  between two points.
- **Shape**: drag the slider from 0 (zig-zag) to 1 (loops).
- **Corners**: Fan, Butt or Mitre.
- **Exclusions**: how many there are. **Add** asks for closed curves to cut
  out; **Clear** removes them all.

With several battings selected, a change applies to all of them as one undo
step.

### Shape

Shape sets the size of the loops on the faces. At 0 they shrink to points,
leaving a zig-zag; at 0.5 the lines between them run straight across; at 1
they are half circles and the lines cross.

![Battings at shape 0, 0.25, 0.5, 0.75 and 1](docs/shapes.png)

### Exclusions: `BattingExclude`

Cut a batting away inside closed curves, for example around a steel profile
passing through the insulation. Select the battings, then the closed curves
(the **Add** button on the Properties page does the same). `Clear` removes
all exclusions.

An exclusion lying inside another one makes a hole in it, as with nested
hatch boundaries. Pick a tube's outer and inner outline and the batting is
cut away only in the tube's wall.

![One exclusion, and two nested exclusions cutting only the ring between them](docs/exclusions.png)

The exclusions are copies of the curves you picked and move with the
batting; the curves themselves are left alone. A batting cut into several
pieces is a group: clicking any piece selects all of it.

### Curves

The pattern bends along curves, and an open batting starts and ends halfway
round a loop, square with the ends of its base.

![A batting following a curved wall](docs/ring.png)

### Corners

Where the base has a sharp corner, the batting turns it in one of three ways:

- **Fan** (the default): the loops fan out from the inside of the corner and
  grow to fill the outside of it, all in one curve.
- **Butt**: the batting is interrupted, like two boards butted together. The
  first run carries on to the outer face of the second, and the second starts
  against it. At corners sharper than a right angle, each run is cut along
  the face it meets.
- **Mitre**: both runs carry on to the outer corner and are cut along the
  diagonal from the inside corner to the outside one, which is drawn as the
  joint.

![Fan, Butt and Mitre corners at a right angle and at a sharp corner](docs/corners.png)

Butt and Mitre battings are made of several curves, grouped like a batting
cut by exclusions. A corner too sharp to fan, with walls meeting at under
about 30°, gets a butt joint instead; where a segment is too short for the
fan, the corner is rounded off.

### Change the width later: `BattingWidth`

Select battings and give a new width, typed or picked. The Properties page
does the same.

### Get the base curve back: `BattingExtractBase`

Select battings to get a copy of the curve each one follows, as an ordinary
curve on the current layer. The copies are left selected, ready to use, and
the battings stay as they are.

![The base curve of a batting extracted and selected](docs/extract.png)

## Good to know

- Base points can be moved but not added or removed. To do that, take the
  base out with `BattingExtractBase`, edit it, and turn it into a batting
  again with `CurveToBatting`.
- Scale changes the width with it. Scaling unevenly distorts the pattern until
  the batting is next edited.
- To change an exclusion's shape, Clear the exclusions and add them again.

## Build from source

```bash
dotnet build Batting.csproj -c Release
```

The plug-in is `bin/Release/net7.0-windows/Batting.rhp`; drag it into Rhino
to load it. RhinoCommon comes from NuGet, built against Rhino 8.0 so it loads
in every Rhino 8.

To package it for the Package Manager, copy the `.rhp` into `dist/` and run
`"C:\Program Files\Rhino 8\System\Yak.exe" build --platform win` there.

| File | |
|---|---|
| `BattingPattern.cs` | Builds the pattern, bends it along the base, turns the corners and cuts out exclusions. |
| `BattingData.cs` | The data stored on each batting: base curve, width, shape, corners, drawing plane, exclusions. |
| `BattingSet.cs` | Finds the pieces of a cut batting and rebuilds them together. |
| `BattingGrips.cs` | The grips on the base curve. |
| `BattingDisplay.cs` | Draws the base and exclusions of selected battings. |
| `BattingPlugIn.cs` | Turns the grips on and off as battings are selected. |
| `BattingCommands.cs` | The commands. |
| `BattingPropertiesPage.cs` | The Batting page in the Properties panel. |

## License

MIT
