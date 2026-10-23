using Rhino.DocObjects;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// Grips on a batting's hidden base curve, one per control point. Dragging
    /// them rebuilds the batting, all its pieces included, around the edited
    /// base. BattingDisplay draws the base itself.
    /// </summary>
    public class BattingGrips : CustomObjectGrips
    {
        private readonly Curve m_base;
        private readonly NurbsCurve m_nurbs;
        private readonly double m_width;
        private readonly Vector3d m_axis;
        private readonly double m_shape;
        private readonly Corners m_corners;
        private readonly List<Curve> m_exclusions;
        private readonly Guid m_setId;

        private BattingGrips(BattingData data)
        {
            m_base = data.Base!.DuplicateCurve();
            m_nurbs = m_base.ToNurbsCurve();
            m_width = data.Width;
            m_axis = data.Axis;
            m_shape = data.Shape;
            m_corners = data.Corners;
            m_exclusions = data.Exclusions.Select(curve => curve.DuplicateCurve()).ToList();
            m_setId = data.SetId;

            for (int i = 0; i < Count(m_nurbs); i++)
                AddGrip(new BattingGrip { OriginalLocation = m_nurbs.Points[i].Location });
        }

        /// <summary>
        /// How many grips a base curve gets: one per control point, except that
        /// a closed curve repeats its first control point(s) at the end and
        /// those share a grip.
        /// </summary>
        public static int Count(Curve baseCurve)
        {
            var nurbs = baseCurve as NurbsCurve ?? baseCurve.ToNurbsCurve();
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

        private Curve EditedBase()
        {
            var nurbs = (NurbsCurve)m_nurbs.Duplicate();
            for (int i = 0; i < nurbs.Points.Count; i++)
                nurbs.Points.SetPoint(i, Grip(i % GripCount).CurrentLocation, nurbs.Points[i].Weight);
            // keep polylines as polylines
            if (m_base is PolylineCurve or LineCurve && nurbs.TryGetPolyline(out var polyline))
                return new PolylineCurve(polyline);
            return nurbs;
        }

        private List<Curve> Build() =>
            BattingData.Build(EditedBase(), m_width, m_axis, m_shape, m_corners, m_exclusions, m_setId);

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
                // while dragging, the edited base and the batting it would make
                if (GripsMoved)
                {
                    args.Display.DrawCurve(EditedBase(), args.GripColor);
                    foreach (var curve in Build())
                        args.Display.DrawCurve(curve, args.GripColor);
                }

                // the control polygon, for bases that are not polylines
                if (m_base is not PolylineCurve and not LineCurve)
                {
                    int segments = m_nurbs.IsClosed ? GripCount : GripCount - 1;
                    for (int i = 0; i < segments; i++)
                    {
                        int j = (i + 1) % GripCount;
                        args.DrawControlPolygonLine(Grip(i).CurrentLocation, Grip(j).CurrentLocation, i, j);
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
