using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// Draws the hidden base curve and the exclusions of each selected batting,
    /// and of each batting whose base grips are on, as dashed lines. While at
    /// it, it swaps the pattern's own control points for the base grips if a
    /// shown batting got them some other way than PointsOn.
    /// </summary>
    internal class BattingDisplay : DisplayConduit
    {
        private const int Dashes = 0x00FF00FF;

        // battings to draw the base of; dropped once deselected with grips off
        private static readonly HashSet<Guid> s_shown = new HashSet<Guid>();
        private bool m_fixQueued;

        public static void Track(RhinoObject primary) => s_shown.Add(primary.Id);

        /// <summary>The battings whose base is being drawn in doc.</summary>
        public static List<RhinoObject> Shown(RhinoDoc doc) =>
            s_shown.Select(doc.Objects.FindId).Where(obj => obj != null).ToList()!;

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            var doc = e.RhinoDoc;
            if (doc == null || s_shown.Count == 0)
                return;

            var colour = AppearanceSettings.FeedbackColor;
            foreach (var id in s_shown.ToList())
            {
                var obj = doc.Objects.FindId(id);
                var data = obj == null ? null : BattingData.From(obj.Geometry);
                if (obj == null || data?.Base == null || (!obj.GripsOn && obj.IsSelected(false) == 0))
                {
                    s_shown.Remove(id);
                    continue;
                }

                e.Display.DrawPatternedPolyline(Points(data.Base), colour, Dashes, 2, data.Base.IsClosed);
                foreach (var exclusion in data.Exclusions)
                    e.Display.DrawPatternedPolyline(Points(exclusion), colour, Dashes, 1, true);

                if (NeedsFixing(obj, data))
                    QueueFix(doc);
            }
        }

        /// <summary>
        /// True when the batting shows grips that are not its base grips, or
        /// one of its pieces shows grips at all.
        /// </summary>
        private static bool NeedsFixing(RhinoObject obj, BattingData data) =>
            (obj.GripsOn && (obj.GetGrips()?.Length ?? 0) != BattingGrips.Count(data.Base!)) ||
            BattingSet.Pieces(obj).Any(piece => piece.GripsOn);

        private void QueueFix(RhinoDoc doc)
        {
            if (m_fixQueued)
                return;
            m_fixQueued = true;
            // never change grips while Rhino is drawing
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                m_fixQueued = false;
                foreach (var id in s_shown.ToList())
                {
                    var obj = doc.Objects.FindId(id);
                    var data = obj == null ? null : BattingData.From(obj.Geometry);
                    if (obj == null || data?.Base == null || !NeedsFixing(obj, data))
                        continue;
                    foreach (var piece in BattingSet.Pieces(obj))
                        piece.GripsOn = false;
                    if (obj.GripsOn)
                    {
                        obj.GripsOn = false;
                        BattingGrips.TurnOn(obj);
                    }
                }
                doc.Views.Redraw();
            });
        }

        private static IEnumerable<Point3d> Points(Curve curve)
        {
            if (curve.TryGetPolyline(out var polyline))
                return polyline;
            var parameters = curve.DivideByCount(Math.Max(64, 8 * (curve.SpanCount + 1)), true);
            return parameters?.Select(curve.PointAt) ?? new[] { curve.PointAtStart, curve.PointAtEnd };
        }
    }
}
