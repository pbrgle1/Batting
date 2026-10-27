using Batting;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BattingGH
{
    public class BattingGHInfo : GH_AssemblyInfo
    {
        public override string Name => "Batting";
        public override string Description => "Batting (insulation) symbol along a curve or between two curves.";
        public override Guid Id => new Guid("6E2B1F4C-8A3D-4C7E-B95A-0D4F7C21E8B6");
        public override string AuthorName => "pbrgle1";
        public override string AuthorContact => "https://github.com/pbrgle1/Batting";
        public override string Version => "1.2.1";
        public override System.Drawing.Bitmap Icon => Icons.Get("batting");
    }

    internal static class Icons
    {
        public static System.Drawing.Bitmap Get(string name)
        {
            using var stream = typeof(Icons).Assembly.GetManifestResourceStream($"BattingGH.Icons.{name}.png")!;
            return new System.Drawing.Bitmap(stream);
        }
    }

    /// <summary>
    /// What the components share: the Batting and Faces outputs, and baking.
    /// Baking makes real battings, which keep their base and settings, so
    /// they can be edited in Rhino with grips and the Properties panel like
    /// any batting.
    /// </summary>
    public abstract class BattingComponentBase : GH_Component
    {
        // what this solution built, item by item, to make battings from when baking
        private readonly List<Func<List<Curve>>> m_bakes = new List<Func<List<Curve>>>();

        protected BattingComponentBase(string name, string nickname, string description)
            : base(name, nickname, description, "Batting", "Batting")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Batting", "B", "The batting curves", GH_ParamAccess.list);
            pManager.AddCurveParameter("Faces", "F", "The faces of the insulation layer, if Faces is on", GH_ParamAccess.list);
        }

        protected override void BeforeSolveInstance()
        {
            m_bakes.Clear();
            base.BeforeSolveInstance();
        }

        /// <summary>The style inputs: shape, spacing and faces, then corners and alignment if given.</summary>
        protected static BattingStyle Style(IGH_DataAccess da, int shape, int spacing, int faces, int corners = -1, int align = -1)
        {
            double shapeValue = 1, spacingValue = 1;
            bool facesValue = false;
            int cornersValue = 0, alignValue = 0;
            da.GetData(shape, ref shapeValue);
            da.GetData(spacing, ref spacingValue);
            da.GetData(faces, ref facesValue);
            if (corners >= 0)
                da.GetData(corners, ref cornersValue);
            if (align >= 0)
                da.GetData(align, ref alignValue);
            return new BattingStyle
            {
                Shape = shapeValue,
                Spacing = spacingValue,
                Faces = facesValue,
                Corners = Enum.IsDefined(typeof(Corners), cornersValue) ? (Corners)cornersValue : Corners.Fan,
                Alignment = Enum.IsDefined(typeof(Alignment), alignValue) ? (Alignment)alignValue : Alignment.Centre,
            };
        }

        protected static List<Curve> Exclusions(IGH_DataAccess da, int index)
        {
            var exclusions = new List<Curve>();
            da.GetDataList(index, exclusions);
            return exclusions.Where(curve => curve != null && curve.IsClosed).ToList();
        }

        /// <summary>Sets the outputs for one batting, and keeps what's needed to bake it.</summary>
        protected void Output(IGH_DataAccess da, Curve baseCurve, Curve? other, double width, Vector3d axis,
                              BattingStyle style, List<Curve> exclusions)
        {
            var pattern = other == null
                ? BattingPattern.Create(baseCurve, width, axis, style)
                : BattingPattern.CreateBetween(baseCurve, other, axis, style);
            var faces = !style.Faces ? new List<Curve>()
                : other == null ? BattingPattern.Faces(baseCurve, width, axis, style.Alignment)
                : new List<Curve> { baseCurve.DuplicateCurve(), other.DuplicateCurve() };
            double tolerance = other == null ? width : BattingPattern.Thickness(baseCurve, other, axis);
            List<Curve> Cut(List<Curve> curves) =>
                curves.SelectMany(curve => BattingPattern.CutOut(curve, exclusions, axis, tolerance)).ToList();
            da.SetDataList(0, Cut(pattern));
            da.SetDataList(1, Cut(faces));

            var bakeBase = baseCurve.DuplicateCurve();
            var bakeOther = other?.DuplicateCurve();
            var bakeExclusions = exclusions.Select(curve => curve.DuplicateCurve()).ToList();
            m_bakes.Add(() => BattingData.Build(bakeBase, bakeOther, width, axis, style, bakeExclusions));
        }

        public override bool IsBakeCapable => m_bakes.Count > 0;

        public override void BakeGeometry(RhinoDoc doc, List<Guid> obj_ids) => BakeGeometry(doc, null!, obj_ids);

        public override void BakeGeometry(RhinoDoc doc, ObjectAttributes att, List<Guid> obj_ids)
        {
            foreach (var bake in m_bakes)
            {
                var attributes = att?.Duplicate() ?? doc.CreateDefaultAttributes();
                attributes.ObjectId = Guid.Empty;
                obj_ids.AddRange(BattingSet.Add(doc, bake(), attributes));
            }
            doc.Views.Redraw();
        }

        protected static void NameValues(IGH_Param param, params string[] names)
        {
            if (param is Param_Integer integer)
            {
                for (int i = 0; i < names.Length; i++)
                    integer.AddNamedValue(names[i], i);
            }
        }
    }

    /// <summary>Batting along a curve.</summary>
    public class BattingComponent : BattingComponentBase
    {
        public BattingComponent()
            : base("Batting", "Batting", "Batting (insulation) symbol along a curve. Bake it to get a batting you can edit in Rhino.")
        {
        }

        public override Guid ComponentGuid => new Guid("C4A9E2D7-3B61-4F08-9E5C-7A1D2B8F6E30");

        protected override System.Drawing.Bitmap Icon => Icons.Get("batting");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Curve", "C", "The base curve the batting follows", GH_ParamAccess.item);
            pManager.AddNumberParameter("Width", "W", "Thickness of the insulation; the last width used if left empty", GH_ParamAccess.item);
            pManager.AddNumberParameter("Shape", "S", "0 = zig-zag, 1 = curved loops", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Spacing", "Sp", "Distance between the loops, 0.5 to 2; 1 = standard", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Corners", "Co", "0 = Fan, 1 = Butt, 2 = Mitre", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Align", "A", "0 = Centre, 1 = Left (inside a closed curve), 2 = Right (outside)", GH_ParamAccess.item, 0);
            pManager.AddBooleanParameter("Faces", "F", "Also draw the two faces of the insulation layer", GH_ParamAccess.item, false);
            pManager.AddCurveParameter("Exclusions", "E", "Closed curves to cut the batting away inside", GH_ParamAccess.list);
            pManager[1].Optional = true;
            pManager[7].Optional = true;
            NameValues(pManager[4], "Fan", "Butt", "Mitre");
            NameValues(pManager[5], "Centre", "Left", "Right");
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Curve? curve = null;
            if (!da.GetData(0, ref curve) || curve == null)
                return;
            double width = 0;
            if (!da.GetData(1, ref width))
                width = BattingPlugIn.Instance?.GetWidth(RhinoDoc.ActiveDoc) ?? 1;
            if (width <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The width must be greater than zero.");
                return;
            }
            var axis = BattingPattern.Axis(curve, null, Vector3d.ZAxis);
            Output(da, curve, null, width, axis, Style(da, 2, 3, 6, 4, 5), Exclusions(da, 7));
        }
    }

    /// <summary>Batting filling the layer between two curves.</summary>
    public class BattingBetweenComponent : BattingComponentBase
    {
        public BattingBetweenComponent()
            : base("Batting Between", "BattingBtw",
                   "Batting filling the layer between two curves, following its thickness where it varies. " +
                   "Bake it to get a batting you can edit in Rhino.")
        {
        }

        public override Guid ComponentGuid => new Guid("8F3C6A12-D94E-4B57-A0E1-5C2B7D9E4F81");

        protected override System.Drawing.Bitmap Icon => Icons.Get("between");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Face A", "A", "One face of the insulation layer", GH_ParamAccess.item);
            pManager.AddCurveParameter("Face B", "B", "The other face", GH_ParamAccess.item);
            pManager.AddNumberParameter("Shape", "S", "0 = zig-zag, 1 = curved loops", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Spacing", "Sp", "Distance between the loops, 0.5 to 2; 1 = standard", GH_ParamAccess.item, 1);
            pManager.AddBooleanParameter("Faces", "F", "Also output the two faces", GH_ParamAccess.item, false);
            pManager.AddCurveParameter("Exclusions", "E", "Closed curves to cut the batting away inside", GH_ParamAccess.list);
            pManager[5].Optional = true;
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Curve? a = null, b = null;
            if (!da.GetData(0, ref a) || !da.GetData(1, ref b) || a == null || b == null)
                return;
            var axis = BattingPattern.Axis(a, b, Vector3d.ZAxis);
            Output(da, a, b, 0, axis, Style(da, 2, 3, 4), Exclusions(da, 5));
        }
    }
}
