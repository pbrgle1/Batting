using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Batting
{
    /// <summary>How a batting turns a sharp corner of its base.</summary>
    public enum Corners
    {
        /// <summary>The loops fan out from the inner corner to fill the outer one.</summary>
        Fan = 0,

        /// <summary>Interrupted: the run before the corner carries on to the outer face and the next butts against it.</summary>
        Butt = 1,

        /// <summary>The two runs meet on the diagonal through the corner.</summary>
        Mitre = 2,
    }

    /// <summary>
    /// Builds the batting curve: half-circle loops that alternate between the
    /// two faces of the insulation, joined by straight lines. The pattern is
    /// laid out flat along the length of the base curve and then wrapped onto
    /// it.
    /// </summary>
    public static class BattingPattern
    {
        // Distance between neighbouring loops on the same face, as a fraction
        // of the batting width, for a zig-zag (shape 0) and for full loops
        // (shape 1); shapes in between blend the two.
        private const double ZigzagPitch = 0.57;
        private const double LoopPitch = 0.4;

        // Very sharp corners reach far past the base; this keeps them in check
        // (a multiple of the width).
        private const double MaxReach = 2;

        /// <summary>
        /// The batting along baseCurve, drawn in the plane whose normal is
        /// axis, made of lines and arcs: one curve, or one per run between
        /// corners for Butt and Mitre corners. Shape sets the size of the
        /// loops: 0 shrinks them to points, giving a zig-zag between the
        /// faces; at 0.5 the lines between them run straight across; at 1
        /// they are half circles a pitch wide and the lines cross, the classic
        /// batting coil.
        /// </summary>
        public static List<Curve> Create(Curve baseCurve, double width, Vector3d axis, BattingStyle style,
                                         bool clean = true)
        {
            var look = new Look(style.Shape, style.Spacing);
            var path = Path(baseCurve, width, axis, style.Alignment);
            var pattern = new List<Curve>();
            if (style.Corners == Corners.Butt || style.Corners == Corners.Mitre)
            {
                pattern = Joined(path, width, axis, look, style.Corners);
            }
            else
            {
                // Corners too sharp to fan get butt joints instead; where the
                // segments are too short, the corners are rounded off.
                var fanned = Fanned(path, width, axis, look, out bool tooSharp);
                if (fanned == null && tooSharp)
                    pattern = Joined(path, width, axis, look, Corners.Butt);
                else if ((fanned ?? Run(RoundCorners(path, width), width, axis, look)) is Curve batting)
                    pattern.Add(batting);
            }
            // previews skip fitting arcs, which takes a while on long curved walls
            return clean ? pattern.Select(curve => Clean(curve, width)).ToList() : pattern;
        }

        /// <summary>
        /// The two faces of the insulation layer along baseCurve, half the
        /// width either side of the batting's centreline.
        /// </summary>
        public static List<Curve> Faces(Curve baseCurve, double width, Vector3d axis, Alignment alignment)
        {
            var path = Path(baseCurve, width, axis, alignment);
            var faces = new List<Curve>();
            foreach (double side in new[] { 0.5, -0.5 })
            {
                if (Offset(path, side * width, axis) is Curve face)
                    faces.Add(face);
            }
            return faces;
        }

        /// <summary>
        /// The batting's centreline: the base itself, or the base moved half
        /// the width to its left or right. A closed base is taken
        /// anticlockwise, so left is inside and right outside.
        /// </summary>
        public static Curve Path(Curve baseCurve, double width, Vector3d axis, Alignment alignment)
        {
            if (alignment == Alignment.Centre)
                return baseCurve;
            var curve = baseCurve;
            if (curve.IsClosed && curve.ClosedCurveOrientation(axis) == CurveOrientation.Clockwise)
            {
                curve = curve.DuplicateCurve();
                curve.Reverse();
            }
            return Offset(curve, (alignment == Alignment.Left ? 0.5 : -0.5) * width, axis) ?? baseCurve;
        }

        /// <summary>
        /// The curve moved sideways in its plane, to its left for a positive
        /// distance, keeping corners sharp; null if Rhino can't offset it.
        /// </summary>
        private static Curve? Offset(Curve curve, double distance, Vector3d axis)
        {
            var plane = new Plane(curve.PointAtStart, axis);
            double tolerance = 1e-5 * Math.Abs(distance);

            Curve? Try(double d)
            {
                var pieces = curve.Offset(plane, d, tolerance, CurveOffsetCornerStyle.Sharp);
                if (pieces == null || pieces.Length == 0)
                    return null;
                var joined = pieces.Length == 1 ? pieces : Curve.JoinCurves(pieces, 10 * tolerance);
                return joined.OrderByDescending(piece => piece.GetLength()).FirstOrDefault();
            }

            // Which way Rhino offsets depends on the plane, so check the side
            // and go the other way if it is wrong.
            var offset = Try(distance);
            if (offset == null)
                return null;
            double t = curve.Domain.Mid;
            var across = Vector3d.CrossProduct(axis, curve.TangentAt(t));
            offset.ClosestPoint(curve.PointAt(t), out double u);
            bool left = (offset.PointAt(u) - curve.PointAt(t)) * across > 0;
            return left == (distance > 0) ? offset : Try(-distance);
        }

        /// <summary>
        /// The pattern as lines and arcs, so that DWG and other exports get
        /// lines and arcs rather than splines. Straight runs come out exact;
        /// runs bent along curves are fitted with arcs to a thousandth of the
        /// width.
        /// </summary>
        private static Curve Clean(Curve curve, double width)
        {
            if (curve is LineCurve)
                return curve;
            const CurveSimplifyOptions options = CurveSimplifyOptions.SplitAtFullyMultipleKnots |
                                                 CurveSimplifyOptions.RebuildLines | CurveSimplifyOptions.RebuildArcs;
            if (curve.Simplify(options, 1e-6 * width, RhinoMath.ToRadians(0.01)) is not PolyCurve simple)
                return curve;
            var clean = new PolyCurve();
            for (int i = 0; i < simple.SegmentCount; i++)
            {
                var segment = simple.SegmentCurve(i);
                if (segment is not (LineCurve or ArcCurve or PolylineCurve) &&
                    segment.ToArcsAndLines(1e-3 * width, RhinoMath.ToRadians(1), 0, 0) is PolyCurve arcs)
                    segment = arcs;
                clean.Append(segment);
            }
            clean.RemoveNesting();
            return clean;
        }

        /// <summary>The size and spacing of the loops.</summary>
        private readonly struct Look
        {
            public Look(double shape, double spacing)
            {
                Shape = Math.Max(0, Math.Min(1, shape));
                Spacing = Math.Max(BattingStyle.MinSpacing, Math.Min(BattingStyle.MaxSpacing, spacing));
            }

            public double Shape { get; }
            public double Spacing { get; }

            /// <summary>Distance between neighbouring loops on the same face.</summary>
            public double Pitch(double width) => width * (ZigzagPitch + Shape * (LoopPitch - ZigzagPitch)) * Spacing;

            /// <summary>
            /// How big the loops are, as a fraction of the size that fills a
            /// half pitch: wider spacing spreads the loops apart instead of
            /// growing them, closer spacing shrinks them so they don't overlap.
            /// </summary>
            public double Size => Math.Min(1, 1 / Spacing);
        }

        /// <summary>
        /// The batting filling the layer between two face curves, however
        /// its thickness varies: the loops follow the local thickness and are
        /// smaller where the layer is thinner. The faces are matched by
        /// length, run by run between their corners when both have the same
        /// number of corners; the batting then turns each corner along the
        /// line joining the two faces' corners. Corners and Alignment don't
        /// apply here.
        /// </summary>
        public static List<Curve> CreateBetween(Curve faceA, Curve faceB, Vector3d axis, BattingStyle style,
                                                bool clean = true)
        {
            var look = new Look(style.Shape, style.Spacing);
            faceB = Match(faceA, faceB, axis);
            var runsA = SplitAtCorners(faceA, out bool loop);
            var runsB = SplitAtCorners(faceB, out bool loopB);
            if (runsA.Count != runsB.Count || loop != loopB)
            {
                runsA = new List<Curve> { faceA };
                runsB = new List<Curve> { faceB };
                loop = false;
            }
            else if (loop)
            {
                // start both at the same corner
                int shift = Enumerable.Range(0, runsB.Count)
                    .OrderBy(i => runsB[i].PointAtStart.DistanceTo(runsA[0].PointAtStart)).First();
                runsB = runsB.Skip(shift).Concat(runsB.Take(shift)).ToList();
            }

            // a ring with no corners is one closed run
            bool ring = !loop && runsA.Count == 1 && faceA.IsClosed && faceB.IsClosed;
            var layers = runsA.Select((a, i) => new Layer(a, runsB[i], look, ring)).ToList();
            double step = 0.5 * look.Pitch(1);
            var pieces = new List<NurbsCurve>();
            int side = 1;  // each run starts on the face the one before ends on
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                int count;
                if (ring)
                {
                    count = Math.Max(2, 2 * (int)Math.Round(0.5 * layer.Loops));
                }
                else
                {
                    count = Math.Max(1, (int)Math.Round(layer.Loops));
                    // a loop of runs ends on the face the first run started on
                    if (loop && i == layers.Count - 1 && (count % 2 == 0 ? side : -side) != 1)
                        count += count < layer.Loops ? 1 : -1;
                    if (count < 1)
                        count += 2;
                }
                int last = count % 2 == 0 ? side : -side;
                var pattern = Flat(count * step, 1, ring, refine: true, look, side, ring ? null : last);
                for (int k = 0; k < pattern.Points.Count; k++)
                {
                    var cv = pattern.Points[k];
                    // x counts loops, y runs from face a (-0.5) to face b (0.5)
                    var point = layer.PointAt(cv.Location.X / step, cv.Location.Y + 0.5, count);
                    pattern.Points.SetPoint(k, point, cv.Weight);
                }
                pieces.Add(pattern);
                side = last;
            }
            var batting = pieces.Count == 1 ? pieces[0] : Concatenate(pieces);
            return new List<Curve> { clean ? Clean(batting, layers.Average(layer => layer.Thickness)) : batting };
        }

        /// <summary>
        /// The normal of the plane a curve lies in, or two curves, facing the
        /// same way as fallback; fallback itself for a single line, which
        /// lies in many planes.
        /// </summary>
        public static Vector3d Axis(Curve a, Curve? b, Vector3d fallback)
        {
            var plane = Plane.Unset;
            bool found;
            if (b == null)
            {
                found = !a.IsLinear() && a.TryGetPlane(out plane);
            }
            else
            {
                var points = new[] { a, b }.SelectMany(curve =>
                    new[] { curve.PointAtStart, curve.PointAt(curve.Domain.Mid), curve.PointAtEnd });
                found = Plane.FitPlaneToPoints(points, out plane) == PlaneFitResult.Success;
            }
            if (!found)
                return fallback;
            return plane.ZAxis * fallback < 0 ? -plane.ZAxis : plane.ZAxis;
        }

        /// <summary>The average distance between two face curves.</summary>
        public static double Thickness(Curve faceA, Curve faceB, Vector3d axis)
        {
            var b = Match(faceA, faceB, axis);
            var ta = faceA.DivideByCount(32, true);
            var tb = b.DivideByCount(32, true);
            if (ta == null || tb == null)
                return faceA.PointAtStart.DistanceTo(b.PointAtStart);
            int n = Math.Min(ta.Length, tb.Length);
            return Enumerable.Range(0, n).Average(i => faceA.PointAt(ta[i]).DistanceTo(b.PointAt(tb[i])));
        }

        /// <summary>
        /// faceB running the same way as faceA; closed, it also starts across
        /// from faceA's start.
        /// </summary>
        private static Curve Match(Curve faceA, Curve faceB, Vector3d axis)
        {
            var b = faceB.DuplicateCurve();
            if (faceA.IsClosed && b.IsClosed)
            {
                if (faceA.ClosedCurveOrientation(axis) != b.ClosedCurveOrientation(axis))
                    b.Reverse();
                b.ClosestPoint(faceA.PointAtStart, out double t);
                b.ChangeClosedCurveSeam(t);
            }
            else if (faceA.PointAtStart.DistanceTo(b.PointAtStart) + faceA.PointAtEnd.DistanceTo(b.PointAtEnd) >
                     faceA.PointAtStart.DistanceTo(b.PointAtEnd) + faceA.PointAtEnd.DistanceTo(b.PointAtStart))
            {
                b.Reverse();
            }
            return b;
        }

        /// <summary>
        /// The space between two faces along one run, matched by length and
        /// sampled densely, with how many loops fit up to each sample at the
        /// local thickness.
        /// </summary>
        private class Layer
        {
            private readonly Curve m_a;
            private readonly Curve m_b;
            private readonly double[] m_ta;
            private readonly double[] m_tb;
            private readonly double[] m_loops;
            private readonly bool m_closed;

            public Layer(Curve a, Curve b, Look look, bool closed)
            {
                m_a = a;
                m_b = b;
                m_closed = closed;
                // a first look to see how many loops there are, then enough
                // samples for each loop to be mapped smoothly
                (m_ta, m_tb, m_loops) = Sample(64, look);
                int samples = Math.Min(20000, Math.Max(64, (int)(16 * m_loops[^1])));
                if (samples > 64)
                    (m_ta, m_tb, m_loops) = Sample(samples, look);
                Thickness = Enumerable.Range(0, m_ta.Length).Average(i => m_a.PointAt(m_ta[i]).DistanceTo(m_b.PointAt(m_tb[i])));
            }

            /// <summary>Half pitches along the run, not rounded.</summary>
            public double Loops => m_loops[^1];

            public double Thickness { get; }

            private (double[], double[], double[]) Sample(int n, Look look)
            {
                double[] Params(Curve curve)
                {
                    var t = curve.DivideByCount(n, true)?.ToList();
                    if (t != null && curve.IsClosed)
                        t.Add(curve.Domain.T1);
                    if (t?.Count == n + 1)
                        return t.ToArray();
                    return Enumerable.Range(0, n + 1).Select(i => curve.Domain.ParameterAt((double)i / n)).ToArray();
                }

                var ta = Params(m_a);
                var tb = Params(m_b);
                var loops = new double[n + 1];
                Point3d? lastMid = null;
                double lastThickness = 0;
                for (int i = 0; i <= n; i++)
                {
                    var pa = m_a.PointAt(ta[i]);
                    var pb = m_b.PointAt(tb[i]);
                    var mid = 0.5 * (pa + pb);
                    double thickness = Math.Max(pa.DistanceTo(pb), 1e-9);
                    if (lastMid is Point3d previous)
                    {
                        // one loop on alternate faces every half pitch of the local thickness
                        double halfPitch = 0.5 * look.Pitch(0.5 * (thickness + lastThickness));
                        loops[i] = loops[i - 1] + mid.DistanceTo(previous) / halfPitch;
                    }
                    lastMid = mid;
                    lastThickness = thickness;
                }
                return (ta, tb, loops);
            }

            /// <summary>
            /// The point x loops along, with the run holding count loops, and
            /// v of the way across from face a (0) to face b (1).
            /// </summary>
            public Point3d PointAt(double x, double v, int count)
            {
                double target = x * Loops / count;
                if (m_closed)
                    target = ((target % Loops) + Loops) % Loops;
                int n = m_loops.Length - 1;
                int i = Array.BinarySearch(m_loops, target);
                if (i < 0)
                    i = ~i - 1;
                i = Math.Max(0, Math.Min(n - 1, i));
                double f = (target - m_loops[i]) / Math.Max(1e-12, m_loops[i + 1] - m_loops[i]);

                Point3d On(Curve curve, double[] t)
                {
                    if (f >= 0 && f <= 1)
                        return curve.PointAt(t[i] + f * (t[i + 1] - t[i]));
                    // control points of the end loops sit just past the ends
                    var p0 = curve.PointAt(t[i]);
                    return p0 + f * (curve.PointAt(t[i + 1]) - p0);
                }

                var pa = On(m_a, m_ta);
                var pb = On(m_b, m_tb);
                return pa + v * (pb - pa);
            }
        }

        /// <summary>
        /// The pattern along one smooth path, or null if it is too short.
        /// FirstSide and lastSide, +1 for the left face and -1 for the right,
        /// put the loops at the ends of an open path on those faces.
        /// </summary>
        private static NurbsCurve? Run(Curve path, double width, Vector3d axis, Look look,
                                       int? firstSide = null, int? lastSide = null)
        {
            double length = path.GetLength();
            if (length < 0.05 * width)
                return null;

            bool closed = path.IsClosed;
            var pattern = Flat(length, width, closed, refine: !path.IsLinear(), look, firstSide, lastSide);
            Bend(pattern, path, axis, length, closed, width);
            return pattern;
        }

        /// <summary>
        /// Fan corners: a pattern along each run between corners, ending on
        /// the inner corner, and in each corner a fan of petals from there to
        /// the outer faces (see Petals), all joined into one curve. Null if
        /// there are no corners, a run is too short to reach its inner corners
        /// or a corner is too sharp.
        /// </summary>
        private static Curve? Fanned(Curve baseCurve, double width, Vector3d axis, Look look, out bool tooSharp)
        {
            tooSharp = false;
            var runs = SplitAtCorners(baseCurve, out bool loop);
            int n = runs.Count;
            int corners = loop ? n : n - 1;
            if (corners == 0)
                return null;

            // the side each corner turns to, +1 left and -1 right, and where
            // the inner faces of its runs meet
            var inner = new int[corners];
            var pivots = new Point3d[corners];
            for (int i = 0; i < corners; i++)
            {
                var into = runs[i].TangentAtEnd;
                var outOf = runs[(i + 1) % n].TangentAtStart;
                double reach = 0.5 * width * Math.Tan(0.5 * Vector3d.VectorAngle(into, outOf));
                tooSharp = reach > MaxReach * width;
                if (tooSharp)
                    return null;
                inner[i] = Vector3d.CrossProduct(into, outOf) * axis >= 0 ? 1 : -1;
                var across = Vector3d.CrossProduct(axis, into);
                across.Unitize();
                pivots[i] = runs[i].PointAtEnd + inner[i] * 0.5 * width * across - reach * into;
            }

            // each run from inner corner to inner corner, with the loops at
            // its ends on the inner face, right on the corner
            var paths = new List<Curve>();
            var patterns = new List<NurbsCurve>();
            for (int i = 0; i < n; i++)
            {
                int before = (i + corners - 1) % corners;
                bool hasBefore = loop || i > 0;
                bool hasAfter = loop || i < n - 1;
                var run = runs[i];
                double t0 = run.Domain.T0, t1 = run.Domain.T1;
                if (hasBefore)
                    run.ClosestPoint(pivots[before], out t0);
                if (hasAfter)
                    run.ClosestPoint(pivots[i], out t1);
                var path = t1 > t0 ? run.Trim(t0, t1) : null;
                if (path == null || path.GetLength() < 0.1 * width)
                    return null;
                var pattern = Run(path, width, axis, look, hasBefore ? inner[before] : null, hasAfter ? inner[i] : null);
                if (pattern == null)
                    return null;
                paths.Add(path);
                patterns.Add(pattern);
            }

            var pieces = new List<NurbsCurve>();
            for (int i = 0; i < n; i++)
            {
                pieces.Add(patterns[i]);
                if (i < corners)
                {
                    int next = (i + 1) % n;
                    var petals = Petals(patterns[i].PointAtEnd, paths[i].TangentAtEnd, patterns[next].PointAtStart,
                                        paths[next].TangentAtStart, axis, width, look, inner[i]);
                    if (petals != null)
                        pieces.Add(petals);
                }
            }
            return Concatenate(pieces);
        }

        /// <summary>
        /// Joins quadratic NURBS curves, each starting where the one before
        /// ends, into one. Curve.JoinCurves will not do here: the petals of a
        /// corner start and end on the same point, so they count as closed.
        /// </summary>
        private static NurbsCurve Concatenate(List<NurbsCurve> curves)
        {
            var points = new List<ControlPoint>();
            var knots = new List<double>();
            foreach (var curve in curves)
            {
                // the first control point and double knot repeat the last ones
                bool first = points.Count == 0;
                double shift = first ? 0 : knots[^1] - curve.Knots[0];
                for (int i = first ? 0 : 1; i < curve.Points.Count; i++)
                    points.Add(curve.Points[i]);
                for (int i = first ? 0 : 2; i < curve.Knots.Count; i++)
                    knots.Add(curve.Knots[i] + shift);
            }

            var joined = new NurbsCurve(3, true, 3, points.Count);
            for (int i = 0; i < points.Count; i++)
                joined.Points[i] = points[i];
            for (int i = 0; i < knots.Count; i++)
                joined.Knots[i] = knots[i];
            return joined;
        }

        /// <summary>
        /// The loops filling a Fan corner, from the end of the run before it
        /// (from) to the start of the run after (to), both at the inner corner.
        /// Petals fan out from there at equal angles, each a loop against the
        /// outer faces joined to the corner by two lines: the loops of the
        /// inner face squeezed to a point, so to speak. The loops grow towards
        /// the outer corner, where there is more room. Laid out for a left
        /// turn and mirrored for a right one. Corners too slight for a petal
        /// just join the runs.
        /// </summary>
        private static NurbsCurve? Petals(Point3d from, Vector3d into, Point3d to, Vector3d outOf, Vector3d axis,
                                          double width, Look look, int inner)
        {
            // about as many petals as loops would fit round the outer corner
            double turn = Vector3d.VectorAngle(into, outOf);
            double outside = width * Math.Min(2 * Math.Tan(0.5 * turn), 2 * MaxReach);
            int count = (int)Math.Round(outside / look.Pitch(width));
            if (count == 0)
            {
                if (from.DistanceTo(to) <= 1e-6 * width)
                    return null;
                var line = new SpanBuilder(from);
                line.LineTo(to);
                return line.ToNurbsCurve(refine: false);
            }

            // The inner corner at the origin, the run before coming in along
            // X with its inner face on the X axis and its outer face at
            // y = -width. The outer faces of both runs bound the fan, as unit
            // normals and distances from the corner, and a third line cuts
            // very sharp corners short.
            var plane = new Plane(0.5 * (from + to), into, inner * Vector3d.CrossProduct(axis, into));
            var faces = new[]
            {
                (Normal: new Vector3d(0, -1, 0), Distance: width),
                (Normal: new Vector3d(Math.Sin(turn), -Math.Cos(turn), 0), Distance: width),
                (Normal: new Vector3d(Math.Sin(0.5 * turn), -Math.Cos(0.5 * turn), 0), Distance: MaxReach * width),
            };
            double spread = turn / count;
            double share = look.Shape * Math.Sin(0.5 * spread);
            double maxRadius = 0.5 * look.Shape * look.Size * look.Pitch(width);

            plane.RemapToPlaneSpace(from, out var start);
            plane.RemapToPlaneSpace(to, out var end);
            var petals = new SpanBuilder(start);
            for (int k = 0; k < count; k++)
            {
                double angle = (k + 0.5) * spread - 0.5 * Math.PI;
                var along = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
                var back = new Vector3d(Math.Sin(angle), -Math.Cos(angle), 0);  // towards the run before
                var bounds = faces.Where(face => along * face.Normal > 1e-9).ToList();
                // the biggest loop that keeps clear of its neighbours and the faces
                double radius = Math.Min(maxRadius, share * bounds.Min(face => face.Distance / (along * face.Normal + share)));
                var centre = Point3d.Origin + along * bounds.Min(face => (face.Distance - radius) / (along * face.Normal));
                if (radius < 1e-6 * width)
                {
                    petals.LineTo(centre);  // zig-zag: out to the face and back
                }
                else
                {
                    petals.LineTo(centre + radius * back);
                    petals.ArcTo(centre, radius, centre - radius * back, clockwise: false);
                }
                petals.LineTo(k + 1 < count ? Point3d.Origin : end);
            }
            var curve = petals.ToNurbsCurve(refine: false);
            curve.Transform(Transform.PlaneToPlane(Plane.WorldXY, plane));
            // exactly on the runs' ends, without rounding errors
            curve.Points.SetPoint(0, from);
            curve.Points.SetPoint(curve.Points.Count - 1, to);
            return curve;
        }

        /// <summary>
        /// Butt and Mitre corners: a separate pattern for each run between
        /// corners. For Butt, the run before a corner carries on to the outer
        /// face of the next and the next starts against its inner face; at
        /// corners sharper than a right angle those faces lie aslant, so the
        /// runs are cut along them. For Mitre, both are extended to the outer
        /// corner, with a loop on the outer face there so the two are cut
        /// alike, and cut along the diagonal from the inner corner to the
        /// outer one, which is drawn as the joint.
        /// </summary>
        private static List<Curve> Joined(Curve baseCurve, double width, Vector3d axis, Look look, Corners corners)
        {
            var runs = SplitAtCorners(baseCurve, out bool loop);
            int n = runs.Count;
            var startChange = new double[n];  // positive extends the run, negative shortens it
            var endChange = new double[n];
            var firstSide = new int?[n];
            var lastSide = new int?[n];
            var startCut = new Cut?[n];
            var endCut = new Cut?[n];
            var result = new List<Curve>();

            for (int i = 0; i < (loop ? n : n - 1); i++)
            {
                int next = (i + 1) % n;
                var corner = runs[i].PointAtEnd;
                var into = runs[i].TangentAtEnd;
                var outOf = runs[next].TangentAtStart;
                // which way the corner turns, and the inner face of each run
                // as a unit normal pointing out of it
                int inner = Vector3d.CrossProduct(into, outOf) * axis >= 0 ? 1 : -1;
                var innerNormal = inner * Vector3d.CrossProduct(axis, into);
                innerNormal.Unitize();
                var nextInnerNormal = inner * Vector3d.CrossProduct(axis, outOf);
                nextInnerNormal.Unitize();
                // where the faces of the two runs meet, and how far that is
                // from the corner of the base
                double halfTurn = 0.5 * Vector3d.VectorAngle(into, outOf);
                double full = 0.5 * width * Math.Tan(halfTurn);
                double reach = Math.Min(MaxReach * width, full);
                var innerCorner = corner + 0.5 * width * innerNormal - full * into;
                var outerCorner = corner - 0.5 * width * innerNormal + full * into;
                endChange[i] += reach;

                if (corners == Corners.Mitre)
                {
                    startChange[next] += reach;
                    lastSide[i] = -inner;
                    firstSide[next] = -inner;
                    var normal = into + outOf;  // across the diagonal, from this run to the next
                    endCut[i] = new Cut(innerCorner, outerCorner, normal);
                    startCut[next] = new Cut(innerCorner, outerCorner, -normal);
                    // the joint itself, from the inner corner out to where the runs end
                    result.Add(new LineCurve(innerCorner, innerCorner + (outerCorner - innerCorner) * (0.5 + 0.5 * reach / full)));
                }
                else if (into * outOf >= 0)
                {
                    startChange[next] -= reach;
                }
                else
                {
                    // The outer face of the next run crosses this one aslant,
                    // and this one's inner face crosses the next.
                    var innerFace = new Line(corner + 0.5 * width * innerNormal, into);
                    var nextOuterFace = new Line(corner - 0.5 * width * nextInnerNormal, outOf);
                    Intersection.LineLine(innerFace, nextOuterFace, out double a, out _);
                    var crossing = innerFace.PointAt(a);
                    endCut[i] = new Cut(outerCorner, crossing, -nextInnerNormal);
                    startChange[next] -= Math.Min((innerCorner - corner) * outOf, (crossing - corner) * outOf);
                    startCut[next] = new Cut(innerCorner, crossing, -innerNormal);
                }
            }

            for (int i = 0; i < n; i++)
            {
                var run = Change(Change(runs[i], CurveEnd.Start, startChange[i]), CurveEnd.End, endChange[i]);
                var pattern = run == null ? null : Run(run, width, axis, look, firstSide[i], lastSide[i]);
                if (pattern == null)
                    continue;
                IEnumerable<Curve> pieces = new[] { pattern };
                if (endCut[i] is Cut end)
                    pieces = pieces.SelectMany(piece => end.Apply(piece));
                if (startCut[i] is Cut start)
                    pieces = pieces.SelectMany(piece => start.Apply(piece));
                result.AddRange(pieces);
            }
            return result;
        }

        private static Curve? Change(Curve? curve, CurveEnd end, double amount)
        {
            if (curve == null || Math.Abs(amount) < RhinoMath.ZeroTolerance)
                return curve;
            if (amount > 0)
                return curve.Extend(end, amount, CurveExtensionStyle.Line);
            return curve.GetLength() > -amount ? curve.Trim(end, -amount) : null;
        }

        /// <summary>
        /// A line across a run near a corner, from one face to the other, that
        /// the run is cut back to.
        /// </summary>
        private readonly struct Cut
        {
            private readonly Point3d m_from;
            private readonly Point3d m_to;
            private readonly Vector3d m_beyond;  // towards the side that goes

            public Cut(Point3d from, Point3d to, Vector3d beyond)
            {
                m_from = from;
                m_to = to;
                m_beyond = beyond;
            }

            /// <summary>The parts of curve on this side of the line.</summary>
            public IEnumerable<Curve> Apply(Curve curve)
            {
                // a little longer, to catch loops that end right on the faces
                var centre = 0.5 * (m_from + m_to);
                var half = 0.55 * (m_to - m_from);
                var line = new LineCurve(centre - half, centre + half);
                double tolerance = 1e-6 * half.Length;
                var events = Intersection.CurveCurve(curve, line, tolerance, tolerance);
                var cuts = events?.Select(hit => hit.ParameterA).ToList() ?? new List<double>();
                var pieces = cuts.Count == 0 ? new[] { curve } : curve.Split(cuts) ?? new[] { curve };
                var beyond = m_beyond;
                double reach = 2 * half.Length;
                return pieces.Where(piece =>
                {
                    var mid = piece.PointAt(piece.Domain.Mid);
                    // the line only matters near the corner
                    return (mid - centre) * beyond < 0 || mid.DistanceTo(centre) > reach;
                });
            }
        }

        /// <summary>The smooth stretches of a curve between its corners.</summary>
        private static List<Curve> SplitAtCorners(Curve curve, out bool loop)
        {
            curve = curve.DuplicateCurve();
            var corners = Kinks(curve);
            bool cornerAtSeam = curve.IsClosed && curve.TangentAtStart.IsParallelTo(curve.TangentAtEnd) != 1;
            if (curve.IsClosed && corners.Count > 0 && !cornerAtSeam)
            {
                // move the seam onto a corner, so every run ends at one
                curve.ChangeClosedCurveSeam(corners[0]);
                corners = Kinks(curve);
                cornerAtSeam = true;
            }
            // a loop has a corner where the last run meets the first
            loop = curve.IsClosed && cornerAtSeam;
            if (corners.Count == 0)
                return new List<Curve> { curve };

            var parameters = new List<double> { curve.Domain.T0 };
            parameters.AddRange(corners);
            parameters.Add(curve.Domain.T1);
            var runs = new List<Curve>();
            for (int i = 0; i + 1 < parameters.Count; i++)
            {
                var run = curve.Trim(parameters[i], parameters[i + 1]);
                if (run != null && run.GetLength() > RhinoMath.ZeroTolerance)
                    runs.Add(run);
            }
            return runs;
        }

        private static List<double> Kinks(Curve curve)
        {
            var kinks = new List<double>();
            double t = curve.Domain.T0;
            while (curve.GetNextDiscontinuity(Continuity.G1_continuous, t, curve.Domain.T1, out t))
                kinks.Add(t);
            return kinks;
        }

        /// <summary>
        /// The parts of the batting outside the exclusions. Exclusions are
        /// closed curves in the batting's plane. One lying inside another
        /// makes a hole in it, as with nested hatch boundaries, so the batting
        /// is cut away only between the two.
        /// </summary>
        public static List<Curve> CutOut(Curve batting, IReadOnlyList<Curve> exclusions, Vector3d axis, double width)
        {
            if (exclusions.Count == 0)
                return new List<Curve> { batting };

            double tolerance = 1e-4 * width;
            var cuts = new List<double>();
            foreach (var exclusion in exclusions)
            {
                var events = Intersection.CurveCurve(batting, exclusion, tolerance, tolerance);
                if (events == null)
                    continue;
                foreach (var hit in events)
                {
                    cuts.Add(hit.ParameterA);
                    if (hit.IsOverlap)
                        cuts.Add(hit.OverlapA.T1);
                }
            }

            var plane = new Plane(batting.PointAtStart, axis);
            bool Inside(Point3d point, Curve exclusion) =>
                exclusion.Contains(point, plane, tolerance) == PointContainment.Inside;

            // How many other exclusions each one lies wholly inside: 0 cuts
            // away, 1 is a hole in that cut, 2 cuts again inside the hole, ...
            // Exclusions that cross each other simply add up.
            var depths = exclusions
                .Select(inner => exclusions.Count(outer =>
                    outer != inner && Inside(inner.PointAtStart, outer) &&
                    (Intersection.CurveCurve(inner, outer, tolerance, tolerance)?.Count ?? 0) == 0))
                .ToList();

            bool Excluded(Point3d point)
            {
                int innermost = -1;
                for (int i = 0; i < exclusions.Count; i++)
                {
                    if (Inside(point, exclusions[i]))
                        innermost = Math.Max(innermost, depths[i]);
                }
                return innermost >= 0 && innermost % 2 == 0;
            }

            var pieces = cuts.Count == 0 ? new[] { batting } : batting.Split(cuts) ?? new[] { batting };
            var kept = pieces.Where(piece => !Excluded(piece.PointAt(piece.Domain.Mid))).ToList();
            // a closed batting cut open leaves two pieces meeting at its seam
            return kept.Count > 1 ? Curve.JoinCurves(kept, tolerance).ToList() : kept;
        }

        /// <summary>
        /// Rounds sharp corners with a fillet whose inside just reaches the
        /// corner, so the batting fans round it instead of folding over itself.
        /// </summary>
        private static Curve RoundCorners(Curve curve, double width)
        {
            var domain = curve.Domain;
            bool corner = curve.GetNextDiscontinuity(Continuity.G1_continuous, domain.T0, domain.T1, out _) ||
                (curve.IsClosed && curve.TangentAtStart.IsParallelTo(curve.TangentAtEnd) != 1);
            if (!corner)
                return curve;
            // Segments too short for the fillet keep their corners.
            return Curve.CreateFilletCornersCurve(curve, 0.5 * width, 1e-5 * width, RhinoMath.ToRadians(1)) ?? curve;
        }

        /// <summary>
        /// Batting along the X axis from 0 to length, centred on y = 0. Loop m
        /// is centred at m half-pitches, on the first face (+1 left, -1 right,
        /// left unless given) for even m and the other face for odd m; each
        /// line runs from the end of one loop to the start of the next. The
        /// count is rounded so the pattern fits the length exactly: an open
        /// run starts and ends at the apex of a loop, on the face, so it is
        /// cut square across the ends of the base, and on lastSide if given;
        /// a closed one has an even count so the faces alternate all the way
        /// round.
        /// </summary>
        private static NurbsCurve Flat(double length, double width, bool closed, bool refine, Look look,
                                       int? firstSide = null, int? lastSide = null)
        {
            int count;
            if (closed)
            {
                count = Math.Max(2, 2 * (int)Math.Round(length / look.Pitch(width)));
            }
            else
            {
                double ideal = 2 * length / look.Pitch(width);
                count = Math.Max(1, (int)Math.Round(ideal));
                if (firstSide.HasValue && lastSide.HasValue)
                {
                    // the faces alternate, so both ends on the same face needs an even count
                    bool even = firstSide == lastSide;
                    if ((count % 2 == 0) != even)
                        count += count < ideal ? 1 : -1;
                    count = Math.Max(even ? 2 : 1, count);
                }
            }
            int first = firstSide ?? (lastSide is int last ? (count % 2 == 0 ? last : -last) : 1);
            double step = length / count;    // between neighbouring loops, a half pitch
            double r = look.Shape * look.Size * step;  // loop radius; at shape 1 a loop spans a pitch
            double inset = 0.5 * width - r;  // loop ends sit this far off the centreline

            double Side(int m) => m % 2 == 0 ? first : -first;
            Point3d Centre(int m) => new Point3d(m * step, Side(m) * inset, 0);
            Point3d Apex(int m) => new Point3d(m * step, Side(m) * 0.5 * width, 0);
            Point3d Start(int m) => new Point3d(m * step - r, Side(m) * inset, 0);
            Point3d End(int m) => new Point3d(m * step + r, Side(m) * inset, 0);
            // loops on the left face run clockwise over their top
            bool Clockwise(int m) => Side(m) > 0;

            if (r < 1e-6 * width)
            {
                // no loops left: a straight zig-zag from face to face
                var zigzag = new SpanBuilder(Apex(0));
                for (int m = 1; m <= count; m++)
                    zigzag.LineTo(Apex(m));
                return zigzag.ToNurbsCurve(refine);
            }

            SpanBuilder pattern;
            if (closed)
            {
                pattern = new SpanBuilder(Start(0));
                for (int m = 0; m < count; m++)
                {
                    pattern.ArcTo(Centre(m), r, End(m), Clockwise(m));
                    pattern.LineTo(Start(m + 1));  // the last one is the first, a length on
                }
            }
            else
            {
                pattern = new SpanBuilder(Apex(0));
                pattern.ArcTo(Centre(0), r, End(0), Clockwise(0));
                for (int m = 1; m < count; m++)
                {
                    pattern.LineTo(Start(m));
                    pattern.ArcTo(Centre(m), r, End(m), Clockwise(m));
                }
                pattern.LineTo(Start(count));
                pattern.ArcTo(Centre(count), r, Apex(count), Clockwise(count));
            }
            return pattern.ToNurbsCurve(refine);
        }

        /// <summary>
        /// Collects the pattern as quadratic rational spans and builds one NURBS
        /// curve from them directly, which is far quicker than joining hundreds
        /// of arcs and lines.
        /// </summary>
        private class SpanBuilder
        {
            // homogeneous control points, three per span with shared ends
            private readonly List<Point4d> m_points = new List<Point4d>();

            public SpanBuilder(Point3d start)
            {
                m_points.Add(new Point4d(start.X, start.Y, start.Z, 1));
            }

            private Point3d Last => new Point3d(m_points[^1].X, m_points[^1].Y, m_points[^1].Z);

            public void LineTo(Point3d end) => Span(0.5 * (Last + end), 1, end);

            public void ArcTo(Point3d centre, double radius, Point3d end, bool clockwise)
            {
                var start = Last;
                double a0 = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
                double sweep = Math.Atan2(end.Y - centre.Y, end.X - centre.X) - a0;
                while (clockwise && sweep >= 0)
                    sweep -= 2 * Math.PI;
                while (!clockwise && sweep <= 0)
                    sweep += 2 * Math.PI;

                // NURBS arc spans stay under 90 degrees
                int quarters = (int)Math.Ceiling(Math.Abs(sweep) / (0.5 * Math.PI) - 1e-9);
                double half = 0.5 * sweep / quarters;
                for (int q = 0; q < quarters; q++)
                {
                    double mid = a0 + (2 * q + 1) * half;
                    double t1 = a0 + (2 * q + 2) * half;
                    var middle = centre + radius / Math.Cos(half) * new Vector3d(Math.Cos(mid), Math.Sin(mid), 0);
                    Span(middle, Math.Cos(half), centre + radius * new Vector3d(Math.Cos(t1), Math.Sin(t1), 0));
                }
            }

            private void Span(Point3d middle, double weight, Point3d end)
            {
                m_points.Add(new Point4d(middle.X * weight, middle.Y * weight, middle.Z * weight, weight));
                m_points.Add(new Point4d(end.X, end.Y, end.Z, 1));
            }

            public NurbsCurve ToNurbsCurve(bool refine)
            {
                // Bending moves the control points either side of each arc/line
                // join by slightly different amounts, which leaves a small kink.
                // Two extra knots just inside each span end (Boehm insertion) put
                // control points close to the join and shrink the kink to
                // hundredths of a degree.
                const double u = 0.02;
                const double v = 0.98;
                double w = (v - u) / (1 - u);

                var points = new List<Point4d> { m_points[0] };
                var knots = new List<double> { 0, 0 };
                for (int s = 0; 2 * s + 2 < m_points.Count; s++)
                {
                    var p0 = m_points[2 * s];
                    var p1 = m_points[2 * s + 1];
                    var p2 = m_points[2 * s + 2];
                    if (refine)
                    {
                        var a1 = Lerp(p0, p1, u);
                        var a2 = Lerp(p1, p2, u);
                        points.Add(a1);
                        points.Add(Lerp(a1, a2, v));
                        points.Add(Lerp(a2, p2, w));
                        knots.Add(s + u);
                        knots.Add(s + v);
                    }
                    else
                    {
                        points.Add(p1);
                    }
                    points.Add(p2);
                    knots.Add(s + 1);  // a double knot between spans
                    knots.Add(s + 1);
                }

                var curve = new NurbsCurve(3, true, 3, points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    curve.Points.SetPoint(i, new Point3d(p.X / p.W, p.Y / p.W, p.Z / p.W), p.W);
                }
                for (int i = 0; i < knots.Count; i++)
                    curve.Knots[i] = knots[i];
                return curve;
            }

            // Plain homogeneous interpolation. Point4d's own + operator
            // rescales points with different weights, which is not wanted here.
            private static Point4d Lerp(Point4d a, Point4d b, double t) =>
                new Point4d(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z), a.W + t * (b.W - a.W));
        }

        /// <summary>
        /// Wraps the flat pattern onto path: x becomes distance along the path
        /// and y the offset across it. Moves the control points in place.
        /// </summary>
        private static void Bend(NurbsCurve pattern, Curve path, Vector3d axis,
                                 double length, bool closed, double width)
        {
            // Curve parameters at short, equal steps of arc length, with their
            // rate of change. Looking distances up here instead of measuring
            // each one keeps long curved walls fast enough to preview while
            // dragging grips.
            int steps = Math.Max(16, (int)Math.Ceiling(8 * length / width));
            double h = length / steps;
            var table = path.DivideByCount(steps, true)?.ToList();
            if (table != null && closed)
                table.Add(path.Domain.T1);
            if (table?.Count != steps + 1)
                table = null;
            var slopes = table?.Select(t => 1 / path.DerivativeAt(t, 1)[1].Length).ToList();

            for (int i = 0; i < pattern.Points.Count; i++)
            {
                var cv = pattern.Points[i];
                double x = cv.Location.X;
                double y = cv.Location.Y;
                double s = closed ? ((x % length) + length) % length : Math.Max(0, Math.Min(length, x));
                double t;
                if (table != null && slopes != null)
                {
                    // cubic Hermite, so the stretch along the curve has no steps
                    double f = s / h;
                    int k = Math.Min(steps - 1, (int)f);
                    double a = f - k, a2 = a * a, a3 = a2 * a;
                    t = (2 * a3 - 3 * a2 + 1) * table[k] + (a3 - 2 * a2 + a) * h * slopes[k] +
                        (3 * a2 - 2 * a3) * table[k + 1] + (a3 - a2) * h * slopes[k + 1];
                }
                else
                {
                    path.LengthParameter(s, out t);
                }
                var tangent = path.TangentAt(t);
                var across = Vector3d.CrossProduct(axis, tangent);
                across.Unitize();
                // Control points of the end loops can sit just past an open
                // path's ends, so carry on along the end tangent there.
                double overhang = closed ? 0 : x - s;
                pattern.Points.SetPoint(i, path.PointAt(t) + tangent * overhang + across * y, cv.Weight);
            }
        }
    }
}
