using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Batting
{
    /// <summary>
    /// Batting — draws a polyline as batting, with a live preview. The
    /// polyline becomes the batting's hidden base.
    /// </summary>
    public class BattingCommand : Command
    {
        public override string EnglishName => "Batting";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugIn = BattingPlugIn.Instance!;
            double width = plugIn.GetWidth(doc);
            var style = new StyleOptions(plugIn.Style);
            var axis = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
            var points = new List<Point3d>();
            bool close = false;

            while (true)
            {
                var gp = new GetPoint();
                gp.SetCommandPrompt(points.Count == 0
                    ? "Start of batting"
                    : "Next point of batting. Press Enter when done");
                int widthOption = gp.AddOption("Width", BattingInput.Format(width));
                style.AddTo(gp);
                int closeOption = points.Count >= 3 ? gp.AddOption("Close") : -1;
                int undoOption = points.Count >= 1 ? gp.AddOption("Undo") : -1;
                if (points.Count > 0)
                {
                    gp.SetBasePoint(points[^1], true);
                    gp.DrawLineFromPoint(points[^1], true);
                    gp.DynamicDraw += (sender, e) =>
                    {
                        var preview = new PolylineCurve(new List<Point3d>(points) { e.CurrentPoint });
                        foreach (var curve in BattingInput.Preview(preview, width, axis, style.Style))
                            e.Display.DrawCurve(curve, Rhino.ApplicationSettings.AppearanceSettings.FeedbackColor);
                    };
                }
                gp.AcceptNothing(points.Count >= 2);

                var result = gp.Get();
                if (result == GetResult.Point)
                {
                    var point = gp.Point();
                    if (points.Count == 0)
                        axis = gp.View().ActiveViewport.ConstructionPlane().ZAxis;
                    else if (point.DistanceTo(points[^1]) <= doc.ModelAbsoluteTolerance)
                        continue;
                    points.Add(point);
                }
                else if (result == GetResult.Option)
                {
                    int option = gp.OptionIndex();
                    if (style.Handle(gp))
                        continue;
                    if (option == widthOption)
                    {
                        var widthResult = BattingInput.GetWidth(doc, ref width);
                        if (widthResult != Result.Success)
                            return widthResult;
                    }
                    else if (option == closeOption)
                    {
                        close = true;
                        break;
                    }
                    else if (option == undoOption)
                    {
                        points.RemoveAt(points.Count - 1);
                    }
                }
                else if (result == GetResult.Nothing)
                {
                    break;
                }
                else
                {
                    return Result.Cancel;
                }
            }

            if (close)
                points.Add(points[0]);
            plugIn.SetWidth(doc, width);
            plugIn.Style = style.Style;
            BattingSet.Add(doc, BattingData.Build(new PolylineCurve(points), null, width, axis, style.Style));
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// CurveToBatting — turns curves into battings that follow them.
    /// </summary>
    public class CurveToBattingCommand : Command
    {
        public override string EnglishName => "CurveToBatting";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new GetObject();
            go.SetCommandPrompt("Select curves to convert to batting");
            go.GeometryFilter = ObjectType.Curve;
            go.SetCustomGeometryFilter((obj, geometry, component) => !BattingSet.IsBatting(obj));
            go.SubObjectSelect = false;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            var plugIn = BattingPlugIn.Instance!;
            double width = plugIn.GetWidth(doc);
            var deleteInput = new OptionToggle(plugIn.DeleteInput, "No", "Yes");
            var style = new StyleOptions(plugIn.Style);
            bool closed = go.Objects().All(objRef => objRef.Curve()?.IsClosed == true);
            var result = BattingInput.GetWidth(doc, ref width, deleteInput, style, closed);
            if (result != Result.Success)
                return result;
            plugIn.SetWidth(doc, width);
            plugIn.DeleteInput = deleteInput.CurrentValue;
            plugIn.Style = style.Style;

            var cplaneAxis = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
            foreach (var objRef in go.Objects())
            {
                var curve = objRef.Curve();
                if (curve == null)
                    continue;
                // a line lies in many planes, so use the one it was most likely drawn in
                var axis = !curve.IsLinear() && curve.TryGetPlane(out var plane) ? plane.ZAxis : cplaneAxis;
                var batting = BattingData.Build(curve, null, width, axis, style.Style);
                BattingInput.Place(doc, objRef.Object(), batting, deleteInput.CurrentValue);
            }
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// BattingBetween — fills the layer between two face curves with batting
    /// that follows its thickness where it varies, as in tapered insulation.
    /// </summary>
    public class BattingBetweenCommand : Command
    {
        public override string EnglishName => "BattingBetween";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugIn = BattingPlugIn.Instance!;
            var style = new StyleOptions(plugIn.Style);
            var deleteInput = new OptionToggle(plugIn.DeleteInput, "No", "Yes");

            var go = new GetObject();
            go.SetCommandPrompt("Select the two faces of the insulation layer");
            go.GeometryFilter = ObjectType.Curve;
            go.SetCustomGeometryFilter((obj, geometry, component) => !BattingSet.IsBatting(obj));
            go.SubObjectSelect = false;
            // keep what is picked while options are changed
            go.EnableClearObjectsOnEntry(false);
            go.EnableUnselectObjectsOnExit(false);
            go.DeselectAllBeforePostSelect = false;
            while (true)
            {
                go.ClearCommandOptions();
                style.AddTo(go, between: true);
                go.AddOptionToggle("DeleteInput", ref deleteInput);
                var result = go.GetMultiple(2, 2);
                if (result == GetResult.Option)
                {
                    style.Handle(go);
                    go.EnablePreSelect(false, true);
                    continue;
                }
                if (result != GetResult.Object)
                    return Result.Cancel;
                break;
            }

            var a = go.Object(0).Curve();
            var b = go.Object(1).Curve();
            if (a == null || b == null)
                return Result.Failure;
            plugIn.Style = style.Style;
            plugIn.DeleteInput = deleteInput.CurrentValue;

            var cplaneAxis = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
            var batting = BattingData.Build(a, b, 0, BattingPattern.Axis(a, b, cplaneAxis), style.Style);
            BattingInput.Place(doc, go.Object(0).Object(), batting, deleteInput.CurrentValue);
            if (deleteInput.CurrentValue)
                doc.Objects.Delete(go.Object(1).Object(), true);
            doc.Objects.UnselectAll();
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// BattingWidth — changes the width of existing battings.
    /// </summary>
    public class BattingWidthCommand : Command
    {
        public override string EnglishName => "BattingWidth";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var battings = BattingInput.GetBattings()?
                .Where(batting => !BattingData.From(batting.Geometry)!.IsBetween).ToList();
            if (battings == null)
                return Result.Cancel;
            if (battings.Count == 0)
            {
                RhinoApp.WriteLine("Battings between two curves take their width from the curves.");
                return Result.Nothing;
            }

            double width = BattingData.From(battings[0].Geometry)!.Width;
            var result = BattingInput.GetWidth(doc, ref width);
            if (result != Result.Success)
                return result;
            BattingPlugIn.Instance!.SetWidth(doc, width);

            foreach (var batting in battings)
                BattingSet.Replace(batting, BattingData.From(batting.Geometry)!.Rebuild(width: width));
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// BattingExclude — cuts battings away inside closed curves, or removes
    /// those cut-outs again (Clear).
    /// </summary>
    public class BattingExcludeCommand : Command
    {
        public override string EnglishName => "BattingExclude";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var battings = BattingInput.GetBattings();
            if (battings == null)
                return Result.Cancel;

            var go = new GetObject();
            go.SetCommandPrompt("Select closed curves to cut out of the batting");
            go.GeometryFilter = ObjectType.Curve;
            go.GeometryAttributeFilter = GeometryAttributeFilter.ClosedCurve;
            go.SetCustomGeometryFilter((obj, geometry, component) => !BattingSet.IsBatting(obj));
            go.SubObjectSelect = false;
            // the battings are still selected from the first prompt
            go.EnablePreSelect(false, true);
            int clearOption = go.AddOption("Clear");
            var result = go.GetMultiple(1, 0);
            bool clear = result == GetResult.Option && go.OptionIndex() == clearOption;
            if (!clear && go.CommandResult() != Result.Success)
                return go.CommandResult();

            foreach (var batting in battings)
            {
                var data = BattingData.From(batting.Geometry)!;
                var exclusions = new List<Curve>();
                if (!clear)
                {
                    // exclusions are kept in the batting's plane
                    var plane = new Plane(data.Base!.PointAtStart, data.Axis);
                    exclusions.AddRange(data.Exclusions);
                    exclusions.AddRange(go.Objects().Select(objRef => Curve.ProjectToPlane(objRef.Curve(), plane)));
                }
                BattingSet.Replace(batting, data.Rebuild(exclusions: exclusions));
            }
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// BattingExtractBase — copies the base curve each batting follows out as
    /// an ordinary curve on the current layer, left selected: both faces, for
    /// a batting between two curves. The battings stay as they are.
    /// </summary>
    public class BattingExtractBaseCommand : Command
    {
        public override string EnglishName => "BattingExtractBase";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var battings = BattingInput.GetBattings();
            if (battings == null)
                return Result.Cancel;

            doc.Objects.UnselectAll();
            foreach (var batting in battings)
            {
                // hide the batting's own base, which would hide the copy under it
                batting.GripsOn = false;
                var data = BattingData.From(batting.Geometry)!;
                foreach (var curve in new[] { data.Base, data.Other })
                {
                    if (curve != null)
                        doc.Objects.Select(doc.Objects.AddCurve(curve.DuplicateCurve()));
                }
            }
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// SelBatting — selects all battings.
    /// </summary>
    public class SelBattingCommand : Command
    {
        public override string EnglishName => "SelBatting";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var battings = doc.Objects.GetObjectList(ObjectType.Curve)
                .Where(obj => BattingSet.IsBatting(obj) && obj.IsSelectable(true, false, false, false))
                .ToList();
            foreach (var obj in battings)
                obj.Select(true);
            int count = BattingSet.Primaries(battings).Count;
            RhinoApp.WriteLine(count == 1 ? "1 batting selected." : $"{count} battings selected.");
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// BattingMatch — gives battings the width and style of another, as
    /// MatchProperties does for layers and colours.
    /// </summary>
    public class BattingMatchCommand : Command
    {
        public override string EnglishName => "BattingMatch";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var targets = BattingInput.GetBattings("Select battings to change");
            if (targets == null)
                return Result.Cancel;

            var go = new GetObject();
            go.SetCommandPrompt("Select the batting to match");
            go.GeometryFilter = ObjectType.Curve;
            go.SetCustomGeometryFilter((obj, geometry, component) => BattingSet.IsBatting(obj));
            go.SubObjectSelect = false;
            go.EnablePreSelect(false, true);
            go.DeselectAllBeforePostSelect = false;
            if (go.Get() != GetResult.Object || BattingSet.Primary(go.Object(0).Object()) is not RhinoObject source)
                return Result.Cancel;

            var match = BattingData.From(source.Geometry)!;
            foreach (var target in targets.Where(target => target.Id != source.Id))
            {
                var data = BattingData.From(target.Geometry)!;
                // a batting between two curves has no width of its own to give or take
                double? width = match.IsBetween || data.IsBetween ? null : match.Width;
                BattingSet.Replace(target, data.Rebuild(width: width, style: match.Style));
            }
            doc.Objects.UnselectAll();
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>
    /// The style options the commands share: Shape, Spacing, Corners, Align
    /// and Faces. The list options keep their own index, since Rhino only
    /// reports which option was picked.
    /// </summary>
    internal class StyleOptions
    {
        private OptionDouble m_shape;
        private OptionDouble m_spacing;
        private OptionToggle m_faces;
        private Corners m_corners;
        private Alignment m_alignment;
        private int m_cornersIndex = -1;
        private int m_alignIndex = -1;

        public StyleOptions(BattingStyle style)
        {
            m_shape = new OptionDouble(style.Shape, 0, 1);
            m_spacing = new OptionDouble(style.Spacing, BattingStyle.MinSpacing, BattingStyle.MaxSpacing);
            m_faces = new OptionToggle(style.Faces, "No", "Yes");
            m_corners = style.Corners;
            m_alignment = style.Alignment;
        }

        public BattingStyle Style => new BattingStyle
        {
            Shape = m_shape.CurrentValue,
            Spacing = m_spacing.CurrentValue,
            Corners = m_corners,
            Alignment = m_alignment,
            Faces = m_faces.CurrentValue,
        };

        /// <summary>
        /// Adds the options to get. Between two curves, corners and alignment
        /// don't apply; for closed curves the alignments are Inside and Outside.
        /// </summary>
        public void AddTo(GetBaseClass get, bool between = false, bool closed = false)
        {
            get.AddOptionDouble("Shape", ref m_shape, "0 = zig-zag, 1 = curved");
            get.AddOptionDouble("Spacing", ref m_spacing, "1 = standard");
            m_cornersIndex = between ? -1 : get.AddOptionList("Corners", Enum.GetNames(typeof(Corners)), (int)m_corners);
            m_alignIndex = between ? -1 : get.AddOptionList("Align", BattingStyle.AlignmentNames(closed), (int)m_alignment);
            get.AddOptionToggle("Faces", ref m_faces);
        }

        /// <summary>Takes the choice from a list option, if that is what was just picked.</summary>
        public bool Handle(GetBaseClass get)
        {
            int index = get.OptionIndex();
            if (index < 0)
                return false;
            if (index == m_cornersIndex)
                m_corners = (Corners)get.Option().CurrentListOptionIndex;
            else if (index == m_alignIndex)
                m_alignment = (Alignment)get.Option().CurrentListOptionIndex;
            else
                return false;
            return true;
        }
    }

    internal static class BattingInput
    {
        public static string Format(double width) => width.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>What a batting along baseCurve would look like, faces included.</summary>
        public static IEnumerable<Curve> Preview(Curve baseCurve, double width, Vector3d axis, BattingStyle style)
        {
            var curves = BattingPattern.Create(baseCurve, width, axis, style, clean: false);
            return style.Faces ? curves.Concat(BattingPattern.Faces(baseCurve, width, axis, style.Alignment)) : curves;
        }

        /// <summary>
        /// Puts a new batting in the document: in place of input, keeping its
        /// id, layer, colour and linetype, or beside it with the same attributes.
        /// </summary>
        public static void Place(RhinoDoc doc, RhinoObject input, List<Curve> batting, bool replace)
        {
            if (replace)
            {
                BattingSet.Replace(input, batting);
                return;
            }
            var attributes = input.Attributes.Duplicate();
            attributes.ObjectId = Guid.Empty;
            BattingSet.Add(doc, batting, attributes);
        }

        /// <summary>
        /// Asks for battings; picking any piece of a batting that exclusions
        /// cut apart counts as picking the whole batting.
        /// </summary>
        public static List<RhinoObject>? GetBattings(string prompt = "Select battings")
        {
            var go = new GetObject();
            go.SetCommandPrompt(prompt);
            go.GeometryFilter = ObjectType.Curve;
            go.SetCustomGeometryFilter((obj, geometry, component) => BattingSet.IsBatting(obj));
            go.SubObjectSelect = false;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success)
                return null;
            var battings = BattingSet.Primaries(go.Objects().Select(objRef => objRef.Object()));
            return battings.Count > 0 ? battings : null;
        }

        /// <summary>
        /// Asks for a batting width, typed as a number or measured between two
        /// picked points. Enter keeps the current value.
        /// </summary>
        public static Result GetWidth(RhinoDoc doc, ref double width, OptionToggle? deleteInput = null,
                                      StyleOptions? style = null, bool closed = false)
        {
            while (true)
            {
                var gp = new GetPoint();
                gp.SetCommandPrompt("Batting width");
                gp.SetDefaultNumber(width);
                gp.AcceptNumber(true, false);
                gp.AcceptNothing(true);
                style?.AddTo(gp, closed: closed);
                if (deleteInput != null)
                    gp.AddOptionToggle("DeleteInput", ref deleteInput);

                switch (gp.Get())
                {
                    case GetResult.Option:
                        style?.Handle(gp);
                        continue;
                    case GetResult.Nothing:
                        return Result.Success;
                    case GetResult.Number:
                        if (gp.Number() > doc.ModelAbsoluteTolerance)
                        {
                            width = gp.Number();
                            return Result.Success;
                        }
                        RhinoApp.WriteLine("The width must be greater than zero.");
                        continue;
                    case GetResult.Point:
                        var first = gp.Point();
                        var second = new GetPoint();
                        second.SetCommandPrompt("Second point of width");
                        second.SetBasePoint(first, true);
                        second.DrawLineFromPoint(first, true);
                        if (second.Get() != GetResult.Point)
                            return Result.Cancel;
                        double distance = first.DistanceTo(second.Point());
                        if (distance > doc.ModelAbsoluteTolerance)
                        {
                            width = distance;
                            return Result.Success;
                        }
                        continue;
                    default:
                        return Result.Cancel;
                }
            }
        }
    }
}
