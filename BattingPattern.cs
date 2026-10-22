using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Batting
{
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

        /// <summary>
        /// The batting along baseCurve, drawn in the plane whose normal is
        /// axis, or null if the curve is too short to fit a loop on each face.
        /// Shape sets the size of the loops: 0 shrinks them to points, giving
        /// a zig-zag between the faces; at 0.5 the lines between them run
        /// straight across; at 1 they are half circles a pitch wide and the
        /// lines cross, the classic batting coil.
        /// </summary>
        public static NurbsCurve? Create(Curve baseCurve, double width, Vector3d axis, double shape = 1)
        {
            var path = RoundCorners(baseCurve, width);
            double length = path.GetLength();
            if (length <= width)
                return null;

            bool closed = path.IsClosed;
            var pattern = Flat(length, width, closed, refine: !path.IsLinear(), Math.Max(0, Math.Min(1, shape)));
            Bend(pattern, path, axis, length, closed, width);
            return pattern;
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
        /// is centred at m half-pitches, on the left face for even m and the
        /// right face for odd m; each line runs from the end of one loop to
        /// the start of the next. The count is rounded so the pattern fits the
        /// length exactly: an open run starts and ends at the apex of a loop,
        /// on the face, so it is cut square across the ends of the base; a
        /// closed one has an even count so the faces alternate all the way
        /// round.
        /// </summary>
        private static NurbsCurve Flat(double length, double width, bool closed, bool refine, double shape)
        {
            double pitch = width * (ZigzagPitch + shape * (LoopPitch - ZigzagPitch));
            int count = closed
                ? Math.Max(2, 2 * (int)Math.Round(length / pitch))
                : Math.Max(1, (int)Math.Round(2 * length / pitch));
            double step = length / count;    // between neighbouring loops, a half pitch
            double r = shape * step;         // loop radius; at shape 1 a loop spans a pitch
            double inset = 0.5 * width - r;  // loop ends sit this far off the centreline

            double Side(int m) => m % 2 == 0 ? 1 : -1;
            Point3d Centre(int m) => new Point3d(m * step, Side(m) * inset, 0);
            Point3d Apex(int m) => new Point3d(m * step, Side(m) * 0.5 * width, 0);
            Point3d Start(int m) => new Point3d(m * step - r, Side(m) * inset, 0);
            Point3d End(int m) => new Point3d(m * step + r, Side(m) * inset, 0);
            // loops on the left face run clockwise over their top
            bool Clockwise(int m) => m % 2 == 0;

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
