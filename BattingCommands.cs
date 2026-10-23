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
            var shape = new OptionDouble(plugIn.Shape, 0, 1);
            var corners = new CornersOption(plugIn.Corners);
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
                gp.AddOptionDouble("Shape", ref shape, "0 = zig-zag, 1 = curved");
                corners.AddTo(gp);
                int closeOption = points.Count >= 3 ? gp.AddOption("Close") : -1;
                int undoOption = points.Count >= 1 ? gp.AddOption("Undo") : -1;
                if (points.Count > 0)
                {
                    gp.SetBasePoint(points[^1], true);
                    gp.DrawLineFromPoint(points[^1], true);
                    gp.DynamicDraw += (sender, e) =>
                    {
                        var preview = new PolylineCurve(new List<Point3d>(points) { e.CurrentPoint });
                        foreach (var curve in BattingPattern.Create(preview, width, axis, shape.CurrentValue, corners.Value))
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
                    if (corners.Handle(gp))
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
            plugIn.Shape = shape.CurrentValue;
            plugIn.Corners = corners.Value;
            BattingSet.Add(doc, BattingData.Build(new PolylineCurve(points), width, axis, shape.CurrentValue, corners.Value));
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
            var shape = new OptionDouble(plugIn.Shape, 0, 1);
            var corners = new CornersOption(plugIn.Corners);
            var result = BattingInput.GetWidth(doc, ref width, deleteInput, shape, corners);
            if (result != Result.Success)
                return result;
            plugIn.SetWidth(doc, width);
            plugIn.DeleteInput = deleteInput.CurrentValue;
            plugIn.Shape = shape.CurrentValue;
            plugIn.Corners = corners.Value;

            var cplaneAxis = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
            foreach (var objRef in go.Objects())
            {
                var curve = objRef.Curve();
                if (curve == null)
                    continue;
                // a line lies in many planes, so use the one it was most likely drawn in
                var axis = !curve.IsLinear() && curve.TryGetPlane(out var plane) ? plane.ZAxis : cplaneAxis;
                var batting = BattingData.Build(curve, width, axis, shape.CurrentValue, corners.Value);
                if (deleteInput.CurrentValue)
                {
                    // keeps the object's id, layer, colour, linetype and so on
                    BattingSet.Replace(objRef.Object(), batting);
                }
                else
                {
                    var attributes = objRef.Object().Attributes.Duplicate();
                    attributes.ObjectId = Guid.Empty;
                    BattingSet.Add(doc, batting, attributes);
                }
            }
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
            var battings = BattingInput.GetBattings();
            if (battings == null)
                return Result.Cancel;

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
    /// an ordinary curve on the current layer, left selected. The battings
    /// stay as they are.
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
                var id = doc.Objects.AddCurve(BattingData.From(batting.Geometry)!.Base!.DuplicateCurve());
                doc.Objects.Select(id);
            }
            doc.Views.Redraw();
            return Result.Success;
        }
    }

    /// <summary>The Corners command option: Fan, Butt or Mitre.</summary>
    internal class CornersOption
    {
        private int m_index = -1;

        public CornersOption(Corners value)
        {
            Value = value;
        }

        public Corners Value { get; private set; }

        public void AddTo(GetBaseClass get) =>
            m_index = get.AddOptionList("Corners", Enum.GetNames(typeof(Corners)), (int)Value);

        /// <summary>Takes the choice if the option just picked was this one.</summary>
        public bool Handle(GetBaseClass get)
        {
            if (get.OptionIndex() != m_index)
                return false;
            Value = (Corners)get.Option().CurrentListOptionIndex;
            return true;
        }
    }

    internal static class BattingInput
    {
        public static string Format(double width) => width.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>
        /// Asks for battings; picking any piece of a batting that exclusions
        /// cut apart counts as picking the whole batting.
        /// </summary>
        public static List<RhinoObject>? GetBattings()
        {
            var go = new GetObject();
            go.SetCommandPrompt("Select battings");
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
                                      OptionDouble? shape = null, CornersOption? corners = null)
        {
            while (true)
            {
                var gp = new GetPoint();
                gp.SetCommandPrompt("Batting width");
                gp.SetDefaultNumber(width);
                gp.AcceptNumber(true, false);
                gp.AcceptNothing(true);
                if (shape != null)
                    gp.AddOptionDouble("Shape", ref shape, "0 = zig-zag, 1 = curved");
                corners?.AddTo(gp);
                if (deleteInput != null)
                    gp.AddOptionToggle("DeleteInput", ref deleteInput);

                switch (gp.Get())
                {
                    case GetResult.Option:
                        corners?.Handle(gp);
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
