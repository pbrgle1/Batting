using Rhino.DocObjects;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// Grips on a batting's hidden base curve, one per control point, and on
    /// its second face for a batting between two curves. Dragging them
    /// rebuilds the batting, all its pieces included, around the edited
    /// curves. BattingDisplay draws the curves themselves.
    /// </summary>
    public class BattingGrips : CustomObjectGrips
    {
        private readonly Face m_base;
        private readonly Face? m_other;
        private readonly double m_width;
        private readonly Vector3d m_axis;
        private readonly BattingStyle m_style;
        private readonly List<Curve> m_exclusions;
        private readonly Guid m_setId;

        private BattingGrips(BattingData data)
        {
            m_base = new Face(data.Base!, 0);
            m_other = data.Other == null ? null : new Face(data.Other, m_base.Count);
            m_width = data.Width;
            m_axis = data.Axis;
            m_style = data.Style;
            m_exclusions = data.Exclusions.Select(curve => curve.DuplicateCurve()).ToList();
            m_setId = data.SetId;

            foreach (var face in new[] { m_base, m_other })
            {
                if (face == null)
                    continue;
                for (int i = 0; i < face.Count; i++)
                    AddGrip(new BattingGrip { OriginalLocation = face.Nurbs.Points[i].Location });
            }
        }

        /// <summary>How many grips a batting gets.</summary>
        public static int Count(BattingData data) =>
            Count(data.Base!) + (data.Other == null ? 0 : Count(data.Other));

        /// <summary>
        /// How many grips a curve gets: one per control point, except that a
        /// closed curve repeats its first control point(s) at the end and
        /// those share a grip.
        /// </summary>
        public static int Count(Curve curve)
        {
            var nurbs = curve as NurbsCurve ?? curve.ToNurbsCurve();
            int count = nurbs.Points.Count;
            if (nurbs.IsClosed)
                count -= nurbs.IsPeriodic ? nurbs.Degree : 1;
            return count;
        }

        /// <summary>Shows the base grips of a batting. Rhino also calls this to
        /// put them back after each edit.</summary>
        public static void TurnOn(RhinoObject obj)
        {
            var data = BattingData.From(obj.Geometry);
            if (data?.Base == null)
                return;
            obj.EnableCustomGrips(new BattingGrips(data));
            BattingDisplay.Track(obj);
        }

        /// <summary>One curve the grips edit, and where its grips start in the list.</summary>
        private class Face
        {
            public Face(Curve curve, int first)
            {
                Curve = curve.DuplicateCurve();
                Nurbs = Curve.ToNurbsCurve();
                First = first;
                Count = BattingGrips.Count(Curve);
            }

            public Curve Curve { get; }
            public NurbsCurve Nurbs { get; }
            public int First { get; }
            public int Count { get; }
        }

        private Curve Edited(Face face)
        {
            var nurbs = (NurbsCurve)face.Nurbs.Duplicate();
            for (int i = 0; i < nurbs.Points.Count; i++)
                nurbs.Points.SetPoint(i, Grip(face.First + i % face.Count).CurrentLocation, nurbs.Points[i].Weight);
            // keep polylines as polylines
            if (face.Curve is PolylineCurve or LineCurve && nurbs.TryGetPolyline(out var polyline))
                return new PolylineCurve(polyline);
            return nurbs;
        }

        private List<Curve> Build(bool preview = false) =>
            BattingData.Build(Edited(m_base), m_other == null ? null : Edited(m_other), m_width, m_axis, m_style,
                              m_exclusions, m_setId, preview);

        protected override GeometryBase NewGeometry()
        {
            if (!GripsMoved)
                return null!;
            // Rhino replaces the object these grips are on with the first
            // curve; the batting's other pieces are updated here alongside it.
            var curves = Build();
            if (OwnerObject != null)
                BattingSet.Replace(OwnerObject, curves, replacePrimary: false);
            return curves[0];
        }

        protected override void OnDraw(GripsDrawEventArgs args)
        {
            if (args.DrawDynamicStuff)
            {
                // while dragging, the edited curves and the batting they would make
                if (GripsMoved)
                {
                    args.Display.DrawCurve(Edited(m_base), args.GripColor);
                    if (m_other != null)
                        args.Display.DrawCurve(Edited(m_other), args.GripColor);
                    foreach (var curve in Build(preview: true))
                        args.Display.DrawCurve(curve, args.GripColor);
                }

                // the control polygons, for curves that are not polylines
                foreach (var face in new[] { m_base, m_other })
                {
                    if (face == null || face.Curve is PolylineCurve or LineCurve)
                        continue;
                    int segments = face.Nurbs.IsClosed ? face.Count : face.Count - 1;
                    for (int i = 0; i < segments; i++)
                    {
                        int a = face.First + i;
                        int b = face.First + (i + 1) % face.Count;
                        args.DrawControlPolygonLine(Grip(a).CurrentLocation, Grip(b).CurrentLocation, a, b);
                    }
                }
            }
            base.OnDraw(args);
        }
    }

    public class BattingGrip : CustomGripObject
    {
        public override string ShortDescription(bool plural) =>
            plural ? "batting points" : "batting point";
    }
}
